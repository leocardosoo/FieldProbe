namespace FieldProbe.App.ViewModels;

public sealed record LogEntry(DateTime Timestamp, string Source, string Kind, string Message, string Hex = "")
{
    public string Time => Timestamp.ToString("HH:mm:ss.fff");

    public bool IsTraffic => Kind is "TX" or "RX" or "TRACE";

    public string Text => Hex.Length == 0 ? Message : $"{Message}    [{Hex}]";
}
