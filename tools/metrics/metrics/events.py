"""Registro de eventos y duraciones en formato JSONL (una línea por registro).

Uso típico desde el coordinador del navegador:

    log = EventLog("logs/session.jsonl")
    log.event("focus_lost", app="juego.exe")
    with log.span("hibernate_tab", tab_id=3):
        ...  # guardar snapshot
"""

from __future__ import annotations

import json
import threading
import time
from pathlib import Path
from typing import Any


class EventLog:
    def __init__(self, path: str | Path):
        self.path = Path(path)
        self.path.parent.mkdir(parents=True, exist_ok=True)
        self._lock = threading.Lock()

    def write(self, record: dict[str, Any]) -> None:
        record.setdefault("ts", time.time())
        line = json.dumps(record, ensure_ascii=False)
        with self._lock, self.path.open("a", encoding="utf-8") as f:
            f.write(line + "\n")

    def event(self, name: str, **fields: Any) -> None:
        """Punto en el tiempo: 'focus_lost', 'restore_done', etc."""
        self.write({"type": "event", "name": name, **fields})

    def span(self, name: str, **fields: Any) -> "_Span":
        """Duración de una operación; se registra al salir del bloque with."""
        return _Span(self, name, fields)


class _Span:
    def __init__(self, log: EventLog, name: str, fields: dict[str, Any]):
        self.log = log
        self.name = name
        self.fields = fields
        self._t0 = 0.0

    def __enter__(self) -> "_Span":
        self._t0 = time.perf_counter()
        return self

    def __exit__(self, exc_type, exc, tb) -> bool:
        duration_ms = (time.perf_counter() - self._t0) * 1000
        self.log.write(
            {
                "type": "span",
                "name": self.name,
                "duration_ms": round(duration_ms, 2),
                "ok": exc_type is None,
                **self.fields,
            }
        )
        return False  # no silencia excepciones
