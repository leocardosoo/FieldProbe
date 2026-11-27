#!/usr/bin/env python3
"""
CLP Siemens S7 simulando uma LINHA DE MONTAGEM completa, com DB1 a DB20 e centenas de variáveis
mudando o tempo todo. Serve para testar o FieldProbe com volume parecido com o de uma fábrica:
"Procurar DBs", árvore, monitoramento de muitas variáveis, escrita de setpoints e alarmes.

Precisa do s7_sim.py na mesma pasta (usa o mesmo protocolo). Só biblioteca padrão do Python.

Uso:
    python s7_linha_sim.py                 # porta 102, CPU 1516
    python s7_linha_sim.py --port 1102     # outra porta
    python s7_linha_sim.py --otimizado 17  # DB17 com "Optimized block access" (recusa PUT/GET)
    python s7_linha_sim.py --mapa          # só mostra o mapa de variáveis (endereços) e sai
    python s7_linha_sim.py --falhas 5      # falhas sozinhas, em média 1 a cada 5 min (padrão: desligado)
    python s7_linha_sim.py --auto-reset 30 # a falha se reconhece sozinha depois de 30 s (padrão: só com 'reset')

A linha:
    Esteira de entrada -> Estação 10 (alimentação) -> Estação 20 (prensa) -> Estação 30 (parafusadeira)
    -> Estação 40 (solda) -> Estação 50 (teste de estanqueidade) -> Estação 60 (visão)
    -> Robôs 1 e 2 -> Paletizador. Mais utilidades (ar, energia, chiller), alarmes, receita, MES.

Comandos no terminal (digite e tecle Enter):
    mapa    -> mostra todos os endereços
    falha   -> provoca a falha de uma estação (aparece no DB16 e no Estado da estação)
    reset   -> reconhece os alarmes (o mesmo que escrever 1 em DB16.DBX0.0)
    parar   -> para a linha (botão de parada) · partir -> volta a produzir
    stop / run / erro -> modo da CPU, igual ao s7_sim.py

Escrita (variáveis marcadas com [E] no mapa): setpoints, receita, ordem do MES etc.
Ex.: escreva 45 em DB1.DBD8 (Setpoint de velocidade) e veja a produção acelerar.
"""

import argparse
import math
import random
import socket
import struct
import sys
import threading
import time

import s7_sim
from s7_sim import CPU_STATE, CPUS, PlcMemory, RC_ACCESS_DENIED, RC_NOT_EXIST, RC_OK, RC_OUT_OF_RANGE

# ============================================================================================
# Estrutura dos DBs (como no TIA Portal, com "Optimized block access" desmarcado)
# Tipos: BOOL, INT, WORD, DINT, REAL, STRING[n]  ·  "E" = escrevível pelo FieldProbe
# ============================================================================================

def station(extra):
    """Variáveis comuns a toda estação + as específicas dela."""
    return [
        ("Em_Ciclo", "BOOL", "", "executando ciclo"),
        ("Pronta", "BOOL", "", "pronta para receber peça"),
        ("Falha", "BOOL", "", "estação em falha"),
        ("Peca_Presente", "BOOL", "", "sensor de presença de peça"),
        ("Modo_Manual", "BOOL", "E", "seleção manual/automático"),
        ("Estado", "INT", "", "0=parada 1=pronta 2=em ciclo 3=falha"),
        ("Codigo_Falha", "INT", "", "0 = sem falha"),
        ("Tempo_Ciclo_s", "REAL", "", "último tempo de ciclo"),
        ("Pecas_OK", "DINT", "", "peças boas no turno"),
        ("Pecas_NOK", "DINT", "", "peças refugadas no turno"),
    ] + extra


DBS = {
    1: ("Linha_Geral", [
        ("Linha_Em_Operacao", "BOOL", "", "linha produzindo"),
        ("Emergencia_Ativa", "BOOL", "", "botão de emergência"),
        ("Modo_Automatico", "BOOL", "", ""),
        ("Porta_Seguranca_Aberta", "BOOL", "", ""),
        ("Turno", "INT", "", "1, 2 ou 3"),
        ("Velocidade_Real_ppm", "REAL", "", "peças por minuto"),
        ("Setpoint_Velocidade_ppm", "REAL", "E", "velocidade desejada (10 a 60)"),
        ("OEE_pct", "REAL", "", ""),
        ("Disponibilidade_pct", "REAL", "", ""),
        ("Performance_pct", "REAL", "", ""),
        ("Qualidade_pct", "REAL", "", ""),
        ("Producao_Total", "DINT", "", "desde a partida"),
        ("Producao_Turno", "DINT", "", ""),
        ("Refugo_Turno", "DINT", "", ""),
        ("Meta_Turno", "DINT", "E", ""),
        ("Tempo_Parada_min", "REAL", "", "no turno"),
    ]),
    2: ("Esteira_Entrada", [
        ("Ligada", "BOOL", "", ""),
        ("Falha_Motor", "BOOL", "", ""),
        ("Sensor_Entrada", "BOOL", "", ""),
        ("Sensor_Saida", "BOOL", "", ""),
        ("Fila_Pecas", "INT", "", "peças esperando"),
        ("Velocidade_m_min", "REAL", "", ""),
        ("Corrente_Motor_A", "REAL", "", ""),
        ("Temperatura_Motor_C", "REAL", "", ""),
        ("Horas_Operacao", "DINT", "", ""),
    ]),
    3: ("Estacao10_Alimentacao", station([
        ("Garra_Fechada", "BOOL", "", ""),
        ("Cilindro_Avancado", "BOOL", "", ""),
        ("Vacuo_OK", "BOOL", "", ""),
        ("Vacuo_kPa", "REAL", "", ""),
        ("Pecas_No_Magazine", "INT", "", ""),
    ])),
    4: ("Estacao20_Prensa", station([
        ("Prensa_Em_Cima", "BOOL", "", ""),
        ("Cortina_Luz_OK", "BOOL", "", ""),
        ("Forca_kN", "REAL", "", "força do último ciclo"),
        ("Forca_Max_kN", "REAL", "E", "limite de aprovação"),
        ("Curso_mm", "REAL", "", ""),
        ("Pressao_Hidraulica_bar", "REAL", "", ""),
        ("Temperatura_Oleo_C", "REAL", "", ""),
        ("Golpes_Total", "DINT", "", ""),
    ])),
    5: ("Estacao30_Parafusadeira", station([
        ("Resultado_OK", "BOOL", "", "último aperto"),
        ("Programa", "INT", "E", "programa de aperto (1 a 8)"),
        ("Torque_Nm", "REAL", "", "último aperto"),
        ("Angulo_graus", "REAL", "", ""),
        ("Torque_Min_Nm", "REAL", "E", ""),
        ("Torque_Max_Nm", "REAL", "E", ""),
        ("Parafusos_Total", "DINT", "", ""),
    ])),
    6: ("Estacao40_Solda", station([
        ("Troca_Eletrodo", "BOOL", "", "eletrodo no fim da vida"),
        ("Agua_Refrigeracao_OK", "BOOL", "", ""),
        ("Tempo_Solda_ms", "INT", "", ""),
        ("Corrente_kA", "REAL", "", ""),
        ("Tensao_V", "REAL", "", ""),
        ("Temperatura_Ponteira_C", "REAL", "", ""),
        ("Pontos_Desde_Troca", "DINT", "", "troca a cada 5000"),
    ])),
    7: ("Estacao50_Estanqueidade", station([
        ("Resultado_OK", "BOOL", "", ""),
        ("Pressao_Teste_mbar", "REAL", "", ""),
        ("Vazamento_mbar_s", "REAL", "", ""),
        ("Limite_Vazamento", "REAL", "E", ""),
    ])),
    8: ("Estacao60_Visao", station([
        ("Camera_Online", "BOOL", "", ""),
        ("Iluminacao_OK", "BOOL", "", ""),
        ("Score_pct", "REAL", "", "nota da última inspeção"),
        ("Score_Minimo_pct", "REAL", "E", ""),
        ("Inspecoes", "DINT", "", ""),
    ])),
    9: ("Robo1_Manipulacao", [
        ("Em_Ciclo", "BOOL", "", ""),
        ("Em_Home", "BOOL", "", ""),
        ("Falha", "BOOL", "", ""),
        ("Motores_Ligados", "BOOL", "", ""),
        ("Programa", "INT", "", ""),
        ("Override_pct", "INT", "E", "velocidade do robô (0 a 100)"),
        ("Posicao_X_mm", "REAL", "", ""),
        ("Posicao_Y_mm", "REAL", "", ""),
        ("Posicao_Z_mm", "REAL", "", ""),
        ("Torque_Eixo1_pct", "REAL", "", ""),
        ("Ciclos", "DINT", "", ""),
    ]),
    10: ("Robo2_Paletizacao", [
        ("Em_Ciclo", "BOOL", "", ""),
        ("Em_Home", "BOOL", "", ""),
        ("Falha", "BOOL", "", ""),
        ("Motores_Ligados", "BOOL", "", ""),
        ("Programa", "INT", "", ""),
        ("Override_pct", "INT", "E", ""),
        ("Posicao_X_mm", "REAL", "", ""),
        ("Posicao_Y_mm", "REAL", "", ""),
        ("Posicao_Z_mm", "REAL", "", ""),
        ("Torque_Eixo1_pct", "REAL", "", ""),
        ("Ciclos", "DINT", "", ""),
    ]),
    11: ("Paletizador", [
        ("Palete_Cheio", "BOOL", "", ""),
        ("Palete_Presente", "BOOL", "", ""),
        ("Caixas_Por_Camada", "INT", "E", ""),
        ("Camadas_Por_Palete", "INT", "E", ""),
        ("Camada_Atual", "INT", "", ""),
        ("Caixas_No_Palete", "INT", "", ""),
        ("Paletes_Completos", "DINT", "", ""),
    ]),
    12: ("Utilidades_Ar", [
        ("Compressor1_Ligado", "BOOL", "", ""),
        ("Compressor2_Ligado", "BOOL", "", ""),
        ("Alarme_Pressao_Baixa", "BOOL", "", ""),
        ("Pressao_bar", "REAL", "", ""),
        ("Vazao_Nm3_h", "REAL", "", ""),
        ("Consumo_Total_Nm3", "REAL", "", ""),
        ("Ponto_Orvalho_C", "REAL", "", ""),
    ]),
    13: ("Energia_Painel", [
        ("Tensao_L1_V", "REAL", "", ""),
        ("Tensao_L2_V", "REAL", "", ""),
        ("Tensao_L3_V", "REAL", "", ""),
        ("Corrente_L1_A", "REAL", "", ""),
        ("Corrente_L2_A", "REAL", "", ""),
        ("Corrente_L3_A", "REAL", "", ""),
        ("Potencia_Ativa_kW", "REAL", "", ""),
        ("Fator_Potencia", "REAL", "", ""),
        ("Frequencia_Hz", "REAL", "", ""),
        ("Energia_Total_kWh", "REAL", "", ""),
    ]),
    14: ("Ambiente_Painel", [
        ("Ventilador_Painel", "BOOL", "", ""),
        ("Porta_Painel_Aberta", "BOOL", "", ""),
        ("Temperatura_Ambiente_C", "REAL", "", ""),
        ("Umidade_pct", "REAL", "", ""),
        ("Temperatura_Painel_C", "REAL", "", ""),
    ]),
    15: ("Chiller", [
        ("Bomba_Ligada", "BOOL", "", ""),
        ("Compressor_Ligado", "BOOL", "", ""),
        ("Alarme_Vazao", "BOOL", "", ""),
        ("Temperatura_Entrada_C", "REAL", "", ""),
        ("Temperatura_Saida_C", "REAL", "", ""),
        ("Setpoint_C", "REAL", "E", ""),
        ("Vazao_l_min", "REAL", "", ""),
    ]),
    16: ("Alarmes", [
        ("Reset_Alarmes", "BOOL", "E", "escreva 1 para reconhecer"),
        ("Buzina", "BOOL", "", ""),
        ("Torre_Verde", "BOOL", "", ""),
        ("Torre_Amarela", "BOOL", "", ""),
        ("Torre_Vermelha", "BOOL", "", ""),
        ("Palavra_Alarmes", "WORD", "", "1 bit por estação/equipamento"),
        ("Alarmes_Ativos", "INT", "", ""),
        ("Ultimo_Codigo", "INT", "", ""),
        ("Total_Alarmes_Turno", "DINT", "", ""),
    ]),
    17: ("Rastreabilidade", [
        ("Peca_Rastreada", "BOOL", "", ""),
        ("Numero_Serie", "STRING[20]", "", "última peça (endereço DBB, tipo String, Tam. 20)"),
        ("Codigo_Produto", "STRING[12]", "", ""),
        ("Lote", "DINT", "", ""),
        ("Resultado_Final", "INT", "", "1=OK 2=NOK"),
    ]),
    18: ("Receita", [
        ("Carregar_Receita", "BOOL", "E", "escreva 1 para aplicar"),
        ("Receita_Ativa", "INT", "E", "1 a 5"),
        ("Nome_Receita", "STRING[16]", "", ""),
        ("Torque_Alvo_Nm", "REAL", "", ""),
        ("Forca_Alvo_kN", "REAL", "", ""),
        ("Tempo_Ciclo_Alvo_s", "REAL", "", ""),
    ]),
    19: ("Manutencao", [
        ("Manutencao_Pendente", "BOOL", "", ""),
        ("Horas_Prensa", "DINT", "", ""),
        ("Horas_Robo1", "DINT", "", ""),
        ("Horas_Robo2", "DINT", "", ""),
        ("Horas_Esteira", "DINT", "", ""),
        ("Proxima_Preventiva_h", "REAL", "", ""),
    ]),
    20: ("Interface_MES", [
        ("MES_Online", "BOOL", "", ""),
        ("Nova_Ordem", "BOOL", "E", "MES sinaliza ordem nova"),
        ("Ordem_Aceita", "BOOL", "", "CLP confirma"),
        ("Heartbeat", "INT", "", "incrementa a cada segundo"),
        ("Ordem_Producao", "DINT", "E", ""),
        ("Quantidade_Pedida", "DINT", "E", ""),
        ("Quantidade_Produzida", "DINT", "", ""),
    ]),
}

STATION_DBS = {3: "Estação 10", 4: "Estação 20", 5: "Estação 30", 6: "Estação 40", 7: "Estação 50", 8: "Estação 60"}


class Var:
    def __init__(self, db, name, kind, writable, comment, offset, bit=0, length=0):
        self.db, self.name, self.kind, self.writable, self.comment = db, name, kind, writable, comment
        self.offset, self.bit, self.length = offset, bit, length

    @property
    def address(self):
        if self.kind == "BOOL":
            return f"DB{self.db}.DBX{self.offset}.{self.bit}"
        if self.kind in ("INT", "WORD"):
            return f"DB{self.db}.DBW{self.offset}"
        if self.kind.startswith("STRING"):
            return f"DB{self.db}.DBB{self.offset}"
        return f"DB{self.db}.DBD{self.offset}"


def build_layout():
    """Calcula os offsets como o TIA faz em DB não otimizado: BOOLs juntos em bits, o resto em byte par."""
    layout, sizes = {}, {}
    for db, (_, variables) in DBS.items():
        pos, bit = 0, 0
        vars_db = {}
        for name, kind, flags, comment in variables:
            if kind == "BOOL":
                vars_db[name] = Var(db, name, kind, flags == "E", comment, pos, bit)
                bit += 1
                if bit == 8:
                    pos, bit = pos + 1, 0
                continue
            if bit:
                pos, bit = pos + 1, 0
            pos += pos % 2  # alinha em byte par
            if kind.startswith("STRING"):
                length = int(kind[7:-1])
                vars_db[name] = Var(db, name, kind, flags == "E", comment, pos, length=length)
                pos += length + 2
            else:
                vars_db[name] = Var(db, name, kind, flags == "E", comment, pos)
                pos += 2 if kind in ("INT", "WORD") else 4
        if bit:
            pos += 1
        pos += pos % 2
        layout[db] = vars_db
        sizes[db] = pos
    return layout, sizes


LAYOUT, SIZES = build_layout()


def print_map():
    for db, (title, _) in DBS.items():
        print(f"\nDB{db} · {title} ({SIZES[db]} bytes)")
        for v in LAYOUT[db].values():
            tipo = v.kind
            flag = "[E]" if v.writable else "   "
            comment = f"  · {v.comment}" if v.comment else ""
            print(f"  {flag} {v.address:<16} {tipo:<11} {v.name}{comment}")


# ============================================================================================
# Memória + "programa" do CLP
# ============================================================================================

class LineMemory(PlcMemory):
    def __init__(self, optimized=(), putget=True, fault_minutes=0.0, auto_reset=0.0):
        super().__init__(dbs=(), optimized=optimized, putget=putget)
        self.fault_minutes = fault_minutes  # 0 = sem falhas sozinhas (só pelo comando 'falha')
        self.auto_reset = auto_reset        # 0 = a falha fica até 'reset' ou DB16.DBX0.0
        self.db = {db: bytearray(size) for db, size in SIZES.items()}
        self.areas = {0x81: bytearray(128), 0x82: bytearray(128), 0x83: bytearray(512)}
        self.running_line = True
        self.fault_request = False
        self.reset_request = False
        self.faults = {}  # db da estação -> (código, instante)
        self._t0 = time.time()
        self._init_values()

    # ---------- acesso por nome ----------
    def set(self, db, name, value):
        v = LAYOUT[db][name]
        buf = self.db[db]
        if v.kind == "BOOL":
            if value:
                buf[v.offset] |= 1 << v.bit
            else:
                buf[v.offset] &= ~(1 << v.bit) & 0xFF
        elif v.kind == "INT":
            struct.pack_into(">h", buf, v.offset, int(max(-32768, min(32767, value))))
        elif v.kind == "WORD":
            struct.pack_into(">H", buf, v.offset, int(value) & 0xFFFF)
        elif v.kind == "DINT":
            value = (int(value) + 2**31) % 2**32 - 2**31  # estoura como DINT do CLP
            struct.pack_into(">i", buf, v.offset, value)
        elif v.kind == "REAL":
            struct.pack_into(">f", buf, v.offset, float(value))
        else:  # STRING[n]: byte 0 = tamanho máximo, byte 1 = tamanho atual
            text = str(value).encode("ascii", "replace")[: v.length]
            buf[v.offset] = v.length
            buf[v.offset + 1] = len(text)
            buf[v.offset + 2: v.offset + 2 + v.length] = text.ljust(v.length, b"\x00")

    def get(self, db, name):
        v = LAYOUT[db][name]
        buf = self.db[db]
        if v.kind == "BOOL":
            return bool(buf[v.offset] >> v.bit & 1)
        if v.kind == "INT":
            return struct.unpack_from(">h", buf, v.offset)[0]
        if v.kind == "WORD":
            return struct.unpack_from(">H", buf, v.offset)[0]
        if v.kind == "DINT":
            return struct.unpack_from(">i", buf, v.offset)[0]
        if v.kind == "REAL":
            return struct.unpack_from(">f", buf, v.offset)[0]
        return bytes(buf[v.offset + 2: v.offset + 2 + buf[v.offset + 1]]).decode("ascii", "replace")

    def add(self, db, name, amount=1):
        self.set(db, name, self.get(db, name) + amount)

    def _init_values(self):
        s = self.set
        s(1, "Modo_Automatico", True); s(1, "Turno", 1); s(1, "Setpoint_Velocidade_ppm", 30.0); s(1, "Meta_Turno", 12000)
        s(2, "Ligada", True); s(2, "Horas_Operacao", 18432)
        for db in STATION_DBS:
            s(db, "Pronta", True); s(db, "Estado", 1); s(db, "Tempo_Ciclo_s", 2.0)
        s(3, "Pecas_No_Magazine", 250)
        s(4, "Forca_Max_kN", 85.0); s(4, "Cortina_Luz_OK", True); s(4, "Prensa_Em_Cima", True); s(4, "Golpes_Total", 1_254_870)
        s(5, "Programa", 3); s(5, "Torque_Min_Nm", 11.5); s(5, "Torque_Max_Nm", 13.5); s(5, "Parafusos_Total", 3_870_115)
        s(6, "Agua_Refrigeracao_OK", True); s(6, "Pontos_Desde_Troca", 3200)
        s(7, "Limite_Vazamento", 0.50)
        s(8, "Camera_Online", True); s(8, "Iluminacao_OK", True); s(8, "Score_Minimo_pct", 85.0)
        for db in (9, 10):
            s(db, "Em_Home", True); s(db, "Motores_Ligados", True); s(db, "Override_pct", 80); s(db, "Programa", 1 if db == 9 else 5)
        s(9, "Ciclos", 845_210); s(10, "Ciclos", 214_002)
        s(11, "Caixas_Por_Camada", 8); s(11, "Camadas_Por_Palete", 5); s(11, "Palete_Presente", True); s(11, "Paletes_Completos", 1530)
        s(12, "Compressor1_Ligado", True); s(12, "Consumo_Total_Nm3", 1_204_500.0)
        s(13, "Energia_Total_kWh", 912_345.0)
        s(14, "Ventilador_Painel", True)
        s(15, "Bomba_Ligada", True); s(15, "Compressor_Ligado", True); s(15, "Setpoint_C", 18.0)
        s(16, "Torre_Verde", True)
        s(17, "Codigo_Produto", "CX-4471-B"); s(17, "Lote", 260411)
        s(18, "Receita_Ativa", 2); s(18, "Nome_Receita", "MODELO_B_STD"); s(18, "Torque_Alvo_Nm", 12.5)
        s(18, "Forca_Alvo_kN", 72.0); s(18, "Tempo_Ciclo_Alvo_s", 2.0)
        s(19, "Horas_Prensa", 22110); s(19, "Horas_Robo1", 19870); s(19, "Horas_Robo2", 15422); s(19, "Horas_Esteira", 18432)
        s(19, "Proxima_Preventiva_h", 312.0)
        s(20, "MES_Online", True); s(20, "Ordem_Producao", 4500123); s(20, "Quantidade_Pedida", 5000)

    # ---------- programa ----------
    def simulate(self):
        start = time.time()
        last = start
        part_timer = 0.0
        last_second = int(start)
        serial = 100000
        down_time = 0.0
        while True:
            time.sleep(0.2)
            now = time.time()
            dt, last = now - last, now
            if not CPU_STATE.running:  # STOP: nada muda
                continue
            t = now - start
            with self.lock:
                g, s, add = self.get, self.set, self.add

                # --- comandos (terminal ou escrita do FieldProbe) ---
                if self.reset_request or g(16, "Reset_Alarmes"):
                    self.reset_request = False
                    s(16, "Reset_Alarmes", False)
                    for db in list(self.faults):
                        self._clear_fault(db)
                if self.fault_request:
                    self.fault_request = False
                    self._raise_fault(random.choice(list(STATION_DBS)))
                elif self.fault_minutes > 0 and random.random() < dt / (self.fault_minutes * 60):
                    self._raise_fault(random.choice(list(STATION_DBS)))
                if self.auto_reset > 0:
                    for db, (_, since) in list(self.faults.items()):
                        if time.time() - since >= self.auto_reset:
                            self._clear_fault(db)
                if g(18, "Carregar_Receita"):
                    s(18, "Carregar_Receita", False)
                    self._load_recipe(g(18, "Receita_Ativa"))
                if g(20, "Nova_Ordem"):
                    s(20, "Nova_Ordem", False); s(20, "Ordem_Aceita", True); s(20, "Quantidade_Produzida", 0)

                # --- estado da linha ---
                faulted = bool(self.faults)
                producing = self.running_line and not faulted
                s(1, "Linha_Em_Operacao", producing)
                setpoint = max(10.0, min(60.0, g(1, "Setpoint_Velocidade_ppm")))
                speed = setpoint * (0.93 + 0.05 * math.sin(t / 30)) if producing else 0.0
                s(1, "Velocidade_Real_ppm", speed)
                if not producing:
                    down_time += dt / 60
                s(1, "Tempo_Parada_min", down_time)

                # --- uma peça a cada 60/velocidade segundos ---
                if producing:
                    part_timer += dt * speed / 60
                while part_timer >= 1:
                    part_timer -= 1
                    serial += 1
                    self._produce_part(serial, t)

                # --- estações e robôs (valores "ao vivo") ---
                cycle_phase = (t * max(speed, 1) / 60) % 1
                for db in STATION_DBS:
                    in_fault = db in self.faults
                    s(db, "Em_Ciclo", producing and cycle_phase < 0.7)
                    s(db, "Peca_Presente", producing and cycle_phase < 0.85)
                    s(db, "Pronta", not in_fault and (not producing or cycle_phase >= 0.7))
                    s(db, "Falha", in_fault)
                    s(db, "Estado", 3 if in_fault else 2 if producing and cycle_phase < 0.7 else 1)
                    s(db, "Codigo_Falha", self.faults[db][0] if in_fault else 0)
                s(3, "Garra_Fechada", producing and cycle_phase < 0.5)
                s(3, "Cilindro_Avancado", producing and 0.2 < cycle_phase < 0.6)
                vac = random.uniform(-82, -76) if producing and cycle_phase < 0.5 else random.uniform(-2, 0)
                s(3, "Vacuo_kPa", vac); s(3, "Vacuo_OK", vac < -70)
                s(4, "Prensa_Em_Cima", not (producing and 0.3 < cycle_phase < 0.6))
                s(4, "Curso_mm", 120 * math.sin(math.pi * cycle_phase) if producing else 0.0)
                s(4, "Pressao_Hidraulica_bar", random.uniform(178, 182) if producing else random.uniform(40, 45))
                oil = g(4, "Temperatura_Oleo_C") or 38.0
                s(4, "Temperatura_Oleo_C", oil + ((48 if producing else 35) - oil) * dt / 120)
                s(6, "Temperatura_Ponteira_C", 180 + 120 * math.sin(math.pi * cycle_phase) if producing else 35.0)
                s(7, "Pressao_Teste_mbar", 500 * min(1.0, cycle_phase * 3) if producing else 0.0)

                for db, base in ((9, (400, 0, 900)), (10, (1200, 600, 400))):
                    robot_fault = g(db, "Falha")
                    moving = producing and not robot_fault
                    override = max(0, min(100, g(db, "Override_pct")))
                    phase = t * override / 100
                    s(db, "Em_Ciclo", moving)
                    s(db, "Em_Home", not moving)
                    s(db, "Posicao_X_mm", base[0] + (350 * math.sin(phase / 2) if moving else 0))
                    s(db, "Posicao_Y_mm", base[1] + (250 * math.cos(phase / 3) if moving else 0))
                    s(db, "Posicao_Z_mm", base[2] + (180 * math.sin(phase) if moving else 0))
                    s(db, "Torque_Eixo1_pct", abs(45 * math.sin(phase)) + random.uniform(0, 5) if moving else 2.0)

                # --- esteira ---
                s(2, "Ligada", producing)
                s(2, "Velocidade_m_min", 12 * speed / 30 if producing else 0.0)
                s(2, "Corrente_Motor_A", random.uniform(4.6, 5.4) if producing else 0.2)
                motor = g(2, "Temperatura_Motor_C") or 30.0
                s(2, "Temperatura_Motor_C", motor + ((55 if producing else 28) - motor) * dt / 180)
                s(2, "Sensor_Entrada", producing and cycle_phase < 0.3)
                s(2, "Sensor_Saida", producing and 0.5 < cycle_phase < 0.8)
                s(2, "Fila_Pecas", int(4 + 3 * math.sin(t / 45)) if producing else g(2, "Fila_Pecas"))

                # --- utilidades ---
                air = 6.6 + 0.3 * math.sin(t / 20) + random.uniform(-0.05, 0.05) - (0.4 if producing else 0)
                s(12, "Pressao_bar", air); s(12, "Alarme_Pressao_Baixa", air < 5.8)
                s(12, "Compressor2_Ligado", air < 6.3)
                flow = (310 if producing else 90) + random.uniform(-15, 15)
                s(12, "Vazao_Nm3_h", flow); add(12, "Consumo_Total_Nm3", flow * dt / 3600)
                s(12, "Ponto_Orvalho_C", random.uniform(2.5, 3.5))

                load = 0.85 if producing else 0.25
                for phase_name, offset in (("L1", 0), ("L2", 1.2), ("L3", -0.8)):
                    s(13, f"Tensao_{phase_name}_V", 220 + offset + random.uniform(-1.5, 1.5))
                    s(13, f"Corrente_{phase_name}_A", 140 * load + random.uniform(-4, 4) + offset)
                power = 3 ** 0.5 * 380 * 140 * load * 0.92 / 1000 + random.uniform(-1, 1)
                s(13, "Potencia_Ativa_kW", power); s(13, "Fator_Potencia", random.uniform(0.91, 0.94))
                s(13, "Frequencia_Hz", 60 + random.uniform(-0.03, 0.03)); add(13, "Energia_Total_kWh", power * dt / 3600)

                s(14, "Temperatura_Ambiente_C", 27 + 3 * math.sin(t / 600) + random.uniform(-0.1, 0.1))
                s(14, "Umidade_pct", 62 + 6 * math.sin(t / 900))
                panel = 34 + 4 * load + random.uniform(-0.2, 0.2)
                s(14, "Temperatura_Painel_C", panel); s(14, "Ventilador_Painel", panel > 33)

                chiller_sp = g(15, "Setpoint_C")
                s(15, "Temperatura_Saida_C", chiller_sp + random.uniform(-0.4, 0.4))
                s(15, "Temperatura_Entrada_C", chiller_sp + (5.5 if producing else 2.0) + random.uniform(-0.3, 0.3))
                s(15, "Vazao_l_min", random.uniform(118, 124)); s(15, "Alarme_Vazao", False)

                # --- alarmes / sinaleiro ---
                word = 0
                for i, db in enumerate(STATION_DBS):
                    if db in self.faults:
                        word |= 1 << i
                s(16, "Palavra_Alarmes", word)
                s(16, "Alarmes_Ativos", bin(word).count("1"))
                blink = int(t * 2) % 2 == 0
                s(16, "Torre_Verde", producing)
                s(16, "Torre_Amarela", not producing and not faulted)
                s(16, "Torre_Vermelha", faulted and blink)
                s(16, "Buzina", faulted and any(now - since < 5 for _, since in self.faults.values()))

                # --- MES / horímetros ---
                if int(now) != last_second:
                    last_second = int(now)
                    s(20, "Heartbeat", (g(20, "Heartbeat") + 1) % 32768)
                    if producing and last_second % 3600 == 0:
                        for name in ("Horas_Prensa", "Horas_Robo1", "Horas_Robo2", "Horas_Esteira"):
                            add(19, name)
                        add(2, "Horas_Operacao")
                        s(19, "Proxima_Preventiva_h", max(0.0, g(19, "Proxima_Preventiva_h") - 1))
                s(19, "Manutencao_Pendente", g(19, "Proxima_Preventiva_h") < 24 or g(6, "Troca_Eletrodo"))

                self._update_oee()

    def _produce_part(self, serial, t):
        g, s, add = self.get, self.set, self.add
        ok = True
        # Estação 10: tira peça do magazine (reabastece quando acaba)
        mag = g(3, "Pecas_No_Magazine") - 1
        s(3, "Pecas_No_Magazine", mag if mag > 0 else 250)
        # Estação 20: prensa
        force = random.gauss(72, 3.5)
        s(4, "Forca_kN", force); add(4, "Golpes_Total")
        st_ok = force <= g(4, "Forca_Max_kN")
        ok &= self._count(4, st_ok)
        # Estação 30: aperto
        torque = random.gauss(12.5, 0.35)
        s(5, "Torque_Nm", torque); s(5, "Angulo_graus", random.gauss(720, 25)); add(5, "Parafusos_Total", 4)
        st_ok = g(5, "Torque_Min_Nm") <= torque <= g(5, "Torque_Max_Nm")
        s(5, "Resultado_OK", st_ok)
        ok &= self._count(5, st_ok)
        # Estação 40: solda
        s(6, "Corrente_kA", random.gauss(9.2, 0.2)); s(6, "Tensao_V", random.gauss(2.1, 0.05))
        s(6, "Tempo_Solda_ms", int(random.gauss(240, 6)))
        points = g(6, "Pontos_Desde_Troca") + 6
        s(6, "Pontos_Desde_Troca", points); s(6, "Troca_Eletrodo", points >= 5000)
        if points >= 5200:
            s(6, "Pontos_Desde_Troca", 0)  # "operador trocou o eletrodo"
        ok &= self._count(6, random.random() > 0.004)
        # Estação 50: estanqueidade
        leak = abs(random.gauss(0.18, 0.1))
        s(7, "Vazamento_mbar_s", leak)
        st_ok = leak <= g(7, "Limite_Vazamento")
        s(7, "Resultado_OK", st_ok)
        ok &= self._count(7, st_ok)
        # Estação 60: visão
        score = min(100.0, random.gauss(95, 3))
        s(8, "Score_pct", score); add(8, "Inspecoes")
        ok &= self._count(8, score >= g(8, "Score_Minimo_pct"))
        self._count(3, True)
        for db in STATION_DBS:
            s(db, "Tempo_Ciclo_s", 60 / max(g(1, "Velocidade_Real_ppm"), 1) * random.uniform(0.97, 1.03))
        # Robôs
        add(9, "Ciclos")
        # Linha / rastreabilidade
        add(1, "Producao_Total"); add(1, "Producao_Turno")
        if not ok:
            add(1, "Refugo_Turno")
        s(17, "Numero_Serie", f"SN{time.strftime('%y%m%d')}-{serial}")
        s(17, "Resultado_Final", 1 if ok else 2)
        s(17, "Peca_Rastreada", True)
        if ok:
            add(20, "Quantidade_Produzida")
            add(10, "Ciclos")
            boxes = g(11, "Caixas_No_Palete") + 1
            per_layer = max(1, g(11, "Caixas_Por_Camada"))
            layers = max(1, g(11, "Camadas_Por_Palete"))
            if boxes >= per_layer * layers:
                boxes = 0
                add(11, "Paletes_Completos")
            s(11, "Caixas_No_Palete", boxes)
            s(11, "Camada_Atual", boxes // per_layer + 1)
            s(11, "Palete_Cheio", boxes == per_layer * layers - 1)

    def _count(self, db, ok):
        self.add(db, "Pecas_OK" if ok else "Pecas_NOK")
        return ok

    def _raise_fault(self, db):
        if db in self.faults:
            return
        codes = {3: (101, 102), 4: (201, 202, 203), 5: (301, 302), 6: (401, 402), 7: (501, 502), 8: (601, 602)}
        code = random.choice(codes[db])
        self.faults[db] = (code, time.time())
        self.set(16, "Ultimo_Codigo", code)
        self.add(16, "Total_Alarmes_Turno")
        if db == 4 and random.random() < 0.5:
            self.set(9, "Falha", True)  # às vezes a parada da prensa derruba o robô junto
        print(f"    ALARME: {STATION_DBS[db]} em falha (código {code}) · linha parada · 'reset' ou DB16.DBX0.0 = 1 para reconhecer")

    def _clear_fault(self, db):
        self.faults.pop(db, None)
        self.set(9, "Falha", False)
        self.set(10, "Falha", False)
        print(f"    {STATION_DBS[db]}: falha reconhecida, linha voltando")

    def _load_recipe(self, recipe):
        recipes = {
            1: ("MODELO_A_STD", 10.5, 65.0, 1.8),
            2: ("MODELO_B_STD", 12.5, 72.0, 2.0),
            3: ("MODELO_B_PLUS", 13.0, 76.0, 2.2),
            4: ("MODELO_C_PESADO", 15.0, 82.0, 2.6),
            5: ("TESTE_ENGENHARIA", 12.0, 70.0, 3.0),
        }
        name, torque, force, cycle = recipes.get(recipe, recipes[2])
        s = self.set
        s(18, "Nome_Receita", name); s(18, "Torque_Alvo_Nm", torque); s(18, "Forca_Alvo_kN", force)
        s(18, "Tempo_Ciclo_Alvo_s", cycle)
        s(5, "Torque_Min_Nm", torque - 1.0); s(5, "Torque_Max_Nm", torque + 1.0)
        print(f"    Receita {recipe} carregada: {name}")

    def _update_oee(self):
        g, s = self.get, self.set
        total = g(1, "Producao_Turno")
        scrap = g(1, "Refugo_Turno")
        quality = 100.0 * (total - scrap) / total if total else 100.0
        elapsed = max(1.0, time.time() - self._t0) / 60
        availability = max(0.0, 100.0 * (1 - g(1, "Tempo_Parada_min") / elapsed))
        target = g(1, "Setpoint_Velocidade_ppm") * max(0.01, elapsed - g(1, "Tempo_Parada_min"))
        performance = min(100.0, 100.0 * total / target) if target > 0 else 0.0
        s(1, "Qualidade_pct", quality)
        s(1, "Disponibilidade_pct", availability)
        s(1, "Performance_pct", performance)
        s(1, "OEE_pct", availability * performance * quality / 10000)


def console(memory):
    """Comandos da linha + os do s7_sim (stop, run, erro)."""
    while True:
        try:
            command = input().strip().lower()
        except EOFError:
            return
        if command == "mapa":
            print_map()
        elif command == "falha":
            memory.fault_request = True
        elif command == "reset":
            memory.reset_request = True
        elif command == "parar":
            memory.running_line = False
            print("    Linha parada pelo operador")
        elif command == "partir":
            memory.running_line = True
            print("    Linha em produção")
        elif command == "stop":
            CPU_STATE.set_running(False, [0x4304])
            print("    CPU em STOP (comando do PG)")
        elif command == "run":
            CPU_STATE.set_running(True, [0x4301, 0x4302])
            print("    CPU em RUN")
        elif command == "erro":
            CPU_STATE.set_running(False, [0x2942, 0x4563])
            print("    Falha de acesso à periferia registrada · CPU em STOP")
        elif command:
            print("    Comandos: mapa · falha · reset · parar · partir · stop · run · erro")


def main():
    parser = argparse.ArgumentParser(description="CLP Siemens S7 simulando uma linha de montagem (DB1 a DB20)")
    parser.add_argument("--host", default="0.0.0.0")
    parser.add_argument("--port", type=int, default=102)
    parser.add_argument("--cpu", choices=CPUS.keys(), default="1500", help="Modelo simulado (padrão: 1500)")
    parser.add_argument("--name", default="PLC_Linha_Montagem", help="Nome da CPU no projeto")
    parser.add_argument("--otimizado", default="", help="DBs com acesso otimizado (recusam PUT/GET), ex.: 17")
    parser.add_argument("--sem-putget", action="store_true", help="CPU com PUT/GET desabilitado")
    parser.add_argument("--mapa", action="store_true", help="Mostra o mapa de endereços e sai")
    parser.add_argument("--falhas", type=float, default=0.0,
                        help="Falhas sozinhas, em média 1 a cada N minutos (padrão 0 = só com o comando 'falha')")
    parser.add_argument("--auto-reset", type=float, default=0.0,
                        help="Reconhece a falha sozinho depois de N segundos (padrão 0 = só com 'reset')")
    parser.add_argument("-v", "--verbose", action="store_true")
    args = parser.parse_args()

    if args.mapa:
        print_map()
        return

    optimized = [int(x) for x in args.otimizado.replace(";", ",").split(",") if x.strip()]
    cpu = CPUS[args.cpu]
    memory = LineMemory(optimized, not args.sem_putget, args.falhas, args.auto_reset)
    threading.Thread(target=memory.simulate, daemon=True).start()

    server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    try:
        server.bind((args.host, args.port))
    except OSError as error:
        sys.exit(f"Não consegui abrir a porta {args.port}: {error}. Outro simulador ou o PLCSIM já está usando? Tente --port 1102.")
    server.listen()

    total_vars = sum(len(v) for v in LAYOUT.values())
    major, minor, patch = cpu["fw"]
    print(f"Siemens {cpu['type']} (FW V{major}.{minor}.{patch}) · {args.name} · {args.host}:{args.port} · Ctrl+C para sair")
    print(f"Linha de montagem: 20 DBs, {total_vars} variáveis, {sum(SIZES.values())} bytes no total")
    for db, (title, _) in DBS.items():
        note = " · OTIMIZADO" if db in optimized else ""
        print(f"  DB{db:<3} {title:<26} {SIZES[db]:>3} bytes{note}")
    print("Comandos: mapa · falha · reset · parar · partir · stop · run · erro")
    threading.Thread(target=console, args=(memory,), daemon=True).start()

    try:
        while True:
            conn, peer = server.accept()
            conn.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
            threading.Thread(target=s7_sim.client_thread,
                             args=(conn, f"{peer[0]}:{peer[1]}", memory, cpu, args.name, args.verbose),
                             daemon=True).start()
    except KeyboardInterrupt:
        print("\nEncerrado.")


if __name__ == "__main__":
    main()
