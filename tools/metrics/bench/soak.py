"""Prueba de larga duración: busca fugas del medidor y estima el tamaño del log por hora.

    python bench/soak.py --minutes 5 --interval 0.1
"""
from __future__ import annotations

import argparse
import json
import os
import sys
import tempfile
import time
from pathlib import Path

import psutil

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from metrics.events import EventLog  # noqa: E402
from metrics.sampler import Sampler  # noqa: E402


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--minutes", type=float, default=5)
    ap.add_argument("--interval", type=float, default=0.1)
    ap.add_argument("--app", default="python")
    args = ap.parse_args()

    proc = psutil.Process(os.getpid())
    with tempfile.TemporaryDirectory() as d:
        path = Path(d) / "soak.jsonl"
        log = EventLog(path)
        sampler = Sampler(log, interval=args.interval, app_prefix=args.app)
        sampler.start()
        seconds = int(args.minutes * 60)
        rss = []
        for _ in range(seconds // 10):
            time.sleep(10)
            rss.append(proc.memory_info().rss / 2**20)
        sampler.stop()
        tam_mb = path.stat().st_size / 2**20
        lineas = sum(1 for _ in path.open(encoding="utf-8"))

    por_hora_mb = tam_mb * 3600 / seconds
    tendencia = (rss[-1] - rss[0]) / max(1, len(rss) - 1)  # MB por intervalo de 10 s
    resultado = {
        "minutos": args.minutes,
        "intervalo_s": args.interval,
        "muestras_escritas": lineas,
        "rss_inicio_mb": round(rss[0], 2),
        "rss_final_mb": round(rss[-1], 2),
        "rss_max_mb": round(max(rss), 2),
        "crecimiento_mb_por_10s": round(tendencia, 3),
        "log_mb": round(tam_mb, 2),
        "log_estimado_mb_por_hora": round(por_hora_mb, 1),
        "criterio_fuga_ok": (rss[-1] - rss[0]) < 5,
    }
    print(json.dumps(resultado, indent=2, ensure_ascii=False))
    return 0 if resultado["criterio_fuga_ok"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
