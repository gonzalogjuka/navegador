"""Fase 4 del plan: escenarios de comportamiento real.

    python bench/escenarios.py

  E-01  El sampler refleja una carga real de CPU
  E-02  El caché detecta procesos que aparecen y desaparecen
  E-03  stop() responde rápido aunque el intervalo sea largo
  E-04  Un log grande se reporta en tiempo razonable
"""

from __future__ import annotations

import json
import subprocess
import sys
import tempfile
import time
from pathlib import Path

import psutil

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from metrics.events import EventLog  # noqa: E402
from metrics.sampler import AppProcessCache, Sampler  # noqa: E402
from metrics.stats import build_report, load  # noqa: E402

BUSY = "while True: pass"


def e01_carga_cpu() -> dict:
    with tempfile.TemporaryDirectory() as d:
        path = Path(d) / "cpu.jsonl"
        log = EventLog(path)
        sampler = Sampler(log, interval=0.25)
        sampler.start()
        time.sleep(2)  # línea base sin carga
        carga = [subprocess.Popen([sys.executable, "-c", BUSY]) for _ in range(max(1, psutil.cpu_count() // 2))]
        time.sleep(4)
        for p in carga:
            p.kill()
        time.sleep(2)
        sampler.stop()
        muestras = [r for r in load(path) if r.get("type") == "sample"]
    mitad = len(muestras) // 2
    primera = [m["cpu_pct"] for m in muestras[:8]]
    durante = [m["cpu_pct"] for m in muestras[mitad:mitad + 8]]
    return {
        "cpu_sin_carga_pct": round(sum(primera) / len(primera), 1),
        "cpu_con_carga_pct": round(max(durante), 1),
        "criterio_ok": max(durante) > sum(primera) / len(primera) + 15,
    }


def e02_aparecer_desaparecer() -> dict:
    cache = AppProcessCache("python", refresh_s=0)
    antes = len(cache.get())
    hijo = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(30)"])
    time.sleep(0.5)
    con_hijo = len(cache.get())
    hijo.kill()
    hijo.wait()
    time.sleep(0.5)
    despues = len(cache.get())
    return {
        "antes": antes,
        "con_hijo": con_hijo,
        "despues_de_kill": despues,
        "criterio_ok": con_hijo == antes + 1 and despues == antes,
    }


def e03_stop_responde_rapido() -> dict:
    with tempfile.TemporaryDirectory() as d:
        log = EventLog(Path(d) / "stop.jsonl")
        sampler = Sampler(log, interval=5.0)  # intervalo largo a propósito
        sampler.start()
        time.sleep(0.5)
        t0 = time.perf_counter()
        sampler.stop()
        latencia = (time.perf_counter() - t0) * 1000
    return {
        "latencia_stop_ms": round(latencia, 1),
        "criterio_ok": latencia < 500,
    }


def e04_reporte_log_grande(n: int = 100_000) -> dict:
    with tempfile.TemporaryDirectory() as d:
        path = Path(d) / "grande.jsonl"
        with path.open("w", encoding="utf-8") as f:
            for i in range(n):
                if i % 10 == 0:
                    f.write(json.dumps({"type": "span", "name": "restore_tab", "duration_ms": 50 + i % 97, "ok": True, "ts": i}) + "\n")
                elif i % 50 == 0:
                    f.write(json.dumps({"type": "event", "name": "focus_lost", "ts": i}) + "\n")
                else:
                    f.write(json.dumps({"type": "sample", "ts": i, "cpu_pct": i % 100, "ram_avail_mb": 4000.0, "app_rss_mb": 900.0}) + "\n")
        mb = path.stat().st_size / 2**20
        t0 = time.perf_counter()
        build_report(path)
        dur = time.perf_counter() - t0
    return {
        "lineas": n,
        "tamano_mb": round(mb, 1),
        "tiempo_reporte_s": round(dur, 2),
        "criterio_ok": dur < 5,
    }


def main() -> int:
    resultados = {
        "E-01": e01_carga_cpu(),
        "E-02": e02_aparecer_desaparecer(),
        "E-03": e03_stop_responde_rapido(),
        "E-04": e04_reporte_log_grande(),
    }
    print(json.dumps(resultados, indent=2, ensure_ascii=False))
    return 0 if all(r["criterio_ok"] for r in resultados.values()) else 1


if __name__ == "__main__":
    raise SystemExit(main())
