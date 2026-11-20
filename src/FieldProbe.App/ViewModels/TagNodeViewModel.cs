using System.Collections.ObjectModel;
using FieldProbe.App.Mvvm;
using FieldProbe.Core;

namespace FieldProbe.App.ViewModels;

/// <summary>Nó da árvore, com carregamento sob demanda dos filhos (OPC UA, tags de programa Logix...).</summary>
public sealed class TagNodeViewModel : ObservableObject
{
    private bool _isExpanded;
    private bool _loaded;

    public TagNodeViewModel(TagNode node, ConnectionViewModel connection)
    {
        Node = node;
        Connection = connection;

        foreach (var child in node.Children)
            Children.Add(new TagNodeViewModel(child, connection));

        if (node.HasLazyChildren && node.Children.Count == 0)
            Children.Add(Placeholder(connection, "Carregando..."));
        else
            _loaded = true;
    }

    private TagNodeViewModel(string text, ConnectionViewModel connection)
    {
        Node = new TagNode(text);
        Connection = connection;
        IsPlaceholder = true;
        _loaded = true;
    }

    public TagNode Node { get; }

    public ConnectionViewModel Connection { get; }

    public bool IsPlaceholder { get; }

    public string Name => Node.Name;

    public string? ToolTip => Node.Description ?? Node.Address?.Address;

    public bool IsVariable => Node.Address is not null;

    /// <summary>Pasta onde o usuário pode criar variáveis (DB de S7, área Modbus): mostra o botão "+".</summary>
    public bool CanAddVariable => !IsPlaceholder && Node.CustomFolder is not null;

    /// <summary>Variável criada pelo usuário (pode ser editada ou removida).</summary>
    public bool IsCustom => Node.IsCustom;

    public string? Subtitle => Node.Subtitle;

    public bool HasSubtitle => !string.IsNullOrEmpty(Node.Subtitle);

    /// <summary>Glifo Segoe MDL2: pasta, variável ou informação.</summary>
    public string Glyph => IsPlaceholder ? "" : IsVariable ? "" : "";

    public ObservableCollection<TagNodeViewModel> Children { get; } = new();

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (!Set(ref _isExpanded, value)) return;
            if (value && !_loaded) _ = LoadChildrenAsync();
        }
    }

    private async Task LoadChildrenAsync()
    {
        _loaded = true;
        try
        {
            var children = await Connection.Driver.BrowseAsync(Node);
            Children.Clear();
            foreach (var child in children)
                Children.Add(new TagNodeViewModel(child, Connection));
            if (Children.Count == 0)
                Children.Add(Placeholder(Connection, "(vazio)"));
        }
        catch (Exception ex)
        {
            Children.Clear();
            Children.Add(Placeholder(Connection, $"Erro ao navegar: {ex.Message}"));
            _loaded = false; // permite tentar de novo ao reexpandir
        }
    }

    private static TagNodeViewModel Placeholder(ConnectionViewModel connection, string text) => new(text, connection);

    /// <summary>Nó apenas informativo (erros, avisos).</summary>
    public static TagNodeViewModel Message(ConnectionViewModel connection, string text) => new(text, connection);
}
