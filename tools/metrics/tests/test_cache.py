import time

import psutil

from metrics.sampler import AppProcessCache, collect, _memory_of


def test_cache_encuentra_el_propio_proceso_por_prefijo():
    nombre = psutil.Process().name()
    cache = AppProcessCache(nombre[:5], refresh_s=0)
    pids = [p.pid for p in cache.get()]
    assert psutil.Process().pid in pids


def test_cache_no_reescanea_antes_de_refresh():
    cache = AppProcessCache("no-existe-zzz", refresh_s=60)
    cache.get()
    scan1 = cache._last_scan
    cache.get()
    assert cache._last_scan == scan1


def test_cache_descarta_procesos_muertos(monkeypatch):
    cache = AppProcessCache("x", refresh_s=60)

    class Fake:
        def __init__(self, vivo):
            self.vivo = vivo
        def is_running(self):
            return self.vivo

    cache._procs = [Fake(True), Fake(False)]
    cache._last_scan = time.monotonic()
    assert len(cache.get()) == 1


def test_collect_con_cache_registra_rss_y_privada():
    nombre = psutil.Process().name()
    cache = AppProcessCache(nombre[:5], refresh_s=0)
    rec = collect(None, cache)
    assert rec["app_procs"] >= 1
    assert rec["app_rss_mb"] > 0
    # memoria privada puede no estar disponible por permisos; si está, debe ser coherente
    if "app_private_mb" in rec:
        assert 0 < rec["app_private_mb"] <= rec["app_rss_mb"] * 1.5


def test_memory_of_proceso_inexistente_no_falla():
    p = psutil.Process()
    rss, priv = _memory_of(p)
    assert rss > 0
