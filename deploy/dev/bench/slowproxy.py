#!/usr/bin/env python3
# Bremsende TCP-Weiterleitung zum Messen einer langsamen Leitung: slowproxy.py LISTEN ZIEL RATE_KBIT VERZOEGERUNG_MS
# Rate und Verzoegerung gelten je Richtung; die Datei ./rate.txt (kbit/s) darf waehrend des Laufs geaendert werden.
import asyncio, sys, time, os
LISTEN, TARGET, RATE, DELAY = int(sys.argv[1]), int(sys.argv[2]), float(sys.argv[3]), float(sys.argv[4]) / 1000
HERE = os.path.dirname(os.path.abspath(__file__))
def rate():
    try: return float(open(os.path.join(HERE, 'rate.txt')).read().strip())
    except Exception: return RATE
async def pump(r, w):
    q = asyncio.Queue(); busy = [0.0]
    async def reader():
        while True:
            d = await r.read(8192)
            if not d: await q.put(None); return
            await q.put((time.monotonic(), d))
    async def writer():
        while True:
            it = await q.get()
            if it is None: break
            t, d = it
            bps = rate() * 125.0
            start = max(time.monotonic(), busy[0], t + DELAY)
            busy[0] = start + len(d) / bps
            await asyncio.sleep(max(0, busy[0] - time.monotonic()))
            w.write(d); await w.drain()
        w.close()
    await asyncio.gather(reader(), writer())
async def handle(cr, cw):
    sr, sw = await asyncio.open_connection('127.0.0.1', TARGET)
    await asyncio.gather(pump(cr, sw), pump(sr, cw), return_exceptions=True)
async def main():
    srv = await asyncio.start_server(handle, '127.0.0.1', LISTEN); await srv.serve_forever()
asyncio.run(main())
