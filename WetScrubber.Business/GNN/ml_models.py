"""
ml_models.py — the actual "self-learning" part.

Two scikit-learn models, each with the same lifecycle:
  train(df)   -> fit a pipeline, cross-validate, persist to model_store/
  load()      -> restore the last-persisted pipeline + metadata
  predict(x)  -> run it, report a confidence band and how many curated
                 rows it was trained on (so callers/UI can show "based on
                 6 samples" style honesty instead of false precision)

Both fall back to `None` when there isn't enough curated data yet — the
caller (chemistrypredictor.py) is responsible for falling back to the
original heuristic in that case. That fallback is intentional, not a bug:
a RandomForest "trained" on 3 rows is worse than an honest similarity
lookup over those same 3 rows.
"""

from __future__ import annotations

import json
import time
from dataclasses import dataclass, field
from pathlib import Path
from typing import Optional

import joblib
import numpy as np
import pandas as pd
from sklearn.compose import ColumnTransformer
from sklearn.ensemble import RandomForestRegressor
from sklearn.model_selection import KFold, cross_val_score
from sklearn.multioutput import MultiOutputRegressor
from sklearn.pipeline import Pipeline
from sklearn.preprocessing import OneHotEncoder, StandardScaler

from db import MIN_ROWS_CHEMISTRY, MIN_ROWS_DESIGN

STORE_DIR = Path(__file__).parent / "model_store"
STORE_DIR.mkdir(exist_ok=True)


def _confidence_band(n_samples: int, min_rows: int, cv_r2: Optional[float]) -> str:
    """Confidence is driven by how much curated data backs the model, not
    just by fit quality — a model trained on 9 rows with great CV score is
    still thin. Mirrors the HighSimilarity/ModerateSimilarity/LowSimilarity
    bands the KNN fallback already reports, so the .NET side and UI don't
    need to know which engine answered."""
    if n_samples < min_rows:
        return "LowSimilarity"
    if cv_r2 is not None and cv_r2 >= 0.6 and n_samples >= min_rows * 3:
        return "HighSimilarity"
    if n_samples >= min_rows * 2:
        return "ModerateSimilarity"
    return "LowSimilarity"


@dataclass
class TrainResult:
    trained: bool
    n_samples: int
    cv_r2: Optional[float]
    trained_at: float = field(default_factory=time.time)
    message: str = ""


class ChemistryModel:
    """Predicts Henry's constant, max efficiency, stoichiometric ratio,
    and pH operating range for a pollutant/liquid pair. Replaces the
    hand-picked-feature similarity score from the MVP with a learned
    RandomForest over the curated ChemicalReaction rows."""

    TARGETS = ["stoich_ratio", "max_efficiency", "min_ph", "max_ph"]
    # henrys_constant is modeled separately (see henrys_pipeline below),
    # log-scaled, since its raw range spans several orders of magnitude
    # (0.00002 .. 0.01) and would otherwise dominate/get dominated by the
    # other targets' loss if fit jointly.

    MODEL_PATH = STORE_DIR / "chemistry_model.joblib"
    META_PATH = STORE_DIR / "chemistry_meta.json"

    def __init__(self):
        self.pipeline: Optional[Pipeline] = None
        self.henrys_pipeline: Optional[Pipeline] = None
        self.meta: dict = {"trained": False, "n_samples": 0}
        self._liquid_catalog: dict = {}
        self._pollutant_catalog: dict = {}

    # -- persistence -----------------------------------------------------
    def load(self) -> bool:
        if not self.MODEL_PATH.exists() or not self.META_PATH.exists():
            return False
        bundle = joblib.load(self.MODEL_PATH)
        self.pipeline = bundle["pipeline"]
        self.henrys_pipeline = bundle["henrys_pipeline"]
        self._liquid_catalog = bundle.get("liquid_catalog", {})
        self._pollutant_catalog = bundle.get("pollutant_catalog", {})
        self.meta = json.loads(self.META_PATH.read_text())
        return True

    def _save(self):
        joblib.dump(
            {
                "pipeline": self.pipeline,
                "henrys_pipeline": self.henrys_pipeline,
                "liquid_catalog": self._liquid_catalog,
                "pollutant_catalog": self._pollutant_catalog,
            },
            self.MODEL_PATH,
        )
        self.META_PATH.write_text(json.dumps(self.meta, indent=2))

    # -- training ----------------------------------------------------------
    def train(self, df: pd.DataFrame) -> TrainResult:
        n = len(df)
        if n < MIN_ROWS_CHEMISTRY:
            self.meta = {"trained": False, "n_samples": n, "trained_at": time.time()}
            self.META_PATH.write_text(json.dumps(self.meta, indent=2))
            return TrainResult(False, n, None, message=f"Need {MIN_ROWS_CHEMISTRY} curated reactions, have {n}")

        features = df[["pollutant_molecular_weight", "liquid_code", "liquid_default_ph"]].copy()
        targets = df[self.TARGETS]
        henrys = np.log10(df["pollutant_default_henrys"].clip(lower=1e-8))

        pre = ColumnTransformer(
            [
                ("num", StandardScaler(), ["pollutant_molecular_weight", "liquid_default_ph"]),
                ("cat", OneHotEncoder(handle_unknown="ignore"), ["liquid_code"]),
            ]
        )
        pipeline = Pipeline(
            [("pre", pre), ("reg", MultiOutputRegressor(RandomForestRegressor(n_estimators=200, random_state=42)))]
        )
        henrys_pipeline = Pipeline(
            [("pre", pre), ("reg", RandomForestRegressor(n_estimators=200, random_state=42))]
        )

        cv_r2 = None
        try:
            k = min(5, n)
            if k >= 2:
                scores = cross_val_score(pipeline, features, targets, cv=KFold(n_splits=k, shuffle=True, random_state=42), scoring="r2")
                cv_r2 = float(np.mean(scores))
        except Exception:
            cv_r2 = None

        pipeline.fit(features, targets)
        henrys_pipeline.fit(features, henrys)

        self.pipeline = pipeline
        self.henrys_pipeline = henrys_pipeline
        self._liquid_catalog = (
            df.drop_duplicates("liquid_code").set_index("liquid_code")[["liquid_density", "liquid_default_ph"]].to_dict("index")
        )
        self._pollutant_catalog = (
            df.drop_duplicates("pollutant_code").set_index("pollutant_code")[["pollutant_molecular_weight", "pollutant_default_henrys"]].to_dict("index")
        )
        self.meta = {
            "trained": True,
            "n_samples": n,
            "cv_r2": cv_r2,
            "trained_at": time.time(),
        }
        self._save()
        return TrainResult(True, n, cv_r2)

    # -- prediction --------------------------------------------------------
    def predict(self, pollutant_name: str, pollutant_molecular_weight: float, liquid_type: str) -> Optional[dict]:
        if self.pipeline is None or not self.meta.get("trained"):
            return None

        liquid_info = self._liquid_catalog.get(liquid_type, {})
        liquid_default_ph = liquid_info.get("liquid_default_ph", 7.0)

        x = pd.DataFrame(
            [{
                "pollutant_molecular_weight": pollutant_molecular_weight,
                "liquid_code": liquid_type,
                "liquid_default_ph": liquid_default_ph,
            }]
        )
        pred = self.pipeline.predict(x)[0]
        henrys_log = self.henrys_pipeline.predict(x)[0]
        stoich, max_eff, min_ph, max_ph = pred

        n = self.meta.get("n_samples", 0)
        band = _confidence_band(n, MIN_ROWS_CHEMISTRY, self.meta.get("cv_r2"))

        return {
            "henrys_law_constant": round(float(10 ** henrys_log), 6),
            "max_removal_efficiency": round(float(max_eff), 1),
            "stoichiometric_ratio": round(float(max(stoich, 0)), 2),
            "min_operating_ph": round(float(min_ph), 1),
            "max_operating_ph": round(float(max_ph), 1),
            "confidence_band": band,
            "source": "learned_model",
            "trained_on_n_samples": n,
        }


class DesignOutcomeModel:
    """Calibration model: given design inputs + what the deterministic
    calculation engine predicted, learn the correction toward what field
    measurements actually showed. This is the model DesignOutcome.cs's
    comment describes as "a future model can be trained on it once
    there's enough volume" — this is that future model."""

    TARGETS = ["measured_efficiency", "measured_pressure_drop"]
    MODEL_PATH = STORE_DIR / "design_model.joblib"
    META_PATH = STORE_DIR / "design_meta.json"

    FEATURES_NUM = [
        "design_gas_flow_rate", "inlet_temperature", "moisture_content",
        "liquid_ph", "liquid_temperature", "design_lg_ratio",
        "tower_diameter", "tower_height", "packing_height",
        "design_predicted_efficiency", "design_predicted_pressure_drop",
    ]
    FEATURES_CAT = ["scrubber_type"]

    def __init__(self):
        self.pipeline: Optional[Pipeline] = None
        self.meta: dict = {"trained": False, "n_samples": 0}

    def load(self) -> bool:
        if not self.MODEL_PATH.exists() or not self.META_PATH.exists():
            return False
        self.pipeline = joblib.load(self.MODEL_PATH)
        self.meta = json.loads(self.META_PATH.read_text())
        return True

    def _save(self):
        joblib.dump(self.pipeline, self.MODEL_PATH)
        self.META_PATH.write_text(json.dumps(self.meta, indent=2))

    def train(self, df: pd.DataFrame) -> TrainResult:
        required_cols = self.FEATURES_NUM + self.TARGETS
        if df.empty or not set(required_cols).issubset(df.columns):
            self.meta = {"trained": False, "n_samples": 0, "trained_at": time.time()}
            self.META_PATH.write_text(json.dumps(self.meta, indent=2))
            return TrainResult(False, 0, None, message=f"Need {MIN_ROWS_DESIGN} recorded outcomes, have 0")

        df = df.dropna(subset=required_cols)
        n = len(df)
        if n < MIN_ROWS_DESIGN:
            self.meta = {"trained": False, "n_samples": n, "trained_at": time.time()}
            self.META_PATH.write_text(json.dumps(self.meta, indent=2))
            return TrainResult(False, n, None, message=f"Need {MIN_ROWS_DESIGN} recorded outcomes, have {n}")

        x = df[self.FEATURES_NUM + self.FEATURES_CAT]
        y = df[self.TARGETS]

        pre = ColumnTransformer(
            [
                ("num", StandardScaler(), self.FEATURES_NUM),
                ("cat", OneHotEncoder(handle_unknown="ignore"), self.FEATURES_CAT),
            ]
        )
        pipeline = Pipeline(
            [("pre", pre), ("reg", MultiOutputRegressor(RandomForestRegressor(n_estimators=200, random_state=42)))]
        )

        cv_r2 = None
        try:
            k = min(5, n)
            if k >= 2:
                scores = cross_val_score(pipeline, x, y, cv=KFold(n_splits=k, shuffle=True, random_state=42), scoring="r2")
                cv_r2 = float(np.mean(scores))
        except Exception:
            cv_r2 = None

        pipeline.fit(x, y)
        self.pipeline = pipeline
        self.meta = {"trained": True, "n_samples": n, "cv_r2": cv_r2, "trained_at": time.time()}
        self._save()
        return TrainResult(True, n, cv_r2)

    def predict(self, design_inputs: dict) -> Optional[dict]:
        if self.pipeline is None or not self.meta.get("trained"):
            return None
        x = pd.DataFrame([{**{k: design_inputs.get(k) for k in self.FEATURES_NUM + self.FEATURES_CAT}}])
        pred = self.pipeline.predict(x)[0]
        eff, dp = pred
        n = self.meta.get("n_samples", 0)
        band = _confidence_band(n, MIN_ROWS_DESIGN, self.meta.get("cv_r2"))
        return {
            "predicted_removal_efficiency": round(float(eff), 2),
            "predicted_pressure_drop": round(float(dp), 2),
            "confidence_band": band,
            "source": "learned_model",
            "trained_on_n_samples": n,
        }
