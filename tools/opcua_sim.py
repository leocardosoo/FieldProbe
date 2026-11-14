#!/usr/bin/env python3
"""
Servidor OPC UA simulado para testar o FieldProbe.

Requer:  pip install asyncua

Uso:
    python opcua_sim.py                 # opc.tcp://0.0.0.0:4840/fieldprobe/
    python opcua_sim.py --port 4841

Endpoint sem segurança (SecurityPolicy None) e login anônimo, para facilitar o teste.
O diagnóstico do FieldProbe vai apontar exatamente isso como risco — é o comportamento esperado.

Nós criados (namespace "urn:fieldprobe:simulator", normalmente ns=2):
    Linha1/Temperatura   Double   senoide (somente leitura)
    Linha1/Pressao       Float    aleatório (somente leitura)
    Linha1/Contador      UInt32   incrementa a cada 0,5 s
    Linha1/EmOperacao    Boolean  alterna a cada 10 s
    Linha1/Setpoint      Double   ESCREVÍVEL
    Linha1/Receita       String   ESCREVÍVEL
    Linha1/Habilitado    Boolean  ESCREVÍVEL
    Linha1/Vibracao      Double[] array com 5 valores
    Linha2/Motor/Rpm     Int32
    Linha2/Motor/Corrente Double
NodeIds em texto: ns=2;s=Linha1.Temperatura, ns=2;s=Linha1.Setpoint, ...
"""

import argparse
import asyncio
import inspect
import math
import random
import socket
from pathlib import Path

from asyncua import Server, ua


async def _maybe_await(result):
    if inspect.isawaitable(result):
        await result


async def setup_certificate(server, app_uri):
    """
    Gera (uma vez) e carrega um certificado autoassinado.
    Sem certificado, o asyncua devolve certificados diferentes no GetEndpoints e no CreateSession,
    e clientes rigorosos (como o FieldProbe) recusam com
    "Server did not return the same certificate used to create the channel".
    """
    from cryptography.x509.oid import ExtendedKeyUsageOID
    from asyncua.crypto.cert_gen import setup_self_signed_certificate

    folder = Path(__file__).with_name("opcua_sim_pki")
    folder.mkdir(exist_ok=True)
    key_file = folder / "server_key.pem"
    cert_file = folder / "server_cert.der"

    await setup_self_signed_certificate(
        key_file, cert_file, app_uri, socket.gethostname(),
        [ExtendedKeyUsageOID.CLIENT_AUTH, ExtendedKeyUsageOID.SERVER_AUTH],
        {"countryName": "BR", "stateOrProvinceName": "SP", "localityName": "SP", "organizationName": "FieldProbe Simulator"},
    )
    await server.load_certificate(str(cert_file))
    await server.load_private_key(str(key_file))


async def main():
    parser = argparse.ArgumentParser(description="Servidor OPC UA simulado")
    parser.add_argument("--port", type=int, default=4840)
    args = parser.parse_args()

    server = Server()
    await server.init()
    server.set_endpoint(f"opc.tcp://0.0.0.0:{args.port}/fieldprobe/")
    server.set_server_name("FieldProbe OPC UA Simulator")
    app_uri = "urn:fieldprobe:simulator:server"
    await _maybe_await(server.set_application_uri(app_uri))
    server.set_security_policy([ua.SecurityPolicyType.NoSecurity])
    try:
        await setup_certificate(server, app_uri)
        print("Certificado do servidor carregado (pasta tools/opcua_sim_pki).")
    except Exception as ex:
        print(f"Aviso: não foi possível gerar/carregar o certificado ({ex}). Alguns clientes podem recusar a conexão.")

    idx = await server.register_namespace("urn:fieldprobe:simulator")
    objects = server.nodes.objects

    def nid(name):
        return f"ns={idx};s={name}"

    linha1 = await objects.add_object(nid("Linha1"), "Linha1")
    temperatura = await linha1.add_variable(nid("Linha1.Temperatura"), "Temperatura", 25.0)
    pressao = await linha1.add_variable(nid("Linha1.Pressao"), "Pressao", 4.2, ua.VariantType.Float)
    contador = await linha1.add_variable(nid("Linha1.Contador"), "Contador", 0, ua.VariantType.UInt32)
    em_operacao = await linha1.add_variable(nid("Linha1.EmOperacao"), "EmOperacao", True)
    setpoint = await linha1.add_variable(nid("Linha1.Setpoint"), "Setpoint", 50.0)
    receita = await linha1.add_variable(nid("Linha1.Receita"), "Receita", "PADRAO-01")
    habilitado = await linha1.add_variable(nid("Linha1.Habilitado"), "Habilitado", False)
    vibracao = await linha1.add_variable(nid("Linha1.Vibracao"), "Vibracao", [0.0] * 5)
    for node in (setpoint, receita, habilitado):
        await node.set_writable()

    linha2 = await objects.add_object(nid("Linha2"), "Linha2")
    motor = await linha2.add_object(nid("Linha2.Motor"), "Motor")
    rpm = await motor.add_variable(nid("Linha2.Motor.Rpm"), "Rpm", 0, ua.VariantType.Int32)
    corrente = await motor.add_variable(nid("Linha2.Motor.Corrente"), "Corrente", 0.0)

    print(f"Servidor OPC UA em opc.tcp://localhost:{args.port}/fieldprobe/  (namespace ns={idx}) · Ctrl+C para sair")
    print(f"Exemplos de NodeId: {nid('Linha1.Temperatura')}  |  {nid('Linha1.Setpoint')} (escrevível)")

    async with server:
        tick = 0
        while True:
            tick += 1
            t = tick / 2
            await temperatura.write_value(round(70 + 5 * math.sin(t / 6), 3))
            await pressao.write_value(round(4.2 + random.uniform(-0.15, 0.15), 3), ua.VariantType.Float)
            await contador.write_value(tick, ua.VariantType.UInt32)
            if tick % 20 == 0:
                await em_operacao.write_value(not await em_operacao.read_value())
            await vibracao.write_value([round(random.uniform(0, 2), 3) for _ in range(5)])
            running = await em_operacao.read_value()
            await rpm.write_value(int(1750 + 25 * math.sin(t)) if running else 0, ua.VariantType.Int32)
            await corrente.write_value(round(12.5 + random.uniform(-0.4, 0.4), 2) if running else 0.0)
            await asyncio.sleep(0.5)


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        print("\nEncerrado.")
