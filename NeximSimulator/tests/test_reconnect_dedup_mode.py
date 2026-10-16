"""Two real TCP sessions against the simulator; no SQL or Bridge changes."""
import socket
import subprocess
import threading
import unittest

from test_dedup_mode import DLL, wire, read_frame, DedupTests

ACK = "PARTSUSAGE_ACK\t3001\t0\tNXT1\t1\t1\tRECONNECT_DEDUP\tRECON001"


class ReconnectTests(unittest.TestCase):
    finish = DedupTests.finish
    initialize = DedupTests.initialize
    check_time = DedupTests.check_time

    def setUp(self):
        self.lines = []
        ready = threading.Event()
        self.process = subprocess.Popen(["dotnet", str(DLL), "--reconnect-dedup-test"],
                                        stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                        text=True, encoding="utf-8", errors="replace",
                                        creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        def capture():
            for line in self.process.stdout:
                self.lines.append(line)
                if "escuchando" in line:
                    ready.set()
        self.reader = threading.Thread(target=capture, daemon=True)
        self.reader.start()
        self.addCleanup(self.finish)
        self.assertTrue(ready.wait(5), "".join(self.lines))
        self.connect()

    def connect(self):
        self.peer = socket.create_connection(("127.0.0.1", 30040), timeout=5)
        self.addCleanup(self.peer.close)

    def next_session(self):
        self.peer.sendall(wire(ACK))
        self.assertEqual(self.peer.recv(1), b"", "First session did not close cleanly")
        self.peer.close()
        self.assertIsNone(self.process.poll(), "Simulator exited between sessions")
        self.connect()

    def test_identical_final_frames_across_sessions_and_clean_exit(self):
        self.initialize()
        original = read_frame(self.peer)
        records = original[5:-1].decode().split("\r")
        fields = records[0].split("\t")
        self.check_time(fields[2])
        self.assertEqual(fields[:2], ["PARTSUSAGE", "3001"])
        self.assertEqual(fields[3:], ["LINE1", "NXT1", "1", "1", "RECONNECT_DEDUP", "RECON001", "2"])
        self.assertEqual(records[1:], [
            "1\t5\tRES-10K\tRES-10K-AVL\t100\t102\t2\t1\t1\t0\t0\t0",
            "1\t6\tCAP-100N\tCAP-100N-AVL\t50\t50\t0\t0\t0\t0\t0\t0", ""])
        self.next_session()
        # A whole timestamp second passes without initialization or retransmission.
        self.peer.settimeout(1.1)
        with self.assertRaises(socket.timeout):
            self.peer.recv(1)
        self.peer.settimeout(5)
        self.initialize()
        self.assertEqual(original, read_frame(self.peer), "Final wire bytes changed across sessions")
        self.peer.sendall(wire(ACK))
        self.assertEqual(self.peer.recv(1), b"", "Extra events sent or second session left open")
        self.assertEqual(self.process.wait(timeout=5), 0)
        self.reader.join(timeout=2)
        output = "".join(self.lines)
        labels = ["SESSION 1 TX PARTSUSAGE", "SESSION 1 RX ACK", "CONNECTION CLOSED",
                  "SESSION 2 INITIALIZATION COMPLETED", "SESSION 2 TX EXACT DUPLICATE",
                  "SESSION 2 RX ACK", "RECONNECT DEDUP TEST PASSED"]
        positions = []
        for label in labels:
            self.assertEqual(output.count(label), 1, output)
            positions.append(output.index(label))
        self.assertEqual(positions, sorted(positions))
        self.assertNotIn("KEEPALIVE enviado", output)


def bad_ack_case(session, mutation):
    def test(self):
        self.initialize()
        read_frame(self.peer)
        if session == 2:
            self.next_session()
            self.initialize()
            read_frame(self.peer)
        fields = ACK.split("\t")
        if mutation == "extra":
            fields.append("extra")
        elif mutation == "missing":
            fields.pop()
        else:
            fields[mutation] = "1" if mutation == 2 else "WRONG"
        self.peer.sendall(wire("\t".join(fields)))
        self.assertEqual(self.peer.recv(1), b"")
        self.assertEqual(self.process.wait(timeout=5), 1)
        self.reader.join(timeout=2)
        self.assertNotIn("RECONNECT DEDUP TEST PASSED", "".join(self.lines))
    return test


def subscription_case(session, events):
    def test(self):
        if session == 2:
            self.initialize()
            read_frame(self.peer)
            self.next_session()
        self.initialize(events)
        self.assertEqual(self.peer.recv(1), b"")
        self.assertEqual(self.process.wait(timeout=5), 1)
    return test


for session in (1, 2):
    for mutation in (*range(8), "missing", "extra"):
        setattr(ReconnectTests, f"test_bad_ack_{session}_{mutation}", bad_ack_case(session, mutation))
    for index, events in enumerate([[("PARTSUSAGE", 0)], [("LOADCOMPIV", 0)]]):
        setattr(ReconnectTests, f"test_subscription_{session}_{index}", subscription_case(session, events))

# Only collect this module's new tests; existing tests are run separately.
del DedupTests

if __name__ == "__main__":
    unittest.main(verbosity=2)
