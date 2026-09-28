"""Loopback integration tests; only starts NeximSimulator, never Bridge or SQL.
Run after dotnet build: python NeximSimulator/tests/test_partsusage.py
Requires port 30040 to be free. Each case terminates only its own simulator process.
"""

import datetime
import pathlib
import socket
import struct
import subprocess
import threading
import time

DLL = pathlib.Path(__file__).resolve().parents[1] / "bin/Debug/net8.0/NeximSimulator.dll"
ACK = ["PARTSUSAGE_ACK", "1001", "0", "NXT1", "1", "1", "TESTJOB_T", "10001"]


def wire(message):
    payload = b"\x02" + message.encode("utf-8") + b"\x03"
    return struct.pack(">I", len(payload)) + payload


def exact(peer, size):
    data = b""
    while len(data) < size:
        part = peer.recv(size - len(data))
        assert part, "Unexpected disconnect"
        data += part
    return data


def read(peer):
    size = struct.unpack(">I", exact(peer, 4))[0]
    assert 2 <= size <= 4096, size
    payload = exact(peer, size)
    assert payload[0] == 2 and payload[-1] == 3
    return payload[1:-1].decode("utf-8")


def run_case(name, response, expected_log):
    lines = []
    ready = threading.Event()
    process = subprocess.Popen(
        ["dotnet", str(DLL)], stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
        text=True, encoding="utf-8", errors="replace",
        creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
    )

    def capture():
        for line in process.stdout:
            lines.append(line)
            if "escuchando" in line:
                ready.set()

    reader = threading.Thread(target=capture, daemon=True)
    reader.start()
    try:
        assert ready.wait(5), "Simulator did not start (port 30040 must be free): " + "".join(lines)
        with socket.create_connection(("127.0.0.1", 30040), timeout=5) as peer:
            peer.sendall(wire("SETEV\t1\t\t1\rPARTSUSAGE\t1\r"))
            assert read(peer) == "SETEV_ACK\t1\t\t0"
            peer.settimeout(0.15)
            try:
                unexpected = peer.recv(1)
                raise AssertionError(f"Data before STARTEV: {unexpected!r}")
            except socket.timeout:
                pass
            peer.settimeout(5)
            peer.sendall(wire("STARTEV\t2\t"))
            assert read(peer) == "STARTEV_ACK\t2\t\t0"
            started = time.monotonic()
            message = read(peer)
            assert time.monotonic() - started >= 1.9, "Missing two-second delay"
            records = message.split("\r")
            assert len(records) == 4 and records[-1] == "", repr(message)
            header = records[0].split("\t")
            assert len(header) == 10
            assert header[:2] == ["PARTSUSAGE", "1001"]
            assert header[3:] == ["LINE1", "NXT1", "1", "1", "TESTJOB_T", "10001", "2"]
            assert len(header[2]) == 14 and header[2].isascii() and header[2].isdigit()
            sent_at = datetime.datetime.strptime(header[2], "%Y%m%d%H%M%S").replace(tzinfo=datetime.timezone.utc)
            assert abs((datetime.datetime.now(datetime.timezone.utc) - sent_at).total_seconds()) < 5
            assert records[1] == "1\t5\tRES-10K\tRES-10K-AVL\t100\t102\t2\t1\t1\t0\t0\t0"
            assert records[2] == "1\t6\tCAP-100N\tCAP-100N-AVL\t50\t50\t0\t0\t0\t0\t0\t0"
            if response is not None:
                # Deliberately fragment the ACK header and payload.
                for value in wire(response):
                    peer.sendall(bytes([value]))
            deadline = time.monotonic() + (23 if response is None else 3)
            while expected_log not in "".join(lines) and time.monotonic() < deadline:
                time.sleep(0.02)
            output = "".join(lines)
            assert expected_log in output, output
            assert output.count("TX PARTSUSAGE:") == 1, output
            if response is not None:
                assert "RX: STX " in output, output
            if expected_log != "PARTSUSAGE_ACK validado":
                assert "PARTSUSAGE_ACK validado" not in output, output
            else:
                peer.settimeout(0.2)
                try:
                    extra = peer.recv(1)
                    raise AssertionError(f"Unexpected data after ACK: {extra!r}")
                except socket.timeout:
                    pass
        print("PASS", name, flush=True)
    finally:
        if process.poll() is None:
            process.terminate()
        process.wait(timeout=5)
        reader.join(timeout=2)
        process.stdout.close()


run_case("official frame, UTC, two records, delay, valid fragmented ACK", "\t".join(ACK), "PARTSUSAGE_ACK validado")
for index, field in enumerate(["EventName", "SeqID", "Result", "MachineName", "ModuleNo", "LaneNo", "ProgramName", "PanelNo"]):
    changed = ACK.copy()
    changed[index] = "1" if field == "Result" else "WRONG"
    run_case("reject " + field, "\t".join(changed), field + " esperado")
run_case("reject missing field", "\t".join(ACK[:-1]), "se requieren 8 campos")
run_case("reject extra field", "\t".join(ACK + ["extra"]), "se requieren 8 campos")
run_case("ACK timeout", None, "Timeout de 20 segundos esperando PARTSUSAGE_ACK")
print("12/12 passed; no Bridge or SQL access.")
