using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Channels;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;

namespace AutoBrightness.Camera;

/// <summary>
/// An open camera stream with exclusive control. Snapshots the camera's controls on open and restores
/// them on dispose, so other apps never inherit a locked exposure.
/// </summary>
internal sealed class CameraSession : IAsyncDisposable
{
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(5);

    private readonly MediaCapture _capture;
    private readonly MediaFrameReader _reader;
    private CameraControls.Snapshot? _snapshot;
    private bool _remembered;
    private readonly Channel<LumaFrame> _frames = Channel.CreateBounded<LumaFrame>(
        new BoundedChannelOptions(4) { FullMode = BoundedChannelFullMode.DropOldest });
    private int _disposed;

    public CameraDevice Device { get; }
    public CameraControls Controls { get; }
    public MediaFrameFormat Format { get; }
    public double FrameRate => (double)Format.FrameRate.Numerator / Math.Max(1u, Format.FrameRate.Denominator);

    /// <summary>Raised on the capture thread for every converted frame.</summary>
    public event Action<LumaFrame>? FrameArrived;

    private CameraSession(CameraDevice device, MediaCapture capture, MediaFrameReader reader, MediaFrameFormat format)
    {
        Device = device;
        _capture = capture;
        _reader = reader;
        Format = format;
        Controls = new CameraControls(capture.VideoDeviceController);
        _reader.FrameArrived += OnFrameArrived;
    }

    public static async Task<CameraSession> OpenAsync(CameraDevice device, int minWidth = 160, bool remember = true)
    {
        var capture = new MediaCapture();
        try
        {
            await capture.InitializeAsync(new MediaCaptureInitializationSettings
            {
                VideoDeviceId = device.Id,
                StreamingCaptureMode = StreamingCaptureMode.Video,
                MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                SharingMode = MediaCaptureSharingMode.ExclusiveControl,
            });
        }
        catch (UnauthorizedAccessException ex)
        {
            capture.Dispose();
            throw new CameraException(CameraFailure.AccessDenied,
                "Camera access is blocked. Allow desktop apps to use the camera in Windows privacy settings.", ex);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            capture.Dispose();
            throw new CameraException(CameraFailure.Unavailable, $"The camera could not be opened; another app may be using it. ({ex.Message.Trim()})", ex);
        }

        try
        {
            var source = capture.FrameSources.Values.FirstOrDefault(s => s.Info.SourceKind == MediaFrameSourceKind.Color)
                         ?? throw new CameraException(CameraFailure.Unsupported, "The camera has no color stream.");

            // The smallest uncompressed format keeps CPU cost negligible; metering needs very few pixels.
            var format = source.SupportedFormats
                .Where(f => f.VideoFormat.Width >= minWidth)
                .OrderBy(f => f.Subtype is "MJPG" or "H264" ? 1 : 0)
                .ThenBy(f => f.VideoFormat.Width * f.VideoFormat.Height)
                .ThenByDescending(f => (double)f.FrameRate.Numerator / Math.Max(1u, f.FrameRate.Denominator))
                .FirstOrDefault() ?? source.CurrentFormat;
            await source.SetFormatAsync(format);

            var reader = await capture.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Nv12);
            reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
            var session = new CameraSession(device, capture, reader, format);
            var status = await reader.StartAsync();
            if (status != MediaFrameReaderStartStatus.Success)
            {
                await session.DisposeAsync();
                throw new CameraException(CameraFailure.Unavailable, $"The camera stream failed to start ({status}).");
            }
            // Snapshot after the stream starts: property reads before streaming delayed the first frame by ~4 s.
            session._snapshot = session.Controls.Save();
            if (remember) CameraRecovery.Remember(device.Id, session._snapshot);
            session._remembered = remember;
            return session;
        }
        catch (Exception ex) when (ex is not CameraException)
        {
            capture.Dispose();
            throw new CameraException(CameraFailure.Unavailable, $"The camera stream failed to start. ({ex.Message.Trim()})", ex);
        }
    }

    internal int EventsRaised, FramesWithoutBitmap, FramesFailed;
    internal DateTime? FirstEventAt;
    internal Exception? LastError;

    private void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        Interlocked.Increment(ref EventsRaised);
        FirstEventAt ??= DateTime.UtcNow;
        try
        {
            using var frame = sender.TryAcquireLatestFrame();
            using var bitmap = frame?.VideoMediaFrame?.SoftwareBitmap;
            if (bitmap is null) { Interlocked.Increment(ref FramesWithoutBitmap); return; }

            using var gray = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Gray8);
            var buffer = new Windows.Storage.Streams.Buffer((uint)(gray.PixelWidth * gray.PixelHeight * 2));
            gray.CopyToBuffer(buffer);
            var bytes = buffer.ToArray(0, (int)buffer.Length);
            var luma = new LumaFrame(bytes, gray.PixelWidth, gray.PixelHeight, bytes.Length / gray.PixelHeight, DateTime.UtcNow);
            _frames.Writer.TryWrite(luma);
            FrameArrived?.Invoke(luma);
        }
        catch (Exception ex)
        {
            // A dropped frame is harmless; a stream that stops producing frames surfaces as a timeout.
            Interlocked.Increment(ref FramesFailed);
            LastError = ex;
        }
    }

    public async Task<LumaFrame> NextFrameAsync(CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(FrameTimeout);
        try
        {
            return await _frames.Reader.ReadAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new CameraException(CameraFailure.Stalled,
                "The camera stopped delivering frames. Unplugging and reconnecting it usually fixes this.");
        }
    }

    /// <summary>Drops queued frames, then skips <paramref name="skip"/> more so the next read reflects new settings.</summary>
    public async Task SettleAsync(int skip, CancellationToken ct = default)
    {
        while (_frames.Reader.TryRead(out _)) { }
        for (var i = 0; i < skip; i++) await NextFrameAsync(ct);
    }

    /// <summary>Sums the histograms of the next <paramref name="count"/> frames over the region of interest.</summary>
    public async Task<(Histogram Histogram, LumaFrame Last)> CaptureAsync(int count, Roi roi, CancellationToken ct = default)
    {
        var bins = new int[256];
        LumaFrame last = null!;
        for (var i = 0; i < count; i++)
        {
            last = await NextFrameAsync(ct);
            var h = last.Histogram(roi).Bins;
            for (var b = 0; b < 256; b++) bins[b] += h[b];
        }
        return (new Histogram(bins), last);
    }

    /// <summary>When false, the camera keeps whatever settings the session left; used by diagnostics.</summary>
    internal bool RestoreOnDispose { get; set; } = true;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _reader.FrameArrived -= OnFrameArrived;
        if (_snapshot is not null && RestoreOnDispose) Controls.Restore(_snapshot);
        if (_remembered) CameraRecovery.Forget();
        try { await _reader.StopAsync(); } catch (Exception) { /* already stopped */ }
        _reader.Dispose();
        _capture.Dispose();
    }
}
