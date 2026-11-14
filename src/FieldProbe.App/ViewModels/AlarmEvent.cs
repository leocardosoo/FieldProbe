using FieldProbe.App.Mvvm;

namespace FieldProbe.App.ViewModels;

/// <summary>Evento do histórico de alarmes.</summary>
public sealed class AlarmEvent : ObservableObject
{
    private bool _acknowledged;

    public AlarmEvent(DateTime timestamp, string connection, string item, string address, AlarmState state, string description, string value)
    {
        Timestamp = timestamp;
        Connection = connection;
        Item = item;
        Address = address;
        State = state;
        Description = description;
        Value = value;
        _acknowledged = state == AlarmState.Normal;
    }

    public DateTime Timestamp { get; }
    public string Time => Timestamp.ToString("dd/MM HH:mm:ss");
    public string Connection { get; }
    public string Item { get; }
    public string Address { get; }
    public AlarmState State { get; }
    public string Description { get; }
    public string Value { get; }

    public bool IsReturnToNormal => State == AlarmState.Normal;

    public bool Acknowledged
    {
        get => _acknowledged;
        set => Set(ref _acknowledged, value);
    }
}
