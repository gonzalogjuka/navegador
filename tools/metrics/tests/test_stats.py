import json

from metrics.events import EventLog
from metrics.stats import (
    build_report,
    interval_summary,
    percentile,
    span_summary,
)


def test_percentile_basico():
    vals = [10, 20, 30, 40, 50]
    assert percentile(vals, 50) == 30
    assert percentile(vals, 95) == 50
    assert percentile([], 50) is None


def test_span_y_eventos_se_escriben_y_resumen(tmp_path):
    path = tmp_path / "s.jsonl"
    log = EventLog(path)
    with log.span("hibernate_tab", tab_id=1):
        pass
    try:
        with log.span("hibernate_tab", tab_id=2):
            raise RuntimeError("falla simulada")
    except RuntimeError:
        pass

    from metrics.stats import load
    records = load(path)
    summary = span_summary(records)
    assert summary["hibernate_tab"]["count"] == 2
    assert summary["hibernate_tab"]["ok_pct"] == 50.0


def test_intervalo_entre_eventos():
    records = [
        {"type": "event", "name": "focus_lost", "ts": 100.0},
        {"type": "event", "name": "hibernate_done", "ts": 100.25},
        {"type": "event", "name": "focus_lost", "ts": 200.0},
        {"type": "event", "name": "hibernate_done", "ts": 200.75},
    ]
    res = interval_summary(records, "focus_lost", "hibernate_done")
    assert res["count"] == 2
    assert res["max_ms"] == 750.0


def test_reporte_completo_desde_archivo(tmp_path):
    path = tmp_path / "r.jsonl"
    lines = [
        {"type": "event", "name": "record_start", "ts": 1.0},
        {"type": "sample", "ts": 1.5, "cpu_pct": 20.0, "ram_avail_mb": 4000.0, "app_rss_mb": 900.0},
        {"type": "sample", "ts": 2.0, "cpu_pct": 60.0, "ram_avail_mb": 3500.0, "app_rss_mb": 1100.0},
    ]
    path.write_text("\n".join(json.dumps(x) for x in lines) + "\nlinea corrupta{\n", encoding="utf-8")
    report = build_report(path)
    assert report["samples"]["samples"] == 2
    assert report["samples"]["ram_avail_min_mb"] == 3500.0
    assert report["samples"]["app_rss_max_mb"] == 1100.0
    assert report["events"]["record_start"] == 1
