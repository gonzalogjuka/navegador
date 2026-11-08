import os
import signal
import subprocess
import sys
import time
from pathlib import Path

import pytest

from metrics.stats import build_report, load

RAIZ = Path(__file__).resolve().parent.parent


def _run_record(out, seconds, interval=0.5, extra=None):
    cmd = [sys.executable, "-m", "metrics", "record", "--out", str(out),
           "--seconds", str(seconds), "--interval", str(interval)] + (extra or [])
    return subprocess.Popen(cmd, cwd=RAIZ)


def test_I01_grabacion_cuenta_muestras_esperadas(tmp_path):
    out = tmp_path / "i01.jsonl"
    p = _run_record(out, seconds=10, interval=0.5)
    assert p.wait(timeout=60) == 0
    muestras = [r for r in load(out) if r.get("type") == "sample"]
    assert 18 <= len(muestras) <= 22, f"muestras={len(muestras)}"
    eventos = [r["name"] for r in load(out) if r.get("type") == "event"]
    assert eventos[0] == "record_start" and eventos[-1] == "record_stop"


@pytest.mark.skipif(os.name == "nt", reason="SIGINT no se envía igual en Windows; probar Ctrl+C a mano")
def test_I02_ctrl_c_cierra_limpio(tmp_path):
    out = tmp_path / "i02.jsonl"
    p = _run_record(out, seconds=60, interval=0.5)
    time.sleep(3)
    p.send_signal(signal.SIGINT)
    assert p.wait(timeout=30) == 0
    registros = load(out)
    assert registros[-1]["name"] == "record_stop"
    assert build_report(out)["samples"]["samples"] >= 3


def test_I03_reporte_coherente(tmp_path):
    out = tmp_path / "i03.jsonl"
    p = _run_record(out, seconds=4, interval=0.5, extra=["--app", "python"])
    assert p.wait(timeout=60) == 0
    rep = build_report(out)
    s = rep["samples"]
    assert s["cpu_avg_pct"] >= 0
    assert 0 <= s["ram_avail_min_mb"]
    assert s["app_rss_max_mb"] >= s["app_rss_avg_mb"] > 0
