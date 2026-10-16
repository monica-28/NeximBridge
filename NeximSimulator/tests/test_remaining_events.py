"""Loopback-only tests. Build NeximSimulator first; port 30040 must be free.
Never starts NeximBridge or connects to SQL Server.
"""
import datetime
import pathlib
import socket
import struct
import subprocess
import threading
import unittest

DLL = pathlib.Path(__file__).resolve().parents[1] / "bin/Debug/net8.0/NeximSimulator.dll"
EVENTS = [(name, 1) for name in ("PARTSUSAGE", "PDERROR", "PRODSTARTED", "PRODCOMPLETEDII", "PRODSTOPPED")]
EVENTS += [("LOADCOMPIV", 0), ("CHANGECOMPII", 0), ("UNLOADCOMP", 0), ("MCALARMON", 1), ("MCALARMOFF", 1)]
OLDER_ACKS = [
    "PARTSUSAGE_ACK\t1001\t0\tNXT1\t1\t1\tTESTJOB_T\t10001",
    "PDERROR_ACK\t1002\t0\tNXT1\t1\t1\tTESTJOB_T",
    "PRODSTARTED_ACK\t1003\t0\tNXT1\t1\t1\t0\tTESTJOB_T\t10002",
    "PRODCOMPLETEDII_ACK\t1004\t0\tNXT1\t1\t1\t0\tTESTJOB_T\t10002",
    "PRODSTOPPED_ACK\t1005\t0\tNXT1\t1\t1\t0\tTESTJOB_T\t10003\t5\t1",
]
DETAILS = [
    "2\r1\t5\tRES-10K\tFEEDER-005\tREEL-A\tVENDOR-A\tLOT-A\t202609\tLIGHT-A\t5000\t0\r"
    "1\t6\tCAP-100N\tFEEDER-006\tREEL-B\tVENDOR-B\tLOT-B\t202609\tLIGHT-B\t3000\t0\r",
    "1\r1\t5\tRES-10K\tFEEDER-005\tREEL-A\tREEL-C\t4500\r",
    "1\r1\t5\tRES-10K\tFEEDER-005\tREEL-A\t250\t120\r",
    "00000001\t00000010", "00000001\t00000010",
]


def wire(message):
    body = b"\x02" + message.encode() + b"\x03"
    return struct.pack(">I", len(body)) + body


def read(peer):
    def exact(length):
        data = b""
        while len(data) < length:
            part = peer.recv(length - len(data))
            assert part, "Unexpected disconnect"
            data += part
        return data
    size = struct.unpack(">I", exact(4))[0]
    assert 2 <= size <= 4096
    body = exact(size)
    assert body[0] == 2 and body[-1] == 3
    return body[1:-1].decode()


class RemainingTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.lines = []
        ready = threading.Event()
        cls.process = subprocess.Popen(["dotnet", str(DLL)], stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                      text=True, encoding="utf-8", errors="replace",
                                      creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        def capture():
            for line in cls.process.stdout:
                cls.lines.append(line)
                if "escuchando" in line:
                    ready.set()
        cls.reader = threading.Thread(target=capture, daemon=True)
        cls.reader.start()
        if not ready.wait(5):
            cls.tearDownClass()
            raise AssertionError("Simulator did not start: " + "".join(cls.lines))

    @classmethod
    def tearDownClass(cls):
        if cls.process.poll() is None:
            cls.process.terminate()
        cls.process.wait(timeout=5)
        cls.reader.join(timeout=2)
        cls.process.stdout.close()

    def connect(self, events=EVENTS):
        peer = socket.create_connection(("127.0.0.1", 30040), timeout=5)
        self.addCleanup(peer.close)
        peer.sendall(wire(f"SETEV\t1\t\t{len(events)}\r" + "".join(f"{n}\t{a}\r" for n, a in events)))
        self.assertEqual(read(peer), "SETEV_ACK\t1\t\t0")
        peer.sendall(wire("STARTEV\t2\t"))
        self.assertEqual(read(peer), "STARTEV_ACK\t2\t\t0")
        for index, ack in enumerate(OLDER_ACKS):
            self.assertTrue(read(peer).startswith(f"{EVENTS[index][0]}\t{1001 + index}\t"))
            peer.sendall(wire(ack))
        return peer

    def receive_new(self, peer, index):
        message = read(peer)
        name = EVENTS[index + 5][0]
        fields = message.split("\t", 6)
        self.assertEqual(fields[:2], [name, str(1006 + index)])
        self.assertEqual(fields[3:6], ["LINE1", "NXT1", "1"])
        self.assertEqual(fields[6], DETAILS[index])
        self.assertEqual(len(fields[2]), 14)
        timestamp = datetime.datetime.strptime(fields[2], "%Y%m%d%H%M%S").replace(tzinfo=datetime.timezone.utc)
        self.assertLess(abs((datetime.datetime.now(datetime.timezone.utc) - timestamp).total_seconds()), 5)
        if index < 3:
            # Intentionally send no ACK: next event must arrive without waiting.
            self.assertTrue(message.endswith("\r"))
        else:
            self.assertNotIn("\r", message)

    def test_exact_sequence_and_monitoring_without_ack(self):
        peer = self.connect()
        for index in range(5):
            self.receive_new(peer, index)
            if index >= 3:
                ack = wire(f"{EVENTS[index + 5][0]}_ACK\t{1006 + index}\t0\tNXT1\t1")
                for value in ack:
                    peer.sendall(bytes([value]))
        peer.settimeout(.2)
        with self.assertRaises(socket.timeout):
            peer.recv(1)  # Remains open, no repeated production/component events.


def subscription_case(index, missing):
    def test(self):
        name, ack = EVENTS[index + 5]
        events = [(n, a) for n, a in EVENTS if not (missing and n == name)]
        if not missing:
            events = [(n, 1 - a if n == name else a) for n, a in events]
        peer = self.connect(events)
        for earlier in range(index):
            self.receive_new(peer, earlier)
            if earlier >= 3:
                peer.sendall(wire(f"{EVENTS[earlier + 5][0]}_ACK\t{1006 + earlier}\t0\tNXT1\t1"))
        self.assertEqual(peer.recv(1), b"", "Scenario ran without required subscription flag")
    return test


def alarm_case(index, mutation):
    def test(self):
        peer = self.connect()
        for earlier in range(index + 1):
            self.receive_new(peer, earlier)
            if earlier >= 3 and earlier != index:
                peer.sendall(wire(f"{EVENTS[earlier + 5][0]}_ACK\t{1006 + earlier}\t0\tNXT1\t1"))
        ack = [EVENTS[index + 5][0] + "_ACK", str(1006 + index), "0", "NXT1", "1"]
        if isinstance(mutation, int):
            ack[mutation] = "1" if mutation == 2 else "WRONG"
        elif mutation == "missing":
            ack.pop()
        elif mutation == "extra":
            ack.append("extra")
        if mutation == "timeout":
            peer.settimeout(23)
        else:
            peer.sendall(wire("\t".join(ack)))
        self.assertEqual(peer.recv(1), b"", "Invalid/missing ACK accepted")
    return test


for index in range(5):
    for missing in (True, False):
        setattr(RemainingTests, f"test_subscription_{index}_{'missing' if missing else 'wrong_flag'}", subscription_case(index, missing))
for index in (3, 4):
    for mutation in (0, 1, 2, 3, 4, "missing", "extra", "timeout"):
        setattr(RemainingTests, f"test_alarm_{index}_{mutation}", alarm_case(index, mutation))

if __name__ == "__main__":
    unittest.main(verbosity=2)
