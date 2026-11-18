using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using FieldProbe.Core;

namespace FieldProbe.Drivers.EtherNetIP;

/// <summary>Identidade de um device EtherNet/IP (resposta ao comando ListIdentity 0x63).</summary>
public sealed record EipIdentity(
    ushort VendorId,
    ushort DeviceType,
    ushort ProductCode,
    byte RevisionMajor,
    byte RevisionMinor,
    ushort Status,
    uint SerialNumber,
    string ProductName,
    byte State)
{
    public string VendorName => VendorId switch
    {
        1 => "Rockwell Automation / Allen-Bradley",
        47 => "Omron",
        _ => $"Vendor {VendorId}"
    };

    /// <summary>Atributo State do objeto Identity.</summary>
    public string StateText => State switch
    {
        0 => "inexistente",
        1 => "em autoteste",
        2 => "em espera (standby)",
        3 => "operacional",
        4 => "falha grave recuperável",
        5 => "falha grave não recuperável",
        255 => "padrão (não informado)",
        _ => $"código {State}"
    };

    /// <summary>Palavra de Status do objeto Identity, bit a bit (ODVA, Volume 1, objeto Identity).</summary>
    public string StatusText
    {
        get
        {
            var parts = new List<string>
            {
                ((Status >> 4) & 0x0F) switch
                {
                    0x0 => "autoteste ou status indefinido",
                    0x1 => "atualização de firmware em andamento",
                    0x2 => "pelo menos uma conexão de I/O com falha",
                    0x3 => "nenhuma conexão de I/O estabelecida",
                    0x4 => "configuração não volátil corrompida",
                    0x5 => "falha grave",
                    0x6 => "pelo menos uma conexão de I/O em RUN",
                    0x7 => "conexões de I/O estabelecidas, todas em IDLE",
                    var x => $"status estendido 0x{x:X}"
                }
            };
            if ((Status & 0x0001) != 0) parts.Add("com dono (owned)");
            if ((Status & 0x0004) != 0) parts.Add("configurado");
            if ((Status & 0x0100) != 0) parts.Add("FALHA LEVE recuperável");
            if ((Status & 0x0200) != 0) parts.Add("FALHA LEVE não recuperável");
            if ((Status & 0x0400) != 0) parts.Add("FALHA GRAVE recuperável");
            if ((Status & 0x0800) != 0) parts.Add("FALHA GRAVE não recuperável");
            return string.Join(" · ", parts);
        }
    }

    public DiagnosticSeverity StatusSeverity =>
        State is 4 or 5 || (Status & 0x0C00) != 0 ? DiagnosticSeverity.Critical
        : (Status & 0x0300) != 0 || ((Status >> 4) & 0x0F) is 0x2 or 0x4 or 0x5 ? DiagnosticSeverity.Warning
        : State == 3 ? DiagnosticSeverity.Ok
        : DiagnosticSeverity.Info;

    public string DeviceTypeName => DeviceType switch
    {
        0x00 => "Generic Device",
        0x02 => "AC Drive",
        0x0C => "Communications Adapter",
        0x0E => "Programmable Logic Controller",
        0x18 => "Human-Machine Interface",
        0x2B => "Generic Device (keyable)",
        _ => $"Tipo 0x{DeviceType:X2}"
    };
}

/// <summary>Implementação direta do ListIdentity (encapsulamento EtherNet/IP, TCP 44818).</summary>
public static class ListIdentity
{
    public static async Task<EipIdentity> QueryAsync(
        string host, int port, int timeoutMs, Action<FrameDirection, byte[], string>? log, CancellationToken ct)
    {
        using var tcp = new TcpClient { NoDelay = true };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Math.Max(1000, timeoutMs));

        try
        {
            await tcp.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            var stream = tcp.GetStream();

            // Cabeçalho de encapsulamento (24 bytes, little-endian): comando 0x0063, demais campos zerados.
            var request = new byte[24];
            BinaryPrimitives.WriteUInt16LittleEndian(request, 0x0063);
            log?.Invoke(FrameDirection.Tx, request, "EtherNet/IP ListIdentity (0x63)");
            await stream.WriteAsync(request, cts.Token).ConfigureAwait(false);

            var header = new byte[24];
            await stream.ReadExactlyAsync(header, cts.Token).ConfigureAwait(false);
            var length = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2));
            var status = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
            var data = new byte[length];
            await stream.ReadExactlyAsync(data, cts.Token).ConfigureAwait(false);
            log?.Invoke(FrameDirection.Rx, header.Concat(data).ToArray(), $"ListIdentity resposta ({length} bytes, status {status})");

            if (status != 0) throw new InvalidDataException($"O device retornou status de encapsulamento {status}.");
            return Parse(data);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Sem resposta de {host}:{port} em {timeoutMs} ms.");
        }
    }

    /// <summary>Interpreta o item CIP Identity (tipo 0x0C) da resposta.</summary>
    public static EipIdentity Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2) throw new InvalidDataException("Resposta ListIdentity vazia.");
        var itemCount = BinaryPrimitives.ReadUInt16LittleEndian(data);
        if (itemCount == 0) throw new InvalidDataException("Resposta ListIdentity sem itens.");

        var pos = 2;
        var itemType = BinaryPrimitives.ReadUInt16LittleEndian(data[pos..]);
        var itemLength = BinaryPrimitives.ReadUInt16LittleEndian(data[(pos + 2)..]);
        pos += 4;
        if (itemType != 0x000C || data.Length < pos + itemLength) throw new InvalidDataException($"Item inesperado 0x{itemType:X4}.");

        // Versão do protocolo (2) + sockaddr (16, big-endian) = 18 bytes antes da identidade.
        pos += 18;
        var vendor = BinaryPrimitives.ReadUInt16LittleEndian(data[pos..]);
        var deviceType = BinaryPrimitives.ReadUInt16LittleEndian(data[(pos + 2)..]);
        var productCode = BinaryPrimitives.ReadUInt16LittleEndian(data[(pos + 4)..]);
        var major = data[pos + 6];
        var minor = data[pos + 7];
        var status = BinaryPrimitives.ReadUInt16LittleEndian(data[(pos + 8)..]);
        var serial = BinaryPrimitives.ReadUInt32LittleEndian(data[(pos + 10)..]);
        var nameLength = data[pos + 14];
        var name = Encoding.ASCII.GetString(data.Slice(pos + 15, nameLength));
        var stateIndex = pos + 15 + nameLength;
        var state = stateIndex < data.Length ? data[stateIndex] : (byte)0xFF;

        return new EipIdentity(vendor, deviceType, productCode, major, minor, status, serial, name, state);
    }
}
