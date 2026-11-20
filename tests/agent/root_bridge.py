"""Bruecke fuer die C#-Tests (RootFilesTests): laedt den ECHTEN Agenten (tools/julos-agent.py) und den ECHTEN root-Helfer (tools/julos-agent-helper.py) und beantwortet Anfragen des Servers.
Eine Zeile JSON je Anfrage auf stdin {"method","path","body"}, eine Zeile Antwort auf stdout {"status","body"}. Das Wurzelverzeichnis des Helfers ist der erste Parameter (ein Wegwerfordner,
nie das echte "/"), die Rechte kommen aus dem zweiten (Woerter, kommagetrennt); der Agent selbst laeuft ohne Netz, ohne Socket und ohne root.
Aufruf:  python3 -I root_bridge.py WURZEL WOERTER
"""
import importlib.util
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
TOOLS = os.path.join(HERE, "..", "..", "tools")


def load(name, file):
    spec = importlib.util.spec_from_file_location(name, os.path.join(TOOLS, file))
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


def main():
    root, words = sys.argv[1], {w for w in sys.argv[2].split(",") if w}
    agent = load("julos_agent_bridge", "julos-agent.py")
    helper = load("julos_helper_bridge", "julos-agent-helper.py")
    h = helper.Helper(permissions=lambda: set(words), fs_root=root)
    agent.permissions = lambda args=None: set(words)

    def helper_call(request, on_line=None, timeout=3600, path=None):
        # wie der Socket-Dienst: die Anfrage geht als eine JSON-Zeile durch den Helfer (serve), die Antwort kommt als Zeilen zurueck
        import io
        out = io.BytesIO()
        helper.serve(io.BytesIO(json.dumps(request, separators=(",", ":")).encode() + b"\n"), out, h)
        for line in out.getvalue().splitlines():
            msg = json.loads(line)
            if isinstance(msg.get("r"), dict):
                return msg["r"]
        return {"ok": False, "code": "helper_closed"}

    agent.helper_call = helper_call
    for line in sys.stdin:
        req = json.loads(line)
        status, body = agent.host_api(req["method"], req["path"], req.get("body"))
        sys.stdout.write(json.dumps({"status": status, "body": body}) + "\n")
        sys.stdout.flush()


if __name__ == "__main__":
    main()
