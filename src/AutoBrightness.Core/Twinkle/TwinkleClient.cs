using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AutoBrightness.Twinkle;

public sealed record TwinkleMonitor(string Key, string Name, string Type, int Brightness);

public interface IBrightnessBackend
{
    Task<IReadOnlyList<TwinkleMonitor>> ListAsync(CancellationToken ct = default);
    Task<int> GetBrightnessAsync(string monitorKey, CancellationToken ct = default);
    Task SetBrightnessAsync(string monitorKey, int percent, CancellationToken ct = default);
}

public sealed class TwinkleUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Client for Twinkle Tray's local command pipe (\\.\pipe\twinkle-tray\cmds), present in the Store and
/// installer builds since 1.16. Messages are JSON, and every message gets a reply.
/// </summary>
public sealed class TwinkleClient(string pipeName = TwinkleClient.DefaultPipeName) : IBrightnessBackend
{
    public const string DefaultPipeName = @"twinkle-tray\cmds";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);
    private readonly string _pipeName = pipeName;

    public async Task<IReadOnlyList<TwinkleMonitor>> ListAsync(CancellationToken ct = default)
    {
        var reply = await SendAsync(new { type = "list" }, ct);
        try
        {
            var root = JsonNode.Parse(reply) as JsonObject ?? throw new JsonException("not an object");
            return root.Select(kv => new TwinkleMonitor(
                    kv.Key,
                    ReadString(kv.Value?["name"]) ?? kv.Key,
                    ReadString(kv.Value?["type"]) ?? "?",
                    ReadInt(kv.Value?["brightness"])))
                .ToList();
        }
        catch (JsonException ex)
        {
            throw new TwinkleUnavailableException($"Unexpected reply from Twinkle Tray: {Shorten(reply)}", ex);
        }
    }

    public async Task<int> GetBrightnessAsync(string monitorKey, CancellationToken ct = default)
    {
        var reply = await SendAsync(new { type = "get", monitor = monitorKey, property = "brightness" }, ct);
        return double.TryParse(reply.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)
               && v is >= 0 and <= 100
            ? (int)Math.Round(v)
            : throw new TwinkleUnavailableException($"Twinkle Tray did not report brightness for {monitorKey} ({Shorten(reply)}).");
    }

    /// <summary>
    /// Twinkle Tray answers every command, even "set" (with "undefined"). The reply must be read before closing:
    /// if the pipe is already closed, Twinkle Tray's write fails with EPIPE and it shows an uncaught-exception dialog.
    /// </summary>
    public Task SetBrightnessAsync(string monitorKey, int percent, CancellationToken ct = default) =>
        SendAsync(new { type = "set", monitor = monitorKey, vcp = "brightness", value = Math.Clamp(percent, 0, 100) }, ct);

    private static int ReadInt(JsonNode? n) =>
        n is JsonValue v && v.TryGetValue<double>(out var d) && double.IsFinite(d) ? (int)Math.Clamp(Math.Round(d), 0, 100) : 0;

    private static string? ReadString(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static string Shorten(string s) => s.Length > 200 ? s[..200] + "…" : s;

    private async Task<string> SendAsync(object message, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        try
        {
            // CurrentUserOnly: only talk to a pipe server running as this user, not one another account created first.
            await using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(timeout.Token);
            await pipe.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message)), timeout.Token);
            await pipe.FlushAsync(timeout.Token);
            var buffer = new byte[1 << 16];
            var total = 0;
            // Replies arrive in one write; keep reading only while the JSON is still incomplete.
            do
            {
                var n = await pipe.ReadAsync(buffer.AsMemory(total), timeout.Token);
                if (n == 0) break;
                total += n;
            } while (total < buffer.Length && !IsComplete(buffer.AsSpan(0, total)));
            return Encoding.UTF8.GetString(buffer, 0, total);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TwinkleUnavailableException("Twinkle Tray is not responding. Make sure it is running.");
        }
        catch (IOException ex)
        {
            throw new TwinkleUnavailableException("Could not talk to Twinkle Tray. Make sure it is running.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new TwinkleUnavailableException("Twinkle Tray's command pipe belongs to another user or refused the connection.", ex);
        }
    }

    private static bool IsComplete(ReadOnlySpan<byte> data)
    {
        var s = Encoding.UTF8.GetString(data).Trim();
        if (s.Length == 0) return false;
        if (s[0] != '{' && s[0] != '[') return true;
        try { JsonDocument.Parse(s).Dispose(); return true; }
        catch (JsonException) { return false; }
    }
}
