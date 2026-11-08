"""Corre las pruebas pesadas cuando la PC está libre (nadie jugando ni trabajando con algo pesado).

    tools\\metrics\\.venv\\Scripts\\python.exe tools\\compare\\cuando_libre.py [--exe dist\\Navegador.exe] [--minutes 15]

Espera a que la PC esté libre (compare.wait_idle) y corre, en orden:
  1. tools/smoke/privada-rastros.ps1 (rastros de la ventana privada en disco),
  2. tools/smoke/formulario.txt (el tope de RAM no descarta una pestaña con texto escrito),
  3. tools/smoke/cpu-suspendida.txt (una pestaña suspendida no gasta CPU),
  4. compare.py --esperar-pc-libre (memoria contra Chrome y Edge, ~65 min).
Todo queda en resultados/<fecha>-libre.md. Nada aparece en pantalla: son las mismas pruebas ocultas de siempre.
Para dejarlo esperando sin ventana: Start-Process -WindowStyle Hidden con este script (ver README).
"""

from __future__ import annotations

import argparse
import json
import shutil
import subprocess
import sys
import tempfile
from datetime import datetime
from pathlib import Path

from compare import DEFAULT_EXE, REPO, RESULTS, wait_idle


def events(data_dir: Path) -> list[dict]:
    out = []
    for log in sorted((data_dir / "logs").glob("*.jsonl")):
        out += [json.loads(line) for line in log.read_text(encoding="utf-8").splitlines() if line.strip()]
    return out


def run_steps(exe: Path, steps: str, settings: dict) -> list[dict]:
    data = Path(tempfile.mkdtemp(prefix="navlibre-"))
    try:
        (data / "settings.json").write_text(json.dumps(settings), encoding="utf-8")
        subprocess.run([str(exe), "--data-dir", str(data), "--automation", str(REPO / "tools" / "smoke" / steps)], timeout=900)
        return events(data)
    finally:
        shutil.rmtree(data, ignore_errors=True)


def formulario(exe: Path) -> str:
    ev = run_steps(exe, "formulario.txt", {"RamBudgetMb": 128, "BudgetKeepRecentMinutes": 0})
    skipped = {e["tab_id"] for e in ev if e.get("name") == "discard_skipped"}
    discarded = {e["tab_id"] for e in ev if e.get("name") == "budget_discard"}
    final = [e["result"] for e in ev if e.get("name") == "automation_js"][-1:]
    ok = 1 in skipped and 1 not in discarded and bool(discarded) and final == ['"borrador importante"']
    return (f"{'OK' if ok else 'FALLA'} — se salvó la pestaña {sorted(set(skipped))}, se descartaron {sorted(set(discarded))}, "
            f"texto al volver: {final[0] if final else '—'}")


def cpu(exe: Path) -> str:
    ev = run_steps(exe, "cpu-suspendida.txt", {"SuspendAfterSeconds": 15, "FocusLostSeconds": 600})
    t_susp = next((e["ts"] for e in ev if e.get("name") == "suspend_tab"), None)
    t_resume = next((e["ts"] for e in ev if e.get("name") == "resume_tab"), None)
    samples = [e for e in ev if e.get("type") == "sample" and e.get("renderer_cpu_pct") is not None]
    if t_susp is None or t_resume is None:
        return "FALLA — no hubo suspend_tab/resume_tab"
    live = [e["renderer_cpu_pct"] for e in samples if e["ts"] < t_susp and e["renderer_cpu_pct"] > 0]
    asleep = [e["renderer_cpu_pct"] for e in samples if t_susp + 5 < e["ts"] < t_resume]
    back = [e["renderer_cpu_pct"] for e in samples if e["ts"] > t_resume + 5]
    avg = lambda xs: round(sum(xs) / len(xs), 2) if xs else None
    ok = bool(live and asleep and back) and avg(asleep) < 0.5 and avg(live) > 5 and avg(back) > 5
    return f"{'OK' if ok else 'FALLA'} — CPU de renderers: viva {avg(live)} %, suspendida {avg(asleep)} %, al volver {avg(back)} %"


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--exe", type=Path, default=DEFAULT_EXE)
    ap.add_argument("--minutes", type=float, default=15)
    a = ap.parse_args()
    exe = a.exe.resolve()
    stamp = datetime.now().strftime("%Y%m%d-%H%M%S")
    RESULTS.mkdir(exist_ok=True)
    report = RESULTS / f"{stamp}-libre.md"
    lines = [f"# Pruebas con la PC libre ({stamp})", "", f"Ejecutable: `{exe}`", ""]

    def write():
        report.write_text("\n".join(lines) + "\n", encoding="utf-8")

    print("Esperando a que la PC esté libre…", flush=True)
    wait_idle()
    lines.append(f"PC libre desde {datetime.now():%H:%M}.")
    write()

    rastros = subprocess.run(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
                              str(REPO / "tools" / "smoke" / "privada-rastros.ps1"), "-Exe", str(exe)],
                             capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=1800)
    lines += ["## Rastros de la ventana privada", "```", rastros.stdout.strip(), "```", ""]
    write()
    lines += ["## Formulario y tope de RAM", formulario(exe), ""]
    write()
    lines += ["## CPU de una pestaña suspendida", cpu(exe), ""]
    write()

    cmp = subprocess.run([sys.executable, str(Path(__file__).with_name("compare.py")), "--esperar-pc-libre",
                          "--minutes", str(a.minutes), "--exe", str(exe)],
                         capture_output=True, text=True, encoding="utf-8", errors="replace")
    tables = sorted(RESULTS.glob("*-comparacion.md"))
    lines += ["## Comparación contra Chrome y Edge", tables[-1].read_text(encoding="utf-8") if tables and cmp.returncode == 0
              else f"Falló (código {cmp.returncode}):\n```\n{cmp.stdout[-3000:]}\n{cmp.stderr[-3000:]}\n```"]
    write()
    print(f"-> {report}", flush=True)


if __name__ == "__main__":
    main()
