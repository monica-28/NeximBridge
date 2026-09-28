"""Loopback integration tests; only starts NeximSimulator, never Bridge or SQL.
Run after dotnet build: python NeximSimulator/tests/test_prodcompletedii.py
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
ACK = ["PRODCOMPLETEDII_ACK", "1004", "0", "NXT1", "1", "1", "0", "TESTJOB_T", "10002"]


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
            peer.sendall(wire("SETEV\t1\t\t5\rPARTSUSAGE\t1\rPDERROR\t1\rPRODSTARTED\t1\rPRODCOMPLETEDII\t1\rPRODSTOPPED\t1\r"))
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
            assert read(peer).startswith("PARTSUSAGE\t1001\t")
            peer.settimeout(0.15)
            try:
                raise AssertionError(f"PDERROR before PARTSUSAGE_ACK: {peer.recv(1)!r}")
            except socket.timeout:
                pass
            peer.settimeout(5)
            peer.sendall(wire("PARTSUSAGE_ACK\t1001\t0\tNXT1\t1\t1\tTESTJOB_T\t10001"))
            assert read(peer).startswith("PDERROR\t1002\t")
            peer.settimeout(0.15)
            try:
                raise AssertionError(f"PRODSTARTED before PDERROR_ACK: {peer.recv(1)!r}")
            except socket.timeout:
                pass
            peer.settimeout(5)
            peer.sendall(wire("PDERROR_ACK\t1002\t0\tNXT1\t1\t1\tTESTJOB_T"))
            assert read(peer).startswith("PRODSTARTED\t1003\t")
            peer.settimeout(0.15)
            try:
                raise AssertionError(f"PRODCOMPLETEDII before PRODSTARTED_ACK: {peer.recv(1)!r}")
            except socket.timeout:
                pass
            peer.settimeout(5)
            peer.sendall(wire("PRODSTARTED_ACK\t1003\t0\tNXT1\t1\t1\t0\tTESTJOB_T\t10002"))
            message = read(peer)
            assert "\r" not in message
            header = message.split("\t")
            assert len(header) == 14
            assert header[:2] == ["PRODCOMPLETEDII", "1004"]
            assert header[3:] == ["LINE1", "NXT1", "1", "1", "0", "TESTJOB_T", "10002", "12", "2", "A200", "45000"]
            assert len(header[2]) == 14 and header[2].isascii() and header[2].isdigit()
            sent_at = datetime.datetime.strptime(header[2], "%Y%m%d%H%M%S").replace(tzinfo=datetime.timezone.utc)
            assert abs((datetime.datetime.now(datetime.timezone.utc) - sent_at).total_seconds()) < 5
            if response is not None:
                # Deliberately fragment the ACK header and payload.
                for value in wire(response):
                    peer.sendall(bytes([value]))
            deadline = time.monotonic() + (23 if response is None else 3)
            while expected_log not in "".join(lines) and time.monotonic() < deadline:
                time.sleep(0.02)
            output = "".join(lines)
            assert expected_log in output, output
            assert output.count("TX PRODCOMPLETEDII:") == 1, output
            if response is not None:
                assert "RX PRODCOMPLETEDII_ACK: STX " in output, output
            if expected_log != "PRODCOMPLETEDII_ACK validado":
                assert "PRODCOMPLETEDII_ACK validado" not in output, output
            else:
                assert read(peer).startswith("PRODSTOPPED\t1005\t")
                peer.sendall(wire("PRODSTOPPED_ACK\t1005\t0\tNXT1\t1\t1\t0\tTESTJOB_T\t10003\t5\t1"))
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


run_case("official frame, UTC, fourteen fields, no CR, sequencing, valid fragmented ACK", "\t".join(ACK), "PRODCOMPLETEDII_ACK validado")
for index, field in enumerate(["EventName", "SeqID", "Result", "MachineName", "ModuleNo", "LaneNo", "ProductMode", "ProgramName", "PanelNo"]):
    changed = ACK.copy()
    changed[index] = "1" if field == "Result" else "WRONG"
    run_case("reject " + field, "\t".join(changed), field + " esperado")
run_case("reject missing field", "\t".join(ACK[:-1]), "se requieren 9 campos")
run_case("reject extra field", "\t".join(ACK + ["extra"]), "se requieren 9 campos")
run_case("ACK timeout", None, "Timeout de 20 segundos esperando PRODCOMPLETEDII_ACK")
print("13/13 passed; no Bridge or SQL access.")
