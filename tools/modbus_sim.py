#!/usr/bin/env python3
"""
Simulador Modbus TCP para testar o OT Comm Tester sem CLP físico.
Não precisa de bibliotecas externas (só Python 3.8+).

Uso:
    python modbus_sim.py                  # porta 502
    python modbus_sim.py --port 5020      # outra porta
    python modbus_sim.py --delay 200      # atraso de 200 ms em cada resposta
    python modbus_sim.py --drop 10        # 10% das requisições sem resposta (testa timeout)
    python modbus_sim.py --unit 1         # só responde ao Unit ID 1 (padrão: qualquer)
    python modbus_sim.py -v               # mostra cada requisição
    python modbus_sim.py --mapa-real      # como um equipamento real: só algumas faixas existem
                                          # (o resto responde "Illegal Data Address"), bom para
                                          # testar o "Procurar registradores"

Mapa de dados (offsets 0-based, como no app: IR:0, HR:10...):
    IR:0        contador (incrementa a cada segundo)
    IR:1-2      Float32 senoide 0..100, ordem ABCD
    IR:3-4      a mesma senoide em ordem CDAB (para testar word swap)
    IR:5        valor aleatório 0..1000
    IR:6-7      Int32 = -123456 (ABCD)
    DI:0        alterna a cada segundo
    DI:1        aleatório
    HR:0        1234 (leitura/escrita)
    HR:1-2      Float32 = 3.14159 (ABCD)
    HR:3-6      Float64 = 2.718281828 (ABCD)
    HR:10-14    String "BANCADA-01" (5 registradores)
    HR, CO      demais endereços: memória livre para escrita
    --mapa-real HR:0-19, HR:100-149, IR:0-9, CO:0-15, DI:0-7 (o resto não existe)
    FC 43       responde à identificação do device (fabricante, código, revisão)
"""

import argparse
import asyncio
import math
import random
import struct
import time

SIZE = 10000

ILLEGAL_FUNCTION = 0x01
ILLEGAL_ADDRESS = 0x02
ILLEGAL_VALUE = 0x03


class DataStore:
    def __init__(self):
        self.coils = [False] * SIZE
        self.discrete = [False] * SIZE
        self.holding = [0] * SIZE
        self.input = [0] * SIZE
        self._set_float32(self.holding, 1, 3.14159)
        self._set_float64(self.holding, 3, 2.718281828)
        self._set_string(self.holding, 10, "BANCADA-01", 5)
        self.holding[0] = 1234
        self._set_int32(self.input, 6, -123456)

    @staticmethod
    def _set_float32(area, offset, value, word_swap=False):
        hi, lo = struct.unpack(">HH", struct.pack(">f", value))
        area[offset], area[offset + 1] = (lo, hi) if word_swap else (hi, lo)

    @staticmethod
    def _set_float64(area, offset, value):
        words = struct.unpack(">HHHH", struct.pack(">d", value))
        area[offset:offset + 4] = list(words)

    @staticmethod
    def _set_int32(area, offset, value):
        hi, lo = struct.unpack(">HH", struct.pack(">i", value))
        area[offset], area[offset + 1] = hi, lo

    @staticmethod
    def _set_string(area, offset, text, registers):
        raw = text.encode("ascii")[: registers * 2].ljust(registers * 2, b"\0")
        for i in range(registers):
            area[offset + i] = (raw[i * 2] << 8) | raw[i * 2 + 1]

    def tick(self, start_time):
        elapsed = time.monotonic() - start_time
        seconds = int(elapsed)
        sine = 50 + 50 * math.sin(elapsed / 5)
        self.input[0] = seconds & 0xFFFF
        self._set_float32(self.input, 1, sine)
        self._set_float32(self.input, 3, sine, word_swap=True)
        self.input[5] = random.randint(0, 1000)
        self.discrete[0] = seconds % 2 == 0
        self.discrete[1] = random.random() > 0.5


def exception_pdu(function, code):
    return bytes([function | 0x80, code])


# Faixas que existem no modo --mapa-real (como num inversor ou medidor real). None = tudo até SIZE.
REAL_MAP = {
    0x01: [(0, 15)],                 # CO
    0x02: [(0, 7)],                  # DI
    0x03: [(0, 19), (100, 149)],     # HR
    0x04: [(0, 9)],                  # IR
}
VALID = None


def check_range(start, count, max_count, function=None):
    if count < 1 or count > max_count:
        return ILLEGAL_VALUE
    if start + count > SIZE:
        return ILLEGAL_ADDRESS
    if VALID is not None and function in VALID:
        # o pedido inteiro precisa caber numa faixa que existe
        if not any(first <= start and start + count - 1 <= last for first, last in VALID[function]):
            return ILLEGAL_ADDRESS
    return None


def handle_pdu(store, pdu):
    function = pdu[0]

    if function in (0x01, 0x02):  # Read Coils / Discrete Inputs
        start, count = struct.unpack(">HH", pdu[1:5])
        err = check_range(start, count, 2000, function)
        if err:
            return exception_pdu(function, err)
        area = store.coils if function == 0x01 else store.discrete
        data = bytearray((count + 7) // 8)
        for i in range(count):
            if area[start + i]:
                data[i // 8] |= 1 << (i % 8)
        return bytes([function, len(data)]) + bytes(data)

    if function in (0x03, 0x04):  # Read Holding / Input Registers
        start, count = struct.unpack(">HH", pdu[1:5])
        err = check_range(start, count, 125, function)
        if err:
            return exception_pdu(function, err)
        area = store.holding if function == 0x03 else store.input
        values = area[start:start + count]
        return bytes([function, count * 2]) + struct.pack(f">{count}H", *values)

    if function == 0x05:  # Write Single Coil
        address, value = struct.unpack(">HH", pdu[1:5])
        if address >= SIZE:
            return exception_pdu(function, ILLEGAL_ADDRESS)
        if value not in (0x0000, 0xFF00):
            return exception_pdu(function, ILLEGAL_VALUE)
        store.coils[address] = value == 0xFF00
        return pdu[:5]

    if function == 0x06:  # Write Single Register
        address, value = struct.unpack(">HH", pdu[1:5])
        if address >= SIZE:
            return exception_pdu(function, ILLEGAL_ADDRESS)
        store.holding[address] = value
        return pdu[:5]

    if function == 0x0F:  # Write Multiple Coils
        start, count, byte_count = struct.unpack(">HHB", pdu[1:6])
        err = check_range(start, count, 1968)
        if err:
            return exception_pdu(function, err)
        data = pdu[6:6 + byte_count]
        for i in range(count):
            store.coils[start + i] = bool(data[i // 8] & (1 << (i % 8)))
        return pdu[:5]

    if function == 0x10:  # Write Multiple Registers
        start, count, byte_count = struct.unpack(">HHB", pdu[1:6])
        err = check_range(start, count, 123)
        if err or byte_count != count * 2:
            return exception_pdu(function, err or ILLEGAL_VALUE)
        values = struct.unpack(f">{count}H", pdu[6:6 + byte_count])
        store.holding[start:start + count] = list(values)
        return pdu[:5]

    if function == 0x2B and len(pdu) >= 4 and pdu[1] == 0x0E:  # Read Device Identification (básica)
        objects = [b"FieldProbe Simulator", b"MB-SIM-01", b"1.2.0"]
        body = bytes([0x2B, 0x0E, pdu[2], 0x01, 0x00, 0x00, len(objects)])
        for obj_id, value in enumerate(objects):
            body += bytes([obj_id, len(value)]) + value
        return body

    return exception_pdu(function, ILLEGAL_FUNCTION)


async def handle_client(reader, writer, store, args):
    peer = writer.get_extra_info("peername")
    print(f"[+] Cliente conectado: {peer}")
    try:
        while True:
            header = await reader.readexactly(7)
            tid, proto, length, unit = struct.unpack(">HHHB", header)
            pdu = await reader.readexactly(length - 1)

            if args.verbose:
                print(f"    {peer} TID={tid} Unit={unit} FC={pdu[0]:02X} {pdu.hex(' ')}")

            if proto != 0:
                continue
            if args.unit and unit != args.unit:
                continue  # Unit ID errado: não responde (como um gateway real costuma fazer)
            if args.drop and random.uniform(0, 100) < args.drop:
                if args.verbose:
                    print("    (resposta descartada de propósito)")
                continue
            if args.delay:
                await asyncio.sleep(args.delay / 1000)

            response = handle_pdu(store, pdu)
            writer.write(struct.pack(">HHHB", tid, 0, len(response) + 1, unit) + response)
            await writer.drain()
    except (asyncio.IncompleteReadError, ConnectionResetError):
        pass
    finally:
        print(f"[-] Cliente desconectado: {peer}")
        writer.close()


async def simulate(store):
    start = time.monotonic()
    while True:
        store.tick(start)
        await asyncio.sleep(0.1)


async def main():
    parser = argparse.ArgumentParser(description="Simulador Modbus TCP para testes")
    parser.add_argument("--host", default="0.0.0.0")
    parser.add_argument("--port", type=int, default=502)
    parser.add_argument("--unit", type=int, default=0, help="Unit ID aceito (0 = qualquer)")
    parser.add_argument("--delay", type=int, default=0, help="Atraso por resposta em ms")
    parser.add_argument("--drop", type=float, default=0, help="Porcentagem de respostas descartadas")
    parser.add_argument("--mapa-real", action="store_true",
                        help="Só HR:0-19, HR:100-149, IR:0-9, CO:0-15, DI:0-7 existem (o resto responde Illegal Data Address)")
    parser.add_argument("-v", "--verbose", action="store_true")
    args = parser.parse_args()

    global VALID
    if args.mapa_real:
        VALID = REAL_MAP
        print("Mapa real: HR:0-19, HR:100-149, IR:0-9, CO:0-15, DI:0-7 (o resto não existe)")

    store = DataStore()
    server = await asyncio.start_server(lambda r, w: handle_client(r, w, store, args), args.host, args.port)
    print(f"Simulador Modbus TCP ouvindo em {args.host}:{args.port} (Ctrl+C para sair)")
    asyncio.create_task(simulate(store))
    async with server:
        await server.serve_forever()


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        print("\nEncerrado.")
