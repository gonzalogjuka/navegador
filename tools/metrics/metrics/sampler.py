"""Muestreo periódico del sistema y de los procesos de una aplicación.

Cada muestra queda como una línea {"type": "sample", ...} en el EventLog.
Las tasas de disco y red se calculan contra la muestra anterior.

Para la app observada se usa un caché de procesos que se refresca cada
`refresh_s` segundos, en vez de recorrer todos los procesos en cada muestra.
Se registra RSS y, si el sistema lo permite, memoria privada (USS en Linux,
private en Windows). En Windows el proceso en primer plano también se registra.
"""

from __future__ import annotations

import threading
import time
from typing import Any, Optional

import psutil

from .events import EventLog

MB = 2**20


def foreground_process_name() -> Optional[str]:
    """Nombre del ejecutable con el foco actual (solo Windows)."""
    try:
        import ctypes
        from ctypes import wintypes
    except ImportError:
        return None
    if not hasattr(ctypes, "windll"):
        return None
    user32 = ctypes.windll.user32
    hwnd = user32.GetForegroundWindow()
    if not hwnd:
        return None
    pid = wintypes.DWORD()
    user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
    try:
        return psutil.Process(pid.value).name()
    except psutil.Error:
        return None


class AppProcessCache:
    """Mantiene la lista de procesos cuyo nombre empieza con `prefix`.

    La lista completa se recorre solo cada `refresh_s` segundos. Entre
    refrescos se descartan los procesos que ya no existen.
    """

    def __init__(self, prefix: str, refresh_s: float = 2.0):
        self.prefix = prefix.lower()
        self.refresh_s = refresh_s
        self._procs: list[psutil.Process] = []
        self._last_scan = float("-inf")

    def get(self) -> list[psutil.Process]:
        now = time.monotonic()
        if now - self._last_scan >= self.refresh_s:
            self._procs = [
                p for p in psutil.process_iter(["name"])
                if (p.info.get("name") or "").lower().startswith(self.prefix)
            ]
            self._last_scan = now
        vivos = []
        for p in self._procs:
            try:
                if p.is_running():
                    vivos.append(p)
            except psutil.Error:
                pass
        self._procs = vivos
        return vivos


def _memory_of(p: psutil.Process) -> tuple[Optional[int], Optional[int]]:
    """Devuelve (rss_bytes, memoria_privada_bytes). Cualquiera puede ser None."""
    try:
        fi = p.memory_full_info()
        privada = getattr(fi, "private", None) or getattr(fi, "uss", None)
        return fi.rss, privada
    except (psutil.AccessDenied, psutil.ZombieProcess):
        try:
            return p.memory_info().rss, None
        except psutil.Error:
            return None, None
    except psutil.Error:
        return None, None


def collect(
    prev: Optional[dict[str, Any]],
    cache: Optional[AppProcessCache] = None,
) -> dict[str, Any]:
    """Toma una muestra. `prev` es el estado de la muestra anterior (o None)."""
    now = time.time()
    vm = psutil.virtual_memory()
    disk = psutil.disk_io_counters()
    net = psutil.net_io_counters()

    rec: dict[str, Any] = {
        "cpu_pct": psutil.cpu_percent(interval=None),
        "ram_used_pct": vm.percent,
        "ram_avail_mb": round(vm.available / MB, 1),
        "foreground": foreground_process_name(),
    }

    if prev is not None:
        dt = max(now - prev["ts"], 1e-6)
        if disk and prev.get("disk"):
            rec["disk_read_mbps"] = round((disk.read_bytes - prev["disk"].read_bytes) / MB / dt, 3)
            rec["disk_write_mbps"] = round((disk.write_bytes - prev["disk"].write_bytes) / MB / dt, 3)
        if net and prev.get("net"):
            rec["net_recv_mbps"] = round((net.bytes_recv - prev["net"].bytes_recv) / MB / dt, 3)
            rec["net_sent_mbps"] = round((net.bytes_sent - prev["net"].bytes_sent) / MB / dt, 3)

    if cache is not None:
        rss, privada = [], []
        procs = cache.get()
        for p in procs:
            r, pv = _memory_of(p)
            if r is not None:
                rss.append(r)
            if pv is not None:
                privada.append(pv)
        rec["app_procs"] = len(procs)
        rec["app_rss_mb"] = round(sum(rss) / MB, 1)
        if privada:
            rec["app_private_mb"] = round(sum(privada) / MB, 1)

    rec["_state"] = {"ts": now, "disk": disk, "net": net}
    return rec


class Sampler:
    """Hilo que muestrea cada `interval` segundos y escribe en el log."""

    def __init__(
        self,
        log: EventLog,
        interval: float = 0.5,
        app_prefix: Optional[str] = None,
        refresh_s: float = 2.0,
    ):
        self.log = log
        self.interval = interval
        self.cache = AppProcessCache(app_prefix, refresh_s) if app_prefix else None
        self._stop = threading.Event()
        self._thread = threading.Thread(target=self._run, name="sampler", daemon=True)

    def start(self) -> None:
        psutil.cpu_percent(interval=None)  # primera lectura para que la siguiente sea válida
        self._thread.start()

    def stop(self) -> None:
        self._stop.set()
        self._thread.join(timeout=5)

    def _run(self) -> None:
        prev = None
        while not self._stop.is_set():
            rec = collect(prev, self.cache)
            prev = rec.pop("_state")
            rec["type"] = "sample"
            self.log.write(rec)
            self._stop.wait(self.interval)
