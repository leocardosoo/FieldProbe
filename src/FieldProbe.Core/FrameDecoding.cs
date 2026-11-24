using System.Buffers.Binary;
using System.Text;

namespace FieldProbe.Core;

/// <summary>Um campo do pacote: posição, tamanho, nome, valor interpretado e uma explicação curta.</summary>
public sealed record DecodedField(int Offset, int Length, string Name, string Value, string Note = "", int Level = 0);

/// <summary>Camada do protocolo (ex.: "Modbus TCP · MBAP", "S7comm · Parâmetros").</summary>
public sealed class DecodedLayer
{
    public DecodedLayer(string name) => Name = name;

    public string Name { get; }

    public List<DecodedField> Fields { get; } = new();
}

/// <summary>Pacote decodificado, camada por camada, para a janela "Decodificar pacote".</summary>
public sealed class DecodedFrame
{
    public DecodedFrame(string protocol) => Protocol = protocol;

    public string Protocol { get; }

    /// <summary>Resumo em uma linha (ex.: "Requisição · FC03 Read Holding Registers · HR:0, 4 registradores").</summary>
    public string Summary { get; set; } = string.Empty;

    public List<DecodedLayer> Layers { get; } = new();

    public List<string> Warnings { get; } = new();

    /// <summary>Texto para copiar (relatório do pacote).</summary>
    public string ToReport(byte[] data)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{Protocol} · {Summary}");
        foreach (var warning in Warnings) sb.AppendLine($"AVISO: {warning}");
        foreach (var layer in Layers)
        {
            sb.AppendLine().AppendLine($"[{layer.Name}]");
            foreach (var f in layer.Fields)
            {
                var bytes = f.Length > 0 && f.Offset + f.Length <= data.Length
                    ? string.Join(" ", data.Skip(f.Offset).Take(Math.Min(f.Length, 16)).Select(b => b.ToString("X2"))) + (f.Length > 16 ? " ..." : string.Empty)
                    : string.Empty;
                sb.Append(new string(' ', 2 + f.Level * 2)).Append($"{f.Offset,4}  {bytes,-24}  {f.Name}: {f.Value}");
                if (f.Note.Length > 0) sb.Append($"  ({f.Note})");
                sb.AppendLine();
            }
        }
        return sb.ToString();
    }
}

/// <summary>Drivers que sabem explicar os bytes do próprio tráfego (TX/RX do log).</summary>
public interface IFrameDecoder
{
    /// <summary>Decodifica o pacote; nulo quando não reconhece o formato.</summary>
    DecodedFrame? DecodeFrame(byte[] data, FrameDirection direction);
}

/// <summary>O pacote acabou antes do que o cabeçalho prometia.</summary>
public sealed class TruncatedFrameException : Exception
{
    public TruncatedFrameException(string message) : base(message) { }
}

/// <summary>
/// Leitor sequencial que vai lendo o pacote e registrando cada campo na camada atual.
/// Se faltar byte, lança <see cref="TruncatedFrameException"/> (o decodificador marca o pacote como incompleto).
/// </summary>
public sealed class FrameCursor
{
    private readonly byte[] _data;
    private DecodedLayer? _layer;

    public FrameCursor(byte[] data, DecodedFrame frame)
    {
        _data = data;
        Frame = frame;
    }

    public DecodedFrame Frame { get; }

    public byte[] Data => _data;

    public int Position { get; set; }

    public int Length => _data.Length;

    public int Remaining => _data.Length - Position;

    public void Layer(string name)
    {
        _layer = new DecodedLayer(name);
        Frame.Layers.Add(_layer);
    }

    public void Warn(string message) => Frame.Warnings.Add(message);

    private void Ensure(int count, string name)
    {
        if (count < 0 || Remaining < count)
            throw new TruncatedFrameException($"O pacote termina antes do campo '{name}' (precisava de {count} byte(s), restam {Math.Max(0, Remaining)}).");
    }

    /// <summary>Registra um campo na posição atual e avança.</summary>
    public void Field(int length, string name, string value, string note = "", int level = 0)
    {
        Ensure(length, name);
        AddAt(Position, length, name, value, note, level);
        Position += length;
    }

    /// <summary>Registra um campo numa posição qualquer, sem mover o cursor.</summary>
    public void AddAt(int offset, int length, string name, string value, string note = "", int level = 0)
    {
        if (_layer is null) Layer("Pacote");
        _layer!.Fields.Add(new DecodedField(offset, length, name, value, note, level));
    }

    // ---------- espiar sem registrar ----------
    public byte PeekU8(int relative = 0)
    {
        Ensure(relative + 1, "byte");
        return _data[Position + relative];
    }

    public ushort PeekU16BE(int relative = 0)
    {
        Ensure(relative + 2, "word");
        return BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(Position + relative));
    }

    public ushort PeekU16LE(int relative = 0)
    {
        Ensure(relative + 2, "word");
        return BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(Position + relative));
    }

    // ---------- ler e registrar ----------
    public byte U8(string name, Func<byte, string>? text = null, string note = "", int level = 0)
    {
        Ensure(1, name);
        var v = _data[Position];
        Field(1, name, text?.Invoke(v) ?? v.ToString(), note, level);
        return v;
    }

    public ushort U16BE(string name, Func<ushort, string>? text = null, string note = "", int level = 0)
    {
        Ensure(2, name);
        var v = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(Position));
        Field(2, name, text?.Invoke(v) ?? v.ToString(), note, level);
        return v;
    }

    public ushort U16LE(string name, Func<ushort, string>? text = null, string note = "", int level = 0)
    {
        Ensure(2, name);
        var v = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(Position));
        Field(2, name, text?.Invoke(v) ?? v.ToString(), note, level);
        return v;
    }

    public uint U32LE(string name, Func<uint, string>? text = null, string note = "", int level = 0)
    {
        Ensure(4, name);
        var v = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(Position));
        Field(4, name, text?.Invoke(v) ?? v.ToString(), note, level);
        return v;
    }

    public uint U32BE(string name, Func<uint, string>? text = null, string note = "", int level = 0)
    {
        Ensure(4, name);
        var v = BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(Position));
        Field(4, name, text?.Invoke(v) ?? v.ToString(), note, level);
        return v;
    }

    /// <summary>Bloco de bytes (valor = hex, ou o texto informado).</summary>
    public byte[] Bytes(int count, string name, string? value = null, string note = "", int level = 0)
    {
        Ensure(count, name);
        var bytes = _data.AsSpan(Position, count).ToArray();
        Field(count, name, value ?? HexOf(bytes, 24), note, level);
        return bytes;
    }

    /// <summary>Texto ASCII de tamanho fixo.</summary>
    public string Ascii(int count, string name, string note = "", int level = 0)
    {
        Ensure(count, name);
        var text = Printable(_data.AsSpan(Position, count));
        Field(count, name, $"\"{text}\"", note, level);
        return text;
    }

    /// <summary>O resto do pacote, como um campo só.</summary>
    public void Rest(string name, string note = "", int level = 0)
    {
        if (Remaining > 0) Bytes(Remaining, name, null, note, level);
    }

    public static string HexOf(ReadOnlySpan<byte> bytes, int max = 32)
    {
        var shown = bytes.Length > max ? bytes[..max] : bytes;
        var sb = new StringBuilder();
        foreach (var b in shown)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(b.ToString("X2"));
        }
        if (bytes.Length > max) sb.Append($" ... (+{bytes.Length - max})");
        return sb.ToString();
    }

    public static string Printable(ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder(bytes.Length);
        foreach (var b in bytes) sb.Append((b is >= 0x20 and < 0x7F) ? (char)b : '·');
        return sb.ToString();
    }

    public static string Hex8(byte v) => $"0x{v:X2}";

    public static string Hex16(ushort v) => $"0x{v:X4}";

    public static string Hex32(uint v) => $"0x{v:X8}";
}

/// <summary>Executa um decodificador protegendo contra pacote truncado ou malformado.</summary>
public static class FrameDecoding
{
    public static DecodedFrame Run(string protocol, byte[] data, Action<FrameCursor> decode)
    {
        var frame = new DecodedFrame(protocol);
        var cursor = new FrameCursor(data, frame);
        try
        {
            decode(cursor);
            if (cursor.Remaining > 0 && cursor.Position > 0)
            {
                cursor.Layer("Bytes não interpretados");
                cursor.Rest("Restante", "o decodificador não reconheceu esta parte");
            }
        }
        catch (TruncatedFrameException ex)
        {
            frame.Warnings.Add("Pacote incompleto: " + ex.Message);
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or InvalidOperationException)
        {
            frame.Warnings.Add("Não foi possível decodificar o restante do pacote: " + ex.Message);
        }
        return frame;
    }
}
