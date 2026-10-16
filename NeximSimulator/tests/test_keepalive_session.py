"""Run after building NeximSimulator; requires local port 30040 to be free.
Uses a test TCP client only. Does not start NeximBridge or access SQL.
"""
import pathlib
import socket
import struct
import subprocess
import threading
import time

dll = pathlib.Path(__file__).resolve().parents[1] / "bin/Debug/net8.0/NeximSimulator.dll"
lines = []
process = subprocess.Popen(["dotnet", str(dll)], stdout=subprocess.PIPE,
    stderr=subprocess.STDOUT, text=True, encoding="utf-8", errors="replace",
    creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))


def capture():
    for line in process.stdout:
        lines.append(line)


thread = threading.Thread(target=capture, daemon=True)
thread.start()


def wait_log(text, start=0, timeout=5):
    end = time.monotonic() + timeout
    while time.monotonic() < end:
        if text in "".join(lines[start:]):
            return
        time.sleep(.02)
    raise AssertionError("Missing log " + text + "\n" + "".join(lines))


def send(peer, text):
    payload = b"\x02" + text.encode() + b"\x03"
    peer.sendall(struct.pack(">I", len(payload)) + payload)


def exact(peer, length):
    data = b""
    while len(data) < length:
        part = peer.recv(length - len(data))
        assert part, "Unexpected disconnect"
        data += part
    return data


def read(peer):
    size = struct.unpack(">I", exact(peer, 4))[0]
    assert 2 <= size <= 4096
    payload = exact(peer, size)
    assert payload[0] == 2 and payload[-1] == 3
    return payload[1:-1].decode()


def connect_and_ack_partsusage():
    peer = socket.create_connection(("127.0.0.1", 30040), timeout=130)
    send(peer, "SETEV\t1\t\t10\rPARTSUSAGE\t1\rPDERROR\t1\rPRODSTARTED\t1\rPRODCOMPLETEDII\t1\rPRODSTOPPED\t1\rLOADCOMPIV\t0\rCHANGECOMPII\t0\rUNLOADCOMP\t0\rMCALARMON\t1\rMCALARMOFF\t1\r")
    assert read(peer) == "SETEV_ACK\t1\t\t0"
    send(peer, "STARTEV\t2\t")
    assert read(peer) == "STARTEV_ACK\t2\t\t0"
    assert read(peer).startswith("PARTSUSAGE\t1001\t")
    send(peer, "PARTSUSAGE_ACK\t1001\t0\tNXT1\t1\t1\tTESTJOB_T\t10001")
    assert read(peer).startswith("PDERROR\t1002\t")
    send(peer, "PDERROR_ACK\t1002\t0\tNXT1\t1\t1\tTESTJOB_T")
    assert read(peer).startswith("PRODSTARTED\t1003\t")
    send(peer, "PRODSTARTED_ACK\t1003\t0\tNXT1\t1\t1\t0\tTESTJOB_T\t10002")
    assert read(peer).startswith("PRODCOMPLETEDII\t1004\t")
    send(peer, "PRODCOMPLETEDII_ACK\t1004\t0\tNXT1\t1\t1\t0\tTESTJOB_T\t10002")
    assert read(peer).startswith("PRODSTOPPED\t1005\t")
    send(peer, "PRODSTOPPED_ACK\t1005\t0\tNXT1\t1\t1\t0\tTESTJOB_T\t10003\t5\t1")
    assert read(peer).startswith("LOADCOMPIV\t1006\t")
    assert read(peer).startswith("CHANGECOMPII\t1007\t")
    assert read(peer).startswith("UNLOADCOMP\t1008\t")
    assert read(peer).startswith("MCALARMON\t1009\t")
    send(peer, "MCALARMON_ACK\t1009\t0\tNXT1\t1")
    assert read(peer).startswith("MCALARMOFF\t1010\t")
    send(peer, "MCALARMOFF_ACK\t1010\t0\tNXT1\t1")
    return peer


try:
    wait_log("escuchando")
    with connect_and_ack_partsusage() as peer:
        wait_log("PARTSUSAGE_ACK recibido")
        wait_log("mantenida")
        for seq in (1011, 1012):
            started = time.monotonic()
            assert read(peer) == f"KEEPALIVE\t{seq}", "Unexpected repeated PARTSUSAGE or wrong heartbeat"
            assert time.monotonic() - started >= 119.5, "KEEPALIVE interval is not 120 seconds"
            send(peer, f"KEEPALIVE_ACK\t{seq}")
            wait_log(f"KEEPALIVE_ACK validado: SeqID={seq}")
        assert sum("TX PARTSUSAGE:" in line for line in lines) == 1
        checkpoint = len(lines)
    wait_log("cerrada", checkpoint, timeout=2)
    print("PASS persistent session, two KEEPALIVEs, one PARTSUSAGE, idle disconnect", flush=True)

    with connect_and_ack_partsusage() as peer:
        assert read(peer) == "KEEPALIVE\t1011"
        checkpoint = len(lines)
        send(peer, "KEEPALIVE_ACK\t9999")
        assert peer.recv(1) == b"", "Invalid SeqID accepted"
        wait_log("se esperaba KEEPALIVE_ACK y SeqID=1011", checkpoint)
    print("PASS incorrect KEEPALIVE_ACK rejected; new client accepted", flush=True)

    with connect_and_ack_partsusage() as peer:
        assert read(peer) == "KEEPALIVE\t1011"
        checkpoint = len(lines)
        peer.settimeout(35)
        assert peer.recv(1) == b"", "ACK timeout not enforced"
        wait_log("Timeout de 30 segundos esperando KEEPALIVE_ACK", checkpoint)
    print("PASS missing KEEPALIVE_ACK times out cleanly", flush=True)
finally:
    if process.poll() is None:
        process.terminate()
    process.wait(timeout=5)
    thread.join(timeout=2)
    process.stdout.close()
