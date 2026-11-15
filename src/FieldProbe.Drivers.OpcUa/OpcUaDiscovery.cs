using Workstation.ServiceModel.Ua;
using Workstation.ServiceModel.Ua.Channels;

namespace FieldProbe.Drivers.OpcUa;

/// <summary>Informações que qualquer servidor OPC UA divulga sem login (serviço GetEndpoints).</summary>
public sealed record OpcUaServerInfo(
    string? ApplicationName,
    string? ProductUri,
    IReadOnlyList<string> Policies,
    bool AllowsNoSecurity,
    bool AllowsAnonymous);

public static class OpcUaDiscovery
{
    /// <summary>
    /// Pede a lista de endpoints ao servidor. Não abre sessão nem precisa de certificado:
    /// é o mesmo pedido que qualquer cliente OPC UA faz antes de conectar.
    /// Retorna null se o servidor não responder.
    /// </summary>
    public static async Task<OpcUaServerInfo?> GetServerInfoAsync(string ip, int port, int timeoutMs, CancellationToken ct)
    {
        try
        {
            var request = new GetEndpointsRequest { EndpointUrl = $"opc.tcp://{ip}:{port}" };
            var response = await DiscoveryService.GetEndpointsAsync(request)
                .WaitAsync(TimeSpan.FromMilliseconds(Math.Max(2000, timeoutMs * 2)), ct)
                .ConfigureAwait(false);

            var endpoints = response.Endpoints ?? Array.Empty<EndpointDescription>();
            if (endpoints.Length == 0) return null;

            var server = endpoints[0].Server;
            var policies = endpoints
                .Select(e => ShortPolicy(e.SecurityPolicyUri))
                .Distinct()
                .ToList();

            return new OpcUaServerInfo(
                server?.ApplicationName?.Text,
                server?.ProductUri,
                policies,
                endpoints.Any(e => e.SecurityMode == MessageSecurityMode.None),
                endpoints.Any(e => e.UserIdentityTokens?.Any(t => t.TokenType == UserTokenType.Anonymous) == true));
        }
        catch
        {
            return null;
        }
    }

    private static string ShortPolicy(string? uri) =>
        string.IsNullOrEmpty(uri) ? "-" : uri[(uri.LastIndexOf('#') + 1)..];
}
