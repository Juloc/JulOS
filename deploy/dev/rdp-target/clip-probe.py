#!/usr/bin/env python3
"""Nur fuer das Wegwerf-Ziel der Test-Instanz: ein kleiner HTTP-Dienst in der Desktop-Sitzung, mit dem die Tests die Zwischenablage der VM
setzen und lesen (xclip), ohne dass der Test-Container Docker braucht. Hoert auf Port 9777 im Netz der Test-Instanz; es gibt keinen veroeffentlichten Port.
  GET  /get?sel=clipboard|primary&type=<ziel>   Inhalt (404, wenn nichts da ist), Standard: clipboard, UTF8_STRING
  GET  /targets?sel=clipboard                   die angebotenen Ziele, eines je Zeile
  POST /set?sel=clipboard&type=<ziel>           Koerper wird der Inhalt (xclip bleibt als Besitzer im Hintergrund)
  POST /set?...&stall=1                         wie /set, aber der Besitzer wird angehalten (SIGSTOP): er antwortet nie auf Anfragen, wie ein haengender Zwischenablage-Verwalter
  POST /clear                                   alle Besitzer beenden (die Auswahl ist leer)
  POST /entry                                   ein kleines Fenster "clip-entry" mit einem Eingabefeld (Koerper = Inhalt), zum Markieren und Kopieren mit der Tastatur
  POST /entry-close                             schliesst es wieder
Ton (fuer die Browser-Tests, wie deploy/dev/play-tone.sh und record-mic.sh, nur dass kein Docker noetig ist):
  POST /tone?s=3                                spielt s Sekunden einen 440-Hz-Ton ueber das Tongeraet der Sitzung (paplay, im Hintergrund)
  POST /record-start                            nimmt das Mikrofon der Sitzung auf (parecord, 16 Bit, 16 kHz, Mono) in eine Datei; so oeffnet xrdp den Mikrofon-Kanal (503, wenn parecord gleich endet)
  GET  /record-level                            {"samples": n, "loud": Zahl der Samples ueber 4000, "peak": hoechster Betrag} der Aufnahme bisher
  POST /record-stop                             beendet die Aufnahme
  GET  /sinks                                   pactl list short sinks und sources (Diagnose)
"""
import http.server, json, math, os, struct, subprocess, tempfile, time, urllib.parse, wave

REC = "/tmp/julos-rec.raw"
env = dict(os.environ, XDG_RUNTIME_DIR="/run/user/%d" % os.getuid())
recorder = None


def tone_file(seconds):
    rate = 44100
    f = tempfile.NamedTemporaryFile(suffix=".wav", delete=False)
    f.close()
    with wave.open(f.name, "wb") as w:
        w.setnchannels(2); w.setsampwidth(2); w.setframerate(rate)
        w.writeframes(b"".join(struct.pack("<hh", v, v) for v in (int(12000 * math.sin(2 * math.pi * 440 * i / rate)) for i in range(int(rate * seconds)))))
    return f.name

SELS = ("clipboard", "primary", "secondary")

def sel_of(q):
    s = q.get("sel", ["clipboard"])[0]
    return s if s in SELS else "clipboard"


def type_of(q, default):
    t = q.get("type", [default])[0]
    return t if all(c.isalnum() or c in "/_-.+" for c in t) else default


class H(http.server.BaseHTTPRequestHandler):
    def reply(self, code, body=b"", ctype="application/octet-stream"):
        self.send_response(code)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Connection", "close")
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        u = urllib.parse.urlparse(self.path)
        q = urllib.parse.parse_qs(u.query)
        if u.path == "/get":
            r = subprocess.run(["xclip", "-selection", sel_of(q), "-o", "-t", type_of(q, "UTF8_STRING")], capture_output=True, timeout=10)
            return self.reply(200, r.stdout) if r.returncode == 0 else self.reply(404, r.stderr)
        if u.path == "/targets":
            r = subprocess.run(["xclip", "-selection", sel_of(q), "-o", "-t", "TARGETS"], capture_output=True, timeout=10)
            return self.reply(200, r.stdout) if r.returncode == 0 else self.reply(404, r.stderr)
        if u.path == "/record-level":
            data = open(REC, "rb").read() if os.path.exists(REC) else b""
            samples = struct.unpack("<%dh" % (len(data) // 2), data[: len(data) & ~1])
            return self.reply(200, json.dumps({"samples": len(samples), "loud": sum(1 for x in samples if abs(x) > 4000), "peak": max((abs(x) for x in samples), default=0)}).encode(), "application/json")
        if u.path == "/sinks":
            r = subprocess.run("pactl list short sinks; pactl list short sources", shell=True, capture_output=True, timeout=10, env=env)
            return self.reply(200, r.stdout + r.stderr)
        self.reply(404)

    def do_POST(self):
        u = urllib.parse.urlparse(self.path)
        q = urllib.parse.parse_qs(u.query)
        body = self.rfile.read(int(self.headers.get("Content-Length", "0")))
        if u.path == "/set":
            p = subprocess.Popen(["xclip", "-selection", sel_of(q), "-i", "-t", type_of(q, "UTF8_STRING")], stdin=subprocess.PIPE, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            p.stdin.write(body)
            p.stdin.close()
            p.wait(timeout=10)       # xclip loest sich ab und bleibt als Besitzer im Hintergrund
            if q.get("stall", ["0"])[0] == "1":
                time.sleep(0.3)
                subprocess.run(["pkill", "-STOP", "-u", str(os.getuid()), "-x", "xclip"])
            return self.reply(200, b"ok")
        if u.path == "/entry":
            subprocess.Popen(["python3", "/opt/julos/clip-entry.py", body.decode("utf-8", "replace")], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            return self.reply(200, b"ok")
        if u.path == "/entry-close":
            subprocess.run(["pkill", "-u", str(os.getuid()), "-f", "clip-entry.py"])
            return self.reply(200, b"ok")
        if u.path == "/tone":
            wav = tone_file(float(q.get("s", ["3"])[0]))
            subprocess.Popen("paplay %s; rm -f %s" % (wav, wav), shell=True, env=env, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            return self.reply(200, b"ok")
        if u.path in ("/record-start", "/record-stop"):
            global recorder
            if recorder is not None:
                recorder.terminate()
                recorder = None
            if u.path == "/record-start":
                if os.path.exists(REC):
                    os.remove(REC)
                recorder = subprocess.Popen(["parecord", "--raw", "--format=s16le", "--rate=16000", "--channels=1", "--latency-msec=50", REC], env=env, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
                time.sleep(1.5)
                if recorder.poll() is not None:          # kein Tongeraet (die Sitzung ist noch nicht ganz oben): der Aufrufer versucht es erneut
                    recorder = None
                    return self.reply(503, b"parecord endete gleich")
            return self.reply(200, b"ok")
        if u.path == "/clear":
            subprocess.run(["pkill", "-KILL", "-u", str(os.getuid()), "-x", "xclip"])
            return self.reply(200, b"ok")
        self.reply(404)

    def log_message(self, *args):
        pass


http.server.ThreadingHTTPServer(("0.0.0.0", 9777), H).serve_forever()
