using System.Buffers.Binary;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using FieldProbe.Core;

namespace FieldProbe.Drivers.EtherNetIP;

/// <summary>Status de uma resposta CIP: General Status (1 byte) + Extended Status (0 ou mais palavras).</summary>
public sealed record CipStatus(byte General, IReadOnlyList<ushort> Extended)
{
    public bool IsSuccess => General == 0x00;

    public ushort? FirstExtended => Extended.Count > 0 ? Extended[0] : null;

    public string Code => FirstExtended is { } ext ? $"0x{General:X2}/0x{ext:X4}" : $"0x{General:X2}";

    public string Description => CipErrors.Describe(General, FirstExtended);

    public override string ToString() => $"CIP {Code}: {Description}";
}

/// <summary>Resultado de uma leitura CIP direta (Read Tag).</summary>
public sealed record CipReadResult(CipStatus Status, ushort? DataType, byte[] Data);

/// <summary>
/// Cliente CIP mínimo (EtherNet/IP explícito, sem conexão): RegisterSession + Read Tag (0x4C),
/// encaminhado pelo Path com Unconnected Send (0x52) quando há backplane (ControlLogix/CompactLogix).
/// Usado para obter o General/Extended Status exato que o controlador devolve, que a libplctag não expõe.
/// </summary>
public static class CipClient
{
    private const ushort CmdRegisterSession = 0x0065;
    private const ushort CmdSendRRData = 0x006F;

    /// <summary>Abre uma sessão EtherNet/IP e devolve o session handle (teste rápido de conectividade).</summary>
    public static async Task<uint> RegisterSessionAsync(string host, int port, int timeoutMs,
        Action<FrameDirection, byte[], string>? log, CancellationToken ct)
    {
        using var tcp = new TcpClient { NoDelay = true };
        using var cts = Timeout(ct, timeoutMs);
        await tcp.ConnectAsync(host, port > 0 ? port : 44818, cts.Token).ConfigureAwait(false);
        return await RegisterAsync(tcp.GetStream(), log, cts.Token).ConfigureAwait(false);
    }

    /// <summary>Lê um tag pelo nome simbólico e devolve o status CIP exato (sucesso ou erro).</summary>
    public static async Task<CipReadResult> ReadTagAsync(string host, int port, string path, string tagName, int timeoutMs,
        Action<FrameDirection, byte[], string>? log, CancellationToken ct)
    {
        using var tcp = new TcpClient { NoDelay = true };
        using var cts = Timeout(ct, timeoutMs);
        await tcp.ConnectAsync(host, port > 0 ? port : 44818, cts.Token).ConfigureAwait(false);
        var stream = tcp.GetStream();

        var session = await RegisterAsync(stream, log, cts.Token).ConfigureAwait(false);

        var readTag = BuildReadTag(tagName);
        var route = ParseRoutePath(path);
        var message = route.Length == 0 ? readTag : BuildUnconnectedSend(readTag, route);

        var reply = await SendRRDataAsync(stream, session, message, $"CIP Read Tag '{tagName}'" + (route.Length > 0 ? $" via Path {path}" : string.Empty),
            log, cts.Token).ConfigureAwait(false);
        return ParseReply(reply);
    }

    // ===================== Montagem das mensagens =====================

    /// <summary>Read Tag (0x4C): caminho simbólico ANSI (0x91) + índices de array + quantidade de elementos = 1.</summary>
    public static byte[] BuildReadTag(string tagName)
    {
        var path = BuildSymbolicPath(tagName);
        var message = new List<byte> { 0x4C, (byte)(path.Length / 2) };
        message.AddRange(path);
        message.Add(0x01);
        message.Add(0x00);
        return message.ToArray();
    }

    /// <summary>Converte "Program:Main.Tag[3].Membro" em segmentos simbólicos/elemento. Um ".N" final (bit) é ignorado.</summary>
    public static byte[] BuildSymbolicPath(string tagName)
    {
        var bytes = new List<byte>();
        var parts = tagName.Trim().Split('.');
        for (var p = 0; p < parts.Length; p++)
        {
            var part = parts[p];
            if (p > 0 && part.Length > 0 && part.All(char.IsDigit)) break; // acesso a bit: lê a palavra inteira

            var bracket = part.IndexOf('[');
            var name = bracket >= 0 ? part[..bracket] : part;
            var ascii = Encoding.ASCII.GetBytes(name);
            bytes.Add(0x91);
            bytes.Add((byte)ascii.Length);
            bytes.AddRange(ascii);
            if (ascii.Length % 2 == 1) bytes.Add(0x00);

            if (bracket < 0) continue;
            var close = part.IndexOf(']', bracket);
            var indexes = part[(bracket + 1)..(close > bracket ? close : part.Length)]
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var text in indexes)
            {
                var index = uint.Parse(text, CultureInfo.InvariantCulture);
                if (index <= 0xFF)
                {
                    bytes.Add(0x28);
                    bytes.Add((byte)index);
                }
                else if (index <= 0xFFFF)
                {
                    bytes.Add(0x29);
                    bytes.Add(0x00);
                    bytes.Add((byte)index);
                    bytes.Add((byte)(index >> 8));
                }
                else
                {
                    bytes.Add(0x2A);
                    bytes.Add(0x00);
                    var buffer = new byte[4];
                    BinaryPrimitives.WriteUInt32LittleEndian(buffer, index);
                    bytes.AddRange(buffer);
                }
            }
        }
        return bytes.ToArray();
    }

    /// <summary>Path no formato do libplctag ("1,0" = backplane, slot 0) em segmentos de porta.</summary>
    public static byte[] ParseRoutePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return Array.Empty<byte>();
        var bytes = new List<byte>();
        foreach (var item in path.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!byte.TryParse(item, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                return Array.Empty<byte>(); // endereço IP ou formato não suportado: envia direto
            bytes.Add(value);
        }
        if (bytes.Count % 2 == 1) bytes.Add(0x00);
        return bytes.ToArray();
    }

    /// <summary>Unconnected Send (0x52) ao Connection Manager, levando a mensagem até a CPU pelo Path.</summary>
    public static byte[] BuildUnconnectedSend(byte[] embedded, byte[] route)
    {
        var message = new List<byte>
        {
            0x52, 0x02, 0x20, 0x06, 0x24, 0x01, // serviço + caminho: classe 0x06 (Connection Manager), instância 1
            0x0A, 0x0E                          // prioridade/time tick e timeout ticks
        };
        message.Add((byte)embedded.Length);
        message.Add((byte)(embedded.Length >> 8));
        message.AddRange(embedded);
        if (embedded.Length % 2 == 1) message.Add(0x00);
        message.Add((byte)(route.Length / 2));
        message.Add(0x00);
        message.AddRange(route);
        return message.ToArray();
    }

    // ===================== Encapsulamento =====================

    private static async Task<uint> RegisterAsync(NetworkStream stream, Action<FrameDirection, byte[], string>? log, CancellationToken ct)
    {
        var data = new byte[] { 0x01, 0x00, 0x00, 0x00 }; // versão do protocolo 1, opções 0
        var (header, _) = await ExchangeAsync(stream, Encapsulate(CmdRegisterSession, 0, data), "EtherNet/IP RegisterSession", log, ct)
            .ConfigureAwait(false);
        return BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
    }

    private static async Task<byte[]> SendRRDataAsync(NetworkStream stream, uint session, byte[] cip, string description,
        Action<FrameDirection, byte[], string>? log, CancellationToken ct)
    {
        var data = new byte[16 + cip.Length];
        // interface handle (4) = 0 · timeout (2) = 10 · 2 itens: endereço nulo + dados não conectados (0x00B2)
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), 10);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(6), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(12), 0x00B2);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(14), (ushort)cip.Length);
        cip.CopyTo(data, 16);

        var (_, payload) = await ExchangeAsync(stream, Encapsulate(CmdSendRRData, session, data), description, log, ct).ConfigureAwait(false);

        // payload: interface (4) + timeout (2) + nº de itens (2) + itens (tipo 2, tamanho 2, dados)
        if (payload.Length < 8) throw new InvalidDataException("Resposta SendRRData muito curta.");
        var count = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(6));
        var pos = 8;
        for (var i = 0; i < count && pos + 4 <= payload.Length; i++)
        {
            var type = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(pos));
            var length = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(pos + 2));
            pos += 4;
            if (type == 0x00B2 && pos + length <= payload.Length) return payload.AsSpan(pos, length).ToArray();
            pos += length;
        }
        throw new InvalidDataException("Resposta SendRRData sem dados CIP.");
    }

    private static byte[] Encapsulate(ushort command, uint session, byte[] data)
    {
        var packet = new byte[24 + data.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, command);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), (ushort)data.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), session);
        Encoding.ASCII.GetBytes("FldProbe").CopyTo(packet, 12); // sender context
        data.CopyTo(packet, 24);
        return packet;
    }

    private static async Task<(byte[] Header, byte[] Data)> ExchangeAsync(NetworkStream stream, byte[] request, string description,
        Action<FrameDirection, byte[], string>? log, CancellationToken ct)
    {
        log?.Invoke(FrameDirection.Tx, request, description);
        await stream.WriteAsync(request, ct).ConfigureAwait(false);

        var header = new byte[24];
        await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2));
        var status = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
        var data = new byte[length];
        await stream.ReadExactlyAsync(data, ct).ConfigureAwait(false);
        log?.Invoke(FrameDirection.Rx, header.Concat(data).ToArray(), $"Resposta ({length} bytes de dados, status de encapsulamento 0x{status:X4})");

        if (status != 0) throw new InvalidDataException(CipErrors.DescribeEncapsulation(status));
        return (header, data);
    }

    /// <summary>Resposta CIP: serviço|0x80, reservado, General Status, tamanho do Extended (palavras), Extended, dados.</summary>
    public static CipReadResult ParseReply(byte[] reply)
    {
        if (reply.Length < 4) throw new InvalidDataException("Resposta CIP muito curta.");
        var general = reply[2];
        var extendedWords = reply[3];
        var extended = new List<ushort>();
        var pos = 4;
        for (var i = 0; i < extendedWords && pos + 2 <= reply.Length; i++, pos += 2)
            extended.Add(BinaryPrimitives.ReadUInt16LittleEndian(reply.AsSpan(pos)));

        var status = new CipStatus(general, extended);
        if (!status.IsSuccess && general != 0x06) return new CipReadResult(status, null, Array.Empty<byte>());

        ushort? dataType = reply.Length >= pos + 2 ? BinaryPrimitives.ReadUInt16LittleEndian(reply.AsSpan(pos)) : null;
        var start = pos + 2;
        if (dataType == 0x02A0) start += 2; // estrutura: tipo 0x02A0 + handle da estrutura
        var data = start <= reply.Length ? reply.AsSpan(start).ToArray() : Array.Empty<byte>();
        return new CipReadResult(status, dataType, data);
    }

    private static CancellationTokenSource Timeout(CancellationToken ct, int timeoutMs)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Math.Max(1000, timeoutMs));
        return cts;
    }
}

/// <summary>Tradução dos códigos de erro CIP (ODVA, Volume 1, Apêndice B) e dos códigos estendidos mais comuns.</summary>
public static class CipErrors
{
    private static readonly Dictionary<byte, string> General = new()
    {
        [0x00] = "sucesso",
        [0x01] = "falha de conexão",
        [0x02] = "recurso indisponível no controlador (memória ou conexões esgotadas)",
        [0x03] = "valor de parâmetro inválido",
        [0x04] = "erro no caminho/nome do tag: sintaxe inválida ou segmento não reconhecido",
        [0x05] = "destino desconhecido: o tag (ou objeto) não existe no controlador",
        [0x06] = "transferência parcial: o dado é maior que a resposta (leitura fragmentada)",
        [0x07] = "conexão perdida",
        [0x08] = "serviço não suportado por este equipamento",
        [0x09] = "valor de atributo inválido",
        [0x0A] = "erro na lista de atributos",
        [0x0B] = "o objeto já está no modo/estado solicitado",
        [0x0C] = "conflito de estado do objeto: o controlador está num modo que não permite a operação",
        [0x0D] = "o objeto já existe",
        [0x0E] = "atributo não pode ser alterado",
        [0x0F] = "sem permissão: acesso negado (External Access do tag, proteção do controlador ou CIP Security)",
        [0x10] = "conflito de estado do equipamento (ex.: chave em PROG, download em andamento)",
        [0x11] = "resposta grande demais",
        [0x13] = "dados insuficientes no pedido (tamanho ou tipo errado na escrita)",
        [0x14] = "atributo não suportado",
        [0x15] = "dados demais no pedido",
        [0x1E] = "erro em um dos serviços de um pacote múltiplo",
        [0x1F] = "erro específico do fabricante",
        [0x20] = "parâmetro inválido",
        [0x26] = "tamanho do caminho inválido (nome do tag mal formado)",
        [0xFF] = "erro geral (veja o código estendido)"
    };

    private static readonly Dictionary<ushort, string> ConnectionManager = new()
    {
        [0x0100] = "conexão já em uso (Forward Open duplicado)",
        [0x0106] = "conflito de propriedade da conexão (outro cliente controla este recurso)",
        [0x0107] = "conexão não encontrada no destino",
        [0x0109] = "tamanho de conexão inválido",
        [0x0111] = "RPI não suportado",
        [0x0113] = "o controlador está sem conexões livres (limite atingido)",
        [0x0114] = "código de fabricante ou de produto não confere",
        [0x0115] = "tipo de equipamento não confere",
        [0x0116] = "revisão não confere",
        [0x0203] = "tempo de conexão esgotado",
        [0x0204] = "o módulo indicado no Path não respondeu (slot errado ou CPU ausente)",
        [0x0205] = "erro de parâmetro no Unconnected Send",
        [0x0311] = "porta inválida no Path",
        [0x0312] = "slot/endereço inválido no Path",
        [0x0315] = "segmento inválido no Path (confira o Path, ex.: 1,0)"
    };

    private static readonly Dictionary<ushort, string> Logix = new()
    {
        [0x2101] = "a posição da chave/modo do controlador não permite a operação",
        [0x2104] = "o deslocamento passa do fim do tag",
        [0x2105] = "índice fora da faixa: acesso além do fim do tag/array",
        [0x2107] = "o tipo de dado do pedido não confere com o tipo do tag"
    };

    public static string Describe(byte general, ushort? extended)
    {
        var text = General.TryGetValue(general, out var g) ? g : $"código 0x{general:X2} desconhecido";
        if (extended is not { } ext) return text;

        if (general == 0x01 && ConnectionManager.TryGetValue(ext, out var cm)) return $"{text}: {cm}";
        if (general == 0xFF && Logix.TryGetValue(ext, out var lx)) return lx;
        return $"{text} (estendido 0x{ext:X4})";
    }

    /// <summary>Mensagem curta e prática para o usuário, com o código CIP no fim.</summary>
    public static string Hint(CipStatus status, string tagName)
    {
        var ext = status.FirstExtended;
        var text = (status.General, ext) switch
        {
            (0x05, _) => $"O tag '{tagName}' não existe no CLP. Confira o nome; tags de programa usam Program:NomeDoPrograma.Tag",
            (0x04 or 0x26, _) => $"Nome de tag inválido: '{tagName}'. Confira a escrita do endereço",
            (0xFF, 0x2105 or 0x2104) => $"Índice fora da faixa em '{tagName}': o array no CLP é menor",
            (0xFF, 0x2107) => "O tipo de dado não confere com o tag do CLP: escolha o Tipo correto ou Auto",
            (0xFF, 0x2101) => "A chave/modo do CLP não permite esta operação",
            (0x0F, _) => $"Sem permissão para acessar '{tagName}' (External Access do tag ou proteção do CLP)",
            (0x01, 0x0311 or 0x0312 or 0x0315) => $"Path inválido até a CPU: {ConnectionManager[ext!.Value]}",
            (0x01, 0x0204) => "A CPU indicada no Path não respondeu: confira o slot (ex.: Path 1,0)",
            (0x01, 0x0113) => "O CLP está sem conexões livres: feche outros clientes ou aguarde",
            (0x08, _) => "O CLP não suporta este serviço",
            _ => Capitalize(Describe(status.General, ext))
        };
        return $"{text} [CIP {status.Code}]";
    }

    /// <summary>Erros que não mudam sozinhos: não adianta tentar de novo a cada ciclo.</summary>
    public static bool IsPermanent(CipStatus status) => (status.General, status.FirstExtended) switch
    {
        (0x04 or 0x05 or 0x0F or 0x26, _) => true,
        (0xFF, 0x2104 or 0x2105 or 0x2107) => true,
        (0x01, 0x0311 or 0x0312 or 0x0315) => true,
        _ => false
    };

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    public static string DescribeEncapsulation(uint status) => status switch
    {
        0x0001 => "Encapsulamento EtherNet/IP: comando inválido ou não suportado.",
        0x0002 => "Encapsulamento EtherNet/IP: o equipamento está sem memória para atender.",
        0x0003 => "Encapsulamento EtherNet/IP: dados mal formados.",
        0x0064 => "Encapsulamento EtherNet/IP: sessão inválida.",
        0x0065 => "Encapsulamento EtherNet/IP: tamanho de mensagem inválido.",
        0x0069 => "Encapsulamento EtherNet/IP: versão de protocolo não suportada.",
        _ => $"Encapsulamento EtherNet/IP: status 0x{status:X4}."
    };

    /// <summary>Nome do tipo de dado CIP/Logix.</summary>
    public static string TypeName(ushort type) => type switch
    {
        0x00C1 => "BOOL",
        0x00C2 => "SINT",
        0x00C3 => "INT",
        0x00C4 => "DINT",
        0x00C5 => "LINT",
        0x00C6 => "USINT",
        0x00C7 => "UINT",
        0x00C8 => "UDINT",
        0x00CA => "REAL",
        0x00CB => "LREAL",
        0x00D3 => "DWORD",
        0x02A0 => "estrutura (UDT/STRING)",
        _ => $"0x{type:X4}"
    };
}
