FieldProbe — Componentes de terceiros
=====================================

O FieldProbe usa os componentes abaixo. Cada um continua sob a sua própria licença.
Antes de cada release, confira a lista completa com:
    dotnet list src/FieldProbe.App/FieldProbe.App.csproj package --include-transitive
e inclua o texto integral de cada licença na pasta do instalador.

-------------------------------------------------------------------------------
Workstation.UaClient 3.2.3 (cliente OPC UA)
  Copyright (c) Converter Systems LLC
  Licença: MIT
  https://github.com/convertersystems/opc-ua-client

S7netplus 0.20.0 (protocolo Siemens S7)
  Copyright (c) os autores do S7.Net / S7netplus
  Licença: MIT
  https://github.com/S7NetPlus/s7netplus

libplctag.NET 1.5.2 e libplctag (biblioteca nativa) (EtherNet/IP / CIP)
  Copyright (c) Kyle Hayes e colaboradores
  Licença: MPL-2.0 (wrapper .NET); biblioteca nativa sob MPL-2.0 ou LGPL-2.1 (à escolha)
  https://github.com/libplctag/libplctag.NET  ·  https://github.com/libplctag/libplctag
  Observação: a biblioteca é usada sem modificações e carregada dinamicamente.
  Se modificar arquivos dela, as modificações precisam ser disponibilizadas conforme a MPL-2.0.

System.IO.Ports 8.0.0 · System.Security.Cryptography.ProtectedData 8.0.0 · .NET Runtime 8
  Copyright (c) .NET Foundation e colaboradores
  Licença: MIT
  https://github.com/dotnet/runtime

Dependências transitivas (ex.: BouncyCastle, Microsoft.Extensions.Logging.Abstractions,
System.Threading.Tasks.Dataflow) — licenças permissivas (MIT ou similares). Conferir a cada release.
-------------------------------------------------------------------------------

Implementações próprias (sem código de terceiros):
  Modbus TCP / RTU / RTU over TCP, MQTT 3.1.1, EtherNet/IP ListIdentity, gráfico de tendência.

Marcas: Modbus é marca da Modbus Organization; OPC UA da OPC Foundation; EtherNet/IP e CIP da ODVA;
SIMATIC/S7 da Siemens AG; Allen-Bradley, ControlLogix e CompactLogix da Rockwell Automation;
Omron da OMRON Corporation. O FieldProbe não é afiliado, endossado nem certificado por essas organizações.
