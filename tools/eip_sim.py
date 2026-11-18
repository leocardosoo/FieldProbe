#!/usr/bin/env python3
"""
CLP EtherNet/IP (CompactLogix) simulado para testar o FieldProbe (só biblioteca padrão do Python).

Uso:
    python eip_sim.py                    # escuta na porta 44818 (padrão EtherNet/IP), CPU no slot 0
    python eip_sim.py --slot 2           # CPU no slot 2 (o Path no FieldProbe precisa ser 1,2)
    python eip_sim.py -v                 # mostra cada pedido CIP no terminal

O que ele responde:
    - ListIdentity (scanner do FieldProbe): 1769-L33ER CompactLogix, Rockwell Automation;
    - RegisterSession, Forward Open / Large Forward Open, Forward Close;
    - Read Tag (0x4C), Read Tag Fragmented (0x52), Write Tag (0x4D), Write Tag Fragmented (0x53)
      e Multiple Service Packet (0x0A), com ou sem conexão, direto ou via Unconnected Send (Path 1,0).

Tags (no FieldProbe, conexão "EtherNet/IP (Allen-Bradley)", Tipo de CLP ControlLogix, Path 1,0):
    Contador                    DINT    incrementa a cada 0,5 s
    Temperatura                 REAL    senoide entre 60 e 90
    Ligado                      BOOL    alterna a cada 10 s
    Setpoint                    REAL    ESCREVÍVEL (começa em 75,0)
    Valores[0] .. Valores[9]    DINT    array de 10 posições (ESCREVÍVEL)
    Program:MainProgram.Etapa   INT     ESCREVÍVEL

Para ver os erros CIP traduzidos no FieldProbe, monitore de propósito:
    Inexistente          -> CIP 0x05: o tag não existe
    Valores[50]          -> CIP 0xFF/0x2105: índice fora da faixa
    Path 1,3 na conexão  -> CIP 0x01/0x0312: slot inválido no Path
A árvore de tags (navegação) também funciona, inclusive Program:MainProgram.

Comandos no terminal (digite e tecle Enter) para ver o Estado no Diagnóstico do FieldProbe:
    falha  -> falha grave recuperável (major fault)
    leve   -> falha leve recuperável (minor fault)
    ok     -> volta ao normal
"""

import argparse
import math
import random
import socket
import struct
import threading
import time

TYPES = {"BOOL": (0xC1, "<B", 1), "SINT": (0xC2, "<b", 1), "INT": (0xC3, "<h", 2),
         "DINT": (0xC4, "<i", 4), "REAL": (0xCA, "<f", 4)}

IDENTITY = {"status": 0x0060, "state": 3}  # conexões de I/O em RUN · operacional

GENERAL = {0x01: "falha de conexão", 0x04: "erro no caminho/nome", 0x05: "tag não existe",
           0x08: "serviço não suportado", 0x13: "dados insuficientes", 0xFF: "erro geral"}


class Tag:
    def __init__(self, type_name, value, count=1):
        self.type_name = type_name
        self.code, self.fmt, self.size = TYPES[type_name]
        self.values = [value] * count
        self.count = count

    def pack(self, start, count):
        return b"".join(struct.pack(self.fmt, v) for v in self.values[start:start + count])


class Controller:
    def __init__(self):
        self.lock = threading.Lock()
        self.tags = {
            "contador": Tag("DINT", 0),
            "temperatura": Tag("REAL", 75.0),
            "ligado": Tag("BOOL", 0),
            "setpoint": Tag("REAL", 75.0),
            "valores": Tag("DINT", 0, 10),
            "program:mainprogram.etapa": Tag("INT", 1),
        }
        self.names = {"contador": "Contador", "temperatura": "Temperatura", "ligado": "Ligado", "setpoint": "Setpoint",
                      "valores": "Valores", "program:mainprogram.etapa": "Program:MainProgram.Etapa"}
        for i in range(10):
            self.tags["valores"].values[i] = i * 100

    def simulate(self):
        start = time.time()
        while True:
            t = time.time() - start
            with self.lock:
                self.tags["contador"].values[0] = (self.tags["contador"].values[0] + 1) % 2147483647
                self.tags["temperatura"].values[0] = 75 + 15 * math.sin(t / 10) + random.uniform(-0.2, 0.2)
                self.tags["ligado"].values[0] = 1 if int(t // 10) % 2 == 0 else 0
            time.sleep(0.5)


# ===================== CIP =====================

def reply(service, status=0x00, extended=(), data=b""):
    ext = b"".join(struct.pack("<H", e) for e in extended)
    return bytes([service | 0x80, 0x00, status, len(extended)]) + ext + data


def parse_symbolic(path):
    """Segmentos simbólicos (0x91) + elementos (0x28/0x29/0x2A) -> (nome, [índices]) ou None se mal formado."""
    names, indexes, pos = [], [], 0
    while pos < len(path):
        seg = path[pos]
        if seg == 0x91:
            length = path[pos + 1]
            names.append(path[pos + 2:pos + 2 + length].decode("ascii", "replace"))
            pos += 2 + length + (length % 2)
        elif seg == 0x28:
            indexes.append(path[pos + 1])
            pos += 2
        elif seg == 0x29:
            indexes.append(struct.unpack("<H", path[pos + 2:pos + 4])[0])
            pos += 4
        elif seg == 0x2A:
            indexes.append(struct.unpack("<I", path[pos + 2:pos + 6])[0])
            pos += 6
        else:
            return None
    if not names:
        return None
    return ".".join(names), indexes


class CipServer:
    def __init__(self, controller, slot, verbose):
        self.ctl = controller
        self.slot = slot
        self.verbose = verbose
        self.connections = {}  # id O->T (escolhido aqui) -> id T->O (escolhido pelo cliente)

    def log(self, text):
        if self.verbose:
            print("    " + text)

    def route_ok(self, route):
        # Sem segmento de porta (Micro800): mensagem direta para a CPU
        if len(route) == 0 or route[0] == 0x20:
            return True
        # Path esperado: porta 1 (backplane), link = slot da CPU
        return len(route) >= 2 and route[0] == 0x01 and route[1] == self.slot

    def handle(self, msg):
        service = msg[0]
        self.log(f"CIP serviço 0x{service:02X} · caminho {msg[2:2 + msg[1] * 2].hex(' ')}")
        path_words = msg[1]
        path = msg[2:2 + path_words * 2]
        body = msg[2 + path_words * 2:]

        if path == b"\x20\x06\x24\x01":  # Connection Manager
            if service == 0x52:
                return self.unconnected_send(body)
            if service in (0x54, 0x5B):
                return self.forward_open(service, body)
            if service == 0x4E:
                return self.forward_close(body)
            return reply(service, 0x08)

        if service == 0x0A and path == b"\x20\x02\x24\x01":  # Message Router: Multiple Service Packet
            return self.multiple(body)

        if service == 0x55 and b"\x20\x6b" in path:  # Get Instance Attribute List (lista de tags, @tags)
            return self.list_tags(path, body)

        if service in (0x4C, 0x52, 0x4D, 0x53):
            return self.tag_service(service, path, body)

        self.log(f"Serviço 0x{service:02X} não simulado -> CIP 0x08")
        return reply(service, 0x08)

    def list_tags(self, path, body):
        """Lista de símbolos (classe 0x6B) nos atributos pedidos: 1 nome, 2 tipo, 7 tamanho do elemento, 8 dimensões."""
        program = None
        if path[0] == 0x91:  # Program:Nome.@tags
            length = path[1]
            program = path[2:2 + length].decode("ascii", "replace").lower()
        start = struct.unpack("<H", path[-2:])[0] if path[-3] == 0x25 else path[-1]
        count = struct.unpack("<H", body[0:2])[0]
        attributes = [struct.unpack("<H", body[2 + 2 * i:4 + 2 * i])[0] for i in range(count)]

        entries = []
        with self.ctl.lock:
            for name, tag in self.ctl.tags.items():
                display = self.ctl.names[name]
                if program is None and ":" in name:
                    continue
                if program is not None and not name.startswith(program + "."):
                    continue
                short = display.split(".")[-1] if program else display
                symbol_type = tag.code | (0x2000 if tag.count > 1 else 0)
                entries.append((short, symbol_type, tag.size, tag.count if tag.count > 1 else 0))
        if program is None:
            entries.append(("Program:MainProgram", 0x1068, 0, 0))

        data = b""
        for instance, (name, symbol_type, size, dim0) in enumerate(entries, start=1):
            if instance < start:
                continue
            data += struct.pack("<I", instance)
            for attribute in attributes:
                if attribute == 1:
                    raw = name.encode("ascii")
                    data += struct.pack("<H", len(raw)) + raw
                elif attribute == 2:
                    data += struct.pack("<H", symbol_type)
                elif attribute == 7:
                    data += struct.pack("<H", size)
                elif attribute == 8:
                    data += struct.pack("<III", dim0, 0, 0)
        self.log(f"Lista de tags{f' de {program}' if program else ''}: {len(entries)} símbolos (atributos {attributes})")
        return reply(0x55, 0x00, (), data)

    def unconnected_send(self, body):
        size = struct.unpack("<H", body[2:4])[0]
        embedded = body[4:4 + size]
        pos = 4 + size + (size % 2)
        route_words = body[pos]
        route = body[pos + 2:pos + 2 + route_words * 2]
        if not self.route_ok(route):
            self.log(f"Unconnected Send recusado: Path {list(route)} (CPU no slot {self.slot})")
            return reply(0x52, 0x01, (0x0312,), b"\x00")
        return self.handle(embedded)

    def forward_open(self, service, body):
        large = service == 0x5B
        t_o_id = body[6:10]
        serial = body[10:12]
        vendor = body[12:14]
        orig_serial = body[14:18]
        param = 4 if large else 2
        pos = 18 + 4 + 4 + param + 4 + param + 1  # mult+reservado, RPI O->T, params, RPI T->O, params, transporte
        route_words = body[pos]
        route = body[pos + 1:pos + 1 + route_words * 2]
        if not self.route_ok(route):
            self.log(f"Forward Open recusado: Path {list(route)} (CPU no slot {self.slot})")
            return reply(service, 0x01, (0x0312,), b"\x00")
        o_t_id = struct.pack("<I", random.randint(0x10000000, 0x7FFFFFFF))
        self.connections[o_t_id] = t_o_id
        self.log(f"{'Large ' if large else ''}Forward Open aceito")
        data = o_t_id + t_o_id + serial + vendor + orig_serial + struct.pack("<II", 100000, 100000) + b"\x00\x00"
        return reply(service, 0x00, (), data)

    def forward_close(self, body):
        self.log("Forward Close")
        return reply(0x4E, 0x00, (), body[2:4] + body[4:6] + body[6:10] + b"\x00\x00")

    def multiple(self, body):
        count = struct.unpack("<H", body[0:2])[0]
        offsets = [struct.unpack("<H", body[2 + 2 * i:4 + 2 * i])[0] for i in range(count)] + [len(body)]
        replies = [self.handle(body[offsets[i]:offsets[i + 1]]) for i in range(count)]
        status = 0x1E if any(r[2] not in (0x00, 0x06) for r in replies) else 0x00
        header = struct.pack("<H", count)
        offset = 2 + 2 * count
        for r in replies:
            header += struct.pack("<H", offset)
            offset += len(r)
        return reply(0x0A, status, (), header + b"".join(replies))

    def tag_service(self, service, path, body):
        parsed = parse_symbolic(path)
        if parsed is None:
            self.log("Caminho do tag mal formado -> 0x04")
            return reply(service, 0x04, (), b"\x00")
        name, indexes = parsed
        with self.ctl.lock:
            tag = self.ctl.tags.get(name.lower())
            if tag is None:
                self.log(f"'{name}' não existe -> CIP 0x05")
                return reply(service, 0x05, (), b"\x00")
            start = indexes[0] if indexes else 0
            if start >= tag.count:
                self.log(f"'{name}[{start}]' fora da faixa -> CIP 0xFF/0x2105")
                return reply(service, 0xFF, (0x2105,))

            if service in (0x4C, 0x52):  # leitura
                count = max(1, struct.unpack("<H", body[0:2])[0])
                if start + count > tag.count:
                    return reply(service, 0xFF, (0x2105,))
                data = tag.pack(start, count)
                if service == 0x52:
                    offset = struct.unpack("<I", body[2:6])[0]
                    data = data[offset:]
                self.log(f"Leitura '{name}{f'[{start}]' if indexes else ''}' ({tag.type_name} x{count})")
                return reply(service, 0x00, (), struct.pack("<H", tag.code) + data)

            # escrita
            type_code, count = struct.unpack("<HH", body[0:4])
            data = body[8:] if service == 0x53 else body[4:]
            if type_code != tag.code:
                self.log(f"Escrita em '{name}' com tipo 0x{type_code:X2} -> CIP 0xFF/0x2107")
                return reply(service, 0xFF, (0x2107,))
            if len(data) < tag.size * count:
                return reply(service, 0x13)
            for i in range(count):
                if start + i < tag.count:
                    tag.values[start + i] = struct.unpack(tag.fmt, data[i * tag.size:(i + 1) * tag.size])[0]
            print(f"    escrita '{name}{f'[{start}]' if indexes else ''}' = {tag.values[start]}")
            return reply(service, 0x00)


# ===================== Encapsulamento =====================

def list_identity():
    name = b"1769-L33ER/A LOGIX5333ER"
    identity = (struct.pack("<H", 1) + struct.pack(">HHI", 2, 44818, 0x7F000001) + bytes(8)
                + struct.pack("<HHHBBHI", 1, 0x0E, 0x009B, 33, 11, IDENTITY["status"], 0x00C0FFEE)
                + bytes([len(name)]) + name + bytes([IDENTITY["state"]]))
    return struct.pack("<HHH", 1, 0x000C, len(identity)) + identity


def recv_exact(conn, n):
    data = b""
    while len(data) < n:
        chunk = conn.recv(n - len(data))
        if not chunk:
            raise ConnectionError
        data += chunk
    return data


def client_thread(conn, peer, server):
    print(f"[+] Cliente conectado: {peer}")
    session = 0
    try:
        while True:
            header = recv_exact(conn, 24)
            command, length, handle, status = struct.unpack("<HHII", header[:12])
            context = header[12:20]
            data = recv_exact(conn, length) if length else b""
            server.log(f"Encapsulamento 0x{command:04X} ({length} bytes)")

            def send(payload, cmd=command, sess=None, st=0):
                conn.sendall(struct.pack("<HHII", cmd, len(payload), session if sess is None else sess, st)
                             + context + b"\x00\x00\x00\x00" + payload)

            if command == 0x0063:
                send(list_identity(), sess=0)
            elif command == 0x0004:  # ListServices
                svc = struct.pack("<HHHHH", 1, 0x0100, 20, 1, 0x0120) + b"Communications\x00\x00"
                send(svc, sess=0)
            elif command == 0x0064:  # ListInterfaces
                send(struct.pack("<H", 0), sess=0)
            elif command == 0x0065:
                session = random.randint(0x1000, 0xFFFFFF)
                send(data[:4])
            elif command == 0x0066:
                break
            elif command in (0x006F, 0x0070):
                if handle != session:
                    send(b"", st=0x0064)
                    continue
                count = struct.unpack("<H", data[6:8])[0]
                pos, items = 8, []
                for _ in range(count):
                    item_type, item_len = struct.unpack("<HH", data[pos:pos + 4])
                    items.append((item_type, data[pos + 4:pos + 4 + item_len]))
                    pos += 4 + item_len

                if command == 0x006F:  # sem conexão
                    cip = next(d for t, d in items if t == 0x00B2)
                    answer = server.handle(cip)
                    payload = struct.pack("<IHH", 0, 0, 2) + struct.pack("<HH", 0, 0) + struct.pack("<HH", 0x00B2, len(answer)) + answer
                else:  # conectado: endereço 0xA1 + dados 0xB1 (sequência + CIP)
                    connected = next(d for t, d in items if t == 0x00B1)
                    sequence, cip = connected[:2], connected[2:]
                    answer = server.handle(cip)
                    conn_id = next(d for t, d in items if t == 0x00A1)
                    conn_id = server.connections.get(conn_id, conn_id)
                    payload = (struct.pack("<IHH", 0, 0, 2) + struct.pack("<HH", 0x00A1, 4) + conn_id
                               + struct.pack("<HH", 0x00B1, len(answer) + 2) + sequence + answer)
                send(payload)
            else:
                server.log(f"Comando de encapsulamento 0x{command:04X} não simulado")
                send(b"", st=0x0001)
    except (ConnectionError, OSError):
        pass
    except (StopIteration, struct.error, IndexError) as ex:
        print(f"    pedido não entendido pelo simulador: {ex!r}")
    finally:
        conn.close()
        print(f"[-] Cliente desconectado: {peer}")


def console_commands():
    """Comandos do terminal para mudar o estado do controlador simulado."""
    while True:
        try:
            command = input().strip().lower()
        except EOFError:
            return
        if command == "falha":
            IDENTITY.update(status=0x0450, state=4)
            print("    Falha grave recuperável (major fault)")
        elif command == "leve":
            IDENTITY.update(status=0x0160, state=3)
            print("    Falha leve recuperável (minor fault)")
        elif command == "ok":
            IDENTITY.update(status=0x0060, state=3)
            print("    Controlador normal")
        elif command:
            print("    Comandos: falha · leve · ok")


def main():
    parser = argparse.ArgumentParser(description="CLP EtherNet/IP (CompactLogix) simulado para testes")
    parser.add_argument("--host", default="0.0.0.0")
    parser.add_argument("--port", type=int, default=44818)
    parser.add_argument("--slot", type=int, default=0, help="Slot da CPU (Path 1,<slot>)")
    parser.add_argument("-v", "--verbose", action="store_true")
    args = parser.parse_args()

    controller = Controller()
    threading.Thread(target=controller.simulate, daemon=True).start()
    server = CipServer(controller, args.slot, args.verbose)

    sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    sock.bind((args.host, args.port))
    sock.listen()
    print(f"CompactLogix 1769-L33ER simulado em {args.host}:{args.port} · CPU no slot {args.slot} (Path 1,{args.slot}) · Ctrl+C para sair")
    print("Tags: Contador · Temperatura · Ligado · Setpoint (escrita) · Valores[0..9] · Program:MainProgram.Etapa")
    print("Erros de teste: Inexistente (0x05) · Valores[50] (0xFF/0x2105) · Path errado (0x01/0x0312)")
    threading.Thread(target=console_commands, daemon=True).start()
    print("Comandos: falha · leve · ok (digite e tecle Enter)")
    try:
        while True:
            conn, peer = sock.accept()
            conn.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
            threading.Thread(target=client_thread, args=(conn, f"{peer[0]}:{peer[1]}", server), daemon=True).start()
    except KeyboardInterrupt:
        print("\nEncerrado.")


if __name__ == "__main__":
    main()
