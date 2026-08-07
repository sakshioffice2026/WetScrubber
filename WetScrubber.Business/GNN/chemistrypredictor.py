"""
Chemistry & design-outcome prediction service — self-learning version.

WHAT CHANGED FROM THE MVP
==========================
The original version of this file did the GNN's job (predict missing
chemistry for an uncurated pollutant/liquid pair) using k-nearest-neighbor
similarity over a hardcoded list of known reactions, because there wasn't
enough curated data yet for a trained model.

This version is self-learning:
  - It pulls curated data straight from the WetScrubber MySQL DB
    (ChemicalReactions for chemistry, DesignOutcomes for design
    calibration) instead of a hardcoded list.
  - It trains real scikit-learn models (see ml_models.py) once there's
    enough curated data (MIN_TRAINING_ROWS_* env vars, default 8 rows).
  - It retrains automatically: a background watcher polls the DB every
    RETRAIN_POLL_MINUTES (default 15) and retrains whenever the curated
    row counts change — i.e. whenever a reviewer promotes a prediction
    into ChemicalReactions or a field outcome gets recorded into
    DesignOutcomes. No .NET changes are required for this to work.
  - The .NET side can also call POST /retrain right after such a write,
    for an immediate refresh instead of waiting for the next poll (see
    WetScrubber.Business/AI/ModelRetrainClient.cs).
  - Below the minimum row threshold, /predict transparently falls back
    to the original KNN heuristic — same behavior as before, so nothing
    breaks on a fresh install with no curated data yet.

RUN
===
pip install -r requirements.txt --break-system-packages
export WETSCRUBBER_DB_URL="mysql+pymysql://user:pass@host:3306/wetscrubber"
uvicorn chemistrypredictor:app --host 0.0.0.0 --port 8500

Then point WetScrubber's ChemistryPredictionOptions.BaseUrl at
http://localhost:8500 (already wired in Program.cs).

If WETSCRUBBER_DB_URL isn't set, the service still runs — it just never
has curated data to train on, so every prediction uses the KNN fallback.
"""

from __future__ import annotations

import logging
import os
from contextlib import asynccontextmanager
from typing import List, Optional

from apscheduler.schedulers.background import BackgroundScheduler
from fastapi import FastAPI, Query
from pydantic import BaseModel

import db
from ml_models import ChemistryModel, DesignOutcomeModel
from training_state import load_state, save_state

logging.basicConfig(level=logging.INFO)
log = logging.getLogger("chemistrypredictor")

RETRAIN_POLL_MINUTES = float(os.environ.get("RETRAIN_POLL_MINUTES", "15"))

chemistry_model = ChemistryModel()
design_model = DesignOutcomeModel()


# ── Original KNN heuristic — unchanged, kept as the fallback for when
# there isn't enough curated data for the learned model yet. ──────────
KNOWN_REACTIONS = [
    {"pollutant": "SO2", "liquid": "Caustic Soda", "molecular_weight": 64.07,
     "henrys_constant": 0.0083, "max_efficiency": 99.0, "stoich_ratio": 2.0,
     "min_ph": 6.5, "max_ph": 9.0},
    {"pollutant": "HCl", "liquid": "Caustic Soda", "molecular_weight": 36.46,
     "henrys_constant": 0.00002, "max_efficiency": 99.5, "stoich_ratio": 1.0,
     "min_ph": 5.0, "max_ph": 9.0},
    {"pollutant": "NH3", "liquid": "Sulfuric Acid", "molecular_weight": 17.03,
     "henrys_constant": 0.00061, "max_efficiency": 98.0, "stoich_ratio": 1.0,
     "min_ph": 2.0, "max_ph": 5.0},
    {"pollutant": "H2S", "liquid": "Sodium Hypochlorite", "molecular_weight": 34.08,
     "henrys_constant": 0.0102, "max_efficiency": 97.0, "stoich_ratio": 4.0,
     "min_ph": 8.0, "max_ph": 11.0},
    {"pollutant": "Cl2", "liquid": "Caustic Soda", "molecular_weight": 70.90,
     "henrys_constant": 0.0074, "max_efficiency": 99.0, "stoich_ratio": 2.0,
     "min_ph": 9.0, "max_ph": 12.0},
]


def _knn_similarity(a: dict, molecular_weight: float, liquid_type: str) -> float:
    weight_diff = abs(a["molecular_weight"] - molecular_weight)
    weight_score = 1.0 / (1.0 + weight_diff / 20.0)
    liquid_match = 1.0 if a["liquid"].lower() == liquid_type.lower() else 0.3
    return weight_score * 0.6 + liquid_match * 0.4


def _knn_confidence_band(top_score: float) -> str:
    if top_score >= 0.75:
        return "HighSimilarity"
    if top_score >= 0.45:
        return "ModerateSimilarity"
    return "LowSimilarity"


def _knn_predict(pollutant_name: str, molecular_weight: float, liquid_type: str) -> dict:
    scored = sorted(
        ((_knn_similarity(r, molecular_weight, liquid_type), r) for r in KNOWN_REACTIONS),
        key=lambda pair: pair[0],
        reverse=True,
    )
    top_k = scored[:3]
    total_weight = sum(score for score, _ in top_k) or 1e-6

    def weighted(field: str) -> float:
        return sum(score * r[field] for score, r in top_k) / total_weight

    return {
        "henrys_law_constant": round(weighted("henrys_constant"), 6),
        "max_removal_efficiency": round(weighted("max_efficiency"), 1),
        "stoichiometric_ratio": round(weighted("stoich_ratio"), 2),
        "min_operating_ph": round(min(r["min_ph"] for _, r in top_k), 1),
        "max_operating_ph": round(max(r["max_ph"] for _, r in top_k), 1),
        "confidence_band": _knn_confidence_band(top_k[0][0]),
        "nearest_matches": [f"{r['pollutant']} + {r['liquid']}" for _, r in top_k],
        "source": "knn_fallback",
        "trained_on_n_samples": 0,
    }


# ── Training orchestration ─────────────────────────────────────────────
def retrain_chemistry() -> dict:
    df = db.fetch_chemistry_training_data()
    result = chemistry_model.train(df)
    log.info("chemistry retrain: trained=%s n=%s cv_r2=%s", result.trained, result.n_samples, result.cv_r2)
    return result.__dict__


def retrain_design() -> dict:
    df = db.fetch_design_training_data()
    result = design_model.train(df)
    log.info("design retrain: trained=%s n=%s cv_r2=%s", result.trained, result.n_samples, result.cv_r2)
    return result.__dict__


def check_and_retrain_if_changed() -> dict:
    """Called on a timer. Only retrains the model(s) whose curated row
    count actually moved since the last check — this is what makes the
    service self-learning without needing anyone to remember to hit
    /retrain."""
    if not db.db_available():
        return {"skipped": "no WETSCRUBBER_DB_URL configured"}

    state = load_state()
    counts = db.fetch_row_counts()
    changed = {}

    if counts["chemistry"] != state["chemistry_row_count"]:
        changed["chemistry"] = retrain_chemistry()
        state["chemistry_row_count"] = counts["chemistry"]

    if counts["design"] != state["design_row_count"]:
        changed["design"] = retrain_design()
        state["design_row_count"] = counts["design"]

    save_state(state)
    return {"changed": changed, "counts": counts} if changed else {"changed": None, "counts": counts}


scheduler = BackgroundScheduler()


@asynccontextmanager
async def lifespan(app: FastAPI):
    chemistry_model.load()
    design_model.load()
    if db.db_available():
        # Run once at startup so a fresh deploy picks up whatever's
        # already curated, then keep polling.
        try:
            check_and_retrain_if_changed()
        except Exception:
            log.exception("initial retrain check failed")
        scheduler.add_job(
            check_and_retrain_if_changed,
            "interval",
            minutes=RETRAIN_POLL_MINUTES,
            id="auto_retrain_watcher",
        )
        scheduler.start()
        log.info("auto-retrain watcher started: every %s minutes", RETRAIN_POLL_MINUTES)
    else:
        log.warning("WETSCRUBBER_DB_URL not set — running on KNN fallback only, no self-learning")
    yield
    if scheduler.running:
        scheduler.shutdown(wait=False)


app = FastAPI(title="WetScrubber Prediction Service (self-learning)", lifespan=lifespan)


# ── Chemistry prediction ───────────────────────────────────────────────
class PredictionRequest(BaseModel):
    pollutant_name: str
    pollutant_molecular_weight: float
    liquid_type: str


class PredictionResponse(BaseModel):
    henrys_law_constant: float
    max_removal_efficiency: float
    stoichiometric_ratio: float
    min_operating_ph: float
    max_operating_ph: float
    confidence_band: str
    nearest_matches: List[str] = []
    source: str                      # "learned_model" | "knn_fallback"
    trained_on_n_samples: int


@app.post("/predict", response_model=PredictionResponse)
def predict_reaction(req: PredictionRequest) -> PredictionResponse:
    learned = chemistry_model.predict(req.pollutant_name, req.pollutant_molecular_weight, req.liquid_type)
    if learned is not None:
        learned.setdefault("nearest_matches", [])
        return PredictionResponse(**learned)
    return PredictionResponse(**_knn_predict(req.pollutant_name, req.pollutant_molecular_weight, req.liquid_type))


# ── Design outcome prediction (calibration model) ──────────────────────
class DesignPredictionRequest(BaseModel):
    scrubber_type: str
    design_gas_flow_rate: float
    inlet_temperature: float
    moisture_content: float
    liquid_ph: float
    liquid_temperature: float
    design_lg_ratio: float
    tower_diameter: float
    tower_height: float
    packing_height: float
    design_predicted_efficiency: float   # what the deterministic engine says
    design_predicted_pressure_drop: float


class DesignPredictionResponse(BaseModel):
    predicted_removal_efficiency: Optional[float] = None
    predicted_pressure_drop: Optional[float] = None
    confidence_band: str
    source: str
    trained_on_n_samples: int
    message: Optional[str] = None


@app.post("/predict/design", response_model=DesignPredictionResponse)
def predict_design_outcome(req: DesignPredictionRequest) -> DesignPredictionResponse:
    learned = design_model.predict(req.model_dump())
    if learned is not None:
        return DesignPredictionResponse(**learned)
    n = design_model.meta.get("n_samples", 0)
    return DesignPredictionResponse(
        confidence_band="LowSimilarity",
        source="none",
        trained_on_n_samples=n,
        message=f"Calibration model not trained yet ({n} recorded outcomes so far; needs {db.MIN_ROWS_DESIGN}). "
                f"Deterministic engine's own prediction is the best available estimate.",
    )


# ── Retrain & status ────────────────────────────────────────────────────
@app.post("/retrain")
def retrain(target: str = Query("all", pattern="^(all|chemistry|design)$")):
    """Called by the .NET app right after a human reviewer promotes a
    prediction into ChemicalReactions, or after a DesignOutcome gets
    recorded — for an immediate refresh instead of waiting for the next
    background poll. Also safe to call manually / from a cron job."""
    result = {}
    if target in ("all", "chemistry"):
        result["chemistry"] = retrain_chemistry()
    if target in ("all", "design"):
        result["design"] = retrain_design()

    if db.db_available():
        state = load_state()
        counts = db.fetch_row_counts()
        state["chemistry_row_count"] = counts["chemistry"]
        state["design_row_count"] = counts["design"]
        save_state(state)

    return result


@app.get("/model/status")
def model_status():
    return {
        "db_configured": db.db_available(),
        "chemistry": chemistry_model.meta,
        "design": design_model.meta,
        "min_rows_chemistry": db.MIN_ROWS_CHEMISTRY,
        "min_rows_design": db.MIN_ROWS_DESIGN,
        "auto_retrain_poll_minutes": RETRAIN_POLL_MINUTES,
    }


@app.get("/health")
def health():
    return {
        "status": "ok",
        "db_configured": db.db_available(),
        "chemistry_model_trained": chemistry_model.meta.get("trained", False),
        "design_model_trained": design_model.meta.get("trained", False),
        "known_reactions_fallback": len(KNOWN_REACTIONS),
    }
