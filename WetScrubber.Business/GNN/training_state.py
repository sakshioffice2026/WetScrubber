"""
training_state.py — remembers how many curated rows existed at last
training, so the background watcher can tell "data changed, retrain" apart
from "nothing changed, do nothing" without re-training on every poll.
"""

from __future__ import annotations

import json
import time
from pathlib import Path

STATE_PATH = Path(__file__).parent / "model_store" / "training_state.json"

DEFAULT_STATE = {
    "chemistry_row_count": -1,
    "design_row_count": -1,
    "last_checked": 0.0,
}


def load_state() -> dict:
    if STATE_PATH.exists():
        try:
            return {**DEFAULT_STATE, **json.loads(STATE_PATH.read_text())}
        except Exception:
            pass
    return dict(DEFAULT_STATE)


def save_state(state: dict) -> None:
    state = {**state, "last_checked": time.time()}
    STATE_PATH.write_text(json.dumps(state, indent=2))
