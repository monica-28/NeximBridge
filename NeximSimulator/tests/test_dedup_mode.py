"""Loopback wire test; no Bridge, SQL or real Nexim access."""
import datetime
import pathlib
import socket
import struct
import subprocess
import threading
import time
import unittest

DLL = pathlib.Path(__file__).resolve().parents[1] / "bin/Debug/net8.0/NeximSimulator.dll"
ACK = "PARTSUSAGE_ACK\t2001\t0\tNXT1\t1\t1\tDEDUP_TEST\tDEDUP001"


def wire(message):
    payload = b"\x02" + message.encode() + b"\x03"
    return struct.pack(">I", len(payload)) + payload


def read_frame(peer):
    def exact(size):
        data = b""
        while len(data) < size:
            part = peer.recv(size - len(data))
            assert part, "Unexpected disconnect"
            data += part
        return data
    header = exact(4)
    size = struct.unpack(">I", header)[0]
    assert 2 <= size <= 4096
    payload = exact(size)
    assert payload[:1] == b"\x02" and payload[-1:] == b"\x03"
    return header + payload


class DedupTests(unittest.TestCase):
    def setUp(self):
        self.lines = []
        ready = threading.Event()
        self.process = subprocess.Popen(["dotnet", str(DLL), "--dedup-test"], stdout=subprocess.PIPE,
                                        stderr=subprocess.STDOUT, text=True, encoding="utf-8", errors="replace",
                                        creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        def capture():
            for line in self.process.stdout:
                self.lines.append(line)
                if "escuchando" in line:
                    ready.set()
        self.reader = threading.Thread(target=capture, daemon=True)
        self.reader.start()
        self.addCleanup(self.finish)
        self.assertTrue(ready.wait(5), "Simulator startup failed: " + "".join(self.lines))
        self.peer = socket.create_connection(("127.0.0.1", 30040), timeout=5)
        self.addCleanup(self.peer.close)

    def finish(self):
        if self.process.poll() is None:
            self.process.terminate()
        self.process.wait(timeout=5)
        self.reader.join(timeout=2)
        self.process.stdout.close()

    def initialize(self, events=(("PARTSUSAGE", 1), ("LOADCOMPIV", 0))):
        self.peer.sendall(wire(f"SETEV\t1\t\t{len(events)}\r" + "".join(f"{name}\t{ack}\r" for name, ack in events)))
        self.assertEqual(read_frame(self.peer), wire("SETEV_ACK\t1\t\t0"))
        self.peer.settimeout(.1)
        with self.assertRaises(socket.timeout):
            self.peer.recv(1)
        self.peer.settimeout(5)
        self.peer.sendall(wire("STARTEV\t2\t"))
        self.assertEqual(read_frame(self.peer), wire("STARTEV_ACK\t2\t\t0"))

    def test_exact_retransmissions_utc_delays_no_load_ack_and_no_other_scenarios(self):
        self.initialize()
        original = read_frame(self.peer)
        body = original[5:-1].decode()
        records = body.split("\r")
        fields = records[0].split("\t")
        self.assertEqual(fields[:2], ["PARTSUSAGE", "2001"])
        self.assertEqual(fields[3:], ["LINE1", "NXT1", "1", "1", "DEDUP_TEST", "DEDUP001", "2"])
        self.assertEqual(records[1:], [
            "1\t5\tRES-10K\tRES-10K-AVL\t100\t102\t2\t1\t1\t0\t0\t0\r".rstrip("\r"),
            "1\t6\tCAP-100N\tCAP-100N-AVL\t50\t50\t0\t0\t0\t0\t0\t0", ""])
        self.check_time(fields[2])
        # Cross a whole timestamp second before allowing the retransmission.
        self.peer.settimeout(1.1)
        with self.assertRaises(socket.timeout):
            self.peer.recv(1)
        self.peer.settimeout(5)
        self.peer.sendall(wire(ACK))
        self.assertEqual(original, read_frame(self.peer), "PARTSUSAGE bytes or Time regenerated")
        self.peer.settimeout(.1)
        with self.assertRaises(socket.timeout):
            self.peer.recv(1)  # LOADCOMPIV must wait for the duplicate ACK.
        self.peer.settimeout(5)
        self.peer.sendall(wire(ACK))
        load = read_frame(self.peer)
        started = time.monotonic()
        fields = load[5:-1].decode().split("\t", 6)
        self.assertEqual(fields[:2], ["LOADCOMPIV", "2002"])
        self.assertEqual(fields[3:6], ["LINE1", "NXT1", "1"])
        self.check_time(fields[2])
        self.assertEqual(fields[6], "2\r" +
                         "1\t15\tDEDUP-RES\tFEEDER-D15\tREEL-D15\tVENDOR-D\tLOT-D15\t202610\tLIGHT-D\t9000\t0\r" +
                         "1\t16\tDEDUP-CAP\tFEEDER-D16\tREEL-D16\tVENDOR-D\tLOT-D16\t202610\tLIGHT-D\t8000\t0\r")
        # No ACK is sent for either LOADCOMPIV frame.
        self.assertEqual(load, read_frame(self.peer), "LOADCOMPIV bytes or Time regenerated")
        self.assertGreaterEqual(time.monotonic() - started, .45)
        started = time.monotonic()
        self.assertEqual(self.peer.recv(1), b"", "Extra event or KEEPALIVE sent")
        self.assertGreaterEqual(time.monotonic() - started, .9)
        self.assertEqual(self.process.wait(timeout=5), 0)
        self.reader.join(timeout=2)
        output = "".join(self.lines)
        for label in ("TX PARTSUSAGE ORIGINAL", "RX PARTSUSAGE_ACK ORIGINAL", "TX PARTSUSAGE DUPLICATE",
                      "RX PARTSUSAGE_ACK DUPLICATE", "TX LOADCOMPIV ORIGINAL", "TX LOADCOMPIV DUPLICATE"):
            self.assertEqual(output.count(label), 1, output)
        self.assertNotIn("KEEPALIVE enviado", output)
        for event in ("PDERROR", "PRODSTARTED", "PRODCOMPLETEDII", "PRODSTOPPED", "CHANGECOMPII", "UNLOADCOMP", "MCALARMON", "MCALARMOFF"):
            self.assertNotIn("TX " + event, output)

    def check_time(self, text):
        self.assertEqual(len(text), 14)
        value = datetime.datetime.strptime(text, "%Y%m%d%H%M%S").replace(tzinfo=datetime.timezone.utc)
        self.assertLess(abs((datetime.datetime.now(datetime.timezone.utc) - value).total_seconds()), 5)


def bad_ack_case(duplicate, mutation):
    def test(self):
        self.initialize()
        original = read_frame(self.peer)
        if duplicate:
            self.peer.sendall(wire(ACK))
            self.assertEqual(original, read_frame(self.peer))
        fields = ACK.split("\t")
        if mutation == "extra":
            fields.append("extra")
        elif mutation == "missing":
            fields.pop()
        else:
            fields[mutation] = "1" if mutation == 2 else "WRONG"
        self.peer.sendall(wire("\t".join(fields)))
        self.assertEqual(self.peer.recv(1), b"", "Invalid ACK accepted")
        self.assertEqual(self.process.wait(timeout=5), 1)
    return test


for duplicate in (False, True):
    for mutation in (0, 1, 2, 3, 4, 5, 6, 7, "missing", "extra"):
        setattr(DedupTests, f"test_bad_ack_{duplicate}_{mutation}", bad_ack_case(duplicate, mutation))


def subscription_case(events):
    def test(self):
        self.initialize(events)
        self.assertEqual(self.peer.recv(1), b"", "Sent event without required subscription")
        self.assertEqual(self.process.wait(timeout=5), 1)
    return test


for index, events in enumerate([
    [("PARTSUSAGE", 0), ("LOADCOMPIV", 0)], [("PARTSUSAGE", 1), ("LOADCOMPIV", 1)],
    [("LOADCOMPIV", 0)], [("PARTSUSAGE", 1)],
]):
    setattr(DedupTests, f"test_subscription_{index}", subscription_case(events))


if __name__ == "__main__":
    unittest.main(verbosity=2)
