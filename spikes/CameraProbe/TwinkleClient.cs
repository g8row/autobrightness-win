using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CameraProbe;

/// <summary>Client for Twinkle Tray's local command pipe (\.\pipe\twinkle-tray\cmds).</summary>
public static class TwinkleClient
{
    private const string PipeName = @"twinkle-tray\cmds";

    public static async Task<string> SendAsync(object message, bool expectReply = true)
    {
        await using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(2000);
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));
        await pipe.WriteAsync(bytes);
        await pipe.FlushAsync();
        if (!expectReply) return "";

        var buf = new byte[1 << 16];
        using var cts = new CancellationTokenSource(2000);
        var n = await pipe.ReadAsync(buf, cts.Token);
        return Encoding.UTF8.GetString(buf, 0, n);
    }

    public static async Task<JsonObject> ListAsync() =>
        JsonNode.Parse(await SendAsync(new { type = "list" }))!.AsObject();

    public static async Task<int> GetBrightnessAsync(string monitor) =>
        int.Parse(await SendAsync(new { type = "get", monitor, property = "brightness" }));

    public static Task SetBrightnessAsync(string monitor, int value) =>
        SendAsync(new { type = "set", monitor, vcp = "brightness", value }, expectReply: false);
}
