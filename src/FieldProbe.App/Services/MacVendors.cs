namespace FieldProbe.App.Services;

/// <summary>
/// Identifica o fabricante pelo prefixo do MAC (OUI, os 3 primeiros bytes).
/// Tabela embutida só com fabricantes de automação: funciona offline.
/// Fonte: registro IEEE (consultado via maclookup.app).
/// </summary>
public static class MacVendors
{
    private static readonly Dictionary<string, string> Table = new(StringComparer.OrdinalIgnoreCase)
    {
        // Siemens
        ["00:01:E3"] = "Siemens", ["00:0B:A3"] = "Siemens", ["00:0E:8C"] = "Siemens", ["00:1B:1B"] = "Siemens",
        ["00:1F:F8"] = "Siemens", ["08:00:06"] = "Siemens", ["10:DF:FC"] = "Siemens", ["20:87:56"] = "Siemens",
        ["20:A8:B9"] = "Siemens", ["28:63:36"] = "Siemens", ["30:2F:1E"] = "Siemens", ["30:B8:51"] = "Siemens",
        ["38:4B:24"] = "Siemens", ["40:EC:F8"] = "Siemens", ["88:3F:99"] = "Siemens", ["AC:64:17"] = "Siemens",
        ["D4:F5:27"] = "Siemens",

        // Rockwell Automation / Allen-Bradley
        ["00:00:BC"] = "Rockwell Automation", ["00:1D:9C"] = "Rockwell Automation", ["08:61:95"] = "Rockwell Automation",
        ["18:4C:08"] = "Rockwell Automation", ["34:C0:F9"] = "Rockwell Automation", ["40:41:01"] = "Rockwell Automation",
        ["44:CC:6E"] = "Rockwell Automation", ["5C:21:67"] = "Rockwell Automation", ["5C:88:16"] = "Rockwell Automation",
        ["68:C8:EB"] = "Rockwell Automation", ["E4:90:69"] = "Rockwell Automation", ["F4:54:33"] = "Rockwell Automation",

        // Schneider Electric (inclui Telemecanique e Foxboro)
        ["00:00:54"] = "Schneider Electric", ["00:80:F4"] = "Schneider Electric (Telemecanique)",
        ["00:00:6C"] = "Schneider Electric", ["00:11:00"] = "Schneider Electric", ["00:0C:81"] = "Schneider Electric",
        ["9C:0E:51"] = "Schneider Electric", ["E4:F5:8E"] = "Schneider Electric",

        // Outros fabricantes de automação
        ["00:A0:45"] = "Phoenix Contact", ["A8:74:1D"] = "Phoenix Contact", ["CC:CC:EA"] = "Phoenix Contact",
        ["00:30:DE"] = "WAGO",
        ["00:01:05"] = "Beckhoff",
        ["00:90:E8"] = "Moxa",
        ["00:60:65"] = "B&R", ["00:11:1E"] = "B&R", ["00:15:1E"] = "B&R",
        ["00:80:63"] = "Hirschmann", ["64:60:38"] = "Hirschmann", ["EC:74:BA"] = "Hirschmann",
        ["00:00:0A"] = "Omron",
        ["00:00:64"] = "Yokogawa",
        ["00:00:68"] = "Rosemount (Emerson)",
    };

    /// <summary>Retorna o fabricante ou null se o prefixo não estiver na tabela.</summary>
    public static string? Lookup(string? mac)
    {
        if (string.IsNullOrWhiteSpace(mac)) return null;
        var normalized = mac.Trim().Replace('-', ':').ToUpperInvariant();
        if (normalized.Length < 8) return null;
        return Table.TryGetValue(normalized[..8], out var vendor) ? vendor : null;
    }
}
