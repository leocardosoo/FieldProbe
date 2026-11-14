#!/usr/bin/env python3
"""
fieldprobe_cli.py - FieldProbe em linha de comando: teste rápido de comunicação OT pelo terminal.

  Modbus TCP  -> sem dependências
  OPC UA      -> pip install asyncua
  check       -> testa ping/portas industriais de um IP

Exemplos:
  python fieldprobe_cli.py check 192.168.0.10
  python fieldprobe_cli.py modbus 192.168.0.10                       (abre o shell interativo)
  python fieldprobe_cli.py modbus 127.0.0.1 --port 5020 r IR:1 f32   (comando único)
  python fieldprobe_cli.py modbus 192.168.0.10 --unit 2 watch HR:0 IR:1:f32
  python fieldprobe_cli.py opcua opc.tcp://192.168.0.20:4840         (shell interativo)
  python fieldprobe_cli.py opcua opc.tcp://192.168.0.20:4840 info    (endpoints e segurança)
"""

import argparse
import asyncio
import socket
import struct
import subprocess
import sys
import time

# =====================================================================
#  Conversão de tipos (registradores de 16 bits <-> valores)
# =====================================================================

TYPES = {  # nome: (formato struct, registradores)
    "bool": (None, 1), "i16": (">h", 1), "u16": (">H", 1),
    "i32": (">i", 2), "u32": (">I", 2), "f32": (">f", 2), "f64": (">d", 4),
    "str": (None, None),
}
ORDERS = ("ABCD", "CDAB", "BADC", "DCBA")


def apply_order(raw, order, is_string=False):
    """CDAB/DCBA invertem os registradores; BADC/DCBA trocam os bytes dentro de cada registrador."""
    data = bytearray(raw)
    if order in ("BADC", "DCBA"):
        for i in range(0, len(data) - 1, 2):
            data[i], data[i + 1] = data[i + 1], data[i]
    if order in ("CDAB", "DCBA") and not is_string:
        words = [data[i:i + 2] for i in range(0, len(data), 2)]
        data = bytearray(b"".join(reversed(words)))
    return bytes(data)


def regs_to_value(regs, dtype, order):
    raw = apply_order(struct.pack(f">{len(regs)}H", *regs), order, dtype == "str")
    if dtype == "bool":
        return regs[0] != 0
    if dtype == "str":
        return raw.rstrip(b"\0 ").decode("ascii", errors="replace")
    return struct.unpack(TYPES[dtype][0], raw)[0]


def value_to_regs(text, dtype, order, length=1):
    text = str(text).strip()
    if dtype == "bool":
        raw = struct.pack(">H", 1 if parse_bool(text) else 0)
    elif dtype == "str":
        raw = text.encode("ascii")[: length * 2].ljust(length * 2, b"\0")
    elif dtype in ("f32", "f64"):
        raw = struct.pack(TYPES[dtype][0], float(text.replace(",", ".")))
    else:
        raw = struct.pack(TYPES[dtype][0], int(text, 0))
    raw = apply_order(raw, order, dtype == "str")
    return list(struct.unpack(f">{len(raw) // 2}H", raw))


def parse_bool(text):
    t = str(text).strip().lower()
    if t in ("1", "true", "on", "sim"):
        return True
    if t in ("0", "false", "off", "nao", "não"):
        return False
    raise ValueError(f"Valor booleano inválido: {text}")


# =====================================================================
#  Modbus TCP (socket puro)
# =====================================================================

AREAS = {"CO": 1, "DI": 2, "IR": 4, "HR": 3}  # área -> function code de leitura
EXCEPTIONS = {
    1: "Illegal Function", 2: "Illegal Data Address", 3: "Illegal Data Value",
    4: "Server Device Failure", 6: "Server Device Busy",
    10: "Gateway Path Unavailable", 11: "Gateway Target Failed to Respond (Unit ID?)",
}


def parse_address(text):
    """Aceita HR:0, IR:10, CO:3, DI:7 (0-based) ou 40001, 30011, 00004, 10008 (1-based).
    Também aceita tipo/ordem/tamanho junto: HR:1:f32, IR:3:f32:CDAB, HR:10:str:5"""
    parts = text.strip().upper().split(":")
    dtype, order, length = None, "ABCD", 1
    if parts[0] in AREAS:
        area, offset = parts[0], int(parts[1])
        extra = parts[2:]
    else:
        n = int(parts[0])
        digits = len(parts[0])
        area_digit, one_based = (n // 100000, n % 100000) if digits >= 6 else (n // 10000, n % 10000)
        area = {0: "CO", 1: "DI", 3: "IR", 4: "HR"}.get(area_digit)
        if area is None or one_based < 1:
            raise ValueError(f"Endereço inválido: {text}")
        offset = one_based - 1
        extra = parts[1:]
    for e in extra:
        if e.lower() in TYPES:
            dtype = e.lower()
        elif e in ORDERS:
            order = e
        elif e.isdigit():
            length = int(e)
    if dtype is None:
        dtype = "bool" if area in ("CO", "DI") else "u16"
    return area, offset, dtype, order, length


class ModbusError(Exception):
    pass


class ModbusClient:
    def __init__(self, host, port=502, unit=1, timeout=2.0, show_raw=False):
        self.host, self.port, self.unit, self.timeout = host, port, unit, timeout
        self.show_raw = show_raw
        self.sock = None
        self.tid = 0

    def connect(self):
        self.close()
        self.sock = socket.create_connection((self.host, self.port), timeout=self.timeout)
        self.sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)

    def close(self):
        if self.sock:
            try:
                self.sock.close()
            except OSError:
                pass
        self.sock = None

    def _recv(self, n):
        buf = b""
        while len(buf) < n:
            chunk = self.sock.recv(n - len(buf))
            if not chunk:
                raise ConnectionError("Conexão fechada pelo device")
            buf += chunk
        return buf

    def transact(self, pdu):
        if self.sock is None:
            self.connect()
        self.tid = (self.tid + 1) & 0xFFFF
        adu = struct.pack(">HHHB", self.tid, 0, len(pdu) + 1, self.unit) + pdu
        t0 = time.perf_counter()
        try:
            if self.show_raw:
                print(f"  TX >> {adu.hex(' ')}")
            self.sock.sendall(adu)
            header = self._recv(7)
            tid, _, length, _ = struct.unpack(">HHHB", header)
            body = self._recv(length - 1)
        except (OSError, ConnectionError) as ex:
            self.close()  # força reconexão na próxima tentativa
            if isinstance(ex, socket.timeout):
                raise ModbusError(f"Timeout: sem resposta em {self.timeout} s (Unit ID {self.unit} correto?)")
            raise ModbusError(f"Conexão perdida: {ex}")
        ms = (time.perf_counter() - t0) * 1000
        if self.show_raw:
            print(f"  RX << {(header + body).hex(' ')}   ({ms:.1f} ms)")
        if tid != self.tid:
            self.close()
            raise ModbusError(f"Transaction ID divergente ({self.tid} != {tid})")
        if body[0] & 0x80:
            code = body[1]
            raise ModbusError(f"Exceção {code:02X}: {EXCEPTIONS.get(code, 'desconhecida')}")
        return body, ms

    def read(self, area, offset, dtype="u16", order="ABCD", length=1):
        fc = AREAS[area]
        if area in ("CO", "DI"):
            body, ms = self.transact(struct.pack(">BHH", fc, offset, 1))
            return bool(body[2] & 1), ms
        count = length if dtype == "str" else TYPES[dtype][1]
        body, ms = self.transact(struct.pack(">BHH", fc, offset, count))
        regs = struct.unpack(f">{count}H", body[2:2 + count * 2])
        return regs_to_value(regs, dtype, order), ms

    def write(self, area, offset, value, dtype="u16", order="ABCD", length=1):
        if area == "CO":
            return self.transact(struct.pack(">BHH", 5, offset, 0xFF00 if parse_bool(value) else 0))[1]
        if area != "HR":
            raise ModbusError(f"{area} é somente leitura")
        regs = value_to_regs(value, dtype, order, length)
        if len(regs) == 1:
            return self.transact(struct.pack(">BHH", 6, offset, regs[0]))[1]
        pdu = struct.pack(">BHHB", 16, offset, len(regs), len(regs) * 2) + struct.pack(f">{len(regs)}H", *regs)
        return self.transact(pdu)[1]


def fmt(value):
    if isinstance(value, float):
        return f"{value:.6g}"
    return repr(value) if isinstance(value, str) else str(value)


MODBUS_HELP = """
Comandos:
  r  <end> [tipo] [ordem] [tam]   ler          ex.: r HR:0 | r IR:1 f32 | r IR:3 f32 CDAB | r HR:10 str 5
  w  <end> <valor> [tipo] [ordem] escrever     ex.: w HR:0 500 | w HR:1 3,14 f32 | w CO:0 1
  watch <end>[:tipo[:ordem]] ...  monitorar    ex.: watch IR:0 IR:1:f32 DI:0   (Ctrl+C para parar)
  scan [área] [qtd]               varre área   ex.: scan HR 20 | scan (todas as áreas, 10 cada)
  unit <id>                       troca o Unit ID
  raw                             liga/desliga o tráfego em hex
  stats                           estatísticas da sessão
  q                               sair
Tipos: bool i16 u16 i32 u32 f32 f64 str  |  Ordens: ABCD CDAB BADC DCBA
Endereços: HR:0 IR:0 CO:0 DI:0 (0-based) ou 40001 30001 00001 10001 (1-based)
"""


class Stats:
    def __init__(self):
        self.ok = self.err = 0
        self.lat = []

    def add(self, ms=None):
        if ms is None:
            self.err += 1
        else:
            self.ok += 1
            self.lat.append(ms)

    def __str__(self):
        total = self.ok + self.err
        if not self.lat:
            return f"Req: {total} | Erros: {self.err}"
        return (f"Req: {total} | Erros: {self.err} ({100 * self.err / total:.1f}%) | "
                f"Latência min/méd/máx: {min(self.lat):.1f}/{sum(self.lat) / len(self.lat):.1f}/{max(self.lat):.1f} ms")


def modbus_command(client, stats, tokens):
    cmd, args = tokens[0].lower(), tokens[1:]

    def addr_with_opts(a, rest):
        area, off, dtype, order, length = parse_address(a)
        for t in rest:
            if t.lower() in TYPES:
                dtype = t.lower()
            elif t.upper() in ORDERS:
                order = t.upper()
            elif t.isdigit():
                length = int(t)
        return area, off, dtype, order, length

    if cmd in ("r", "read"):
        area, off, dtype, order, length = addr_with_opts(args[0], args[1:])
        try:
            value, ms = client.read(area, off, dtype, order, length)
            stats.add(ms)
            print(f"{area}:{off} ({dtype} {order}) = {fmt(value)}   [{ms:.1f} ms]")
        except ModbusError as ex:
            stats.add()
            print(f"ERRO: {ex}")

    elif cmd in ("w", "write"):
        area, off, dtype, order, length = addr_with_opts(args[0], args[2:])
        try:
            ms = client.write(area, off, args[1], dtype, order, length)
            stats.add(ms)
            print(f"OK: {area}:{off} <= {args[1]} ({dtype} {order})   [{ms:.1f} ms]")
        except (ModbusError, ValueError) as ex:
            stats.add()
            print(f"ERRO: {ex}")

    elif cmd == "watch":
        interval = 1.0
        items = []
        for a in args:
            if a.startswith("--interval="):
                interval = float(a.split("=")[1])
            else:
                items.append((a, parse_address(a)))
        print(f"Monitorando a cada {interval}s (Ctrl+C para parar)")
        try:
            while True:
                line = [time.strftime("%H:%M:%S")]
                for label, (area, off, dtype, order, length) in items:
                    try:
                        value, ms = client.read(area, off, dtype, order, length)
                        stats.add(ms)
                        line.append(f"{label}={fmt(value)} ({ms:.0f}ms)")
                    except ModbusError as ex:
                        stats.add()
                        line.append(f"{label}=ERRO[{ex}]")
                print("  ".join(line))
                time.sleep(interval)
        except KeyboardInterrupt:
            print("\nParado.")

    elif cmd == "scan":
        areas = [args[0].upper()] if args else list(AREAS)
        count = int(args[1]) if len(args) > 1 else 10
        for area in areas:
            values = []
            for off in range(count):
                try:
                    value, ms = client.read(area, off, "bool" if area in ("CO", "DI") else "u16")
                    stats.add(ms)
                    values.append(f"{off}={int(value)}")
                except ModbusError as ex:
                    stats.add()
                    values.append(f"{off}=ERRO")
                    if "Timeout" in str(ex) or "Conexão" in str(ex):
                        break
            print(f"{area}: " + "  ".join(values))

    elif cmd == "unit":
        client.unit = int(args[0])
        print(f"Unit ID = {client.unit}")
    elif cmd == "raw":
        client.show_raw = not client.show_raw
        print(f"Tráfego bruto: {'ligado' if client.show_raw else 'desligado'}")
    elif cmd == "stats":
        print(stats)
    elif cmd in ("h", "help", "?"):
        print(MODBUS_HELP)
    else:
        print("Comando desconhecido. Digite 'help'.")


def run_modbus(args):
    client = ModbusClient(args.host, args.port, args.unit, args.timeout, args.raw)
    stats = Stats()
    try:
        client.connect()
        print(f"Conectado em {args.host}:{args.port} (Unit ID {args.unit})")
    except OSError as ex:
        print(f"Falha ao conectar em {args.host}:{args.port}: {ex}")
        return 1

    if args.command:
        modbus_command(client, stats, args.command)
        client.close()
        return 0

    print("Digite 'help' para ver os comandos.")
    while True:
        try:
            line = input("modbus> ").strip()
        except (EOFError, KeyboardInterrupt):
            break
        if not line:
            continue
        if line.lower() in ("q", "quit", "exit", "sair"):
            break
        try:
            modbus_command(client, stats, line.split())
        except (IndexError, ValueError) as ex:
            print(f"Comando inválido ({ex}). Digite 'help'.")
    print(stats)
    client.close()
    return 0


# =====================================================================
#  OPC UA (asyncua)
# =====================================================================

OPCUA_HELP = """
Comandos:
  ls [nodeid]           navegar (sem argumento = pasta Objects)   ex.: ls ns=2;i=1
  r  <nodeid>           ler valor                                  ex.: r ns=2;s=Temperatura
  w  <nodeid> <valor>   escrever (o tipo é lido do servidor)       ex.: w ns=2;s=Setpoint 42,5
  sub <nodeid> ...      subscription (notificação por mudança, Ctrl+C para parar)
  q                     sair
"""


def convert_for_variant(text, vtype):
    from asyncua import ua
    name = vtype.name
    if name == "Boolean":
        return parse_bool(text)
    if name in ("Float", "Double"):
        return float(text.replace(",", "."))
    if name in ("SByte", "Byte", "Int16", "UInt16", "Int32", "UInt32", "Int64", "UInt64"):
        return int(text, 0)
    if name == "String":
        return text
    raise ValueError(f"Escrita de {name} não suportada neste script")


async def opcua_info(url, timeout):
    from asyncua import Client
    client = Client(url, timeout=timeout)
    endpoints = await client.connect_and_get_server_endpoints()
    print(f"{len(endpoints)} endpoint(s) em {url}:\n")
    weak = []
    for ep in endpoints:
        policy = ep.SecurityPolicyUri.split("#")[-1]
        mode = ep.SecurityMode.name
        tokens = [t.TokenType.name for t in ep.UserIdentityTokens]
        print(f"  {ep.EndpointUrl}\n    Policy: {policy:<22} Mode: {mode:<15} Login: {', '.join(tokens)}")
        if policy == "None":
            weak.append("Endpoint sem criptografia (SecurityPolicy None)")
        if policy in ("Basic128Rsa15", "Basic256"):
            weak.append(f"Policy obsoleta: {policy}")
        if "Anonymous" in tokens:
            weak.append(f"Acesso anônimo permitido ({policy}/{mode})")
    if weak:
        print("\nPontos de atenção:")
        for w in sorted(set(weak)):
            print(f"  ! {w}")
    else:
        print("\nNenhum ponto de atenção básico encontrado.")


async def opcua_command(client, tokens):
    from asyncua import ua
    cmd, args = tokens[0].lower(), tokens[1:]
    loop = asyncio.get_running_loop()

    if cmd == "ls":
        node = client.get_node(args[0]) if args else client.nodes.objects
        for child in await node.get_children():
            name = (await child.read_browse_name()).Name
            ncls = (await child.read_node_class()).name
            extra = ""
            if ncls == "Variable":
                try:
                    extra = f" = {await child.read_value()!r}"
                except Exception:
                    extra = " = (sem leitura)"
            print(f"  [{ncls[:3]}] {name:<30} {child.nodeid.to_string()}{extra}")

    elif cmd in ("r", "read"):
        node = client.get_node(args[0])
        t0 = time.perf_counter()
        dv = await node.read_data_value()
        ms = (time.perf_counter() - t0) * 1000
        print(f"{args[0]} = {dv.Value.Value!r}  ({dv.Value.VariantType.name}, {dv.StatusCode.name}, "
              f"origem {dv.SourceTimestamp})   [{ms:.1f} ms]")

    elif cmd in ("w", "write"):
        node = client.get_node(args[0])
        vtype = await node.read_data_type_as_variant_type()
        value = convert_for_variant(" ".join(args[1:]), vtype)
        await node.write_value(ua.DataValue(ua.Variant(value, vtype)))
        print(f"OK: {args[0]} <= {value!r} ({vtype.name})")

    elif cmd == "sub":
        class Handler:
            def datachange_notification(self, node, val, data):
                print(f"{time.strftime('%H:%M:%S')}  {node.nodeid.to_string()} = {val!r}")

        sub = await client.create_subscription(500, Handler())
        await sub.subscribe_data_change([client.get_node(a) for a in args])
        print("Subscription ativa (Enter para parar)")
        await loop.run_in_executor(None, input)
        await sub.delete()

    elif cmd in ("h", "help", "?"):
        print(OPCUA_HELP)
    else:
        print("Comando desconhecido. Digite 'help'.")


async def run_opcua_async(args):
    try:
        from asyncua import Client
    except ImportError:
        print("Para OPC UA instale a biblioteca:  pip install asyncua")
        return 1

    if args.command and args.command[0].lower() == "info":
        await opcua_info(args.url, args.timeout)
        return 0

    client = Client(args.url, timeout=args.timeout)
    if args.user:
        client.set_user(args.user)
        client.set_password(args.password or "")
    try:
        await client.connect()
    except Exception as ex:
        print(f"Falha ao conectar em {args.url}: {ex}")
        print("Dica: rode com 'info' para ver as políticas de segurança aceitas pelo servidor.")
        return 1
    print(f"Conectado em {args.url}")

    try:
        if args.command:
            await opcua_command(client, args.command)
            return 0
        print("Digite 'help' para ver os comandos.")
        loop = asyncio.get_running_loop()
        while True:
            try:
                line = (await loop.run_in_executor(None, input, "opcua> ")).strip()
            except (EOFError, KeyboardInterrupt):
                break
            if not line:
                continue
            if line.lower() in ("q", "quit", "exit", "sair"):
                break
            try:
                await opcua_command(client, line.split())
            except Exception as ex:
                print(f"ERRO: {ex}")
    finally:
        await client.disconnect()
    return 0


# =====================================================================
#  Check de rede
# =====================================================================

COMMON_PORTS = {
    502: "Modbus TCP", 102: "Siemens S7 (ISO-TSAP)", 44818: "EtherNet/IP",
    4840: "OPC UA", 1883: "MQTT", 8883: "MQTT TLS", 47808: "BACnet (UDP, não testável aqui)",
    20000: "DNP3", 2404: "IEC 60870-5-104", 80: "HTTP (web do device)", 443: "HTTPS",
}


def run_check(args):
    flag = "-n" if sys.platform.startswith("win") else "-c"
    try:
        ok = subprocess.run(["ping", flag, "1", args.host], capture_output=True, timeout=5).returncode == 0
        print(f"Ping {args.host}: {'OK' if ok else 'sem resposta (o device pode bloquear ICMP)'}")
    except Exception:
        print("Ping: não disponível")

    ports = args.ports or [p for p in COMMON_PORTS if p != 47808]
    for port in ports:
        name = COMMON_PORTS.get(port, "")
        t0 = time.perf_counter()
        try:
            with socket.create_connection((args.host, port), timeout=args.timeout):
                status = f"ABERTA ({(time.perf_counter() - t0) * 1000:.0f} ms)"
        except ConnectionRefusedError:
            status = "fechada"
        except socket.timeout:
            status = "sem resposta (filtrada/firewall)"
        except OSError as ex:
            status = f"erro: {ex}"
        print(f"  {port:>5}  {name:<24} {status}")
    return 0


# =====================================================================

def add_modbus_options(p, suppress=False):
    d = (lambda v: argparse.SUPPRESS) if suppress else (lambda v: v)
    p.add_argument("--port", type=int, default=d(502))
    p.add_argument("--unit", type=int, default=d(1))
    p.add_argument("--timeout", type=float, default=d(2.0))
    p.add_argument("--raw", action="store_true", default=d(False), help="mostra os frames em hex")


def add_opcua_options(p, suppress=False):
    d = (lambda v: argparse.SUPPRESS) if suppress else (lambda v: v)
    p.add_argument("--user", default=d(None))
    p.add_argument("--password", default=d(None))
    p.add_argument("--timeout", type=float, default=d(4.0))


def main():
    parser = argparse.ArgumentParser(description="FieldProbe CLI - teste rápido de comunicação OT")
    sub = parser.add_subparsers(dest="mode", required=True)

    m = sub.add_parser("modbus", help="Modbus TCP")
    m.add_argument("host")
    add_modbus_options(m)
    m.add_argument("command", nargs=argparse.REMAINDER, help="comando único (sem isso abre o shell)")

    o = sub.add_parser("opcua", help="OPC UA (requer asyncua)")
    o.add_argument("url", help="ex.: opc.tcp://192.168.0.20:4840")
    add_opcua_options(o)
    o.add_argument("command", nargs=argparse.REMAINDER)

    c = sub.add_parser("check", help="ping + portas industriais")
    c.add_argument("host")
    c.add_argument("ports", nargs="*", type=int)
    c.add_argument("--timeout", type=float, default=1.5)

    args = parser.parse_args()

    # Permite as opções depois do host também (ex.: modbus 10.0.0.5 --port 5020 r HR:0)
    if args.mode in ("modbus", "opcua"):
        late = argparse.ArgumentParser(add_help=False)
        (add_modbus_options if args.mode == "modbus" else add_opcua_options)(late, suppress=True)
        extra, rest = late.parse_known_args(args.command)
        vars(args).update(vars(extra))
        args.command = rest

    if args.mode == "modbus":
        return run_modbus(args)
    if args.mode == "opcua":
        return asyncio.run(run_opcua_async(args))
    return run_check(args)


if __name__ == "__main__":
    sys.exit(main())
