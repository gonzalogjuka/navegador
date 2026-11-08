"""Fase 3 del plan: costo del propio medidor.

    python bench/overhead.py --seconds 60

Mide:
  P-01   tiempo promedio y máximo de collect() sin filtro de app
  P-01b  lo mismo con filtro de app (caché de procesos)
  P-02   crecimiento de RSS del proceso medidor durante la corrida
  P-03   muestras reales vs. esperadas a 0.1, 0.5 y 1 s
"""

from __future__ import annotations

import argparse
import json
import os
import statistics
import sys
import tempfile
import time
from pathlib import Path

import psutil

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from metrics.events import EventLog  # noqa: E402
from metrics.sampler import AppProcessCache, Sampler, collect  # noqa: E402
from metrics.stats import load  # noqa: E402


def p01_costo_collect(n: int = 200, prefix: str | None = None) -> dict:
    cache = AppProcessCache(prefix, refresh_s=2.0) if prefix else None
    tiempos = []
    prev = None
    for _ in range(n):
        t0 = time.perf_counter()
        rec = collect(prev, cache)
        tiempos.append((time.perf_counter() - t0) * 1000)
        prev = rec.pop("_state")
    return {
        "promedio_ms": round(statistics.mean(tiempos), 3),
        "max_ms": round(max(tiempos), 3),
        "criterio_ok": statistics.mean(tiempos) < 20,
    }


def p02_crecimiento_memoria(seconds: int) -> dict:
    proc = psutil.Process(os.getpid())
    with tempfile.TemporaryDirectory() as d:
        log = EventLog(Path(d) / "mem.jsonl")
        sampler = Sampler(log, interval=0.1)
        base = proc.memory_info().rss / 2**20
        sampler.start()
        mediciones = []
        for _ in range(seconds):
            time.sleep(1)
            mediciones.append(proc.memory_info().rss / 2**20)
        sampler.stop()
    crecimiento = mediciones[-1] - mediciones[0]
    return {
        "rss_inicio_mb": round(base, 2),
        "rss_primer_seg_mb": round(mediciones[0], 2),
        "rss_final_mb": round(mediciones[-1], 2),
        "crecimiento_mb": round(crecimiento, 2),
        "criterio_ok": crecimiento < 5,  # sin tendencia sostenida de fuga
    }


def p03_muestras_reales(seconds: int) -> dict:
    resultados = {}
    for intervalo in (0.1, 0.5, 1.0):
        with tempfile.TemporaryDirectory() as d:
            path = Path(d) / "m.jsonl"
            log = EventLog(path)
            sampler = Sampler(log, interval=intervalo)
            sampler.start()
            time.sleep(seconds)
            sampler.stop()
            reales = sum(1 for r in load(path) if r.get("type") == "sample")
        esperadas = seconds / intervalo
        desvio = abs(reales - esperadas) / esperadas * 100
        resultados[str(intervalo)] = {
            "esperadas": round(esperadas),
            "reales": reales,
            "desvio_pct": round(desvio, 1),
            "criterio_ok": desvio < 10,
        }
    return resultados


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--seconds", type=int, default=60)
    ap.add_argument("--app", default="python", help="prefijo de proceso para P-01b")
    args = ap.parse_args()

    resultado = {
        "P-01": p01_costo_collect(),
        "P-01b_con_app": p01_costo_collect(prefix=args.app),
        "P-02": p02_crecimiento_memoria(args.seconds),
        "P-03": p03_muestras_reales(min(args.seconds, 10)),
    }
    print(json.dumps(resultado, indent=2, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
