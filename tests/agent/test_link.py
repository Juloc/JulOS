"""Tests fuer die Verbindung des Agenten zum Server (tools/julos-agent.py): Fristen, gestreutes Neuverbinden, Keepalive. Ohne Netz und ohne Anzeige.

    python3 -m unittest discover -s tests/agent
"""
import importlib.util
import os
import socket
import threading
import time
import unittest
from unittest import mock

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
spec = importlib.util.spec_from_file_location("julos_agent_link_under_test", os.path.join(ROOT, "tools", "julos-agent.py"))
agent = importlib.util.module_from_spec(spec)
spec.loader.exec_module(agent)


class Deadlines(unittest.TestCase):
    def test_a_dead_line_is_noticed_in_about_ten_seconds_not_at_the_next_20_second_beat(self):
        self.assertLessEqual(agent.DEAD_S, 10)
        self.assertLessEqual(agent.HEARTBEAT_S, 5)
        self.assertGreater(agent.DEAD_S, agent.HEARTBEAT_S)       # at least one answer fits in the deadline

    def test_the_server_gets_its_beat_in_the_hello_so_it_can_use_a_short_idle_limit(self):
        with open(os.path.join(ROOT, "tools", "julos-agent.py"), encoding="utf-8") as f:
            src = f.read()
        self.assertIn('"hb": HEARTBEAT_S', src)


class Reconnect(unittest.TestCase):
    def test_after_a_long_connection_the_retry_is_short_and_spread_out(self):
        waits = {round(agent.reconnect_delay(60, 3600)[1], 3) for _ in range(50)}
        self.assertTrue(all(0.3 <= w <= 2.5 for w in waits))
        self.assertGreater(len(waits), 10)                        # not all agents knock at the same moment

    def test_extremes_of_the_spread(self):
        self.assertEqual(agent.reconnect_delay(60, 3600, uniform=lambda a, b: a), (2, 0.3))
        self.assertEqual(agent.reconnect_delay(60, 3600, uniform=lambda a, b: b), (2, 2.5))

    def test_a_server_that_is_gone_is_tried_with_growing_pauses_up_to_a_minute(self):
        base, seen = 2, []
        for _ in range(8):
            base, nap = agent.reconnect_delay(base, 1, uniform=lambda a, b: 1.0)
            seen.append(nap)
        self.assertEqual(seen, [4, 8, 16, 32, 60, 60, 60, 60])

    def test_short_lived_connections_are_spread_too(self):
        _, a = agent.reconnect_delay(4, 1, uniform=lambda lo, hi: lo)
        _, b = agent.reconnect_delay(4, 1, uniform=lambda lo, hi: hi)
        self.assertLess(a, b)


class Keepalive(unittest.TestCase):
    def test_keepalive_is_switched_on_with_short_probes_where_the_system_has_them(self):
        s = mock.Mock()
        agent.enable_keepalive(s)
        s.setsockopt.assert_any_call(socket.SOL_SOCKET, socket.SO_KEEPALIVE, 1)
        if hasattr(socket, "TCP_KEEPIDLE"):
            s.setsockopt.assert_any_call(socket.IPPROTO_TCP, socket.TCP_KEEPIDLE, 10)

    def test_a_system_that_refuses_it_is_not_an_error(self):
        s = mock.Mock(); s.setsockopt.side_effect = OSError("nicht erlaubt")
        agent.enable_keepalive(s)


class SilentPeer(unittest.TestCase):
    def test_a_peer_that_goes_silent_ends_the_receive_with_an_error_after_the_deadline(self):
        a, b = socket.socketpair()
        try:
            a.settimeout(0.3)                                     # DEAD_S, shortened
            ws = agent.WsClient(a)
            t0 = time.time()
            with self.assertRaises(agent.WsError):
                ws.recv(1024)
            self.assertLess(time.time() - t0, 2)
        finally:
            a.close(); b.close()

    def test_a_peer_that_answers_pings_keeps_the_receive_alive(self):
        a, b = socket.socketpair()
        try:
            a.settimeout(1.0)
            ws = agent.WsClient(a)
            def peer():
                time.sleep(0.4)
                b.sendall(bytes([0x80 | 0xA, 0]))                 # pong (unmasked, empty)
                time.sleep(0.4)
                b.sendall(bytes([0x80 | 0x1, 2]) + b"ok")         # then a text message
            threading.Thread(target=peer, daemon=True).start()
            self.assertEqual(ws.recv(1024), (1, b"ok"))
        finally:
            a.close(); b.close()

    def test_the_heartbeat_sends_the_beat_and_a_websocket_ping(self):
        ws = mock.Mock()
        stop = threading.Event()
        calls = []
        ws.send_text.side_effect = lambda t: calls.append(("text", t))
        ws.send_frame.side_effect = lambda op, payload=b"": (calls.append(("frame", op)), stop.set())
        link = agent.Link.__new__(agent.Link)
        with mock.patch.object(agent, "HEARTBEAT_S", 0.01):
            link.heartbeat(ws, stop)
        self.assertEqual(calls[:2], [("text", '{"t":"hb"}'), ("frame", 0x9)])


if __name__ == "__main__":
    unittest.main()
