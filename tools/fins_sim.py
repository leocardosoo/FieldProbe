#!/usr/bin/env python3
"""
CLP Omron simulado: FINS sobre TCP e UDP (porta 9600), como uma CJ2M.
Serve para testar o driver "Omron FINS" do FieldProbe sem CLP. Só Python 3.8+.

Uso:
    python fins_sim.py                 # TCP e UDP na porta 9600
    python fins_sim.py --port 9601     # outra porta
    python fins_sim.py --no 10         # nó FINS do CLP (padrão 1)
    python fins_sim.py --sem-tcp       # só FINS/UDP (como alguns CP1L-E/CP1H)
    python fins_sim.py --mapa          # só mostra o mapa de variáveis e sai
    python fins_sim.py -v              # mostra cada pedido

Comandos no terminal (Enter depois):
    program -> CPU em PROGRAM (valores congelam)
    monitor -> CPU em MONITOR
    run     -> CPU em RUN
    erro    -> liga um erro não fatal (aparece no Diagnóstico)
    fatal   -> liga um erro fatal
    limpar  -> limpa os erros
    sair    -> encerra
"""

import argparse
import math
import random
import socket
import struct
import threading
import time

# código de área -> (nome, é bit?, nome da memória)
AREAS = {
    0x82: ("D", False, "D"), 0x02: ("D", True, "D"),
    0xB0: ("CIO", False, "CIO"), 0x30: ("CIO", True, "CIO"),
    0xB1: ("W", False, "W"), 0x31: ("W", True, "W"),
    0xB2: ("H", False, "H"), 0x32: ("H", True, "H"),
    0xB3: ("A", False, "A"), 0x33: ("A", True, "A"),
    0x89: ("T/C", False, "TC"),
}
for bank in range(13):
    AREAS[0xA0 + bank] = ("E%X_" % bank, False, "E%X" % bank)
    AREAS[0x20 + bank] = ("E%X_" % bank, True, "E%X" % bank)

SIZES = {"D": 32768, "CIO": 6144, "W": 512, "H": 1536, "A": 1472, "TC": 65536}
for bank in range(13):
    SIZES["E%X" % bank] = 32768

MODES = {"program": 0x00, "monitor": 0x02, "run": 0x04}

END_OK = 0x0000
END_NOT_SUPPORTED = 0x0401
END_TOO_SHORT = 0x1002
END_MISMATCH = 0x1003
END_AREA = 0x1101
END_ADDRESS = 0x1103
END_RANGE = 0x1104
END_READ_ONLY = 0x2101


class Memory:
    def __init__(self):
        self.lock = threading.Lock()
        self.words = {name: [0] * size for name, size in SIZES.items()}
        self.mode = 0x04
        self.fatal = 0
        self.nonfatal = 0
        self.message = ""

    def set_real(self, n, value):
        lo, hi = struct.unpack(">HH", struct.pack(">f", value))[::-1]  # palavra baixa no endereço menor
        self.words["D"][n] = lo
        self.words["D"][n + 1] = hi

    def set_dint(self, n, value):
        hi, lo = struct.unpack(">HH", struct.pack(">i", value))
        self.words["D"][n] = lo
        self.words["D"][n + 1] = hi

    def get_dint(self, n):
        return struct.unpack(">i", struct.pack(">HH", self.words["D"][n + 1], self.words["D"][n]))[0]

    def set_string(self, n, text, words):
        data = text.encode("ascii")[: words * 2].ljust(words * 2, b"\0")
        for i in range(words):
            self.words["D"][n + i] = (data[i * 2] << 8) | data[i * 2 + 1]

    def bit(self, area, word, bit):
        return bool(self.words[area][word] >> bit & 1)

    def set_bit(self, area, word, bit, on):
        if on:
            self.words[area][word] |= 1 << bit
        else:
            self.words[area][word] &= ~(1 << bit) & 0xFFFF


MAP = [
    ("D0", "INT", "contador de ciclos (sobe 1 por segundo em RUN/MONITOR)"),
    ("D2", "INT", "velocidade em rpm (leitura e escrita)"),
    ("D10", "REAL", "temperatura em °C (usa D10 e D11: palavra baixa primeiro)"),
    ("D20", "DINT", "produção total (usa D20 e D21)"),
    ("D30", "String", "nome da máquina, 8 caracteres (D30 a D33)"),
    ("D100", "INT", "setpoint livre (leitura e escrita)"),
    ("0.00-0.07", "BOOL", "entradas (sensores aleatórios)"),
    ("100.00", "BOOL", "saída do motor (segue o W0.00)"),
    ("W0.00", "BOOL", "motor ligado (liga e desliga sozinho a cada 5 s; pode escrever)"),
    ("W0.01", "BOOL", "comando livre (leitura e escrita)"),
    ("H0", "INT", "receita (área retentiva)"),
    ("T0", "INT", "temporizador: valor atual descendo de 100"),
    ("C0", "INT", "contador: peças da caixa (0 a 50)"),
]


def simulate(mem, stop_event):
    start = time.time()
    last_second = int(start)
    while not stop_event.is_set():
        now = time.time()
        with mem.lock:
            if mem.mode != 0x00:
                second = int(now)
                if second != last_second:
                    last_second = second
                    mem.words["D"][0] = (mem.words["D"][0] + 1) & 0xFFFF
                    if mem.bit("W", 0, 0):
                        mem.set_dint(20, mem.get_dint(20) + max(1, mem.words["D"][2] // 600))
                        mem.words["TC"][0x8000] = (mem.words["TC"][0x8000] + 1) % 51
                    mem.words["CIO"][0] = random.randint(0, 255)
                    if second % 5 == 0:
                        mem.set_bit("W", 0, 0, not mem.bit("W", 0, 0))
                    mem.words["TC"][0] = 100 - (second % 101)
                mem.set_real(10, 60.0 + 10.0 * math.sin((now - start) / 8.0) + random.uniform(-0.3, 0.3))
                mem.set_bit("CIO", 100, 0, mem.bit("W", 0, 0))
        time.sleep(0.1)


def handle_fins(mem, frame, args):
    """Recebe o quadro FINS (a partir do ICF) e devolve o quadro de resposta."""
    if len(frame) < 12:
        return None
    header = bytearray(frame[:10])
    mrc, src = frame[10], frame[11]
    params = frame[12:]

    end, data = END_OK, b""
    with mem.lock:
        if (mrc, src) == (0x05, 0x01):
            data = b"CJ2M-CPU33".ljust(20, b" ") + b"02.01".ljust(20, b" ") + bytes(40)
        elif (mrc, src) == (0x06, 0x01):
            status = 0x00 if mem.mode == 0x00 else 0x01
            data = struct.pack(">BBHHHH", status, mem.mode, mem.fatal, mem.nonfatal, 0, 0) + mem.message.encode("ascii").ljust(16, b" ")
        elif (mrc, src) in ((0x01, 0x01), (0x01, 0x02)):
            end, data = memory_access(mem, src == 0x02, params)
        else:
            end = END_NOT_SUPPORTED

        flags = (0x80 if mem.fatal else 0) | (0x40 if mem.nonfatal else 0)

    if args.verbose:
        print("  %s -> %s" % (describe(mrc, src, params), "OK" if end == 0 else "ERRO 0x%04X" % end))

    # resposta: ICF de resposta, troca origem e destino, mesmo SID
    response = bytearray(header)
    response[0] = 0xC0
    response[3], response[4], response[5] = header[6], header[7], header[8]
    response[6], response[7], response[8] = header[3], header[4], header[5]
    response += bytes([mrc, src, (end >> 8) & 0x7F, (end & 0x3F) | flags])
    response += data
    return bytes(response)


def memory_access(mem, write, params):
    if len(params) < 6:
        return END_TOO_SHORT, b""
    code = params[0]
    address = struct.unpack_from(">H", params, 1)[0]
    bit = params[3]
    count = struct.unpack_from(">H", params, 4)[0]
    if code not in AREAS:
        return END_AREA, b""
    _, is_bit, area = AREAS[code]
    size = SIZES[area]
    if count == 0:
        return END_MISMATCH, b""
    if is_bit:
        if bit > 15:
            return END_ADDRESS, b""
        last = address * 16 + bit + count - 1
        if last >= size * 16:
            return END_RANGE, b""
        if write:
            values = params[6:]
            if len(values) != count:
                return END_MISMATCH, b""
            if area == "A" and address < 448:
                return END_READ_ONLY, b""
            for i, v in enumerate(values):
                n = address * 16 + bit + i
                mem.set_bit(area, n // 16, n % 16, v != 0)
            return END_OK, b""
        out = bytearray()
        for i in range(count):
            n = address * 16 + bit + i
            out.append(1 if mem.bit(area, n // 16, n % 16) else 0)
        return END_OK, bytes(out)

    if address + count > size:
        return END_RANGE, b""
    if write:
        values = params[6:]
        if len(values) != count * 2:
            return END_MISMATCH, b""
        if area == "A" and address < 448:
            return END_READ_ONLY, b""
        mem.words[area][address:address + count] = list(struct.unpack(">%dH" % count, values))
        return END_OK, b""
    return END_OK, struct.pack(">%dH" % count, *mem.words[area][address:address + count])


def describe(mrc, src, params):
    if (mrc, src) == (0x05, 0x01):
        return "modelo da CPU"
    if (mrc, src) == (0x06, 0x01):
        return "estado da CPU"
    if mrc == 0x01 and src in (1, 2) and len(params) >= 6:
        name, is_bit, _ = AREAS.get(params[0], ("?%02X" % params[0], False, ""))
        address = struct.unpack_from(">H", params, 1)[0]
        count = struct.unpack_from(">H", params, 4)[0]
        if name == "T/C":
            name, address = ("C", address - 0x8000) if address >= 0x8000 else ("T", address)
        text = ("" if name == "CIO" else name) + str(address) + (".%02d" % params[3] if is_bit else "")
        return "%s %s, %d %s" % ("escrita" if src == 2 else "leitura", text, count, "bit(s)" if is_bit else "palavra(s)")
    return "comando %02X %02X" % (mrc, src)


def recv_exact(conn, size):
    buf = b""
    while len(buf) < size:
        chunk = conn.recv(size - len(buf))
        if not chunk:
            raise ConnectionError
        buf += chunk
    return buf


def tcp_packet(command, payload, error=0):
    return b"FINS" + struct.pack(">III", 8 + len(payload), command, error) + payload


def tcp_client(conn, addr, mem, args, nodes):
    print("Cliente FINS/TCP conectado: %s:%d" % addr)
    try:
        while True:
            header = recv_exact(conn, 16)
            if header[:4] != b"FINS":
                conn.sendall(tcp_packet(3, b"", error=1))
                return
            length, command, _ = struct.unpack_from(">III", header, 4)
            body = recv_exact(conn, length - 8)
            if command == 0:
                wanted = struct.unpack(">I", body[:4])[0] if len(body) >= 4 else 0
                with nodes["lock"]:
                    client_node = wanted or nodes["next"]
                    if not wanted:
                        nodes["next"] = 239 if nodes["next"] <= 2 else nodes["next"] - 1
                print("  handshake: nó do PC = %d, nó do CLP = %d" % (client_node, args.no))
                conn.sendall(tcp_packet(1, struct.pack(">II", client_node, args.no)))
            elif command == 2:
                response = handle_fins(mem, body, args)
                if response is not None:
                    conn.sendall(tcp_packet(2, response))
            else:
                conn.sendall(tcp_packet(3, b"", error=3))
    except (ConnectionError, OSError):
        pass
    finally:
        conn.close()
        print("Cliente FINS/TCP desconectado: %s:%d" % addr)


def udp_loop(sock, mem, args, stop_event):
    while not stop_event.is_set():
        try:
            frame, addr = sock.recvfrom(4096)
        except OSError:
            return
        response = handle_fins(mem, frame, args)
        if response is not None:
            sock.sendto(response, addr)


def print_map():
    print("CPU simulada: CJ2M-CPU33 · FINS TCP/UDP")
    for address, kind, note in MAP:
        print("  %-10s %-7s %s" % (address, kind, note))


def main():
    parser = argparse.ArgumentParser(description="CLP Omron simulado (FINS TCP e UDP)")
    parser.add_argument("--port", type=int, default=9600)
    parser.add_argument("--no", type=int, default=1, help="nó FINS do CLP")
    parser.add_argument("--sem-tcp", action="store_true", help="só FINS/UDP")
    parser.add_argument("--mapa", action="store_true")
    parser.add_argument("-v", "--verbose", action="store_true")
    args = parser.parse_args()

    if args.mapa:
        print_map()
        return

    mem = Memory()
    mem.words["D"][2] = 1500
    mem.words["D"][100] = 42
    mem.words["H"][0] = 3
    mem.set_dint(20, 100000)
    mem.set_string(30, "LINHA-01", 4)

    stop_event = threading.Event()
    threading.Thread(target=simulate, args=(mem, stop_event), daemon=True).start()

    udp = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    udp.bind(("0.0.0.0", args.port))
    threading.Thread(target=udp_loop, args=(udp, mem, args, stop_event), daemon=True).start()

    server = None
    if not args.sem_tcp:
        server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        server.bind(("0.0.0.0", args.port))
        server.listen(5)
        nodes = {"next": 239, "lock": threading.Lock()}

        def accept_loop():
            while not stop_event.is_set():
                try:
                    conn, addr = server.accept()
                except OSError:
                    return
                threading.Thread(target=tcp_client, args=(conn, addr, mem, args, nodes), daemon=True).start()

        threading.Thread(target=accept_loop, daemon=True).start()

    print_map()
    print("Escutando FINS/%s na porta %d (nó do CLP %d). Comandos: program, monitor, run, erro, fatal, limpar, sair"
          % ("UDP" if args.sem_tcp else "TCP e UDP", args.port, args.no))

    try:
        while True:
            command = input().strip().lower()
            with mem.lock:
                if command in MODES:
                    mem.mode = MODES[command]
                    print("    CPU em %s" % command.upper())
                elif command == "erro":
                    mem.nonfatal = 0x0010
                    mem.message = "BATERIA FRACA"
                    print("    erro não fatal ligado")
                elif command == "fatal":
                    mem.fatal = 0x4000
                    mem.message = "ERRO DE MEMORIA"
                    print("    erro fatal ligado")
                elif command == "limpar":
                    mem.fatal = mem.nonfatal = 0
                    mem.message = ""
                    print("    erros limpos")
                elif command in ("sair", "exit", "quit"):
                    break
                elif command:
                    print("    comandos: program, monitor, run, erro, fatal, limpar, sair")
    except (EOFError, KeyboardInterrupt):
        try:
            while True:
                time.sleep(1)
        except KeyboardInterrupt:
            pass
    finally:
        stop_event.set()
        udp.close()
        if server is not None:
            server.close()


if __name__ == "__main__":
    main()
