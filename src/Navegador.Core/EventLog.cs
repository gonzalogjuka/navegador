using System.Diagnostics;
using System.Text.Json;

namespace Navegador.Core;

/// <summary>
/// Log JSONL con el mismo formato que tools/metrics (metrics/events.py):
/// {"type":"event","name":...,"ts":epoch_s}, {"type":"span","name":...,"duration_ms":...,"ok":...},
/// {"type":"sample",...}. Se lee con: python -m metrics report &lt;archivo&gt;.
/// </summary>
public sealed class EventLog : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly object _lock = new();

    public string Path { get; }

    public EventLog(string path)
    {
        Path = path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
    }

    public void Write(Dictionary<string, object?> record)
    {
        record.TryAdd("ts", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0);
        var line = JsonSerializer.Serialize(record);
        lock (_lock) _writer.WriteLine(line);
    }

    public void Event(string name, params (string Key, object? Value)[] fields)
    {
        var r = new Dictionary<string, object?> { ["type"] = "event", ["name"] = name };
        foreach (var (k, v) in fields) r[k] = v;
        Write(r);
    }

    public Span StartSpan(string name, params (string Key, object? Value)[] fields) => new(this, name, fields);

    public void Dispose()
    {
        lock (_lock) _writer.Dispose();
    }

    public sealed class Span
    {
        private readonly EventLog _log;
        private readonly string _name;
        private readonly (string Key, object? Value)[] _fields;
        private readonly long _t0 = Stopwatch.GetTimestamp();
        private bool _done;

        internal Span(EventLog log, string name, (string Key, object? Value)[] fields)
        {
            _log = log;
            _name = name;
            _fields = fields;
        }

        public void End(bool ok = true)
        {
            if (_done) return;
            _done = true;
            var r = new Dictionary<string, object?>
            {
                ["type"] = "span",
                ["name"] = _name,
                ["duration_ms"] = Math.Round(Stopwatch.GetElapsedTime(_t0).TotalMilliseconds, 2),
                ["ok"] = ok,
            };
            foreach (var (k, v) in _fields) r[k] = v;
            _log.Write(r);
        }
    }
}
