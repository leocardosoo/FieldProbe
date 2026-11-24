# FieldProbe

**Teste de comunicação com devices industriais, numa ferramenta só.**
Conecte, navegue, monitore, escreva e diagnostique CLPs, inversores, gateways, servidores OPC UA e brokers MQTT, com log do tráfego, tendência e gravação em CSV.

| Protocolo | Navegação | Leitura / escrita | Monitoramento | Diagnóstico |
|---|---|---|---|---|
| **Modbus TCP** | árvore sugerida | ✔ todos os tipos, 4 ordens de byte | polling | FC 43 (identificação), segurança |
| **Modbus RTU (serial)** | árvore sugerida | ✔ | polling | FC 43, erros de CRC/linha |
| **Modbus RTU over TCP** | árvore sugerida | ✔ | polling | FC 43 |
| **OPC UA** | ✔ browse real (sob demanda) | ✔ tipo nativo detectado | **subscription nativa** | política de segurança, login anônimo, ServerStatus, relógio |
| **MQTT 3.1.1** | ✔ tópicos descobertos | ✔ publish, campos JSON | **notificação nativa** | TLS, autenticação, tópicos/retained |
| **Siemens S7** (200/300/400/1200/1500) | DBs/M/I/Q sugeridos | ✔ bits, INT, REAL, STRING S7 | polling | PUT/GET, PDU |
| **EtherNet/IP** (ControlLogix, CompactLogix, Micro800, MicroLogix, SLC, PLC-5, Omron NJ/NX) | ✔ tags do controlador e de programa (Logix) | ✔ | polling | ListIdentity (produto, firmware, serial) |

Também inclui:
- **Scanner de rede OT** (Ferramentas → Scanner): varre uma faixa de IPs, identifica Modbus (FC 43), EtherNet/IP (ListIdentity), Siemens S7 (COTP), OPC UA (HEL/ACK), MQTT (testa acesso anônimo), DNP3, IEC 104, FINS, MELSEC e serviços como Telnet, FTP, HTTP, SMB, RDP e VNC. Gera achados de segurança e cria conexões com um clique.
- **Relatório de auditoria de segurança OT** (Ferramentas → Relatório): junta a varredura e o diagnóstico das conexões num HTML com nota de exposição, recomendações priorizadas, inventário e boas práticas. Para PDF, use Ctrl+P → Salvar como PDF.
- **Edição de item** (duplo clique ou F2): nome amigável, tipo, ordem, ciclo, **escala, offset e unidade**. A escrita converte o valor de engenharia de volta para o valor bruto.
- **Alarmes por item**: limite alto/baixo (sobre o valor com escala) e falha de comunicação. A linha fica vermelha, há uma aba de histórico com reconhecimento, som opcional e contador na barra de status.
- **Tendência com várias séries**: marque "Gráfico" nos itens; cada série tem uma cor. Tem janela de tempo (1 min a 1 h), escalas independentes, pausa, linhas de alarme e exportação em PNG.
- **Detector de tipo (Modbus)**: lê os registradores e mostra todas as interpretações (UInt16, Int16, Int32, Float32, Float64, texto, em todas as ordens de byte), marcando as plausíveis.
- **Log de comunicação** com o tráfego bruto em hex e decodificado (MBAP/RTU, pacotes MQTT, ListIdentity) ou o trace de cada serviço (OPC UA, S7, CIP). Tem filtro e exportação.
- **Decodificador de pacotes** (duplo clique numa linha TX/RX do log): explica o pacote campo por campo, com os bytes destacados. Cobre Modbus TCP/RTU (MBAP, funções, exceções, CRC), Siemens S7 (TPKT, COTP, S7comm: Setup, Read/Write Var, SZL), EtherNet/IP (encapsulamento, CPF, CIP, Unconnected Send, ListIdentity) e MQTT 3.1.1.
- **Data Access View** com valor, qualidade, timestamp, latência, ciclo, contadores e último erro.
- **Gráfico de tendência** do item selecionado.
- **Gravação em CSV** de todas as leituras (Ctrl+R), com `;` para abrir direto no Excel em português.
- **Projetos `.fpx`**. As senhas são cifradas com o DPAPI do Windows.
- **Confirmação antes de escrever**, como proteção de processo.
- **Reconexão automática** em todos os protocolos.
- Atalhos: Ctrl+N (nova conexão), F9/F10 (conectar/desconectar), F5 (atualizar), Ctrl+S, Ctrl+O, Del, Ctrl+Enter (escrever).

---

## Como compilar e rodar

**Requisitos:** Windows 10/11 e Visual Studio 2022 17.8+ (carga ".NET desktop development"), ou o .NET 8 SDK.

1. Abra `FieldProbe.sln` e pressione **F5**. O Visual Studio restaura os pacotes NuGet automaticamente.
   Pela linha de comando: `dotnet run --project src/FieldProbe.App`
2. Para testar sem hardware, suba um dos simuladores da pasta `tools/` e crie a conexão correspondente.

## Simuladores (pasta `tools/`)

| Simulador | Comando | Conexão no FieldProbe |
|---|---|---|
| Modbus TCP | `python tools/modbus_sim.py` (sem dependências) | Modbus TCP · 127.0.0.1:502 |
| MQTT (broker + devices) | `python tools/mqtt_sim.py` (sem dependências) | MQTT · 127.0.0.1:1883 |
| OPC UA | `pip install asyncua` e depois `python tools/opcua_sim.py` | OPC UA · `opc.tcp://127.0.0.1:4840/fieldprobe/` |
| Siemens S7 (CPU 1214C) | `python tools/s7_sim.py` (sem dependências) | Siemens S7 · 127.0.0.1:102 · rack 0 / slot 1 |
| Siemens S7 · linha de montagem (DB1 a DB20, ~200 variáveis) | `python tools/s7_linha_sim.py` (precisa do `s7_sim.py` na mesma pasta) | Siemens S7 · 127.0.0.1:102 · rack 0 / slot 1 |
| EtherNet/IP (CompactLogix) | `python tools/eip_sim.py` (sem dependências) | EtherNet/IP · 127.0.0.1 · ControlLogix · Path 1,0 |
| Projeto pronto | abra `samples/demo-simuladores.fpx` e clique em **Conectar todas** | Modbus + MQTT + OPC UA |
| CLI rápida | `python tools/fieldprobe_cli.py modbus 127.0.0.1 r HR:0` | (terminal) |

Para Siemens S7 sem CLP, use o PLCSIM Advanced ou um S7-1200 de bancada. Para EtherNet/IP, use um CompactLogix ou o Studio 5000 Logix Emulate.

O `modbus_sim.py` simula falhas: `--delay 300` (latência), `--drop 20` (20% de timeouts) e `--unit 5` (testa Unit ID errado).
Com `--mapa-real`, o `modbus_sim.py` só tem algumas faixas (HR:0-19, HR:100-149, IR:0-9, CO:0-15, DI:0-7), como um equipamento real: bom para testar o **Procurar endereços**.
O `mqtt_sim.py` aceita `--user/--password` para testar a autenticação.
O `s7_sim.py` aceita os comandos `stop`, `run` e `erro` no terminal (modo da CPU e buffer de diagnóstico).
Para testar o **Procurar DBs**: `python tools/s7_sim.py --dbs 1,10,100,999 --otimizado 10` (DB10 existe mas é otimizado).
O `eip_sim.py` devolve erros CIP reais para testar a tradução: tag `Inexistente`, `Valores[50]` ou Path errado.

## Formatos de endereço

| Protocolo | Exemplos |
|---|---|
| Modbus | `HR:0`, `IR:10`, `CO:3`, `DI:7` (0-based) ou `40001`, `30011` (1-based). Para Float32 escolha o tipo, e se o valor vier estranho troque a ordem (ABCD/CDAB...). |
| OPC UA | `ns=2;s=Linha1.Temperatura`, `ns=3;i=1001` (ou navegue e dê duplo clique) |
| MQTT | `fabrica/linha1/temperatura`, campo JSON `fabrica/linha2/dados\|motor.rpm`, monitoramento com curinga `fabrica/+/status` |
| Siemens S7 | `DB1.DBW0` (INT), `DB1.DBD4` (REAL), `DB1.DBX0.0`, `MW10`, `M0.1`, `I0.0`, `QW2` |
| EtherNet/IP | `MeuTag`, `Program:MainProgram.Contador`, `Array[3]`, `UDT.Membro`; SLC/MicroLogix: `N7:0`, `F8:1` |

**Tipo `Auto`:** no OPC UA usa o tipo nativo do nó; no MQTT detecta número, booleano ou texto; no S7 deduz pelo tamanho (X→Bool, B→Byte, W→INT, D→REAL); no EtherNet/IP deduz pelo tamanho do tag.

## Arquitetura

```
FieldProbe.sln
├── src/FieldProbe.Core                 contratos e lógica comum (sem UI)
│   ├── IProtocolDriver / DriverBase    leitura, escrita, browse, subscription, reconexão, estatísticas
│   ├── DriverInfo / DriverRegistry     metadados do protocolo → formulário de conexão gerado automaticamente
│   └── Codec/ValueCodec                conversão de tipos, big-endian, registradores e ordem de bytes
├── src/FieldProbe.Drivers.Modbus       TCP, RTU serial, RTU over TCP (implementação própria) + FC 43
├── src/FieldProbe.Drivers.OpcUa        Workstation.UaClient (MIT): browse, subscriptions, diagnóstico de segurança
├── src/FieldProbe.Drivers.Mqtt         MQTT 3.1.1 próprio (TLS, QoS 0/1, descoberta de tópicos, JSON)
├── src/FieldProbe.Drivers.Siemens      S7netplus (MIT)
├── src/FieldProbe.Drivers.EtherNetIP   libplctag (MPL-2.0) + ListIdentity próprio + listagem de tags Logix
├── src/FieldProbe.App                  WPF (MVVM sem frameworks), tema próprio, tendência, gravação CSV
├── tools/                              simuladores Modbus/MQTT/OPC UA/S7/EtherNet/IP e CLI em Python
├── build/                              publish.ps1 (self-contained) e installer.iss (Inno Setup)
└── docs/COMERCIALIZACAO.md             licenças, marcas e checklist para vender
```

### Adicionar um protocolo

1. Crie `src/FieldProbe.Drivers.Xxx` (net8.0) com referência ao `FieldProbe.Core`.
2. Herde de `DriverBase` e implemente `OpenAsync`, `CloseAsync`, `BrowseAsync`, `ReadCoreAsync` e `WriteCoreAsync`. Se o protocolo tiver notificação nativa, sobrescreva também `Subscribe`. Para ter diagnóstico, implemente `IDiagnosticsProvider`.
3. Exponha um `DriverInfo` com as opções (`DriverOption`). O diálogo de conexão é gerado a partir delas.
4. Registre em `App.xaml.cs → RegisterDrivers()` e adicione a referência no `FieldProbe.App.csproj`.

## Distribuição

```powershell
.\build\publish.ps1 -Version 1.0.0          # gera artifacts\publish (não exige .NET na máquina do cliente)
iscc build\installer.iss /DAppVersion=1.0.0 # gera artifacts\installer\FieldProbe-Setup-1.0.0.exe
```