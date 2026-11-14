using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FieldProbe.Core;

namespace FieldProbe.App.Services;

/// <summary>Projeto (.fpx): conexões e itens monitorados em JSON. Senhas são cifradas com DPAPI (usuário do Windows).</summary>
public sealed class ProjectFile
{
    public const string Extension = ".fpx";
    public const string Filter = "Projeto FieldProbe (*.fpx)|*.fpx|Todos os arquivos (*.*)|*.*";

    private const string ProtectedPrefix = "dpapi:";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public string Product { get; set; } = "FieldProbe";
    public int FormatVersion { get; set; } = 1;
    public List<ConnectionSettings> Connections { get; set; } = new();
    public List<WatchEntry> WatchItems { get; set; } = new();

    public static ProjectFile Load(string path)
    {
        var project = JsonSerializer.Deserialize<ProjectFile>(File.ReadAllText(path), Options) ?? new ProjectFile();
        foreach (var connection in project.Connections)
            TransformSecrets(connection, Unprotect);
        return project;
    }

    public void Save(string path)
    {
        var copy = new ProjectFile
        {
            Connections = Connections.Select(c =>
            {
                var clone = c.Clone();
                TransformSecrets(clone, Protect);
                return clone;
            }).ToList(),
            WatchItems = WatchItems
        };
        File.WriteAllText(path, JsonSerializer.Serialize(copy, Options), Encoding.UTF8);
    }

    private static void TransformSecrets(ConnectionSettings settings, Func<string, string> transform)
    {
        foreach (var key in settings.Options.Keys.Where(k => k.Contains("Password", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            if (!string.IsNullOrEmpty(settings.Options[key]))
                settings.Options[key] = transform(settings.Options[key]);
        }
    }

    private static string Protect(string plain)
    {
        var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
        return ProtectedPrefix + Convert.ToBase64String(data);
    }

    private static string Unprotect(string stored)
    {
        if (!stored.StartsWith(ProtectedPrefix, StringComparison.Ordinal)) return stored;
        try
        {
            var data = ProtectedData.Unprotect(Convert.FromBase64String(stored[ProtectedPrefix.Length..]), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(data);
        }
        catch (CryptographicException)
        {
            // Projeto aberto por outro usuário/PC: a senha precisa ser digitada novamente.
            return string.Empty;
        }
    }
}

public sealed class WatchEntry
{
    public string ConnectionName { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public DataType DataType { get; set; }
    public ByteOrder ByteOrder { get; set; }
    public int Length { get; set; } = 1;
    public int IntervalMs { get; set; } = 1000;
    public string DisplayName { get; set; } = string.Empty;
    public double Scale { get; set; } = 1;
    public double Offset { get; set; }
    public string Unit { get; set; } = string.Empty;
    public double? AlarmHigh { get; set; }
    public double? AlarmLow { get; set; }
    public bool AlarmOnCommFailure { get; set; }
    public bool Trended { get; set; }
}
