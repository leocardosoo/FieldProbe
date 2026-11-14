#!/usr/bin/env python3
"""
Broker MQTT 3.1.1 mínimo + devices simulados, para testar o FieldProbe sem infraestrutura.
Não precisa de bibliotecas externas (Python 3.8+). Não use em produção.

Uso:
    python mqtt_sim.py                        # porta 1883, sem autenticação
    python mqtt_sim.py --port 1884
    python mqtt_sim.py --user teste --password 123   # exige login (testa CONNACK 4)
    python mqtt_sim.py -v                     # mostra cada pacote

Tópicos publicados a cada segundo:
    fabrica/linha1/temperatura    número (texto)        ex.: 72.4
    fabrica/linha1/contador       inteiro
    fabrica/linha1/status         RUN/STOP  (retained)
    fabrica/linha2/dados          JSON  {"temp":..,"pressao":..,"motor":{"rpm":..,"ligado":true},"alarmes":[..]}
    fabrica/info                  JSON  (retained, publicado uma vez)
No FieldProbe, use "fabrica/linha2/dados|motor.rpm" para extrair um campo do JSON.
Qualquer PUBLISH recebido (ex.: escrita do FieldProbe em fabrica/linha1/setpoint) é repassado aos inscritos.
"""

import argparse
import asyncio
import json
import math
import random
import struct
import time

clients = {}      # writer -> {"id": str, "subs": [(filtro, qos)]}
retained = {}     # tópico -> payload (bytes)
args = None


def log(msg):
    if args.verbose:
        print(msg)


def topic_matches(flt, topic):
    if topic.startswith("$") and not flt.startswith("$"):
        return False
    f, t = flt.split("/"), topic.split("/")
    for i, part in enumerate(f):
        if part == "#":
            return True
        if i >= len(t):
            return False
        if part != "+" and part != t[i]:
            return False
    return len(f) == len(t)


def encode_length(n):
    out = bytearray()
    while True:
        d = n % 128
        n //= 128
        if n:
            d |= 0x80
        out.append(d)
        if not n:
            return bytes(out)


def packet(header, body=b""):
    return bytes([header]) + encode_length(len(body)) + body


def mqtt_str(s):
    b = s.encode("utf-8")
    return struct.pack(">H", len(b)) + b


def read_str(data, pos):
    n = struct.unpack(">H", data[pos:pos + 2])[0]
    return data[pos + 2:pos + 2 + n].decode("utf-8"), pos + 2 + n


async def read_packet(reader):
    first = await reader.readexactly(1)
    mult, length = 1, 0
    while True:
        b = (await reader.readexactly(1))[0]
        length += (b & 0x7F) * mult
        if not b & 0x80:
            break
        mult *= 128
    body = await reader.readexactly(length) if length else b""
    return first[0], body


async def deliver(topic, payload, retain_flag=False):
    for writer, info in list(clients.items()):
        qos = max((q for f, q in info["subs"] if topic_matches(f, topic)), default=None)
        if qos is None:
            continue
        qos = min(qos, 1)
        header = 0x30 | (qos << 1) | (1 if retain_flag else 0)
        body = mqtt_str(topic)
        if qos:
            info["pid"] = (info.get("pid", 0) % 65535) + 1
            body += struct.pack(">H", info["pid"])
        try:
            writer.write(packet(header, body + payload))
            await writer.drain()
        except Exception:
            pass


async def handle(reader, writer):
    peer = writer.get_extra_info("peername")
    info = None
    try:
        header, body = await read_packet(reader)
        if header >> 4 != 1:
            return
        # CONNECT
        proto, pos = read_str(body, 0)
        level, flags = body[pos], body[pos + 1]
        keepalive = struct.unpack(">H", body[pos + 2:pos + 4])[0]
        pos += 4
        client_id, pos = read_str(body, pos)
        user = password = None
        if flags & 0x80:
            user, pos = read_str(body, pos)
        if flags & 0x40:
            password, pos = read_str(body, pos)
        code = 0
        if proto != "MQTT" or level != 4:
            code = 1
        elif args.user and (user != args.user or password != args.password):
            code = 4
        writer.write(packet(0x20, bytes([0, code])))
        await writer.drain()
        print(f"[+] {peer} CONNECT clientId={client_id} user={user} keepAlive={keepalive}s -> {'aceito' if code == 0 else f'recusado ({code})'}")
        if code:
            return

        info = {"id": client_id, "subs": []}
        clients[writer] = info

        while True:
            header, body = await read_packet(reader)
            kind = header >> 4
            if kind == 8:  # SUBSCRIBE
                pid = struct.unpack(">H", body[:2])[0]
                pos, codes, new = 2, [], []
                while pos < len(body):
                    flt, pos = read_str(body, pos)
                    qos = min(body[pos], 1)
                    pos += 1
                    info["subs"].append((flt, qos))
                    codes.append(qos)
                    new.append(flt)
                writer.write(packet(0x90, struct.pack(">H", pid) + bytes(codes)))
                await writer.drain()
                log(f"    {client_id} SUBSCRIBE {new}")
                for topic, payload in retained.items():
                    if any(topic_matches(f, topic) for f in new):
                        await deliver_to(writer, info, topic, payload)
            elif kind == 10:  # UNSUBSCRIBE
                pid = struct.unpack(">H", body[:2])[0]
                pos = 2
                while pos < len(body):
                    flt, pos = read_str(body, pos)
                    info["subs"] = [s for s in info["subs"] if s[0] != flt]
                writer.write(packet(0xB0, struct.pack(">H", pid)))
                await writer.drain()
            elif kind == 3:  # PUBLISH
                qos = (header >> 1) & 3
                retain_flag = header & 1
                topic, pos = read_str(body, 0)
                if qos:
                    pid = struct.unpack(">H", body[pos:pos + 2])[0]
                    pos += 2
                payload = body[pos:]
                print(f"    {client_id} PUBLISH {topic} = {payload[:80]!r}{' (retain)' if retain_flag else ''}")
                if qos == 1:
                    writer.write(packet(0x40, struct.pack(">H", pid)))
                elif qos == 2:
                    writer.write(packet(0x50, struct.pack(">H", pid)))
                await writer.drain()
                if retain_flag:
                    if payload:
                        retained[topic] = payload
                    else:
                        retained.pop(topic, None)
                await deliver(topic, payload)
            elif kind == 6:  # PUBREL
                writer.write(packet(0x70, body[:2]))
                await writer.drain()
            elif kind == 12:  # PINGREQ
                writer.write(packet(0xD0))
                await writer.drain()
                log(f"    {client_id} PING")
            elif kind == 14:  # DISCONNECT
                break
    except (asyncio.IncompleteReadError, ConnectionResetError):
        pass
    finally:
        clients.pop(writer, None)
        print(f"[-] {peer} desconectado")
        writer.close()


async def deliver_to(writer, info, topic, payload):
    writer.write(packet(0x31, mqtt_str(topic) + payload))  # QoS 0 + retain
    await writer.drain()


async def publish(topic, value, retain_flag=False):
    payload = value if isinstance(value, bytes) else str(value).encode("utf-8")
    if retain_flag:
        retained[topic] = payload
    await deliver(topic, payload)


async def simulate():
    start = time.monotonic()
    await publish("fabrica/info", json.dumps({"planta": "Planta 1", "linhas": 2, "versao": "1.0"}), True)
    counter = 0
    status = "RUN"
    while True:
        t = time.monotonic() - start
        counter += 1
        if counter % 30 == 0:
            status = "STOP" if status == "RUN" else "RUN"
            await publish("fabrica/linha1/status", status, True)
        elif counter == 1:
            await publish("fabrica/linha1/status", status, True)
        await publish("fabrica/linha1/temperatura", f"{70 + 5 * math.sin(t / 8) + random.uniform(-0.3, 0.3):.2f}")
        await publish("fabrica/linha1/contador", counter)
        await publish("fabrica/linha2/dados", json.dumps({
            "temp": round(55 + 10 * math.sin(t / 12), 2),
            "pressao": round(4.2 + random.uniform(-0.1, 0.1), 3),
            "motor": {"rpm": int(1750 + 30 * math.sin(t / 3)), "ligado": status == "RUN"},
            "alarmes": [] if status == "RUN" else ["Linha parada"],
        }))
        await asyncio.sleep(1)


async def main():
    global args
    parser = argparse.ArgumentParser(description="Broker MQTT simulado para testes")
    parser.add_argument("--host", default="0.0.0.0")
    parser.add_argument("--port", type=int, default=1883)
    parser.add_argument("--user")
    parser.add_argument("--password")
    parser.add_argument("-v", "--verbose", action="store_true")
    args = parser.parse_args()

    server = await asyncio.start_server(handle, args.host, args.port)
    print(f"Broker MQTT simulado em {args.host}:{args.port}{' (com login)' if args.user else ''} · Ctrl+C para sair")
    asyncio.create_task(simulate())
    async with server:
        await server.serve_forever()


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        print("\nEncerrado.")
