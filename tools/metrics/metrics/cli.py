"""CLI del módulo de métricas.

    python -m metrics record --out logs/base.jsonl --seconds 60 --app chrome
    python -m metrics report logs/base.jsonl
"""

from __future__ import annotations

import argparse
import json
import time

from .events import EventLog
from .sampler import Sampler
from .stats import build_report


def _record(args: argparse.Namespace) -> int:
    log = EventLog(args.out)
    log.event("record_start", seconds=args.seconds, app=args.app, interval=args.interval)
    sampler = Sampler(log, interval=args.interval, app_prefix=args.app)
    sampler.start()
    try:
        time.sleep(args.seconds)
    except KeyboardInterrupt:
        pass
    finally:
        sampler.stop()
        log.event("record_stop")
    print(f"Log escrito en {args.out}")
    return 0


def _report(args: argparse.Namespace) -> int:
    print(json.dumps(build_report(args.path), indent=2, ensure_ascii=False))
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(prog="metrics")
    sub = parser.add_subparsers(dest="cmd", required=True)

    rec = sub.add_parser("record", help="grabar muestras del sistema")
    rec.add_argument("--out", required=True)
    rec.add_argument("--seconds", type=float, default=60)
    rec.add_argument("--interval", type=float, default=0.5)
    rec.add_argument("--app", help="prefijo del nombre de proceso a medir, ej. chrome")
    rec.set_defaults(func=_record)

    rep = sub.add_parser("report", help="resumen de un log")
    rep.add_argument("path")
    rep.set_defaults(func=_report)

    args = parser.parse_args()
    return args.func(args)


if __name__ == "__main__":
    raise SystemExit(main())
