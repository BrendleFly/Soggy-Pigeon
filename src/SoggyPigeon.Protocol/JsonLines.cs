using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SoggyPigeon.Protocol;

/// <summary>JSON-lines codec: one UTF-8 JSON object per line, tagged "t".</summary>
public static class PigeonJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Serialize a message object (its "t" property names the type).</summary>
    public static string Encode(object msg)
        => JsonSerializer.Serialize(msg, msg.GetType(), Options);

    /// <summary>Decode a line into (type, strongly-typed message).</summary>
    public static (string Type, object Msg) Decode(string line)
    {
        using var doc = JsonDocument.Parse(line);
        if (!doc.RootElement.TryGetProperty("t", out var tEl))
            throw new InvalidDataException("message has no \"t\"");
        var type = tEl.GetString() ?? throw new InvalidDataException("empty \"t\"");
        if (!PigeonMessages.Types.TryGetValue(type, out var clrType))
            throw new InvalidDataException($"unknown message type '{type}'");
        var msg = doc.RootElement.Deserialize(clrType, Options)
                  ?? throw new InvalidDataException($"could not decode '{type}'");
        return (type, msg);
    }
}

/// <summary>Newline-delimited JSON over a control connection (plain or TLS).</summary>
public sealed class JsonLineConnection : IDisposable
{
    private readonly Stream _stream;
    private readonly TcpClient? _client;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;

    public JsonLineConnection(TcpClient client)
        : this(client.GetStream(), client) { }

    /// <summary>Stream form — the caller has already done any TLS handshake.</summary>
    public JsonLineConnection(Stream stream, TcpClient? client = null)
    {
        _stream = stream;
        _client = client;
        _reader = new StreamReader(stream, new UTF8Encoding(false));
        // newline acts as the frame boundary; do not auto-flush per write
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = false, NewLine = "\n" };
    }

    /// <summary>Serializes writes: the heartbeat loop and UI sends overlap, and
    /// StreamWriter is not thread-safe — interleaved writes corrupt the framing.</summary>
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    public async Task SendAsync(object msg, CancellationToken ct = default)
    {
        var line = PigeonJson.Encode(msg);
        await _sendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _writer.WriteLineAsync(line.AsMemory(), ct).ConfigureAwait(false);
            await _writer.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <summary>Receive the next message; null when the peer closed the connection.</summary>
    public async Task<(string Type, object Msg)?> ReceiveAsync(CancellationToken ct = default)
    {
        var line = await _reader.ReadLineAsync(ct).ConfigureAwait(false);
        if (line is null || line.Length == 0) return null;
        return PigeonJson.Decode(line);
    }

    public void Dispose()
    {
        try { _reader.Dispose(); } catch { }
        try { _writer.Dispose(); } catch { }
        try { _stream.Dispose(); } catch { }
        try { _client?.Dispose(); } catch { }
        _sendGate.Dispose();
    }
}
