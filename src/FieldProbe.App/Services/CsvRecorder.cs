using System.Globalization;
using System.IO;
using System.Text;
using FieldProbe.Core;

namespace FieldProbe.App.Services;

/// <summary>Grava cada leitura/notificação da watch list em CSV (separador ";" para abrir direto no Excel pt-BR).</summary>
public sealed class CsvRecorder : IDisposable
{
    private readonly object _lock = new();
    private StreamWriter? _writer;

    public bool IsRecording => _writer is not null;

    public string? FilePath { get; private set; }

    public long Rows { get; private set; }

    public DateTime StartedAt { get; private set; }

    private DateTime _lastFlush;

    public void Start(string path)
    {
        Stop();
        lock (_lock)
        {
            // FileShare.ReadWrite: o Excel/Bloco de Notas consegue abrir o arquivo enquanto a gravação continua.
            var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)) { AutoFlush = false };
            _writer.WriteLine("timestamp;conexao;nome;endereco;tipo;valor_bruto;valor;unidade;qualidade;latencia_ms;erro");
            FilePath = path;
            Rows = 0;
            StartedAt = DateTime.Now;
            _lastFlush = DateTime.Now;
            _writer.Flush();
        }
    }

    public void Write(string connection, string name, TagValue value, string displayValue, string unit)
    {
        lock (_lock)
        {
            if (_writer is null) return;
            var culture = CultureInfo.CurrentCulture;
            _writer.Write(value.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
            _writer.Write(';');
            _writer.Write(Escape(connection));
            _writer.Write(';');
            _writer.Write(Escape(name));
            _writer.Write(';');
            _writer.Write(Escape(value.Address.Address));
            _writer.Write(';');
            _writer.Write(value.TypeName ?? value.Address.DataType.ToString());
            _writer.Write(';');
            _writer.Write(Escape(Convert.ToString(value.Value, culture) ?? string.Empty));
            _writer.Write(';');
            _writer.Write(Escape(unit.Length > 0 && displayValue.EndsWith(" " + unit, StringComparison.Ordinal) ? displayValue[..^(unit.Length + 1)] : displayValue));
            _writer.Write(';');
            _writer.Write(Escape(unit));
            _writer.Write(';');
            _writer.Write(value.Quality);
            _writer.Write(';');
            _writer.Write(value.Latency.TotalMilliseconds.ToString("F1", culture));
            _writer.Write(';');
            _writer.WriteLine(Escape(value.Error ?? string.Empty));
            Rows++;
            if ((DateTime.Now - _lastFlush).TotalSeconds >= 1)
            {
                _writer.Flush(); // grava no disco a cada segundo: nada se perde se o programa fechar
                _lastFlush = DateTime.Now;
            }
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            _writer?.Flush();
            _writer?.Dispose();
            _writer = null;
        }
    }

    public static string Escape(string text) =>
        text.IndexOfAny(new[] { ';', '"', '\n', '\r' }) >= 0 ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;

    public void Dispose() => Stop();
}
