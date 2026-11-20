using System.Globalization;
using System.Net.Sockets;
using System.Text;
using FieldProbe.Core;

namespace FieldProbe.Drivers.Siemens;

/// <summary>Identificação da CPU (SZL 0x0011 e 0x001C).</summary>
public sealed record S7Identity(string? TypeName, string? OrderNumber, string? Firmware, string? SerialNumber, string? PlcName)
{
    public override string ToString()
    {
        var parts = new List<string> { string.IsNullOrEmpty(TypeName) ? "Siemens S7" : $"Siemens {TypeName}" };
        if (!string.IsNullOrEmpty(OrderNumber)) parts.Add(OrderNumber);
        if (!string.IsNullOrEmpty(Firmware)) parts.Add($"FW {Firmware}");
        if (!string.IsNullOrEmpty(SerialNumber)) parts.Add($"S/N {SerialNumber}");
        if (!string.IsNullOrEmpty(PlcName)) parts.Add($"nome '{PlcName}'");
        return string.Join(" · ", parts);
    }
}

public enum S7CpuMode
{
    Unknown,
    Run,
    Stop,
    Startup,
    Hold
}

/// <summary>Um evento do buffer de diagnóstico da CPU.</summary>
public sealed record S7DiagnosticEvent(ushort EventId, DateTime? Timestamp, string Description, DiagnosticSeverity Severity);

/// <summary>Erro devolvido pela CPU numa leitura de SZL (ex.: lista não suportada).</summary>
public sealed class S7SzlException : Exception
{
    public S7SzlException(ushort szlId, byte returnCode)
        : base($"A CPU não forneceu a SZL 0x{szlId:X4} (código 0x{returnCode:X2}{(returnCode == 0x0A ? ": lista não disponível nesta CPU" : string.Empty)}).")
    {
        SzlId = szlId;
        ReturnCode = returnCode;
    }

    public ushort SzlId { get; }
    public byte ReturnCode { get; }
}

/// <summary>
/// Conexão S7 própria (ISO-on-TCP) para ler as listas de sistema da CPU (SZL): identificação, modo de operação
/// e buffer de diagnóstico. É o mesmo tipo de leitura que o TIA Portal faz em "Online &amp; Diagnostics":
/// não lê nem escreve dados de processo e não depende do acesso PUT/GET.
/// </summary>
public sealed class S7SystemClient : IAsyncDisposable
{
    private readonly TcpClient _tcp;
    private NetworkStream? _stream;
    private readonly int _timeoutMs;
    private readonly Action<FrameDirection, byte[], string>? _log;
    private ushort _pduRef = 1;

    private S7SystemClient(TcpClient tcp, int timeoutMs, Action<FrameDirection, byte[], string>? log)
    {
        _tcp = tcp;
        _timeoutMs = timeoutMs;
        _log = log;
    }

    /// <summary>Abre a conexão (COTP + Setup Communication). Lança exceção se a CPU recusar o rack/slot.</summary>
    public static async Task<S7SystemClient> ConnectAsync(
        string host, int port, int rack, int slot, int timeoutMs, CancellationToken ct,
        Action<FrameDirection, byte[], string>? log = null)
    {
        var tcp = new TcpClient { NoDelay = true };
        var client = new S7SystemClient(tcp, Math.Max(1000, timeoutMs), log);
        try
        {
            using (var cts = client.Timeout(ct))
                await tcp.ConnectAsync(host, port > 0 ? port : 102, cts.Token).ConfigureAwait(false);
            client._stream = tcp.GetStream(); // só depois de conectado

            byte destination = (byte)((rack & 0x07) * 32 + (slot & 0x1F));
            byte[] cr =
            {
                0x03, 0x00, 0x00, 0x16,                     // TPKT (22 bytes)
                0x11, 0xE0, 0x00, 0x00, 0x00, 0x01, 0x00,   // COTP Connection Request
                0xC0, 0x01, 0x0A,                           // TPDU size 1024
                0xC1, 0x02, 0x01, 0x00,                     // TSAP de origem
                0xC2, 0x02, 0x01, destination               // TSAP de destino: PG, rack/slot
            };
            var cc = await client.ExchangeAsync(cr, $"COTP Connection Request (rack {rack} / slot {slot})", ct).ConfigureAwait(false);
            if (cc.Length < 2 || cc[1] != 0xD0)
                throw new IOException($"A CPU recusou a conexão em rack {rack} / slot {slot}.");

            byte[] setup =
            {
                0x03, 0x00, 0x00, 0x19, 0x02, 0xF0, 0x80,       // TPKT + COTP DT
                0x32, 0x01, 0x00, 0x00, 0x00, 0x00,             // S7 job
                0x00, 0x08, 0x00, 0x00,                         // parâmetros: 8 bytes, dados: 0
                0xF0, 0x00, 0x00, 0x01, 0x00, 0x01, 0x01, 0xE0  // Setup Communication, PDU 480
            };
            var response = await client.ExchangeAsync(setup, "S7 Setup Communication", ct).ConfigureAwait(false);
            if (response.Length < 5 || response[3] != 0x32)
                throw new IOException("Resposta inesperada ao Setup Communication.");

            return client;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        _tcp.Dispose();
        return ValueTask.CompletedTask;
    }

    // ===================== Informações de alto nível =====================

    /// <summary>Modelo, código de pedido, firmware, série e nome (null se a CPU não informar nada).</summary>
    public async Task<S7Identity?> ReadIdentityAsync(CancellationToken ct)
    {
        string? orderNumber = null, firmware = null;
        foreach (var record in await ReadSzlAsync(0x0011, 0x0000, ct).ConfigureAwait(false))
        {
            if (record.Length < 28) continue;
            var index = (record[0] << 8) | record[1];
            if (index == 1) orderNumber = Ascii(record, 2, 20);
            if (record[24] == (byte)'V') firmware = $"V{record[25]}.{record[26]}.{record[27]}";
        }

        string? typeName = null, plcName = null, serial = null;
        try
        {
            foreach (var record in await ReadSzlAsync(0x001C, 0x0000, ct).ConfigureAwait(false))
            {
                if (record.Length < 4) continue;
                var index = (record[0] << 8) | record[1];
                var text = Ascii(record, 2, record.Length - 2);
                switch (index)
                {
                    case 1: plcName = text; break;
                    case 5: serial = text; break;
                    case 7: typeName = text; break;
                }
            }
        }
        catch (Exception ex) when (ex is S7SzlException or InvalidDataException)
        {
            // CPUs antigas não têm a SZL 0x001C
        }

        if (orderNumber is null && firmware is null && typeName is null) return null;
        return new S7Identity(NullIfEmpty(typeName), NullIfEmpty(orderNumber), firmware, NullIfEmpty(serial), NullIfEmpty(plcName));
    }

    /// <summary>Modo de operação atual da CPU (SZL 0x0424).</summary>
    public async Task<S7CpuMode> ReadCpuModeAsync(CancellationToken ct)
    {
        var records = await ReadSzlAsync(0x0424, 0x0000, ct).ConfigureAwait(false);
        if (records.Count == 0 || records[0].Length < 4) return S7CpuMode.Unknown;

        // Registro: evento (2) · ae (1) · bzu-id (1): bits 0-3 = modo atual
        return (records[0][3] & 0x0F) switch
        {
            0x08 or 0x09 => S7CpuMode.Run,
            >= 0x01 and <= 0x04 => S7CpuMode.Stop,
            >= 0x05 and <= 0x07 => S7CpuMode.Startup,
            0x0A => S7CpuMode.Hold,
            _ => S7CpuMode.Unknown
        };
    }

    /// <summary>Últimos eventos do buffer de diagnóstico (SZL 0x01A0, índice = quantidade).</summary>
    public async Task<IReadOnlyList<S7DiagnosticEvent>> ReadDiagnosticBufferAsync(int maxEntries, CancellationToken ct)
    {
        var count = (ushort)Math.Clamp(maxEntries, 1, 20); // 20 bytes por evento: cabe numa PDU de 480
        var events = new List<S7DiagnosticEvent>();
        foreach (var record in await ReadSzlAsync(0x01A0, count, ct).ConfigureAwait(false))
        {
            if (record.Length < 20) continue;
            var id = (ushort)((record[0] << 8) | record[1]);
            if (id == 0) continue;
            events.Add(new S7DiagnosticEvent(id, ParseDateAndTime(record.AsSpan(12, 8)), DescribeEvent(id), EventSeverity(id)));
        }
        return events;
    }

    public static string DescribeMode(S7CpuMode mode) => mode switch
    {
        S7CpuMode.Run => "RUN",
        S7CpuMode.Stop => "STOP",
        S7CpuMode.Startup => "STARTUP (partida)",
        S7CpuMode.Hold => "HOLD",
        _ => "desconhecido"
    };

    // ===================== Teste de existência de DBs =====================

    /// <summary>Máximo de itens por pedido Read Var: cabe mesmo numa PDU de 240 bytes.</summary>
    public const int MaxProbeItems = 18;

    /// <summary>Códigos de retorno por item do S7 (Read Var).</summary>
    public const byte ReturnOk = 0xFF;
    public const byte ReturnAccessDenied = 0x03;
    public const byte ReturnOutOfRange = 0x05;
    public const byte ReturnNotExist = 0x0A;

    /// <summary>
    /// Lê 1 byte de cada posição (DB, offset) num único pedido e devolve o código de retorno de cada item:
    /// 0xFF = existe e leu · 0x0A = DB não existe · 0x05 = offset além do fim · 0x03 = acesso negado.
    /// </summary>
    public async Task<byte[]> ProbeDbBytesAsync(IReadOnlyList<(int Db, int Offset)> items, CancellationToken ct)
    {
        if (items.Count is 0 or > MaxProbeItems) throw new ArgumentOutOfRangeException(nameof(items));

        var parameters = new List<byte> { 0x04, (byte)items.Count };
        foreach (var (db, offset) in items)
        {
            var bitAddress = offset * 8;
            parameters.AddRange(new byte[]
            {
                0x12, 0x0A, 0x10, 0x02,                     // item de variável · transporte BYTE
                0x00, 0x01,                                 // 1 byte
                (byte)(db >> 8), (byte)db,                  // número do DB
                0x84,                                       // área DB
                (byte)(bitAddress >> 16), (byte)(bitAddress >> 8), (byte)bitAddress
            });
        }

        var reference = _pduRef++;
        var s7 = new List<byte> { 0x32, 0x01, 0x00, 0x00, (byte)(reference >> 8), (byte)reference,
            (byte)(parameters.Count >> 8), (byte)parameters.Count, 0x00, 0x00 };
        s7.AddRange(parameters);

        var length = 4 + 3 + s7.Count;
        var request = new List<byte> { 0x03, 0x00, (byte)(length >> 8), (byte)length, 0x02, 0xF0, 0x80 };
        request.AddRange(s7);

        var body = await ExchangeAsync(request.ToArray(), $"Read Var · teste de {items.Count} DB(s)", ct).ConfigureAwait(false);

        const int h = 3; // depois do COTP DT
        if (body.Length < h + 12 || body[h] != 0x32 || body[h + 1] != 0x03)
            throw new InvalidDataException("Resposta S7 inesperada ao Read Var.");
        if (body[h + 10] != 0x00)
            throw new IOException($"O CLP recusou a leitura (erro S7 classe 0x{body[h + 10]:X2}, código 0x{body[h + 11]:X2}): o acesso PUT/GET pode estar desabilitado.");

        var paramLength = (body[h + 6] << 8) | body[h + 7];
        var pos = h + 12 + paramLength;
        var codes = new byte[items.Count];
        for (var i = 0; i < items.Count; i++)
        {
            if (pos + 4 > body.Length) throw new InvalidDataException("Resposta Read Var incompleta.");
            codes[i] = body[pos];
            var transport = body[pos + 1];
            var size = (body[pos + 2] << 8) | body[pos + 3];
            var dataBytes = codes[i] != ReturnOk ? 0 : transport is 0x03 or 0x04 or 0x05 ? (size + 7) / 8 : size;
            pos += 4 + dataBytes;
            if (dataBytes % 2 == 1 && i < items.Count - 1) pos++; // alinhamento
        }
        return codes;
    }

    // ===================== Leitura de SZL =====================

    /// <summary>Lê uma SZL e devolve os registros.</summary>
    public async Task<List<byte[]>> ReadSzlAsync(ushort szlId, ushort index, CancellationToken ct)
    {
        var reference = _pduRef++;
        byte[] request =
        {
            0x03, 0x00, 0x00, 0x21, 0x02, 0xF0, 0x80,               // TPKT (33 bytes) + COTP DT
            0x32, 0x07, 0x00, 0x00, (byte)(reference >> 8), (byte)reference, // S7 userdata
            0x00, 0x08, 0x00, 0x08,                                 // parâmetros: 8 bytes, dados: 8
            0x00, 0x01, 0x12, 0x04, 0x11, 0x44, 0x01, 0x00,         // CPU functions · Read SZL
            0xFF, 0x09, 0x00, 0x04,                                 // dados: 4 bytes
            (byte)(szlId >> 8), (byte)szlId, (byte)(index >> 8), (byte)index
        };
        var body = await ExchangeAsync(request, $"Read SZL 0x{szlId:X4} índice 0x{index:X4}", ct).ConfigureAwait(false);

        // body = COTP DT (3) + cabeçalho S7 userdata (10) + parâmetros + dados
        const int s7 = 3;
        if (body.Length < s7 + 10 || body[s7] != 0x32 || body[s7 + 1] != 0x07)
            throw new InvalidDataException("Resposta S7 inesperada.");

        var paramLength = (body[s7 + 6] << 8) | body[s7 + 7];
        var data = s7 + 10 + paramLength;
        if (body.Length < data + 4) throw new InvalidDataException("Resposta de SZL sem dados.");
        if (body[data] != 0xFF) throw new S7SzlException(szlId, body[data]);
        if (body.Length < data + 12) return new List<byte[]>();

        var recordLength = (body[data + 8] << 8) | body[data + 9];
        var recordCount = (body[data + 10] << 8) | body[data + 11];
        var records = new List<byte[]>();
        var position = data + 12;
        for (var i = 0; i < recordCount && recordLength > 0 && position + recordLength <= body.Length; i++)
        {
            records.Add(body.AsSpan(position, recordLength).ToArray());
            position += recordLength;
        }
        return records;
    }

    private async Task<byte[]> ExchangeAsync(byte[] request, string description, CancellationToken ct)
    {
        var stream = _stream ?? throw new InvalidOperationException("Conexão de diagnóstico não aberta.");
        using var cts = Timeout(ct);
        _log?.Invoke(FrameDirection.Tx, request, description);
        await stream.WriteAsync(request, cts.Token).ConfigureAwait(false);

        var tpkt = new byte[4];
        await stream.ReadExactlyAsync(tpkt, cts.Token).ConfigureAwait(false);
        var length = (tpkt[2] << 8) | tpkt[3];
        if (length is < 7 or > 4096) throw new InvalidDataException("TPKT inválido.");
        var body = new byte[length - 4];
        await stream.ReadExactlyAsync(body, cts.Token).ConfigureAwait(false);
        _log?.Invoke(FrameDirection.Rx, tpkt.Concat(body).ToArray(), $"Resposta ({length} bytes)");
        return body;
    }

    private CancellationTokenSource Timeout(CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeoutMs);
        return cts;
    }

    // ===================== Buffer de diagnóstico =====================

    /// <summary>DATE_AND_TIME em BCD: AA MM DD hh mm ss mmm(3 dígitos) + dia da semana.</summary>
    private static DateTime? ParseDateAndTime(ReadOnlySpan<byte> bcd)
    {
        static int Bcd(byte b) => (b >> 4) * 10 + (b & 0x0F);
        try
        {
            var year = Bcd(bcd[0]);
            year += year >= 90 ? 1900 : 2000;
            var millis = Bcd(bcd[6]) * 10 + (bcd[7] >> 4);
            return new DateTime(year, Bcd(bcd[1]), Bcd(bcd[2]), Bcd(bcd[3]), Bcd(bcd[4]), Bcd(bcd[5]), millis);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static readonly Dictionary<ushort, string> KnownEvents = new()
    {
        [0x4301] = "Mudança de modo: STOP → STARTUP",
        [0x4302] = "Mudança de modo: STARTUP → RUN",
        [0x4303] = "STOP pela chave/botão de modo da CPU",
        [0x4304] = "STOP por comando do PG (TIA Portal/STEP 7) ou pela instrução STOP",
        [0x4307] = "Reset de memória iniciado pelo PG",
        [0x4309] = "Reset de memória automático (retentividade perdida na energização)",
        [0x4562] = "STOP por erro de programação (OB de erro ausente ou não tratado)",
        [0x4563] = "STOP por erro de acesso à periferia (OB de erro ausente ou não tratado)",
        [0x4568] = "STOP por erro de tempo de ciclo (watchdog)",
        [0x457F] = "STOP por comando STOP",
        [0x2521] = "Erro de programação: conversão BCD inválida",
        [0x2522] = "Erro de programação: área fora da faixa na leitura",
        [0x2523] = "Erro de programação: área fora da faixa na escrita",
        [0x2942] = "Erro de acesso à periferia na leitura (módulo ausente ou com falha)",
        [0x2943] = "Erro de acesso à periferia na escrita (módulo ausente ou com falha)"
    };

    /// <summary>Descrição do evento: texto conhecido ou a classe do evento (4 bits mais altos do ID).</summary>
    public static string DescribeEvent(ushort id)
    {
        if (KnownEvents.TryGetValue(id, out var known)) return $"{known} (ID 0x{id:X4})";

        var incoming = (id & 0x0100) != 0;
        var cls = (id >> 12) switch
        {
            0x1 => "Início de bloco de organização (OB)",
            0x2 => "Erro síncrono (programação ou acesso)",
            0x3 => incoming ? "Erro/diagnóstico de módulo (entrando)" : "Erro/diagnóstico de módulo (saindo)",
            0x4 => "Mudança de modo da CPU / STOP",
            0x5 => "Mensagem de modo de operação",
            0x6 => "Evento de comunicação",
            0x7 => "Evento de sistema H/F",
            0x8 => "Diagnóstico de módulo",
            0x9 or 0xA or 0xB => "Evento definido pelo usuário",
            _ => "Evento"
        };
        return $"{cls} · ID 0x{id:X4} (veja o texto completo no buffer de diagnóstico do TIA Portal)";
    }

    private static DiagnosticSeverity EventSeverity(ushort id) => id switch
    {
        0x4301 or 0x4302 or 0x4307 => DiagnosticSeverity.Info,
        _ when (id >> 12) == 0x4 => DiagnosticSeverity.Warning,
        _ when (id >> 12) == 0x2 => DiagnosticSeverity.Warning,
        _ when (id >> 12) == 0x3 && (id & 0x0100) != 0 => DiagnosticSeverity.Warning,
        _ => DiagnosticSeverity.Info
    };

    private static string Ascii(byte[] buffer, int offset, int count)
    {
        count = Math.Min(count, buffer.Length - offset);
        return count <= 0 ? string.Empty : Encoding.ASCII.GetString(buffer, offset, count).Trim('\0', ' ');
    }

    private static string? NullIfEmpty(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;

    internal static string FormatTime(DateTime? time) =>
        time?.ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.CurrentCulture) ?? "sem data";
}
