using FieldProbe.Core;

namespace FieldProbe.App.ViewModels;

public sealed record LogEntry(DateTime Timestamp, string Source, string Kind, string Message, string Hex = "",
    byte[]? Data = null, IFrameDecoder? Decoder = null)
{
    public string Time => Timestamp.ToString("HH:mm:ss.fff");

    public bool IsTraffic => Kind is "TX" or "RX" or "TRACE";

    public string Text => Hex.Length == 0 ? Message : $"{Message}    [{Hex}]";

    /// <summary>Pacote TX/RX com bytes e um driver que sabe explicá-lo (duplo clique abre o decodificador).</summary>
    public bool CanDecode => Data is { Length: > 0 } && Decoder is not null && Kind is "TX" or "RX";

    public FrameDirection Direction => Kind == "TX" ? FrameDirection.Tx : FrameDirection.Rx;
}
