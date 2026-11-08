"""Comparación formal de memoria: Navegador vs Chrome vs Edge con las mismas URLs y la misma duración (fase 4 del PLAN).

Nunca muestra ventanas: Navegador corre con --automation (ventanas fuera de pantalla) y Chrome/Edge con --headless=new.
Cada escenario usa su propia carpeta temporal y corre solo (nunca dos a la vez).

Uso:
    python compare.py [--minutes 15] [--scenarios navegador,navegador-sin-hibernar,chrome,edge] [--exe ruta] [--urls N]
"""

from __future__ import annotations

import argparse
import json
import shutil
import socket
import statistics
import subprocess
import sys
import tempfile
import time
import urllib.parse
import urllib.request
from datetime import datetime
from pathlib import Path

import psutil

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[1]
RESULTS = HERE / "resultados"
DEFAULT_EXE = REPO / "dist" / "Navegador.exe"
CHROME = Path(r"D:\Tools\chrome-for-testing\chrome-win64\chrome.exe")
EDGE = Path(r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe")
ALL_SCENARIOS = ["navegador", "navegador-sin-hibernar", "chrome", "edge"]
SAMPLE_SECONDS = 10
OPEN_GAP_MS = 3000  # pausa entre pestañas, igual en todos los escenarios
LOG_EVENTS = ["discard_tab", "budget_discard", "restore_tab", "suspend_tab", "automation_error"]
NO_HIBERNATION = {"SuspendAfterSeconds": 999999, "DiscardAfterMinutes": 999999,
                  "FocusLostSeconds": 999999, "RamBudgetMb": 999999}
MB = 1024 * 1024


def load_urls(n: int) -> list[str]:
    urls = [l.strip() for l in (HERE / "urls.txt").read_text(encoding="utf-8").splitlines()
            if l.strip() and not l.strip().startswith("#")]
    if n > len(urls):
        sys.exit(f"urls.txt tiene {len(urls)} URLs, se pidieron {n}")
    return urls[:n]


# ---------------------------------------------------------------- procesos

def tree(root_pid: int | None, data_dir: str) -> list[psutil.Process]:
    """Proceso raíz + todo proceso cuya línea de comandos menciona la carpeta de datos + sus descendientes."""
    needle = data_dir.lower()
    found: dict[int, psutil.Process] = {}
    for p in psutil.process_iter(["cmdline"]):
        try:
            cmd = " ".join(p.info["cmdline"] or []).lower()
        except (psutil.Error, TypeError):
            continue
        if needle in cmd or p.pid == root_pid:
            found[p.pid] = p
    for p in list(found.values()):
        try:
            for c in p.children(recursive=True):
                found.setdefault(c.pid, c)
        except psutil.Error:
            pass
    return list(found.values())


def sample(root_pid: int | None, data_dir: str) -> dict:
    total, count = 0, 0
    by_type: dict[str, float] = {}  # MB por tipo: host (Navegador.exe), browser, renderer, gpu-process, utility...
    for p in tree(root_pid, data_dir):
        try:
            priv = p.memory_info().private
            cmd = " ".join(p.cmdline())
            name = p.name().lower()
        except psutil.Error:  # el proceso terminó entre la enumeración y la lectura
            continue
        total += priv
        count += 1
        if "--type=" in cmd:
            kind = cmd.split("--type=", 1)[1].split()[0]
        else:
            kind = "host" if name == "navegador.exe" else "browser"
        by_type[kind] = by_type.get(kind, 0) + priv / MB
    return {"private_mb": round(total / MB, 1), "processes": count,
            "by_type_mb": {k: round(v, 1) for k, v in sorted(by_type.items())}}


def kill_tree(root_pid: int | None, data_dir: str) -> int:
    procs = tree(root_pid, data_dir)
    for p in procs:
        try:
            p.kill()
        except psutil.Error:
            pass
    psutil.wait_procs(procs, timeout=10)
    return len(procs)


def remove_dir(path: str) -> None:
    for _ in range(10):
        shutil.rmtree(path, ignore_errors=True)
        if not Path(path).exists():
            return
        time.sleep(1)


# ---------------------------------------------------------------- escenarios

def free_port() -> int:
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


def cdp(port: int, path: str, method: str = "GET"):
    req = urllib.request.Request(f"http://127.0.0.1:{port}{path}", method=method)
    with urllib.request.urlopen(req, timeout=5) as r:
        return json.loads(r.read().decode("utf-8"))


def launch_navegador(exe: Path, data_dir: str, urls: list[str], duration_ms: int, hibernate: bool):
    if not hibernate:
        (Path(data_dir) / "settings.json").write_text(json.dumps(NO_HIBERNATION, indent=2), encoding="utf-8")
    steps = [f"go {urls[0]}", f"wait {OPEN_GAP_MS}"]
    for u in urls[1:]:
        steps += [f"newtab {u}", f"wait {OPEN_GAP_MS}"]
    steps += [f"wait {duration_ms}", "close"]
    steps_file = Path(data_dir) / "steps.txt"
    steps_file.write_text("\n".join(steps) + "\n", encoding="utf-8")
    proc = subprocess.Popen([str(exe), "--data-dir", data_dir, "--automation", str(steps_file)])
    return proc, None


def launch_chromium(exe: Path, data_dir: str, urls: list[str]):
    """Headless con la primera URL; el resto se abre por CDP con la misma pausa que Navegador (open_next_tab)."""
    port = free_port()
    # Sin sync ni extensiones: Edge con perfil nuevo inicia sesión solo con la cuenta de Windows y baja las
    # extensiones del usuario (AdBlock, etc.), que inflarían su número. Navegador tampoco tiene extensiones.
    args = [str(exe), "--headless=new", f"--user-data-dir={data_dir}", "--no-first-run",
            "--no-default-browser-check", "--mute-audio", "--disable-sync", "--disable-extensions",
            f"--remote-debugging-port={port}", urls[0]]
    proc = subprocess.Popen(args, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    return proc, port


def open_next_tab(port: int, url: str) -> None:
    try:
        cdp(port, "/json/new?" + urllib.parse.quote(url, safe=":/?=&%"), method="PUT")
    except OSError as e:
        print(f"  ! no se pudo abrir {url}: {e}", flush=True)


def wait_cdp(port: int, timeout: float = 30) -> None:
    end = time.time() + timeout
    while time.time() < end:
        try:
            cdp(port, "/json/version")
            return
        except OSError:
            time.sleep(0.5)
    raise RuntimeError("el navegador headless no abrió el puerto de depuración")


def read_log_counts(data_dir: str) -> dict:
    counts = {k: 0 for k in LOG_EVENTS}
    for f in (Path(data_dir) / "logs").glob("*.jsonl"):
        for line in f.read_text(encoding="utf-8", errors="replace").splitlines():
            try:
                name = json.loads(line).get("name")
            except json.JSONDecodeError:
                continue
            if name in counts:
                counts[name] += 1
    return counts


def summarize(samples: list[dict]) -> dict:
    mem = sorted(s["private_mb"] for s in samples if s["processes"] > 0)
    if not mem:
        return {}
    pct = lambda q: mem[min(len(mem) - 1, round(q * (len(mem) - 1)))]
    alive = [s for s in samples if s["processes"] > 0]
    return {
        "avg_mb": round(statistics.mean(mem), 1),
        "p50_mb": pct(0.50),
        "p95_mb": pct(0.95),
        "max_mb": mem[-1],
        "avg_processes": round(statistics.mean(s["processes"] for s in alive), 1),
        "end_mb": alive[-1]["private_mb"],
        "end_by_type_mb": alive[-1]["by_type_mb"],
        "samples": len(alive),
        # Carga de toda la PC durante la medición: si es alta, otra app estaba compitiendo y el número es dudoso.
        "system_cpu_avg_pct": round(statistics.mean(s["system_cpu_pct"] for s in alive if "system_cpu_pct" in s), 1)
        if any("system_cpu_pct" in s for s in alive) else None,
    }


def wait_idle(cpu_max: float = 25, free_gb_min: float = 6, checks: int = 3, every_s: int = 60) -> None:
    """Espera a que la PC esté libre (nadie jugando ni compilando): CPU del sistema por debajo de cpu_max % y al
    menos free_gb_min GB de RAM disponible en `checks` controles seguidos, uno cada every_s segundos. Así la prueba no
    le saca recursos al usuario y los números no salen contaminados por otra carga."""
    ok = 0
    while ok < checks:
        cpu = psutil.cpu_percent(interval=every_s)
        free_gb = psutil.virtual_memory().available / 1024 ** 3
        ok = ok + 1 if cpu < cpu_max and free_gb >= free_gb_min else 0
        print(f"  esperando PC libre: CPU {cpu:.0f} %, RAM libre {free_gb:.1f} GB ({ok}/{checks})", flush=True)


def run_scenario(name: str, exe: Path, urls: list[str], minutes: float, stamp: str) -> dict:
    duration_ms = int(minutes * 60_000)
    open_ms = OPEN_GAP_MS * len(urls)
    total_s = (open_ms + duration_ms) / 1000  # misma ventana de medición para todos
    data_dir = tempfile.mkdtemp(prefix=f"navcmp-{name}-")
    proc, port = None, None
    samples: list[dict] = []
    result = {"scenario": name, "urls": urls, "minutes": minutes, "data_dir": data_dir,
              "sample_seconds": SAMPLE_SECONDS, "open_gap_ms": OPEN_GAP_MS}
    print(f"== {name}: {len(urls)} pestañas, {minutes} min (+{open_ms / 1000:.0f} s de apertura)", flush=True)
    try:
        if name.startswith("navegador"):
            proc, _ = launch_navegador(exe, data_dir, urls, duration_ms, hibernate=(name == "navegador"))
            result["exe"] = str(exe)
        else:
            browser = CHROME if name == "chrome" else EDGE
            if not browser.exists():
                raise FileNotFoundError(browser)
            proc, port = launch_chromium(browser, data_dir, urls)
            result["exe"] = str(browser)
            wait_cdp(port)
        root = proc.pid
        t0 = time.time()
        pending = list(urls[1:]) if port else []
        next_open = t0 + OPEN_GAP_MS / 1000
        next_sample = t0 + SAMPLE_SECONDS
        while True:
            now = time.time()
            if now - t0 >= total_s:
                break
            if name.startswith("navegador") and proc.poll() is not None:
                break  # Navegador se cerró solo (paso "close")
            if pending and now >= next_open:
                open_next_tab(port, pending.pop(0))
                next_open += OPEN_GAP_MS / 1000
            if now >= next_sample:
                s = sample(root, data_dir)
                s["t"] = round(now - t0, 1)
                s["system_cpu_pct"] = psutil.cpu_percent(interval=None)  # carga de toda la PC (otra app pesada = número dudoso)
                samples.append(s)
                print(f"  t={s['t']:>6}s  {s['private_mb']:>8} MB  {s['processes']:>2} procesos", flush=True)
                next_sample += SAMPLE_SECONDS
            time.sleep(0.2)
        if port:
            try:
                pages = [t.get("url", "") for t in cdp(port, "/json/list") if t.get("type") == "page"]
                result["pages_end"] = pages  # Edge suma páginas internas propias (edge://...)
                result["open_tabs_end"] = sum(1 for u in pages if u.startswith("http"))
            except OSError:
                result["open_tabs_end"] = None
        if proc and proc.poll() is None and name.startswith("navegador"):
            try:
                proc.wait(timeout=60)  # da tiempo al "close" de la automatización
            except subprocess.TimeoutExpired:
                result["forced_kill"] = True
    except Exception as e:  # se registra y se sigue con el próximo escenario
        result["error"] = f"{type(e).__name__}: {e}"
        print(f"  ! {result['error']}", flush=True)
    finally:
        result["killed_leftovers"] = kill_tree(proc.pid if proc else None, data_dir)
        if name.startswith("navegador"):
            result["log_counts"] = read_log_counts(data_dir)
        remove_dir(data_dir)
    result["samples"] = samples
    result["summary"] = summarize(samples)
    RESULTS.mkdir(exist_ok=True)
    out = RESULTS / f"{stamp}-{name}.json"
    out.write_text(json.dumps(result, indent=2, ensure_ascii=False), encoding="utf-8")
    print(f"  -> {out}", flush=True)
    return result


def markdown(results: list[dict], minutes: float, n_urls: int) -> str:
    lines = [f"Comparación de memoria privada: {n_urls} pestañas, {minutes} min tras abrirlas "
             f"(una cada {OPEN_GAP_MS // 1000} s), muestra cada {SAMPLE_SECONDS} s.", "",
             "| Escenario | Prom. MB | p50 MB | p95 MB | Máx. MB | MB al final | Procesos prom. | Muestras | CPU de la PC % | Eventos del log |",
             "|---|---:|---:|---:|---:|---:|---:|---:|---:|---|"]
    for r in results:
        s = r["summary"]
        ev = ", ".join(f"{k} {v}" for k, v in r.get("log_counts", {}).items()) or "—"
        if "error" in r:
            ev += f" (error: {r['error']})"
        if s:
            lines.append(f"| {r['scenario']} | {s['avg_mb']} | {s['p50_mb']} | {s['p95_mb']} | {s['max_mb']} | "
                         f"{s['end_mb']} | {s['avg_processes']} | {s['samples']} | {s.get('system_cpu_avg_pct', '—')} | {ev} |")
        else:
            lines.append(f"| {r['scenario']} | — | — | — | — | — | — | 0 | — | {ev} |")
    by = {r["scenario"]: r["summary"].get("avg_mb") for r in results}
    lines.append("")
    for nav in ("navegador", "navegador-sin-hibernar"):
        for other in ("chrome", "edge"):
            if by.get(nav) and by.get(other):
                ratio = by[nav] / by[other]
                ok = "cumple" if ratio <= 0.5 else "no cumple"
                lines.append(f"- {nav}/{other} (promedio): {ratio:.2f} ({ok} el criterio ≤ 0,50)")
    tabs = [f"{r['scenario']} {r['open_tabs_end']}" for r in results if "open_tabs_end" in r]
    if tabs:
        lines.append(f"- Pestañas abiertas al final (CDP): {', '.join(tabs)}")
    return "\n".join(lines) + "\n"


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--minutes", type=float, default=15)
    ap.add_argument("--scenarios", default=",".join(ALL_SCENARIOS))
    ap.add_argument("--exe", type=Path, default=DEFAULT_EXE)
    ap.add_argument("--urls", type=int, default=20)
    ap.add_argument("--esperar-pc-libre", action="store_true",
                    help="antes de cada escenario espera a que la CPU y la RAM estén libres (ver wait_idle)")
    a = ap.parse_args()
    scenarios = [s.strip() for s in a.scenarios.split(",") if s.strip()]
    bad = [s for s in scenarios if s not in ALL_SCENARIOS]
    if bad:
        sys.exit(f"escenarios desconocidos: {bad}; válidos: {ALL_SCENARIOS}")
    if any(s.startswith("navegador") for s in scenarios) and not a.exe.exists():
        sys.exit(f"no existe {a.exe}")
    urls = load_urls(a.urls)
    stamp = datetime.now().strftime("%Y%m%d-%H%M%S")
    start = time.time()
    results = []
    for s in scenarios:
        if a.esperar_pc_libre:
            wait_idle()
        results.append(run_scenario(s, a.exe.resolve(), urls, a.minutes, stamp))
    md = markdown(results, a.minutes, len(urls))
    md += f"\nTiempo total: {(time.time() - start) / 60:.1f} min.\n"
    out = RESULTS / f"{stamp}-comparacion.md"
    out.write_text(md, encoding="utf-8")
    print("\n" + md + f"\n-> {out}")


if __name__ == "__main__":
    main()
