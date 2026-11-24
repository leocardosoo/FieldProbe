using FieldProbe.Core;

namespace FieldProbe.App.Hmi;

/// <summary>Tipos de elemento da tela de teste (IHM).</summary>
public enum HmiKind
{
    Texto,
    Lampada,
    Botao,
    Valor,
    Setpoint,
    Barra,

    // Símbolos industriais (desenhados e animados)
    Motor,
    Bomba,
    Valvula,
    Tanque,
    Esteira,
    Sinaleiro
}

/// <summary>Visual da tela.</summary>
public enum HmiTheme
{
    /// <summary>Claro, moderno (cartões brancos arredondados).</summary>
    Claro,

    /// <summary>Estilo SCADA/WinCC: fundo cinza industrial, painéis retos, campos de valor brancos e fonte Arial.</summary>
    WinCC
}

/// <summary>Como a imagem de fundo ocupa a tela.</summary>
public enum HmiImageFit
{
    Ajustar,
    Esticar,
    Original
}

/// <summary>O que o botão faz ao ser clicado.</summary>
public enum HmiButtonMode
{
    /// <summary>Inverte o bit (liga se estiver desligado e vice-versa).</summary>
    Alternar,

    /// <summary>Liga enquanto está pressionado e desliga ao soltar (botão de pulso).</summary>
    Pulso,

    /// <summary>Escreve 1 / true.</summary>
    Ligar,

    /// <summary>Escreve 0 / false.</summary>
    Desligar,

    /// <summary>Escreve um valor fixo (ex.: receita 3).</summary>
    EscreverValor,

    /// <summary>Navega para outra tela (não escreve nada).</summary>
    IrParaTela,

    /// <summary>Volta para a tela anterior (não escreve nada).</summary>
    Voltar
}

/// <summary>Uma tela de teste salva no projeto (.fpx).</summary>
public sealed class HmiScreenData
{
    public string Name { get; set; } = "Tela 1";
    public int Width { get; set; } = 1280;
    public int Height { get; set; } = 720;
    public HmiTheme Theme { get; set; } = HmiTheme.Claro;

    /// <summary>Imagem de fundo (PNG/JPG) guardada dentro do projeto, em Base64, para o .fpx funcionar em outro PC.</summary>
    public string? BackgroundImage { get; set; }
    public HmiImageFit BackgroundFit { get; set; } = HmiImageFit.Ajustar;
    public List<HmiElementData> Elements { get; set; } = new();
}

/// <summary>Um elemento da tela, com posição, aparência e a variável ligada a ele.</summary>
public sealed class HmiElementData
{
    public HmiKind Kind { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 160;
    public double Height { get; set; } = 80;
    public string Text { get; set; } = string.Empty;
    public double FontSize { get; set; } = 14;

    // Variável ligada
    public string ConnectionName { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public DataType DataType { get; set; } = DataType.Auto;
    public ByteOrder ByteOrder { get; set; } = ByteOrder.ABCD;
    public int Length { get; set; } = 1;

    /// <summary>Sinaleiro: endereços das luzes amarela e vermelha (a verde usa <see cref="Address"/>).</summary>
    public string Address2 { get; set; } = string.Empty;
    public string Address3 { get; set; } = string.Empty;

    /// <summary>
    /// Ligado a um item da Data Access View (mesma conexão + endereço): usa o valor com escala, a unidade
    /// e o alarme do item, em vez de configurações próprias.
    /// </summary>
    public bool LinkToItem { get; set; }

    /// <summary>Nome do item ligado (para achar o certo quando há dois itens no mesmo endereço).</summary>
    public string LinkedItemName { get; set; } = string.Empty;

    /// <summary>Botão de navegação: nome da tela de destino.</summary>
    public string TargetScreen { get; set; } = string.Empty;

    // Aparência e comportamento
    public string Unit { get; set; } = string.Empty;
    public int Decimals { get; set; } = 1;
    public double Min { get; set; }
    public double Max { get; set; } = 100;
    public double? AlarmHigh { get; set; }
    public double? AlarmLow { get; set; }
    public string OnColor { get; set; } = "Verde";
    public string OffColor { get; set; } = "Cinza";
    public HmiButtonMode ButtonMode { get; set; } = HmiButtonMode.Alternar;
    public string WriteValue { get; set; } = "1";

    public HmiElementData Clone() => (HmiElementData)MemberwiseClone();
}

/// <summary>Cores com nome (simples de escolher e salvas como texto no projeto).</summary>
public static class HmiColors
{
    public static readonly IReadOnlyDictionary<string, string> All = new Dictionary<string, string>
    {
        ["Verde"] = "#1A7F37",
        ["Vermelho"] = "#CF222E",
        ["Amarelo"] = "#D4A72C",
        ["Laranja"] = "#E36209",
        ["Azul"] = "#0F6CBD",
        ["Roxo"] = "#8250DF",
        ["Cinza"] = "#AFB8C1",
        ["Preto"] = "#1F2328",
        ["Branco"] = "#FFFFFF"
    };

    public static string Hex(string name) => All.TryGetValue(name, out var hex) ? hex : "#AFB8C1";
}
