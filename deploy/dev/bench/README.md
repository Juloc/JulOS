# Display measurement (dev stack)

Measures click-to-picture latency of the two display engines in the browser, with a test video as load.

1. Start the dev stack (`deploy/dev/up.sh` or your own compose project), log in with a throwaway test account, add the connection to `rdp-target`.
2. In the test desktop session, as user `ubuntu` with `DISPLAY` and `XAUTHORITY=/home/ubuntu/.Xauthority` of the xrdp session (check `ls /tmp/.X11-unix`, usually `:11`):
   - `python3 probe.py 560 30 &` (copy `probe.py` into the container first, it is a small window with a red square that turns green on every click or key),
   - `ffplay -loglevel quiet -f lavfi -i "testsrc2=size=480x270:rate=30" -left 20 -top 30 -noborder -alwaysontop &` (the load). Close other windows first. The desktop must be at least about 700 x 300 px.
3. Connect from the browser (engine: connection setting, or `localStorage pd.engine` = `JSON.stringify('stream')` for the stream engine), paste `display-bench.js`, run `JSON.stringify(await window.__bench({}))`. Use `{ video: false }` for the idle case.
4. Wait for a quiet machine first (`uptime`: load 1 min below the number of cores / 2), note the load, run each engine at least 3 rounds alternately, report median and p90 of the click latency. The frame rate value is capped by the sampling and not meaningful.
5. Server CPU: `docker stats --no-stream` on the julos and guacd containers during the run.

Gotchas: the browser pane's tool clicks send no mouse move, so use `hover` before a click; the pane drops the login cookie on a reload, log in again.

## Slow link, sharpness (added with the pacing and refinement work)

- `probe_gtk.py [video [noise]]` is `probe.py` for the test desktop as it is built (GTK, no tkinter there): the red/green square at 620,90 and, with `video`, a moving picture at 200,330 (`noise`: random pixels, a worst case for any link; `TICK=16` environment variable: faster ticks). Copy it into the container, run it as the desktop user with `DISPLAY` and `XAUTHORITY` as above.
- `slowproxy.py LISTEN TARGET RATE_KBIT DELAY_MS` is a throttling TCP forwarder (bandwidth and delay per direction; `rate.txt` next to it changes the rate while it runs). Open the page on `LISTEN` instead of the server's port (cookies are shared between ports; origin and host match because the proxy keeps the headers). With 3000 kbit/s, 30 ms and the noise video the stream's click latency is the measure for `LinkPacer`; `JULOS_STREAM_PACER=off` on the server switches the pacer off for the comparison, `JULOS_STREAM_REFINE=off` the lossless refinement.
- `text_editor.py "Monospace 11" X` (a GTK text view with syntax-coloured text) and `text_terminal.sh` (run it in `xfce4-terminal -x`: black on white, coloured and grey text) are the text samples for sharpness. Method: grab the display canvas of a Guacamole session (exact) and of a stream session of the same desktop with `getImageData`, compare rectangles (mean absolute error and the share of pixels off by more than 32), and draw both enlarged next to each other for a look. `localStorage` `pd.refine` = `"off"` switches the refinement off in the browser.
