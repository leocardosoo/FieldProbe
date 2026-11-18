using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using FieldProbe.Core;
using FieldProbe.Drivers.EtherNetIP;
using FieldProbe.Drivers.Modbus;
using FieldProbe.Drivers.Mqtt;
using FieldProbe.Drivers.OpcUa;
using FieldProbe.Drivers.Siemens;

namespace FieldProbe.App.Services;

public sealed record PortDefinition(int Port, string Name, bool Industrial);

/// <summary>Achado de segurança de um ativo.</summary>
public sealed record ScanFinding(DiagnosticSeverity Severity, string Category, string Message, string Recommendation)
{
    public string SeverityText => Severity switch
    {
        DiagnosticSeverity.Critical => "Crítico",
        DiagnosticSeverity.Warning => "Atenção",
        DiagnosticSeverity.Ok => "OK",
        _ => "Info"
    };
}

/// <summary>Um ativo encontrado na varredura.</summary>
public sealed class ScanResult
{
    public required string Ip { get; init; }
    public string Hostname { get; set; } = string.Empty;
    public string Mac { get; set; } = string.Empty;
    public string Vendor { get; set; } = string.Empty;
    public List<int> OpenPorts { get; } = new();
    public List<string> Protocols { get; } = new();
    public List<string> Identity { get; } = new();
    public List<ScanFinding> Findings { get; } = new();
    public string? SuggestedProtocol { get; set; }
    public int SuggestedPort { get; set; }

    public string PortsText => string.Join(", ", OpenPorts.OrderBy(p => p));
    public string ProtocolsText => string.Join(", ", Protocols.Distinct());
    public string IdentityText
    {
        get
        {
            var parts = new List<string>();
            if (Vendor.Length > 0 && !Identity.Any(i => i.Contains(Vendor, StringComparison.OrdinalIgnoreCase)))
                parts.Add(Vendor);
            parts.AddRange(Identity);
            return parts.Count == 0 ? "-" : string.Join(" · ", parts);
        }
    }

    public DiagnosticSeverity WorstSeverity => Findings.Count == 0 ? DiagnosticSeverity.Info : Findings.Max(f => f.Severity);

    public string SeverityText => WorstSeverity switch
    {
        DiagnosticSeverity.Critical => "Crítico",
        DiagnosticSeverity.Warning => "Atenção",
        _ => "OK"
    };

    public string FindingsText
    {
        get
        {
            var critical = Findings.Count(f => f.Severity == DiagnosticSeverity.Critical);
            var warning = Findings.Count(f => f.Severity == DiagnosticSeverity.Warning);
            return critical + warning == 0 ? "nenhum risco" : $"{critical} crítico(s) · {warning} atenção";
        }
    }

    /// <summary>Nome curto para criar conexão (produto identificado ou IP).</summary>
    public string ShortName
    {
        get
        {
            var name = Identity.FirstOrDefault()?.Split('(')[0].Trim();
            return string.IsNullOrWhiteSpace(name) || name.StartsWith("porta", StringComparison.OrdinalIgnoreCase) ? Ip : $"{name} {Ip}";
        }
    }
}

public sealed record ScanProgress(int Done, int Total, int Found, string Current);

/// <summary>
/// Varredura ativa de uma faixa de IPs (TCP connect) com identificação de protocolos industriais
/// e geração de achados de segurança. Não explora vulnerabilidades: só conecta e identifica.
/// </summary>
public sealed class NetworkScanner
{
    public static readonly IReadOnlyList<PortDefinition> KnownPorts = new PortDefinition[]
    {
        new(502, "Modbus TCP", true),
        new(102, "Siemens S7", true),
        new(44818, "EtherNet/IP", true),
        new(4840, "OPC UA", true),
        new(1883, "MQTT", true),
        new(8883, "MQTT TLS", true),
        new(20000, "DNP3", true),
        new(2404, "IEC 60870-5-104", true),
        new(9600, "Omron FINS", true),
        new(5007, "Mitsubishi MELSEC", true),
        new(80, "HTTP", false),
        new(443, "HTTPS", false),
        new(21, "FTP", false),
        new(22, "SSH", false),
        new(23, "Telnet", false),
        new(445, "SMB", false),
        new(3389, "RDP", false),
        new(5900, "VNC", false)
    };

    /// <summary>Máximo de conexões TCP abertas ao mesmo tempo (evita sobrecarregar o PC e a rede OT).</summary>
    private const int MaxConcurrentSockets = 200;

    private SemaphoreSlim _sockets = new(MaxConcurrentSockets, MaxConcurrentSockets);

    public async Task ScanAsync(
        IReadOnlyList<IPAddress> hosts,
        IReadOnlyList<int> ports,
        int timeoutMs,
        int parallelism,
        IProgress<ScanProgress>? progress,
        Action<ScanResult> onFound,
        CancellationToken ct)
    {
        var done = 0;
        var found = 0;
        _sockets = new SemaphoreSlim(MaxConcurrentSockets, MaxConcurrentSockets);
        using var gate = new SemaphoreSlim(Math.Clamp(parallelism, 1, 256));

        var tasks = hosts.Select(async host =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var result = await ScanHostAsync(host, ports, timeoutMs, ct).ConfigureAwait(false);
                if (result is not null)
                {
                    Interlocked.Increment(ref found);
                    onFound(result);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // host com erro inesperado: segue a varredura
            }
            finally
            {
                gate.Release();
                var current = Interlocked.Increment(ref done);
                progress?.Report(new ScanProgress(current, hosts.Count, Volatile.Read(ref found), host.ToString()));
            }
        }).ToList();

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // interrompido pelo usuário
        }
    }

    private async Task<ScanResult?> ScanHostAsync(IPAddress host, IReadOnlyList<int> ports, int timeoutMs, CancellationToken ct)
    {
        var ip = host.ToString();
        var checks = await Task.WhenAll(ports.Select(async port => (Port: port, Open: await IsOpenAsync(ip, port, timeoutMs, ct).ConfigureAwait(false)))).ConfigureAwait(false);
        var open = checks.Where(c => c.Open).Select(c => c.Port).ToList();
        if (open.Count == 0) return null;

        var result = new ScanResult { Ip = ip };
        result.OpenPorts.AddRange(open);
        result.Mac = await Task.Run(() => ArpLookup(host), ct).ConfigureAwait(false);
        result.Vendor = MacVendors.Lookup(result.Mac) ?? string.Empty;
        result.Hostname = await ReverseDnsAsync(host, ct).ConfigureAwait(false);

        var probeTimeout = Math.Max(1500, timeoutMs * 4);
        foreach (var port in open.OrderBy(p => p))
            await IdentifyAsync(result, port, probeTimeout, ct).ConfigureAwait(false);

        // Protocolo sugerido para "Criar conexão" (o mais rico primeiro).
        (int Port, string Protocol)[] preference =
        {
            (4840, OpcUaDriver.Protocol),
            (44818, EtherNetIpDriver.Protocol),
            (102, S7Driver.Protocol),
            (502, ModbusDrivers.TcpName),
            (1883, MqttDriver.Protocol)
        };
        foreach (var (port, protocol) in preference)
        {
            if (!open.Contains(port)) continue;
            result.SuggestedPort = port;
            result.SuggestedProtocol = protocol;
            break;
        }

        return result;
    }

    private static async Task IdentifyAsync(ScanResult r, int port, int timeoutMs, CancellationToken ct)
    {
        var ip = r.Ip;
        switch (port)
        {
            case 502:
                r.Protocols.Add("Modbus TCP");
                try
                {
                    using var transport = new ModbusTcpTransport(ip, 502, timeoutMs);
                    await transport.ConnectAsync(ct).ConfigureAwait(false);
                    var id = await new ModbusClient(transport).ReadDeviceIdentificationAsync(1, ct).ConfigureAwait(false);
                    if (id.Count > 0) r.Identity.Add(string.Join(" ", id.Select(x => x.Value)));
                }
                catch
                {
                    // FC 43 é opcional no Modbus: muitos devices não respondem
                }
                r.Findings.Add(new(DiagnosticSeverity.Warning, "Modbus TCP",
                    "Porta 502 aberta: Modbus TCP não tem autenticação nem criptografia; qualquer cliente na rede pode ler e escrever.",
                    "Restringir o acesso à porta 502 por firewall/VLAN, permitindo somente o SCADA/IHM autorizado."));
                break;

            case 44818:
                r.Protocols.Add("EtherNet/IP");
                try
                {
                    var id = await ListIdentity.QueryAsync(ip, 44818, timeoutMs, null, ct).ConfigureAwait(false);
                    r.Identity.Add($"{id.ProductName} ({id.VendorName}, fw {id.RevisionMajor}.{id.RevisionMinor:D3}, S/N {id.SerialNumber:X8})");
                }
                catch
                {
                    // segue sem identificação
                }
                r.Findings.Add(new(DiagnosticSeverity.Warning, "EtherNet/IP",
                    "Porta 44818 aberta: EtherNet/IP/CIP clássico não autentica clientes (leitura, escrita e comandos ao controlador).",
                    "Segmentar a rede do controlador e liberar a 44818 só para estações de engenharia/SCADA; avaliar CIP Security."));
                break;

            case 102:
                r.Protocols.Add("Siemens S7 (ISO-TSAP)");
                var s7 = await Probes.S7CotpAsync(ip, timeoutMs, ct).ConfigureAwait(false);
                r.Identity.Add(s7 ?? "porta 102 aberta (ISO-TSAP); a CPU não aceitou rack/slot testados");
                if (s7?.Contains("CPU em STOP", StringComparison.Ordinal) == true)
                    r.Findings.Add(new(DiagnosticSeverity.Warning, "Siemens S7",
                        "A CPU está em STOP: o programa não está rodando.",
                        "Verifique o buffer de diagnóstico (Diagnóstico da conexão S7 ou TIA Portal) antes de colocar em RUN."));
                r.Findings.Add(new(DiagnosticSeverity.Warning, "Siemens S7",
                    "A CPU aceita conexões S7 na porta 102. Se o acesso PUT/GET estiver habilitado, dados podem ser lidos e escritos sem senha.",
                    "Desabilitar PUT/GET quando não for necessário, configurar o nível de proteção de acesso da CPU e segmentar a rede."));
                break;

            case 4840:
                var isUa = await Probes.OpcUaHelloAsync(ip, 4840, timeoutMs, ct).ConfigureAwait(false);
                r.Protocols.Add(isUa ? "OPC UA" : "porta 4840 (sem resposta OPC UA)");
                if (!isUa) break;

                var ua = await OpcUaDiscovery.GetServerInfoAsync(ip, 4840, timeoutMs, ct).ConfigureAwait(false);
                if (ua is null)
                {
                    r.Findings.Add(new(DiagnosticSeverity.Info, "OPC UA",
                        "Servidor OPC UA encontrado. Conecte e rode o Diagnóstico para verificar política de segurança e login anônimo.",
                        "Exigir Basic256Sha256 ou superior, desabilitar login anônimo e gerenciar a lista de certificados confiáveis."));
                    break;
                }

                var uaName = !string.IsNullOrWhiteSpace(ua.ApplicationName) ? ua.ApplicationName : ua.ProductUri;
                if (!string.IsNullOrWhiteSpace(uaName)) r.Identity.Add($"OPC UA: {uaName}");

                r.Findings.Add(new(DiagnosticSeverity.Info, "OPC UA",
                    $"Servidor OPC UA '{uaName ?? "-"}' · políticas oferecidas: {string.Join(", ", ua.Policies)}.",
                    "Manter apenas Basic256Sha256 ou superior e gerenciar a lista de certificados confiáveis."));
                if (ua.AllowsNoSecurity)
                    r.Findings.Add(new(DiagnosticSeverity.Warning, "OPC UA",
                        "O servidor aceita conexões sem criptografia nem assinatura (SecurityPolicy None).",
                        "Desabilitar o endpoint None e exigir Sign ou SignAndEncrypt."));
                if (ua.AllowsAnonymous)
                    r.Findings.Add(new(DiagnosticSeverity.Warning, "OPC UA",
                        "O servidor aceita login anônimo: qualquer cliente na rede pode ler (e talvez escrever).",
                        "Desabilitar o login anônimo e exigir usuário/senha ou certificado."));
                break;

            case 1883:
                var code = await Probes.MqttAnonymousAsync(ip, 1883, timeoutMs, ct).ConfigureAwait(false);
                r.Protocols.Add(code is null ? "porta 1883 (sem resposta MQTT)" : "MQTT");
                if (code == 0)
                    r.Findings.Add(new(DiagnosticSeverity.Critical, "MQTT",
                        "O broker MQTT aceitou conexão anônima e sem TLS: qualquer cliente pode ler e publicar mensagens.",
                        "Exigir usuário/senha ou certificado de cliente, habilitar TLS (porta 8883) e ACLs por tópico."));
                else if (code is 4 or 5)
                    r.Findings.Add(new(DiagnosticSeverity.Warning, "MQTT",
                        "O broker exige autenticação, mas a porta 1883 não usa TLS: usuário e senha trafegam em texto aberto.",
                        "Migrar os clientes para TLS (8883) e desativar a 1883."));
                break;

            case 8883:
                r.Protocols.Add("MQTT TLS");
                r.Findings.Add(new(DiagnosticSeverity.Ok, "MQTT", "Broker MQTT com TLS disponível (porta 8883).", string.Empty));
                break;

            case 20000:
                r.Protocols.Add("DNP3");
                r.Findings.Add(new(DiagnosticSeverity.Warning, "DNP3",
                    "DNP3 sem Secure Authentication não autentica comandos de controle.",
                    "Segmentar e restringir o acesso; avaliar DNP3 SAv5 ou túnel seguro."));
                break;

            case 2404:
                r.Protocols.Add("IEC 60870-5-104");
                r.Findings.Add(new(DiagnosticSeverity.Warning, "IEC 104",
                    "IEC 60870-5-104 não possui autenticação nativa.",
                    "Restringir a porta 2404 aos centros de controle autorizados; avaliar IEC 62351."));
                break;

            case 9600:
                r.Protocols.Add("Omron FINS");
                r.Findings.Add(new(DiagnosticSeverity.Warning, "Omron FINS",
                    "FINS/TCP não autentica clientes.", "Restringir o acesso à porta 9600 por firewall/VLAN."));
                break;

            case 5007:
                r.Protocols.Add("Mitsubishi MELSEC");
                r.Findings.Add(new(DiagnosticSeverity.Warning, "MELSEC",
                    "Protocolo MC/SLMP sem autenticação.", "Restringir o acesso por firewall/VLAN."));
                break;

            case 23:
                r.Protocols.Add("Telnet");
                r.Findings.Add(new(DiagnosticSeverity.Critical, "Telnet",
                    "Telnet aberto: credenciais e comandos trafegam em texto aberto.",
                    "Desabilitar o Telnet e usar SSH; se não for possível, isolar o equipamento."));
                break;

            case 21:
                r.Protocols.Add("FTP");
                r.Findings.Add(new(DiagnosticSeverity.Warning, "FTP",
                    "FTP aberto: credenciais em texto aberto (comum para troca de programas e firmware).",
                    "Desabilitar quando não usado ou trocar por SFTP/FTPS; trocar senhas padrão."));
                break;

            case 80:
                r.Protocols.Add("HTTP");
                r.Findings.Add(new(DiagnosticSeverity.Warning, "Interface web",
                    "Interface web sem HTTPS (porta 80).",
                    "Usar HTTPS quando disponível e trocar a senha padrão do fabricante."));
                break;

            case 443:
                r.Protocols.Add("HTTPS");
                r.Findings.Add(new(DiagnosticSeverity.Info, "Interface web", "Interface web com HTTPS (porta 443).", "Confirmar que a senha padrão foi trocada."));
                break;

            case 22:
                r.Protocols.Add("SSH");
                r.Findings.Add(new(DiagnosticSeverity.Info, "SSH", "SSH disponível (porta 22).", "Usar chaves ou senhas fortes e limitar a origem dos acessos."));
                break;

            case 445:
                r.Protocols.Add("SMB");
                r.Findings.Add(new(DiagnosticSeverity.Warning, "SMB",
                    "Compartilhamento de arquivos SMB aberto na rede OT.",
                    "Confirmar que o SMBv1 está desabilitado, restringir compartilhamentos e manter o Windows atualizado."));
                break;

            case 3389:
                r.Protocols.Add("RDP");
                r.Findings.Add(new(DiagnosticSeverity.Warning, "Acesso remoto",
                    "Área de trabalho remota (RDP) exposta na rede.",
                    "Centralizar o acesso remoto em jump server/VPN com MFA e restringir a origem."));
                break;

            case 5900:
                r.Protocols.Add("VNC");
                r.Findings.Add(new(DiagnosticSeverity.Warning, "Acesso remoto",
                    "VNC exposto (comum em IHMs e painéis).",
                    "Exigir senha forte, restringir a origem e preferir acesso via jump server."));
                break;

            default:
                r.Protocols.Add($"porta {port}");
                break;
        }
    }

    private async Task<bool> IsOpenAsync(string ip, int port, int timeoutMs, CancellationToken ct)
    {
        await _sockets.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var tcp = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(Math.Max(50, timeoutMs));
            try
            {
                await tcp.ConnectAsync(IPAddress.Parse(ip), port, cts.Token).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return false;
            }
            catch (SocketException)
            {
                return false;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }
        finally
        {
            _sockets.Release();
        }
    }

    private static async Task<string> ReverseDnsAsync(IPAddress host, CancellationToken ct)
    {
        try
        {
            var entry = await Dns.GetHostEntryAsync(host).WaitAsync(TimeSpan.FromMilliseconds(800), ct).ConfigureAwait(false);
            return entry.HostName == host.ToString() ? string.Empty : entry.HostName;
        }
        catch
        {
            return string.Empty;
        }
    }

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int SendARP(uint destIp, uint srcIp, byte[] macAddress, ref uint physicalAddressLength);

    /// <summary>MAC via ARP (só funciona para hosts na mesma sub-rede).</summary>
    private static string ArpLookup(IPAddress host)
    {
        try
        {
            // A API do Windows exige buffer de pelo menos 8 bytes (2 ULONGs), mesmo para MAC de 6.
            var mac = new byte[8];
            uint length = 8;
            var destination = BitConverter.ToUInt32(host.GetAddressBytes(), 0);
            if (SendARP(destination, 0, mac, ref length) == 0 && length >= 6)
                return string.Join(":", mac.Take((int)length).Select(b => b.ToString("X2")));
        }
        catch
        {
            // fora do Windows ou sem permissão
        }
        return string.Empty;
    }
}

/// <summary>Interpreta faixas de IP: 192.168.1.0/24, 192.168.1.10-50, 10.0.0.1-10.0.0.20, IPs separados por vírgula.</summary>
public static class IpRangeParser
{
    public const int MaxHosts = 4096;

    public static List<IPAddress> Parse(string text)
    {
        var result = new List<uint>();
        foreach (var part in text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Contains('/'))
            {
                var slash = part.IndexOf('/');
                var baseIp = ToUInt(IPAddress.Parse(part[..slash]));
                var prefix = int.Parse(part[(slash + 1)..]);
                if (prefix is < 20 or > 32) throw new FormatException("Use máscaras entre /20 e /32 (no máximo 4096 hosts).");

                var mask = prefix == 32 ? uint.MaxValue : ~((1u << (32 - prefix)) - 1);
                var network = baseIp & mask;
                var count = 1u << (32 - prefix);
                if (prefix >= 31)
                {
                    for (uint i = 0; i < count; i++) result.Add(network + i);
                }
                else
                {
                    for (uint i = 1; i < count - 1; i++) result.Add(network + i); // sem rede e broadcast
                }
            }
            else if (part.Contains('-'))
            {
                var dash = part.IndexOf('-');
                var start = IPAddress.Parse(part[..dash]);
                var endText = part[(dash + 1)..];
                IPAddress end;
                if (endText.Contains('.'))
                {
                    end = IPAddress.Parse(endText);
                }
                else
                {
                    var bytes = start.GetAddressBytes();
                    bytes[3] = byte.Parse(endText);
                    end = new IPAddress(bytes);
                }

                var a = ToUInt(start);
                var b = ToUInt(end);
                if (b < a) (a, b) = (b, a);
                if (b - a >= MaxHosts) throw new FormatException($"Faixa grande demais (máximo {MaxHosts} hosts).");
                for (var i = a; i <= b; i++) result.Add(i);
            }
            else
            {
                result.Add(ToUInt(IPAddress.Parse(part)));
            }

            if (result.Count > MaxHosts) throw new FormatException($"Faixa grande demais (máximo {MaxHosts} hosts).");
        }

        return result.Distinct().Select(FromUInt).ToList();
    }

    /// <summary>Sugere a sub-rede da placa de rede ativa (limitada a /24).</summary>
    public static string SuggestLocalRange()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var address in nic.GetIPProperties().UnicastAddresses)
                {
                    if (address.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var prefix = Math.Max(24, address.PrefixLength);
                    var mask = ~((1u << (32 - prefix)) - 1);
                    var network = FromUInt(ToUInt(address.Address) & mask);
                    return $"{network}/{prefix}";
                }
            }
        }
        catch
        {
            // sem acesso às interfaces
        }
        return "192.168.0.0/24";
    }

    private static uint ToUInt(IPAddress ip)
    {
        var bytes = ip.GetAddressBytes();
        if (bytes.Length != 4) throw new FormatException("Somente IPv4 é suportado.");
        return BinaryPrimitives.ReadUInt32BigEndian(bytes);
    }

    private static IPAddress FromUInt(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return new IPAddress(bytes);
    }
}
