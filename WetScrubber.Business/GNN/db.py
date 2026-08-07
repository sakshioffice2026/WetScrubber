"""
db.py — read-only access to the WetScrubber MySQL database for the
self-learning service.

Connection string comes from the WETSCRUBBER_DB_URL env var, SQLAlchemy
style, e.g.:

    mysql+pymysql://scrubber_user:secret@localhost:3306/wetscrubber

This is deliberately a *separate* credential from the .NET app's
DefaultConnection — give it a read-only MySQL user. The service never
writes to these tables; all writes still go through the .NET app so the
existing human-review gate (promote a prediction into ChemicalReaction,
record a DesignOutcome) stays the single source of truth for what counts
as "curated" data.
"""

from __future__ import annotations

import os
from functools import lru_cache
from typing import Optional

import pandas as pd
from sqlalchemy import create_engine, text
from sqlalchemy.engine import Engine

DB_URL_ENV = "WETSCRUBBER_DB_URL"

# Minimum curated rows before a model is trusted over the heuristic
# fallback. Below this, predictions still come from the original
# KNN-over-known-reactions / rule-of-thumb logic — same "glide path" the
# original chemistrypredictor.py docstring describes.
MIN_ROWS_CHEMISTRY = int(os.environ.get("MIN_TRAINING_ROWS_CHEMISTRY", "8"))
MIN_ROWS_DESIGN = int(os.environ.get("MIN_TRAINING_ROWS_DESIGN", "8"))


@lru_cache(maxsize=1)
def get_engine() -> Optional[Engine]:
    url = os.environ.get(DB_URL_ENV)
    if not url:
        return None
    return create_engine(url, pool_pre_ping=True, pool_recycle=1800)


def db_available() -> bool:
    return get_engine() is not None


# ── Chemistry training data ────────────────────────────────────────────
CHEMISTRY_QUERY = """
SELECT
    r.Id                     AS reaction_id,
    r.PollutantId             AS pollutant_id,
    r.ScrubbingLiquidId       AS liquid_id,
    r.StoichiometricRatio     AS stoich_ratio,
    r.MaxRemovalEfficiency    AS max_efficiency,
    r.MinOperatingPH          AS min_ph,
    r.MaxOperatingPH          AS max_ph,
    p.Code                    AS pollutant_code,
    p.DefaultMolecularWeight  AS pollutant_molecular_weight,
    p.DefaultHenrysLawConstant AS pollutant_default_henrys,
    l.Code                    AS liquid_code,
    l.DefaultDensity          AS liquid_density,
    l.DefaultPH               AS liquid_default_ph
FROM ChemicalReactions r
JOIN Pollutants p       ON p.Id = r.PollutantId
JOIN ScrubbingLiquids l ON l.Id = r.ScrubbingLiquidId
WHERE r.IsActive = 1
"""


def fetch_chemistry_training_data() -> pd.DataFrame:
    engine = get_engine()
    if engine is None:
        return pd.DataFrame()
    with engine.connect() as conn:
        return pd.read_sql(text(CHEMISTRY_QUERY), conn)


# ── Design outcome training data ───────────────────────────────────────
# Joins a recorded field/measured outcome back to the design inputs that
# produced it. This is the Phase 6 table the DesignOutcome.cs comment
# flagged as "exists purely so data starts accumulating" — this is the
# model that gets trained on it once there's enough volume.
DESIGN_QUERY = """
SELECT
    o.Id                          AS outcome_id,
    o.DesignId                    AS design_id,
    o.PredictedRemovalEfficiency  AS predicted_efficiency,
    o.MeasuredRemovalEfficiency   AS measured_efficiency,
    o.MeasuredPressureDrop        AS measured_pressure_drop,
    o.MeasuredGasFlowRate         AS measured_gas_flow_rate,
    o.MeasuredLiquidToGasRatio    AS measured_lg_ratio,
    d.ScrubberType                AS scrubber_type,
    g.ActualFlowRate              AS design_gas_flow_rate,
    g.InletTemperature            AS inlet_temperature,
    g.MoistureContent             AS moisture_content,
    s.pH                          AS liquid_ph,
    s.Temperature                 AS liquid_temperature,
    s.LiquidToGasRatio            AS design_lg_ratio,
    geo.TowerDiameter             AS tower_diameter,
    geo.TowerHeight               AS tower_height,
    geo.PackingHeight             AS packing_height,
    geo.RemovalEfficiency         AS design_predicted_efficiency,
    geo.PressureDrop              AS design_predicted_pressure_drop
FROM DesignOutcomes o
JOIN ScrubberDesigns d        ON d.DesignId = o.DesignId
LEFT JOIN GasStreams g            ON g.DesignId = d.DesignId
LEFT JOIN ScrubbingLiquidSpecs s  ON s.DesignId = d.DesignId
LEFT JOIN ScrubberGeometries geo  ON geo.DesignId = d.DesignId
"""


def fetch_design_training_data() -> pd.DataFrame:
    engine = get_engine()
    if engine is None:
        return pd.DataFrame()
    with engine.connect() as conn:
        try:
            return pd.read_sql(text(DESIGN_QUERY), conn)
        except Exception:
            # DesignOutcomes table may not exist yet on older DBs that
            # haven't run the Phase 6 migration — treat as "no data yet"
            # rather than crashing the service.
            return pd.DataFrame()


def fetch_row_counts() -> dict:
    """Cheap change-detector for the auto-retrain watcher — no need to
    pull full tables just to notice something changed."""
    engine = get_engine()
    if engine is None:
        return {"chemistry": 0, "design": 0}
    counts = {"chemistry": 0, "design": 0}
    with engine.connect() as conn:
        try:
            counts["chemistry"] = conn.execute(
                text("SELECT COUNT(*) FROM ChemicalReactions WHERE IsActive = 1")
            ).scalar_one()
        except Exception:
            pass
        try:
            counts["design"] = conn.execute(
                text("SELECT COUNT(*) FROM DesignOutcomes")
            ).scalar_one()
        except Exception:
            pass
    return counts
