"""
Chemistry prediction service — MVP version of the "GNN role" we scoped.

WHY THIS ISN'T A REAL GNN YET
==============================
A real graph neural network needs a labeled training set (thousands of
pollutant/liquid pairs with known Henry's constants, stoichiometry, etc.)
to learn from. You don't have that yet — your ChemicalReaction table is
curated by hand and has a handful of rows. So this service does the same
JOB (predict missing chemistry for an uncurated pair) using k-nearest-
neighbor similarity over the reactions you already have, instead of a
trained neural net. When you eventually have enough curated rows (or a
public dataset), swap PredictReaction's internals for a real GNN — the
HTTP contract below (request in, prediction + confidence out) doesn't
need to change, so nothing on the .NET side would need to change either.

RUN
===
pip install fastapi uvicorn --break-system-packages
uvicorn chemistry_predictor:app --host 0.0.0.0 --port 8500

Then point WetScrubber's ChemistryPredictionOptions.BaseUrl at
http://localhost:8500
"""

from fastapi import FastAPI
from pydantic import BaseModel
from typing import List, Optional
import math

app = FastAPI(title="WetScrubber Chemistry Predictor (MVP)")


# ── Known reactions — replace with a real read from your ChemicalReaction
# table (export it to this list, or point this at your MySQL DB directly).
# Kept as a plain in-memory list here so this file runs standalone with
# zero DB setup. ──────────────────────────────────────────────────────
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
    confidence_band: str          # "HighSimilarity" | "ModerateSimilarity" | "LowSimilarity"
    nearest_matches: List[str]    # which known reactions this was based on


def _similarity(a: dict, req: PredictionRequest) -> float:
    """
    Plain feature-distance similarity — molecular weight (numeric) plus a
    same-liquid-family bonus. This is the piece a real GNN would replace
    with a learned molecular embedding distance instead of hand-picked
    features.
    """
    weight_diff = abs(a["molecular_weight"] - req.pollutant_molecular_weight)
    weight_score = 1.0 / (1.0 + weight_diff / 20.0)

    liquid_match = 1.0 if a["liquid"].lower() == req.liquid_type.lower() else 0.3

    return weight_score * 0.6 + liquid_match * 0.4


def _confidence_band(top_score: float) -> str:
    if top_score >= 0.75:
        return "HighSimilarity"
    if top_score >= 0.45:
        return "ModerateSimilarity"
    return "LowSimilarity"


@app.post("/predict", response_model=PredictionResponse)
def predict_reaction(req: PredictionRequest) -> PredictionResponse:
    scored = sorted(
        (( _similarity(r, req), r) for r in KNOWN_REACTIONS),
        key=lambda pair: pair[0],
        reverse=True,
    )

    top_k = scored[:3]
    total_weight = sum(score for score, _ in top_k) or 1e-6

    def weighted(field: str) -> float:
        return sum(score * r[field] for score, r in top_k) / total_weight

    return PredictionResponse(
        henrys_law_constant=round(weighted("henrys_constant"), 6),
        max_removal_efficiency=round(weighted("max_efficiency"), 1),
        stoichiometric_ratio=round(weighted("stoich_ratio"), 2),
        min_operating_ph=round(min(r["min_ph"] for _, r in top_k), 1),
        max_operating_ph=round(max(r["max_ph"] for _, r in top_k), 1),
        confidence_band=_confidence_band(top_k[0][0]),
        nearest_matches=[f"{r['pollutant']} + {r['liquid']}" for _, r in top_k],
    )


@app.get("/health")
def health():
    return {"status": "ok", "known_reactions": len(KNOWN_REACTIONS)}