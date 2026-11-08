import json
import threading

import pytest

from metrics.events import EventLog
from metrics.stats import build_report, interval_summary, load, percentile, span_summary
from metrics.sampler import collect


def test_L01_escritura_concurrente_sin_lineas_mezcladas(tmp_path):
    path = tmp_path / "c.jsonl"
    log = EventLog(path)
    hilos, por_hilo = 8, 200

    def worker(i):
        for j in range(por_hilo):
            log.event("tick", hilo=i, n=j)

    ts = [threading.Thread(target=worker, args=(i,)) for i in range(hilos)]
    for t in ts:
        t.start()
    for t in ts:
        t.join()

    lineas = path.read_text(encoding="utf-8").splitlines()
    assert len(lineas) == hilos * por_hilo
    for linea in lineas:
        json.loads(linea)  # falla si hay mezcla


def test_L02_span_con_excepcion_registra_ok_false_y_propaga(tmp_path):
    path = tmp_path / "s.jsonl"
    log = EventLog(path)
    with pytest.raises(ValueError):
        with log.span("op"):
            raise ValueError("boom")
    registro = load(path)[0]
    assert registro["ok"] is False
    assert registro["name"] == "op"


def test_L03_span_sin_duracion_no_rompe_resumen():
    records = [
        {"type": "span", "name": "op", "duration_ms": 10.0, "ok": True},
        {"type": "span", "name": "op", "ok": True},  # corrupto
    ]
    resumen = span_summary(records)
    assert resumen["op"]["count"] == 1


def test_L04_percentiles_con_pocos_valores():
    assert percentile([5.0], 50) == 5.0
    assert percentile([5.0], 95) == 5.0
    assert percentile([1.0, 2.0], 95) in (1.0, 2.0)


def test_L05_fin_sin_inicio_se_ignora():
    records = [{"type": "event", "name": "hibernate_done", "ts": 1.0}]
    assert interval_summary(records, "focus_lost", "hibernate_done") is None


def test_L06_dos_inicios_seguidos_usa_el_ultimo():
    records = [
        {"type": "event", "name": "focus_lost", "ts": 1.0},
        {"type": "event", "name": "focus_lost", "ts": 2.0},
        {"type": "event", "name": "hibernate_done", "ts": 2.5},
    ]
    res = interval_summary(records, "focus_lost", "hibernate_done")
    assert res["count"] == 1
    assert res["max_ms"] == 500.0


def test_L07_primera_muestra_sin_tasas():
    rec = collect(None, None)
    assert "disk_read_mbps" not in rec
    assert "net_recv_mbps" not in rec
    assert "cpu_pct" in rec


def test_L08_sin_prefijo_no_incluye_campos_app():
    rec = collect(None, None)
    assert "app_procs" not in rec
    assert "app_rss_mb" not in rec


def test_L09_log_vacio_o_corrupto(tmp_path):
    path = tmp_path / "vacio.jsonl"
    path.write_text("{roto\n\n", encoding="utf-8")
    reporte = build_report(path)
    assert reporte["records"] == 0
    assert reporte["samples"] is None
    assert reporte["spans"] == {}
