#!/usr/bin/env python3
"""
CLP Siemens S7 simulado para testar o FieldProbe (só biblioteca padrão do Python).

Uso:
    python s7_sim.py                    # escuta na porta 102 (padrão S7)
    python s7_sim.py --port 1102        # outra porta (o scanner só procura na 102)
    python s7_sim.py --cpu 1500         # simula uma S7-1500 em vez de S7-1200

O que ele responde:
    - COTP (ISO-on-TCP) e Setup Communication, em qualquer rack/slot;
    - identificação da CPU (SZL 0x0011 e 0x001C): modelo, código de pedido, firmware,
      número de série e nome, igual ao "Accessible devices" do TIA Portal;
    - leitura e escrita de variáveis (PUT/GET) nas áreas DB, M, I e Q.

Variáveis do DB1 (tipo de dado entre parênteses):
    DB1.DBW0    Contador     (INT)   incrementa a cada 0,5 s
    DB1.DBD2    Temperatura  (REAL)  senoide entre 60 e 90
    DB1.DBD6    Pressao      (REAL)  aleatório entre 3 e 5
    DB1.DBX10.0 EmOperacao   (BOOL)  alterna a cada 10 s
    DB1.DBD12   Setpoint     (REAL)  ESCREVÍVEL (começa em 75,0)
    DB1.DBW16   Receita      (INT)   ESCREVÍVEL
    MW10, M0.0, IW0, QW0 ...  memórias livres (começam zeradas)
"""

import argparse
import math
import random
import socket
import struct
import threading
import time

CPUS = {
    "1200": {"type": "CPU 1214C DC/DC/DC", "order": "6ES7 214-1AG40-0XB0", "fw": (4, 5, 0)},
    "1500": {"type": "CPU 1516-3 PN/DP", "order": "6ES7 516-3AN02-0AB0", "fw": (2, 9, 4)},
    "300": {"type": "CPU 315-2 PN/DP", "order": "6ES7 315-2EH14-0AB0", "fw": (3, 2, 17)},
}

AREAS = {0x81: "I", 0x82: "Q", 0x83: "M", 0x84: "DB"}


class PlcMemory:
    def __init__(self):
        self.lock = threading.Lock()
        self.db = {1: bytearray(64)}
        self.areas = {0x81: bytearray(64), 0x82: bytearray(64), 0x83: bytearray(256)}
        struct.pack_into(">f", self.db[1], 12, 75.0)  # Setpoint inicial

    def buffer(self, area, db_number):
        if area == 0x84:
            return self.db.setdefault(db_number, bytearray(64))
        return self.areas.get(area)

    def simulate(self):
        counter = 0
        start = time.time()
        while True:
            t = time.time() - start
            with self.lock:
                db1 = self.db[1]
                counter = (counter + 1) % 32768
                struct.pack_into(">h", db1, 0, counter)
                struct.pack_into(">f", db1, 2, 75 + 15 * math.sin(t / 10))
                struct.pack_into(">f", db1, 6, random.uniform(3, 5))
                running = int(t // 10) % 2 == 0
                db1[10] = (db1[10] & 0xFE) | (1 if running else 0)
            time.sleep(0.5)


def tpkt(payload):
    """TPKT + COTP DT na frente do PDU S7."""
    body = b"\x02\xf0\x80" + payload
    return struct.pack(">BBH", 3, 0, len(body) + 4) + body


def szl_records(szl_id, cpu, name):
    major, minor, patch = cpu["fw"]
    if szl_id == 0x0011:
        def module(index, mlfb, ausbg, ausbe):
            return struct.pack(">H", index) + mlfb.ljust(20).encode() + b"\x00\x00" + ausbg + ausbe
        records = [
            module(1, cpu["order"] + " ", b"\x00\x01", b"\x00\x01"),
            module(6, cpu["order"] + " ", b"\x00\x01", b"\x00\x01"),
            module(7, "", b"V" + bytes([major]), bytes([minor, patch])),
        ]
        return 28, records
    if szl_id == 0x001C:
        def text(index, value):
            return struct.pack(">H", index) + value.encode()[:32].ljust(32, b"\x00")
        records = [
            text(1, name),
            text(2, name),
            text(3, ""),
            text(4, "Original Siemens Equipment"),
            text(5, "S C-X4U421302021"),
            text(7, cpu["type"]),
            text(8, ""),
            text(11, "Linha 3"),
        ]
        return 34, records
    return None, []


def handle_userdata(s7, cpu, name):
    pdu_ref = s7[4:6]
    param_len = struct.unpack(">H", s7[6:8])[0]
    params = s7[10:10 + param_len]
    data = s7[10 + param_len:]
    sequence = params[7] if len(params) > 7 else 0
    if len(params) < 7 or params[5] != 0x44 or params[6] != 0x01 or len(data) < 8:
        return None  # só tratamos "CPU functions · Read SZL"

    szl_id, index = struct.unpack(">HH", data[4:8])
    record_len, records = szl_records(szl_id, cpu, name)
    resp_params = bytes([0x00, 0x01, 0x12, 0x08, 0x12, 0x84, 0x01, sequence, 0x00, 0x00, 0x00, 0x00])
    if record_len is None:
        resp_data = b"\xd5\x00\x00\x00"  # SZL não existe
    else:
        szl = struct.pack(">HHHH", szl_id, index, record_len, len(records)) + b"".join(records)
        resp_data = b"\xff\x09" + struct.pack(">H", len(szl)) + szl
    header = b"\x32\x07\x00\x00" + pdu_ref + struct.pack(">HH", len(resp_params), len(resp_data))
    return header + resp_params + resp_data


def parse_items(params):
    count = params[1]
    items, pos = [], 2
    for _ in range(count):
        item = params[pos:pos + 12]
        pos += 12
        transport = item[3]
        length, db_number = struct.unpack(">HH", item[4:8])
        area = item[8]
        address = int.from_bytes(item[9:12], "big")
        items.append((transport, length, db_number, area, address >> 3, address & 7))
    return items


def handle_job(s7, memory, verbose):
    pdu_ref = s7[4:6]
    param_len, data_len = struct.unpack(">HH", s7[6:10])
    params = s7[10:10 + param_len]
    data = s7[10 + param_len:10 + param_len + data_len]
    function = params[0]

    def ack(resp_params, resp_data=b""):
        return (b"\x32\x03\x00\x00" + pdu_ref + struct.pack(">HH", len(resp_params), len(resp_data))
                + b"\x00\x00" + resp_params + resp_data)

    if function == 0xF0:  # Setup Communication
        return ack(bytes([0xF0, 0x00, 0x00, 0x01, 0x00, 0x01, 0x01, 0xE0]))

    if function == 0x04:  # Read Var
        items = parse_items(params)
        out = b""
        with memory.lock:
            for i, (transport, length, db_number, area, byte, bit) in enumerate(items):
                buf = memory.buffer(area, db_number)
                size = length * (2 if transport in (0x04, 0x05) else 4 if transport in (0x06, 0x07, 0x08) else 1)
                if buf is None or byte + max(size, 1) > len(buf):
                    out += b"\x05\x00\x00\x00"  # endereço fora da faixa
                elif transport == 0x01:  # bit
                    value = (buf[byte] >> bit) & 1
                    out += b"\xff\x03\x00\x01" + bytes([value])
                else:
                    chunk = bytes(buf[byte:byte + size])
                    out += b"\xff\x04" + struct.pack(">H", len(chunk) * 8) + chunk
                if verbose:
                    print(f"    leitura {AREAS.get(area, hex(area))}{db_number if area == 0x84 else ''} byte {byte} ({size} bytes)")
                if len(out) % 2 and i < len(items) - 1:
                    out += b"\x00"
        return ack(bytes([0x04, len(items)]), out)

    if function == 0x05:  # Write Var
        items = parse_items(params)
        results, pos = b"", 0
        with memory.lock:
            for transport, length, db_number, area, byte, bit in items:
                item_transport = data[pos + 1]
                bits = struct.unpack(">H", data[pos + 2:pos + 4])[0]
                size = 1 if item_transport == 0x03 else bits // 8 if item_transport == 0x04 else bits
                value = data[pos + 4:pos + 4 + size]
                pos += 4 + size + (size % 2)
                buf = memory.buffer(area, db_number)
                if buf is None or byte + size > len(buf):
                    results += b"\x05"
                    continue
                if item_transport == 0x03:
                    buf[byte] = (buf[byte] | (1 << bit)) if value[0] else (buf[byte] & ~(1 << bit))
                else:
                    buf[byte:byte + size] = value
                results += b"\xff"
                print(f"    escrita {AREAS.get(area, hex(area))}{db_number if area == 0x84 else ''} byte {byte}: {value.hex(' ')}")
        return ack(bytes([0x05, len(items)]), results)

    return None


def recv_exact(conn, n):
    data = b""
    while len(data) < n:
        chunk = conn.recv(n - len(data))
        if not chunk:
            raise ConnectionError
        data += chunk
    return data


def client_thread(conn, peer, memory, cpu, name, verbose):
    print(f"[+] Cliente conectado: {peer}")
    try:
        while True:
            head = recv_exact(conn, 4)
            length = struct.unpack(">H", head[2:4])[0]
            body = recv_exact(conn, length - 4)

            if body[1] == 0xE0:  # COTP Connection Request -> Connection Confirm
                params = body[7:]
                tsap = params[params.find(b"\xc2\x02") + 2:][:2] if b"\xc2\x02" in params else b"\x01\x01"
                cc = bytes([0x11, 0xD0, 0x00, 0x01, 0x00, 0x01, 0x00,
                            0xC0, 0x01, 0x0A, 0xC1, 0x02, 0x01, 0x00, 0xC2, 0x02]) + tsap
                conn.sendall(struct.pack(">BBH", 3, 0, len(cc) + 4) + cc)
                if verbose:
                    print(f"    COTP aceito (rack 0 / slot {tsap[1] & 0x1F})")
                continue

            s7 = body[3:]
            if len(s7) < 10 or s7[0] != 0x32:
                continue
            if s7[1] == 0x01:
                response = handle_job(s7, memory, verbose)
            elif s7[1] == 0x07:
                response = handle_userdata(s7, cpu, name)
                if verbose and response:
                    print(f"    identificação lida (SZL 0x{struct.unpack('>H', s7[10 + struct.unpack('>H', s7[6:8])[0] + 4:][:2])[0]:04X})")
            else:
                response = None
            if response:
                conn.sendall(tpkt(response))
    except (ConnectionError, OSError, IndexError, struct.error):
        pass
    finally:
        conn.close()
        print(f"[-] Cliente desconectado: {peer}")


def main():
    parser = argparse.ArgumentParser(description="CLP Siemens S7 simulado para testes")
    parser.add_argument("--host", default="0.0.0.0")
    parser.add_argument("--port", type=int, default=102)
    parser.add_argument("--cpu", choices=CPUS.keys(), default="1200", help="Modelo simulado")
    parser.add_argument("--name", default="PLC_Linha3", help="Nome da CPU no projeto")
    parser.add_argument("-v", "--verbose", action="store_true")
    args = parser.parse_args()

    cpu = CPUS[args.cpu]
    memory = PlcMemory()
    threading.Thread(target=memory.simulate, daemon=True).start()

    server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    server.bind((args.host, args.port))
    server.listen()
    major, minor, patch = cpu["fw"]
    print(f"Siemens {cpu['type']} ({cpu['order']} · FW V{major}.{minor}.{patch}) simulado em {args.host}:{args.port} · Ctrl+C para sair")
    print("Variáveis: DB1.DBW0 contador · DB1.DBD2 temperatura · DB1.DBD6 pressão · DB1.DBX10.0 em operação · DB1.DBD12 setpoint (escrita)")

    try:
        while True:
            conn, peer = server.accept()
            conn.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
            threading.Thread(target=client_thread, args=(conn, f"{peer[0]}:{peer[1]}", memory, cpu, args.name, args.verbose),
                             daemon=True).start()
    except KeyboardInterrupt:
        print("\nEncerrado.")


if __name__ == "__main__":
    main()
