"""Corre toda la suite de pruebas en Windows y guarda los resultados en un JSON.

    python run_windows.py

Antes de correr, verifica el entorno (versión de Python, dependencias, carpeta)
para que un problema de instalación se explique con un mensaje claro.

Genera: resultados\\resultados_AAAAMMDD_HHMMSS.json
Pasar ese archivo al chat de Claude Code para que lo analice contra INFORME_PRUEBAS.md.
"""

from __future__ import annotations

import datetime as dt
import importlib
import json
import platform
import subprocess
import sys
from pathlib import Path

RAIZ = Path(__file__).resolve().parent
SALIDA = RAIZ / "resultados"
PY_MIN = (3, 10)
DEPENDENCIAS = ["psutil", "pytest"]


def preflight() -> list[str]:
    """Devuelve una lista de problemas. Vacía significa que el entorno está listo."""
    problemas = []
    if sys.version_info < PY_MIN:
        problemas.append(
            f"Python {platform.python_version()} es muy viejo; hace falta {PY_MIN[0]}.{PY_MIN[1]} o superior."
        )
    for dep in DEPENDENCIAS:
        try:
            importlib.import_module(dep)
        except ImportError:
            problemas.append(f"Falta la dependencia '{dep}'. Ejecutá: pip install -r requirements.txt")
    for carpeta in ("metrics", "tests", "bench"):
        if not (RAIZ / carpeta).is_dir():
            problemas.append(f"No encuentro la carpeta '{carpeta}'. ¿Ejecutaste el script desde la carpeta del proyecto?")
    return problemas


def correr(nombre: str, cmd: list[str], timeout: int) -> dict:
    print(f"\n>>> {nombre}: {' '.join(cmd)}", flush=True)
    try:
        p = subprocess.run(
            cmd,
            cwd=RAIZ,
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",  # evita UnicodeDecodeError con cp1252 en Windows
            timeout=timeout,
        )
        return {
            "codigo": p.returncode,
            "salida": p.stdout.strip(),
            "error": p.stderr.strip()[-2000:],
        }
    except subprocess.TimeoutExpired:
        return {"codigo": None, "salida": "", "error": f"Timeout tras {timeout} s"}
    except OSError as e:
        return {"codigo": None, "salida": "", "error": f"No se pudo ejecutar: {e}"}


def main() -> int:
    problemas = preflight()
    if problemas:
        print("Antes de correr las pruebas hay que resolver esto:\n")
        for p in problemas:
            print(f"  - {p}")
        print("\nRevisá INSTRUCCIONES_WINDOWS.md, sección 'Problemas frecuentes'.")
        return 2

    SALIDA.mkdir(exist_ok=True)
    marca = dt.datetime.now().strftime("%Y%m%d_%H%M%S")
    py = sys.executable

    informe = {
        "fecha": marca,
        "sistema": platform.platform(),
        "python": platform.python_version(),
        "pruebas": correr("Suite de tests", [py, "-m", "pytest", "-q", "tests/"], timeout=600),
        "overhead": correr("Costo del medidor (P-01 a P-03)", [py, "bench/overhead.py", "--seconds", "60"], timeout=600),
        "escenarios": correr("Escenarios reales (E-01 a E-04)", [py, "bench/escenarios.py"], timeout=600),
    }

    ruta = SALIDA / f"resultados_{marca}.json"
    ruta.write_text(json.dumps(informe, indent=2, ensure_ascii=False), encoding="utf-8")
    print(f"\nListo. Resultados en: {ruta}")
    print("Pasá ese archivo al chat de Claude Code.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
