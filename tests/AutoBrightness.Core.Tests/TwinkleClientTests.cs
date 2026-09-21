using System.IO.Pipes;
using System.Text;
using AutoBrightness.Twinkle;

namespace AutoBrightness.Tests;

/// <summary>
/// Runs a fake Twinkle Tray pipe server that behaves like the real one: it reads a command and always writes
/// a reply, even for "set". Twinkle Tray crashed with EPIPE when the client hung up before that reply.
/// </summary>
public sealed class TwinkleClientTests
{
    private static async Task<(string Received, bool ReplyDelivered)> ServeOnceAsync(string pipeName, string reply, Func<Task> client)
    {
        await using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var clientTask = Task.Run(client);
        await server.WaitForConnectionAsync(TestContext.Current.CancellationToken);

        var buffer = new byte[4096];
        var n = await server.ReadAsync(buffer, TestContext.Current.CancellationToken);
        var received = Encoding.UTF8.GetString(buffer, 0, n);

        // Twinkle Tray replies after handling the command asynchronously.
        await Task.Delay(50, TestContext.Current.CancellationToken);
        var delivered = true;
        try
        {
            await server.WriteAsync(Encoding.UTF8.GetBytes(reply), TestContext.Current.CancellationToken);
            await server.FlushAsync(TestContext.Current.CancellationToken);
            server.WaitForPipeDrain();
        }
        catch (IOException)
        {
            delivered = false; // what Node reports as EPIPE
        }
        await clientTask;
        return (received, delivered);
    }

    private static string NewPipe() => $"ab-test-{Guid.NewGuid():N}";

    [Fact]
    public async Task SetWaitsForTheReplyBeforeClosing()
    {
        var pipe = NewPipe();
        var client = new TwinkleClient(pipe);
        var (received, delivered) = await ServeOnceAsync(pipe, "undefined", () => client.SetBrightnessAsync("UID260", 42));

        Assert.Contains("\"type\":\"set\"", received);
        Assert.Contains("\"value\":42", received);
        Assert.True(delivered, "client closed the pipe before Twinkle Tray could reply (EPIPE)");
    }

    [Fact]
    public async Task GetParsesBrightness()
    {
        var pipe = NewPipe();
        var client = new TwinkleClient(pipe);
        var result = 0;
        await ServeOnceAsync(pipe, "37", async () => result = await client.GetBrightnessAsync("UID260"));
        Assert.Equal(37, result);
    }

    [Fact]
    public async Task ListParsesMonitors()
    {
        var pipe = NewPipe();
        var client = new TwinkleClient(pipe);
        IReadOnlyList<TwinkleMonitor> monitors = [];
        await ServeOnceAsync(pipe, """{"7&bf77d&0&UID260":{"name":"27GL650F","type":"ddcci","brightness":30}}""",
            async () => monitors = await client.ListAsync());

        var m = Assert.Single(monitors);
        Assert.Equal(("7&bf77d&0&UID260", "27GL650F", "ddcci", 30), (m.Key, m.Name, m.Type, m.Brightness));
    }

    [Fact]
    public async Task MissingTwinkleTrayIsReported() =>
        await Assert.ThrowsAsync<TwinkleUnavailableException>(() => new TwinkleClient(NewPipe()).GetBrightnessAsync("x", TestContext.Current.CancellationToken));
}
