"""Estadísticas sobre un log JSONL generado por EventLog y Sampler."""

from __future__ import annotations

import json
from collections import defaultdict
from pathlib import Path
from statistics import mean
from typing import Any, Iterable, Optional


def load(path: str | Path) -> list[dict[str, Any]]:
    """Lee el log. Las líneas corruptas (por ejemplo, un corte de luz) se saltean."""
    records = []
    with Path(path).open(encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            try:
                records.append(json.loads(line))
            except json.JSONDecodeError:
                continue
    return records


def percentile(values: list[float], p: float) -> Optional[float]:
    """Percentil por rango más cercano. p en 0..100."""
    if not values:
        return None
    ordered = sorted(values)
    k = max(0, min(len(ordered) - 1, round(p / 100 * (len(ordered) - 1))))
    return ordered[k]


def span_summary(records: Iterable[dict[str, Any]]) -> dict[str, dict[str, Any]]:
    """Por cada nombre de span: cantidad, éxito, p50, p95 y máximo en ms."""
    grouped: dict[str, list[dict[str, Any]]] = defaultdict(list)
    for r in records:
        if r.get("type") == "span" and "duration_ms" in r and "name" in r:
            grouped[r["name"]].append(r)

    out = {}
    for name, items in grouped.items():
        durations = [i["duration_ms"] for i in items]
        out[name] = {
            "count": len(items),
            "ok_pct": round(100 * sum(1 for i in items if i.get("ok")) / len(items), 1),
            "p50_ms": percentile(durations, 50),
            "p95_ms": percentile(durations, 95),
            "max_ms": max(durations),
        }
    return out


def interval_summary(records: Iterable[dict[str, Any]], start: str, end: str) -> Optional[dict[str, Any]]:
    """Tiempo entre un evento `start` y el siguiente `end`, en ms.

    Ejemplo: start='focus_lost', end='hibernate_done' mide cuánto tarda
    en quedar hibernado después de perder el foco.
    """
    intervals = []
    pending_ts: Optional[float] = None
    for r in records:
        if r.get("type") != "event":
            continue
        if r["name"] == start:
            pending_ts = r["ts"]
        elif r["name"] == end and pending_ts is not None:
            intervals.append((r["ts"] - pending_ts) * 1000)
            pending_ts = None
    if not intervals:
        return None
    return {
        "count": len(intervals),
        "p50_ms": percentile(intervals, 50),
        "p95_ms": percentile(intervals, 95),
        "max_ms": max(intervals),
    }


def sample_summary(records: Iterable[dict[str, Any]]) -> Optional[dict[str, Any]]:
    """Resumen de muestras del sistema y de la app observada."""
    samples = [r for r in records if r.get("type") == "sample"]
    if not samples:
        return None

    def col(key: str) -> list[float]:
        return [s[key] for s in samples if s.get(key) is not None]

    summary: dict[str, Any] = {"samples": len(samples)}
    cpu = col("cpu_pct")
    if cpu:
        summary["cpu_avg_pct"] = round(mean(cpu), 1)
        summary["cpu_p95_pct"] = percentile(cpu, 95)
    avail = col("ram_avail_mb")
    if avail:
        summary["ram_avail_min_mb"] = min(avail)
    rss = col("app_rss_mb")
    if rss:
        summary["app_rss_max_mb"] = max(rss)
        summary["app_rss_avg_mb"] = round(mean(rss), 1)
    priv = col("app_private_mb")
    if priv:
        summary["app_private_max_mb"] = max(priv)
        summary["app_private_avg_mb"] = round(mean(priv), 1)
    return summary


def build_report(path: str | Path) -> dict[str, Any]:
    records = load(path)
    events = [r for r in records if r.get("type") == "event"]
    event_counts: dict[str, int] = defaultdict(int)
    for e in events:
        event_counts[e["name"]] += 1

    return {
        "records": len(records),
        "events": dict(event_counts),
        "spans": span_summary(records),
        "samples": sample_summary(records),
        # Intervalos clave del flujo de hibernación; se agregan a medida que existan los eventos
        "intervals": {
            "focus_lost->hibernate_done": interval_summary(records, "focus_lost", "hibernate_done"),
            "focus_gained->tab_active_ready": interval_summary(records, "focus_gained", "tab_active_ready"),
        },
    }
