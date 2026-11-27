#!/usr/bin/env python3
"""
CLP Mitsubishi simulado: MC Protocol (SLMP), frame 3E binário sobre TCP.
Serve para testar o driver "Mitsubishi MC (SLMP)" do FieldProbe sem CLP. Só Python 3.8+.

Uso:
    python mc_sim.py                    # porta 5000, CPU Q03UDE
    python mc_sim.py --port 5007        # outra porta
    python mc_sim.py --serie iqr        # CPU iQ-R (R04CPU)
    python mc_sim.py --serie iqf        # CPU iQ-F (FX5U): no FieldProbe escolha a série iQ-F (X/Y octais)
    python mc_sim.py --sem-escrita-run  # recusa escrita com a CPU em RUN (erro 0x0055, como na CPU real)
    python mc_sim.py --mapa             # só mostra o mapa de variáveis e sai
    python mc_sim.py -v                 # mostra cada pedido

Comandos no terminal (Enter depois):
    stop    -> CPU em STOP (SD203 = 2, valores congelam)
    run     -> CPU em RUN
    erro    -> grava um código de erro no SD0 (aparece no Diagnóstico)
    limpar  -> zera o SD0
    sair    -> encerra
"""

import argparse
import math
import random
import socket
import struct
import threading
import time

# Código do dispositivo -> (símbolo, é bit?, quantidade de pontos)
DEVICES = {
    0x91: ("SM", True, 4096), 0xA9: ("SD", False, 4096),
    0x9C: ("X", True, 8192), 0x9D: ("Y", True, 8192),
    0x90: ("M", True, 8192), 0x92: ("L", True, 8192), 0x93: ("F", True, 2048), 0x94: ("V", True, 2048),
    0xA0: ("B", True, 8192), 0xA1: ("SB", True, 2048),
    0xA8: ("D", False, 12288), 0xB4: ("W", False, 8192), 0xB5: ("SW", False, 2048),
    0xAF: ("R", False, 32768), 0xCC: ("Z", False, 20),
    0xC1: ("TS", True, 1024), 0xC0: ("TC", True, 1024), 0xC2: ("TN", False, 1024),
    0xC7: ("SS", True, 1024), 0xC6: ("SC", True, 1024), 0xC8: ("SN", False, 1024),
    0xC4: ("CS", True, 1024), 0xC3: ("CC", True, 1024), 0xC5: ("CN", False, 1024),
}
HEX_DEVICES = {"X", "Y", "B", "W", "SB", "SW"}

MODELS = {
    "q": ("Q03UDECPU", 0x0268),
    "iqr": ("R04CPU", 0x4800),
    "iqf": ("FX5U-32MR/ES", 0x4A21),
}

ERR_POINTS = 0xC051
ERR_RANGE = 0xC056
ERR_LENGTH = 0xC058
ERR_COMMAND = 0xC059
ERR_BIT_ON_WORD = 0xC05C
ERR_WRITE_IN_RUN = 0x0055

MAX_WORDS = 960
MAX_BITS = 3584


class Memory:
    def __init__(self):
        self.lock = threading.Lock()
        self.words = {code: [0] * size for code, (_, is_bit, size) in DEVICES.items() if not is_bit}
        self.bits = {code: [False] * size for code, (_, is_bit, size) in DEVICES.items() if is_bit}
        self.running = True

    # ---- palavras (bit devices lidos em palavras: 16 pontos por palavra) ----
    def read_words(self, code, start, count):
        sym, is_bit, size = DEVICES[code]
        if is_bit:
            if start + count * 16 > size:
                return None
            out = []
            for w in range(count):
                value = 0
                for b in range(16):
                    if self.bits[code][start + w * 16 + b]:
                        value |= 1 << b
                out.append(value)
            return out
        if start + count > size:
            return None
        return self.words[code][start:start + count]

    def write_words(self, code, start, values):
        sym, is_bit, size = DEVICES[code]
        if is_bit:
            if start + len(values) * 16 > size:
                return False
            for w, value in enumerate(values):
                for b in range(16):
                    self.bits[code][start + w * 16 + b] = bool(value >> b & 1)
            return True
        if start + len(values) > size:
            return False
        self.words[code][start:start + len(values)] = values
        return True

    def read_bits(self, code, start, count):
        if start + count > DEVICES[code][2]:
            return None
        return self.bits[code][start:start + count]

    def write_bits(self, code, start, values):
        if start + len(values) > DEVICES[code][2]:
            return False
        self.bits[code][start:start + len(values)] = values
        return True

    # ---- atalhos para o mapa ----
    def set_float(self, code, n, value):
        lo, hi = struct.unpack("<HH", struct.pack("<f", value))
        self.words[code][n] = lo
        self.words[code][n + 1] = hi

    def set_dint(self, code, n, value):
        lo, hi = struct.unpack("<HH", struct.pack("<i", value))
        self.words[code][n] = lo
        self.words[code][n + 1] = hi

    def get_dint(self, code, n):
        return struct.unpack("<i", struct.pack("<HH", self.words[code][n], self.words[code][n + 1]))[0]

    def set_string(self, code, n, text, words):
        data = text.encode("ascii")[: words * 2].ljust(words * 2, b"\0")
        for i in range(words):
            self.words[code][n + i] = data[i * 2] | (data[i * 2 + 1] << 8)


D, M, X, Y, SM, SD = 0xA8, 0x90, 0x9C, 0x9D, 0x91, 0xA9

MAP = [
    ("D0", "Int16", "contador de ciclos (sobe 1 por segundo em RUN)"),
    ("D2", "Int16", "velocidade em rpm (escreva para mudar; 0 a 3000)"),
    ("D10", "Float32", "temperatura do óleo em °C (usa D10 e D11)"),
    ("D20", "Int32", "produção total (usa D20 e D21)"),
    ("D30", "String", "nome da máquina, 8 caracteres (D30 a D33)"),
    ("D100", "Int16", "setpoint livre (leitura e escrita)"),
    ("D100.0", "Bool", "bit 0 do D100 (teste de bit de palavra)"),
    ("M0", "Bool", "motor ligado (liga e desliga sozinho a cada 5 s; pode escrever)"),
    ("M10", "Bool", "comando livre (leitura e escrita)"),
    ("X0-X7", "Bool", "sensores (aleatórios)"),
    ("Y0", "Bool", "saída do motor (segue o M0)"),
    ("SM400", "Bool", "sempre ligado em RUN"),
    ("SM412", "Bool", "pulso de 1 s"),
    ("SD0", "Int16", "código do último erro (terminal: 'erro' / 'limpar')"),
    ("SD203", "Int16", "estado da CPU: 0 = RUN, 2 = STOP"),
]


def simulate(mem, stop_event):
    start = time.time()
    last_second = int(start)
    while not stop_event.is_set():
        now = time.time()
        with mem.lock:
            mem.words[SD][203] = 0 if mem.running else 2
            mem.bits[SM][400] = mem.running
            if mem.running:
                t = now - start
                second = int(now)
                mem.bits[SM][412] = (second % 2) == 0
                if second != last_second:
                    last_second = second
                    mem.words[D][0] = (mem.words[D][0] + 1) & 0xFFFF
                    if mem.bits[M][0]:
                        mem.set_dint(D, 20, mem.get_dint(D, 20) + max(1, mem.words[D][2] // 600))
                    for i in range(8):
                        mem.bits[X][i] = random.random() < 0.5
                    if second % 5 == 0:
                        mem.bits[M][0] = not mem.bits[M][0]
                mem.set_float(D, 10, 60.0 + 10.0 * math.sin(t / 8.0) + random.uniform(-0.3, 0.3))
                mem.bits[Y][0] = mem.bits[M][0]
        time.sleep(0.1)


def handle_request(mem, frame, args):
    """Recebe o pedido completo (a partir do subcabeçalho) e devolve (end_code, dados)."""
    command, sub = struct.unpack_from("<HH", frame, 11)
    body = frame[15:]

    if command == 0x0101:
        name, code = MODELS[args.serie]
        return 0, name.encode("ascii").ljust(16, b" ") + struct.pack("<H", code)

    if command not in (0x0401, 0x1401):
        return ERR_COMMAND, b""
    if sub not in (0x0000, 0x0001):
        return ERR_COMMAND, b""
    if len(body) < 6:
        return ERR_LENGTH, b""

    number = body[0] | (body[1] << 8) | (body[2] << 16)
    code = body[3]
    points = struct.unpack_from("<H", body, 4)[0]
    if code not in DEVICES:
        return 0xC05B, b""
    sym, is_bit, _ = DEVICES[code]
    bit_mode = sub == 0x0001
    if points == 0 or points > (MAX_BITS if bit_mode else MAX_WORDS):
        return ERR_POINTS, b""
    if bit_mode and not is_bit:
        return ERR_BIT_ON_WORD, b""

    data = body[6:]
    with mem.lock:
        if command == 0x0401:
            if bit_mode:
                bits = mem.read_bits(code, number, points)
                if bits is None:
                    return ERR_RANGE, b""
                out = bytearray((points + 1) // 2)
                for i, on in enumerate(bits):
                    if on:
                        out[i // 2] |= 0x10 if i % 2 == 0 else 0x01
                return 0, bytes(out)
            words = mem.read_words(code, number, points)
            if words is None:
                return ERR_RANGE, b""
            return 0, struct.pack("<%dH" % points, *words)

        # escrita
        if args.sem_escrita_run and mem.running:
            return ERR_WRITE_IN_RUN, b""
        if bit_mode:
            if len(data) != (points + 1) // 2:
                return ERR_LENGTH, b""
            values = [bool((data[i // 2] >> (4 if i % 2 == 0 else 0)) & 0x0F) for i in range(points)]
            return (0, b"") if mem.write_bits(code, number, values) else (ERR_RANGE, b"")
        if len(data) != points * 2:
            return ERR_LENGTH, b""
        values = list(struct.unpack("<%dH" % points, data))
        return (0, b"") if mem.write_words(code, number, values) else (ERR_RANGE, b"")


def describe(frame, args):
    command, sub = struct.unpack_from("<HH", frame, 11)
    body = frame[15:]
    if command == 0x0101:
        return "modelo da CPU"
    if command in (0x0401, 0x1401) and len(body) >= 6:
        number = body[0] | (body[1] << 8) | (body[2] << 16)
        sym = DEVICES.get(body[3], ("?%02X" % body[3], False, 0))[0]
        if sym in HEX_DEVICES and not (args.serie == "iqf" and sym in ("X", "Y")):
            text = "%X" % number
        elif args.serie == "iqf" and sym in ("X", "Y"):
            text = "%o" % number
        else:
            text = str(number)
        points = struct.unpack_from("<H", body, 4)[0]
        return "%s %s%s, %d %s" % ("leitura" if command == 0x0401 else "escrita", sym, text, points,
                                    "bit(s)" if sub == 1 else "palavra(s)")
    return "comando 0x%04X" % command


def recv_exact(conn, size):
    buf = b""
    while len(buf) < size:
        chunk = conn.recv(size - len(buf))
        if not chunk:
            raise ConnectionError
        buf += chunk
    return buf


def client_thread(conn, addr, mem, args):
    print("Cliente conectado: %s:%d" % addr)
    try:
        while True:
            header = recv_exact(conn, 9)
            if header[0:2] != b"\x50\x00":
                print("  pacote que não é MC 3E binário (%s): conexão fechada" % header[:2].hex(" "))
                return
            length = struct.unpack_from("<H", header, 7)[0]
            rest = recv_exact(conn, length)
            frame = header + rest
            if len(frame) < 15:
                end, data = ERR_LENGTH, b""
            else:
                end, data = handle_request(mem, frame, args)
            if args.verbose:
                print("  %s -> %s" % (describe(frame, args), "OK" if end == 0 else "ERRO 0x%04X" % end))
            if end == 0:
                payload = struct.pack("<H", 0) + data
            else:
                # informação do erro: rede, PC, módulo, estação, comando, subcomando
                payload = struct.pack("<H", end) + frame[2:7] + frame[11:15]
            response = b"\xD0\x00" + frame[2:7] + struct.pack("<H", len(payload)) + payload
            conn.sendall(response)
    except (ConnectionError, OSError):
        pass
    finally:
        conn.close()
        print("Cliente desconectado: %s:%d" % addr)


def print_map(args):
    name, _ = MODELS[args.serie]
    print("CPU simulada: %s · MC Protocol 3E binário" % name)
    for address, kind, note in MAP:
        print("  %-8s %-8s %s" % (address, kind, note))
    if args.serie == "iqf":
        print("  (iQ-F: X e Y são octais. No FieldProbe escolha a série 'iQ-F (FX5)'.)")


def main():
    parser = argparse.ArgumentParser(description="CLP Mitsubishi simulado (MC Protocol 3E binário)")
    parser.add_argument("--port", type=int, default=5000)
    parser.add_argument("--serie", choices=["q", "iqr", "iqf"], default="q")
    parser.add_argument("--sem-escrita-run", action="store_true", help="recusa escrita com a CPU em RUN (0x0055)")
    parser.add_argument("--mapa", action="store_true", help="mostra o mapa de variáveis e sai")
    parser.add_argument("-v", "--verbose", action="store_true")
    args = parser.parse_args()

    if args.mapa:
        print_map(args)
        return

    mem = Memory()
    mem.words[D][2] = 1500
    mem.words[D][100] = 42
    mem.set_dint(D, 20, 100000)
    mem.set_string(D, 30, "LINHA-01", 4)

    stop_event = threading.Event()
    threading.Thread(target=simulate, args=(mem, stop_event), daemon=True).start()

    server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    server.bind(("0.0.0.0", args.port))
    server.listen(5)

    def accept_loop():
        while not stop_event.is_set():
            try:
                conn, addr = server.accept()
            except OSError:
                return
            threading.Thread(target=client_thread, args=(conn, addr, mem, args), daemon=True).start()

    threading.Thread(target=accept_loop, daemon=True).start()
    print_map(args)
    print("Escutando na porta %d. Comandos: stop, run, erro, limpar, sair" % args.port)

    try:
        while True:
            command = input().strip().lower()
            with mem.lock:
                if command == "stop":
                    mem.running = False
                    print("    CPU em STOP")
                elif command == "run":
                    mem.running = True
                    print("    CPU em RUN")
                elif command == "erro":
                    mem.words[SD][0] = 2110
                    print("    SD0 = 2110 (erro simulado)")
                elif command == "limpar":
                    mem.words[SD][0] = 0
                    print("    SD0 = 0")
                elif command in ("sair", "exit", "quit"):
                    break
                elif command:
                    print("    comandos: stop, run, erro, limpar, sair")
    except (EOFError, KeyboardInterrupt):
        # sem terminal (rodando em segundo plano): fica no ar até ser encerrado
        try:
            while True:
                time.sleep(1)
        except KeyboardInterrupt:
            pass
    finally:
        stop_event.set()
        server.close()


if __name__ == "__main__":
    main()
