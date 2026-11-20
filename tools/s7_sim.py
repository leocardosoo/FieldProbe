#!/usr/bin/env python3
"""
CLP Siemens S7 simulado para testar o FieldProbe (só biblioteca padrão do Python).

Uso:
    python s7_sim.py                    # escuta na porta 102 (padrão S7)
    python s7_sim.py --port 1102        # outra porta (o scanner só procura na 102)
    python s7_sim.py --cpu 1500         # simula uma S7-1500 em vez de S7-1200
    python s7_sim.py --dbs 1,10,100     # DBs que existem no "programa" (padrão: só o DB1)
    python s7_sim.py --otimizado 10     # DB10 existe, mas com "Optimized block access" (recusa PUT/GET)
    python s7_sim.py --sem-putget       # CPU com PUT/GET desabilitado (recusa toda leitura/escrita)

O que ele responde:
    - COTP (ISO-on-TCP) e Setup Communication, em qualquer rack/slot;
    - identificação da CPU (SZL 0x0011 e 0x001C): modelo, código de pedido, firmware,
      número de série e nome, igual ao "Accessible devices" do TIA Portal;
    - modo de operação RUN/STOP (SZL 0x0424) e buffer de diagnóstico (SZL 0x01A0);
    - leitura e escrita de variáveis (PUT/GET) nas áreas DB, M, I e Q.

Como num CLP real, só existem os DBs criados no "programa" (--dbs), cada um com o seu tamanho:
    DB que não existe         -> erro "objeto não existe" (0x0A)
    offset além do fim do DB  -> erro "endereço fora da faixa" (0x05)
    DB otimizado / sem PUT/GET -> erro "acesso negado" (0x03)

Comandos no terminal enquanto ele roda (digite e tecle Enter):
    stop   -> CPU vai para STOP (os valores param de mudar)
    run    -> CPU volta para RUN
    erro   -> simula falha num módulo de I/O: grava o erro no buffer e a CPU vai para STOP

Variáveis do DB1 (tipo de dado entre parênteses):
    DB1.DBW0    Contador     (INT)   incrementa a cada 0,5 s
    DB1.DBD2    Temperatura  (REAL)  senoide entre 60 e 90
    DB1.DBD6    Pressao      (REAL)  aleatório entre 3 e 5
    DB1.DBX10.0 EmOperacao   (BOOL)  alterna a cada 10 s
    DB1.DBD12   Setpoint     (REAL)  ESCREVÍVEL (começa em 75,0)
    DB1.DBW16   Receita      (INT)   ESCREVÍVEL
    (o DB1 tem 18 bytes: DB1.DBW18 ou DB1.DBD16 já dão "fora da faixa")
    Outros DBs de --dbs: 64 bytes zerados.
    MW0..MW254, M0.0.., IW0..IW62, QW0..QW62: memórias que começam zeradas
"""

import argparse
import datetime
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


def bcd_time(moment):
    """DATE_AND_TIME do S7 (8 bytes BCD)."""
    def b(v):
        return ((v // 10) << 4) | (v % 10)
    ms = moment.microsecond // 1000
    weekday = moment.isoweekday() % 7 + 1
    return bytes([b(moment.year % 100), b(moment.month), b(moment.day), b(moment.hour), b(moment.minute),
                  b(moment.second), b(ms // 10), ((ms % 10) << 4) | weekday])


class CpuState:
    """Modo de operação e buffer de diagnóstico simulados."""

    def __init__(self):
        self.lock = threading.Lock()
        self.running = True
        now = datetime.datetime.now()
        self.events = [(0x4302, now), (0x4301, now - datetime.timedelta(seconds=2))]  # mais recente primeiro

    def add(self, event_id):
        with self.lock:
            self.events.insert(0, (event_id, datetime.datetime.now()))
            del self.events[50:]

    def set_running(self, running, event_ids):
        for event_id in event_ids:
            self.add(event_id)
        with self.lock:
            self.running = running


CPU_STATE = CpuState()


# Códigos de retorno por item (S7 Read/Write Var), iguais aos de uma CPU real
RC_OK = 0xFF
RC_ACCESS_DENIED = 0x03   # objeto não acessível (DB otimizado, PUT/GET desabilitado)
RC_OUT_OF_RANGE = 0x05    # endereço além do tamanho do DB/área
RC_NOT_EXIST = 0x0A       # DB não existe


class PlcMemory:
    def __init__(self, dbs=(1,), optimized=(), putget=True):
        self.lock = threading.Lock()
        self.db = {}
        for number in dbs:
            self.db[number] = bytearray(18 if number == 1 else 64)  # DB1: só as variáveis declaradas
        self.db.setdefault(1, bytearray(18))                          # o "programa" sempre usa o DB1
        self.optimized = set(optimized)
        self.putget = putget
        self.areas = {0x81: bytearray(64), 0x82: bytearray(64), 0x83: bytearray(256)}
        struct.pack_into(">f", self.db[1], 12, 75.0)  # Setpoint inicial

    def locate(self, area, db_number, byte, size):
        """Devolve (código de retorno, buffer) como uma CPU real faria."""
        if not self.putget:
            return RC_ACCESS_DENIED, None
        if area == 0x84:
            buf = self.db.get(db_number)
            if buf is None:
                return RC_NOT_EXIST, None
            if db_number in self.optimized:
                return RC_ACCESS_DENIED, None
        else:
            buf = self.areas.get(area)
            if buf is None:
                return RC_NOT_EXIST, None
        if byte + max(size, 1) > len(buf):
            return RC_OUT_OF_RANGE, None
        return RC_OK, buf

    def simulate(self):
        counter = 0
        start = time.time()
        while True:
            if not CPU_STATE.running:  # em STOP o programa não roda: valores congelados
                time.sleep(0.5)
                continue
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


def szl_records(szl_id, cpu, name, index=0):
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
    if szl_id == 0x0424:
        with CPU_STATE.lock:
            running = CPU_STATE.running
            last_id, last_time = CPU_STATE.events[0]
        mode = 0x08 if running else 0x04
        previous = 0x04 if running else 0x08
        record = struct.pack(">HBB", last_id, 0xFF, (previous << 4) | mode) + bytes(8) + bcd_time(last_time)
        return 20, [record]
    if szl_id in (0x00A0, 0x01A0):
        with CPU_STATE.lock:
            events = list(CPU_STATE.events)
        if szl_id == 0x01A0:
            events = events[:max(1, index)]
        records = [struct.pack(">HBBHHI", eid, 0x01, 0x01, 0, 0, 0) + bcd_time(when) for eid, when in events]
        return 20, records
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
    record_len, records = szl_records(szl_id, cpu, name, index)
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
                size = length * (2 if transport in (0x04, 0x05) else 4 if transport in (0x06, 0x07, 0x08) else 1)
                code, buf = memory.locate(area, db_number, byte, size)
                where = f"{AREAS.get(area, hex(area))}{db_number if area == 0x84 else ''} byte {byte}"
                if code != RC_OK:
                    out += bytes([code, 0x00, 0x00, 0x00])
                    # varredura (vários itens por pedido, ex.: "Procurar DBs"): só mostra o que não for "não existe"
                    if verbose or len(items) == 1 or code != RC_NOT_EXIST:
                        print(f"    leitura {where} recusada: {ERRORS[code]}")
                elif transport == 0x01:  # bit
                    value = (buf[byte] >> bit) & 1
                    out += b"\xff\x03\x00\x01" + bytes([value])
                else:
                    chunk = bytes(buf[byte:byte + size])
                    out += b"\xff\x04" + struct.pack(">H", len(chunk) * 8) + chunk
                if verbose and code == RC_OK:
                    print(f"    leitura {where} ({size} bytes)")
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
                code, buf = memory.locate(area, db_number, byte, size)
                if code != RC_OK:
                    results += bytes([code])
                    print(f"    escrita {AREAS.get(area, hex(area))}{db_number if area == 0x84 else ''} byte {byte} recusada: {ERRORS[code]}")
                    continue
                if item_transport == 0x03:
                    buf[byte] = (buf[byte] | (1 << bit)) if value[0] else (buf[byte] & ~(1 << bit))
                else:
                    buf[byte:byte + size] = value
                results += b"\xff"
                print(f"    escrita {AREAS.get(area, hex(area))}{db_number if area == 0x84 else ''} byte {byte}: {value.hex(' ')}")
        return ack(bytes([0x05, len(items)]), results)

    return None


ERRORS = {RC_ACCESS_DENIED: "acesso negado (0x03)", RC_OUT_OF_RANGE: "fora da faixa (0x05)", RC_NOT_EXIST: "objeto não existe (0x0A)"}


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


def console_commands():
    """Lê comandos do terminal para mudar o estado da CPU simulada."""
    while True:
        try:
            command = input().strip().lower()
        except EOFError:
            return
        if command == "stop":
            CPU_STATE.set_running(False, [0x4304])
            print("    CPU em STOP (comando do PG)")
        elif command == "run":
            CPU_STATE.set_running(True, [0x4301, 0x4302])
            print("    CPU em RUN")
        elif command == "erro":
            CPU_STATE.set_running(False, [0x2942, 0x4563])
            print("    Falha de acesso à periferia registrada · CPU em STOP")
        elif command:
            print("    Comandos: stop · run · erro")


def main():
    parser = argparse.ArgumentParser(description="CLP Siemens S7 simulado para testes")
    parser.add_argument("--host", default="0.0.0.0")
    parser.add_argument("--port", type=int, default=102)
    parser.add_argument("--cpu", choices=CPUS.keys(), default="1200", help="Modelo simulado")
    parser.add_argument("--name", default="PLC_Linha3", help="Nome da CPU no projeto")
    parser.add_argument("--dbs", default="1", help="DBs que existem, ex.: 1,10,100 (padrão: 1)")
    parser.add_argument("--otimizado", default="", help="DBs com acesso otimizado (recusam PUT/GET), ex.: 10")
    parser.add_argument("--sem-putget", action="store_true", help="CPU com PUT/GET desabilitado")
    parser.add_argument("-v", "--verbose", action="store_true")
    args = parser.parse_args()

    def numbers(text):
        return [int(x) for x in text.replace(";", ",").split(",") if x.strip()]

    cpu = CPUS[args.cpu]
    memory = PlcMemory(numbers(args.dbs), numbers(args.otimizado), not args.sem_putget)
    threading.Thread(target=memory.simulate, daemon=True).start()

    server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    server.bind((args.host, args.port))
    server.listen()
    major, minor, patch = cpu["fw"]
    print(f"Siemens {cpu['type']} ({cpu['order']} · FW V{major}.{minor}.{patch}) simulado em {args.host}:{args.port} · Ctrl+C para sair")
    print("Variáveis: DB1.DBW0 contador · DB1.DBD2 temperatura · DB1.DBD6 pressão · DB1.DBX10.0 em operação · DB1.DBD12 setpoint (escrita)")
    dbs = ", ".join(f"DB{n} ({len(b)} bytes{', otimizado' if n in memory.optimized else ''})" for n, b in sorted(memory.db.items()))
    print(f"DBs no programa: {dbs}{' · PUT/GET DESABILITADO' if not memory.putget else ''}")

    threading.Thread(target=console_commands, daemon=True).start()
    print("Comandos: stop · run · erro (digite e tecle Enter)")

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
