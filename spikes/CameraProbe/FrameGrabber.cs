using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Channels;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;

namespace CameraProbe;

/// <summary>Streams frames from a camera through MediaCapture and converts them to 8-bit luma.</summary>
public sealed class FrameGrabber : IAsyncDisposable
{
    private readonly MediaCapture _capture;
    private readonly MediaFrameReader _reader;
    private readonly Channel<LumaFrame> _frames = Channel.CreateBounded<LumaFrame>(
        new BoundedChannelOptions(4) { FullMode = BoundedChannelFullMode.DropOldest });

    public MediaFrameFormat Format { get; }
    public MediaCapture Capture => _capture;

    private FrameGrabber(MediaCapture capture, MediaFrameReader reader, MediaFrameFormat format)
    {
        _capture = capture;
        _reader = reader;
        Format = format;
        _reader.FrameArrived += OnFrameArrived;
    }

    public static async Task<FrameGrabber> OpenAsync(string deviceId, int minWidth = 160, bool shared = false)
    {
        var capture = new MediaCapture();
        await capture.InitializeAsync(new MediaCaptureInitializationSettings
        {
            VideoDeviceId = deviceId,
            StreamingCaptureMode = StreamingCaptureMode.Video,
            MemoryPreference = MediaCaptureMemoryPreference.Cpu,
            SharingMode = shared ? MediaCaptureSharingMode.SharedReadOnly : MediaCaptureSharingMode.ExclusiveControl,
        });

        var source = capture.FrameSources.Values.First(s => s.Info.SourceKind == MediaFrameSourceKind.Color);
        var format = source.CurrentFormat;
        if (!shared)
        {
            // Smallest uncompressed format at or above minWidth keeps CPU cost negligible.
            format = source.SupportedFormats
                .Where(f => f.VideoFormat.Width >= minWidth)
                .OrderBy(f => f.Subtype is "MJPG" ? 1 : 0)
                .ThenBy(f => f.VideoFormat.Width * f.VideoFormat.Height)
                .ThenByDescending(f => (double)f.FrameRate.Numerator / Math.Max(1, f.FrameRate.Denominator))
                .FirstOrDefault() ?? format;
            await source.SetFormatAsync(format);
        }

        var reader = await capture.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Nv12);
        reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
        var grabber = new FrameGrabber(capture, reader, format);
        var status = await reader.StartAsync();
        if (status != MediaFrameReaderStartStatus.Success)
            throw new InvalidOperationException($"Frame reader failed to start: {status}");
        return grabber;
    }

    public int FramesArrived;
    public int FramesWithoutBitmap;
    public int FramesFailed;
    public Exception? LastError;

    private void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        Interlocked.Increment(ref FramesArrived);
        try { Convert(sender); }
        catch (Exception ex) { Interlocked.Increment(ref FramesFailed); LastError = ex; }
    }

    private void Convert(MediaFrameReader sender)
    {
        using var frame = sender.TryAcquireLatestFrame();
        var bitmap = frame?.VideoMediaFrame?.SoftwareBitmap;
        if (bitmap is null) { Interlocked.Increment(ref FramesWithoutBitmap); return; }

        using var gray = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Gray8);
        var buffer = new Windows.Storage.Streams.Buffer((uint)(gray.PixelWidth * gray.PixelHeight * 2));
        gray.CopyToBuffer(buffer);
        var bytes = buffer.ToArray(0, (int)buffer.Length);
        var stride = bytes.Length / gray.PixelHeight;
        _frames.Writer.TryWrite(new LumaFrame(bytes, gray.PixelWidth, gray.PixelHeight, stride, DateTime.UtcNow));
        bitmap.Dispose();
    }

    public async Task<LumaFrame> NextFrameAsync(CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try { return await _frames.Reader.ReadAsync(timeout.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"no frame within 5 s (arrived {FramesArrived}, no bitmap {FramesWithoutBitmap}, failed {FramesFailed}: {LastError?.Message})");
        }
    }

    /// <summary>Drops queued frames so the next read reflects settings applied after this call.</summary>
    public void Flush()
    {
        while (_frames.Reader.TryRead(out _)) { }
    }

    public async ValueTask DisposeAsync()
    {
        _reader.FrameArrived -= OnFrameArrived;
        await _reader.StopAsync();
        _reader.Dispose();
        _capture.Dispose();
    }
}

public sealed record LumaFrame(byte[] Pixels, int Width, int Height, int Stride, DateTime Timestamp)
{
    public LumaStats Stats() => LumaStats.From(this);
}

public readonly record struct LumaStats(double Mean, double LinearMean, int P5, int Median, int P95, double ClipHigh, double ClipLow)
{
    // Approximate display gamma; replaced by a measured response curve during calibration.
    private static readonly double[] Linear = Enumerable.Range(0, 256).Select(v => Math.Pow(v / 255.0, 2.2)).ToArray();

    public static LumaStats From(LumaFrame f)
    {
        var hist = new int[256];
        long sum = 0;
        double lin = 0;
        for (var y = 0; y < f.Height; y++)
        {
            var row = y * f.Stride;
            for (var x = 0; x < f.Width; x++)
            {
                var v = f.Pixels[row + x];
                hist[v]++;
                sum += v;
                lin += Linear[v];
            }
        }
        var n = f.Width * f.Height;
        return new LumaStats(
            (double)sum / n, lin / n,
            Percentile(hist, n, 0.05), Percentile(hist, n, 0.5), Percentile(hist, n, 0.95),
            hist.Skip(250).Sum() / (double)n,
            hist.Take(5).Sum() / (double)n);
    }

    private static int Percentile(int[] hist, int n, double q)
    {
        var target = q * n;
        var acc = 0;
        for (var i = 0; i < 256; i++)
        {
            acc += hist[i];
            if (acc >= target) return i;
        }
        return 255;
    }

    public override string ToString() =>
        $"mean {Mean,6:F1}  lin {LinearMean,7:F4}  p5 {P5,3}  med {Median,3}  p95 {P95,3}  clipHi {ClipHigh,6:P1}  clipLo {ClipLow,6:P1}";
}
