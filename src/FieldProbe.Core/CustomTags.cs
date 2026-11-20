using System.Text.Json;
using System.Text.Json.Serialization;

namespace FieldProbe.Core;

/// <summary>Variável criada pelo usuário numa pasta da árvore (ex.: "Velocidade" = DB1.DBD4, Float32).</summary>
public sealed record CustomTag(
    string Folder,
    string Name,
    string Address,
    DataType DataType,
    ByteOrder ByteOrder = ByteOrder.ABCD,
    int Length = 1)
{
    public TagAddress ToAddress() => new(Address, DataType, ByteOrder, Length);

    public string Summary => DataType == DataType.String ? $"{Address} · String[{Length}]" : $"{Address} · {DataType}";
}

/// <summary>Guarda as variáveis do usuário nas opções da conexão (vão junto no arquivo do projeto).</summary>
public static class CustomTags
{
    public const string OptionKey = "CustomTags";

    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    public static List<CustomTag> Load(ConnectionSettings? settings)
    {
        if (settings is null || !settings.Options.TryGetValue(OptionKey, out var text) || string.IsNullOrWhiteSpace(text))
            return new List<CustomTag>();
        try
        {
            return JsonSerializer.Deserialize<List<CustomTag>>(text, Json) ?? new List<CustomTag>();
        }
        catch (JsonException)
        {
            return new List<CustomTag>();
        }
    }

    public static void Save(ConnectionSettings settings, IEnumerable<CustomTag> tags)
    {
        var list = tags.ToList();
        if (list.Count == 0) settings.Options.Remove(OptionKey);
        else settings.Options[OptionKey] = JsonSerializer.Serialize(list, Json);
    }

    public static TagNode ToNode(CustomTag tag) => new(tag.Name, tag.ToAddress())
    {
        Key = tag.Folder,
        IsCustom = true,
        Subtitle = tag.Summary,
        Description = $"{tag.Name}\n{tag.Summary}"
    };

    /// <summary>Coloca na pasta as variáveis do usuário que pertencem a ela.</summary>
    public static void AddTo(TagNode folder, IEnumerable<CustomTag> tags)
    {
        if (folder.CustomFolder is null) return;
        foreach (var tag in tags.Where(t => string.Equals(t.Folder, folder.CustomFolder, StringComparison.OrdinalIgnoreCase)))
            folder.Children.Add(ToNode(tag));
    }
}
