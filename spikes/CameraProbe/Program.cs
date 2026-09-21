using System.Diagnostics;
using CameraProbe;
using Windows.Devices.Enumeration;

var cmd = args.FirstOrDefault() ?? "help";
var opts = ParseOptions(args.Skip(1).ToArray());

try
{
    switch (cmd)
    {
        case "devices": await Devices(); break;
        case "meter": await WithCamera(opts, Meter); break;
        case "sweep": await WithCamera(opts, Sweep); break;
        case "feedback": await WithCamera(opts, Feedback); break;
        case "autoread": await WithCamera(opts, AutoRead); break;
        case "watch": await WithCamera(opts, Watch); break;
        case "rate": await WithCamera(opts, Rate); break;
        default: Help(); break;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}
return 0;

static void Help() => Console.WriteLine("""
    CameraProbe - inspect a webcam as an ambient light meter

      devices                               list cameras and their UVC controls
      meter    [--exposure N|auto] [--gain N] [--frames N]
                                            lock exposure and print luma stats per frame
      sweep    [--gain N] [--from N] [--to N]
                                            step through exposure values (default up to -5), measuring response
      feedback [--exposure N] [--gain N] [--monitor ID]
                                            step monitor brightness via Twinkle Tray and measure the change
      autoread [--monitor ID]               leave auto-exposure on and check whether the chosen value can be read back
      rate     [--exposure N] [--seconds N] frames delivered per second (stream health)
      watch    [--exposure N] [--gain N] [--interval S]
                                            continuous metering (Ctrl+C to stop)

    Common: --device <name or VID substring>   (default: first camera with manual exposure)
            --width N                          minimum stream width (default 160)

    feedback and autoread change monitor brightness through Twinkle Tray and restore it afterwards.
    """);

static Dictionary<string, string> ParseOptions(string[] a)
{
    var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < a.Length; i++)
        if (a[i].StartsWith("--"))
            d[a[i][2..]] = i + 1 < a.Length && !a[i + 1].StartsWith("--") ? a[++i] : "true";
    return d;
}

static async Task<List<(DeviceInformation Info, UvcDevice? Uvc)>> FindCameras()
{
    var infos = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
    var uvcs = UvcDevice.Enumerate();
    return infos.Select(i => (i, uvcs.FirstOrDefault(u =>
        u.DevicePath.Length > 0 && UvcDevice.InstanceKey(u.DevicePath) == UvcDevice.InstanceKey(i.Id)))).ToList();
}

static async Task Devices()
{
    foreach (var (info, uvc) in await FindCameras())
    {
        Console.WriteLine($"== {info.Name}");
        Console.WriteLine($"   {info.Id}");
        if (uvc is null) Console.WriteLine("   no DirectShow/UVC control interface");
        else
        {
            Console.WriteLine($"   IAMCameraControl={uvc.HasCameraControl}  IAMVideoProcAmp={uvc.HasVideoProcAmp}");
            foreach (var p in UvcProp.All)
            {
                if (uvc.GetRange(p) is not { } r) continue;
                var cur = uvc.Get(p);
                Console.WriteLine($"   {p.Name,-22} [{r.Min,6},{r.Max,6}] step {r.Step,-3} def {r.Default,-5} caps {r.Caps,-12} now {cur?.Value,-6} {cur?.Flags}");
            }
        }

        try
        {
            await using var g = await FrameGrabber.OpenAsync(info.Id);
            var src = g.Capture.FrameSources.Values.First(s => s.Info.SourceKind == Windows.Media.Capture.Frames.MediaFrameSourceKind.Color);
            var formats = src.SupportedFormats
                .Select(f => $"{f.Subtype} {f.VideoFormat.Width}x{f.VideoFormat.Height}@{(double)f.FrameRate.Numerator / f.FrameRate.Denominator:F0}")
                .Distinct().ToList();
            Console.WriteLine($"   formats ({formats.Count}): {string.Join(", ", formats.Take(12))}{(formats.Count > 12 ? ", ..." : "")}");
            Console.WriteLine($"   selected: {Describe(g.Format)}");
            var f0 = await g.NextFrameAsync();
            Console.WriteLine($"   first frame {f0.Width}x{f0.Height}: {f0.Stats()}");
        }
        catch (Exception ex) { Console.WriteLine($"   capture failed: {ex.Message}"); }
    }
}

static string Describe(Windows.Media.Capture.Frames.MediaFrameFormat f) =>
    $"{f.Subtype} {f.VideoFormat.Width}x{f.VideoFormat.Height} @ {(double)f.FrameRate.Numerator / f.FrameRate.Denominator:F1} fps";

static async Task WithCamera(Dictionary<string, string> o, Func<Session, Task> body)
{
    var cams = await FindCameras();
    var want = o.GetValueOrDefault("device");
    var (info, uvc) = want is null
        ? cams.FirstOrDefault(c => c.Uvc?.GetRange(UvcProp.Exposure)?.Caps.HasFlag(UvcFlags.Manual) == true)
        : cams.FirstOrDefault(c => c.Info.Name.Contains(want, StringComparison.OrdinalIgnoreCase)
                                   || c.Info.Id.Contains(want, StringComparison.OrdinalIgnoreCase));
    if (info is null) throw new InvalidOperationException("no matching camera (need one with manual exposure)");
    if (uvc is null) throw new InvalidOperationException($"{info.Name} exposes no UVC controls");

    // Snapshot every control so the camera is left exactly as we found it.
    var snapshot = UvcProp.All.Select(p => (Prop: p, State: uvc.Get(p))).Where(x => x.State is not null).ToList();
    Console.WriteLine($"camera: {info.Name}");
    await using var grabber = await FrameGrabber.OpenAsync(info.Id, int.Parse(o.GetValueOrDefault("width") ?? "160"));
    Console.WriteLine($"stream: {Describe(grabber.Format)}");
    try
    {
        await body(new Session(info, uvc, grabber, o));
    }
    finally
    {
        foreach (var (p, v) in snapshot) uvc.Set(p, v!.Value.Value, v.Value.Flags);
        Console.WriteLine("camera controls restored");
    }
}

static async Task Meter(Session s)
{
    s.ApplyLock();
    var n = s.Int("frames", 15);
    var settle = await s.SettleAsync();
    Console.WriteLine($"settled after {settle} frames; exposure readback {s.Uvc.Get(UvcProp.Exposure)}, gain readback {s.Uvc.Get(UvcProp.Gain)}");
    var stats = new List<LumaStats>();
    for (var i = 0; i < n; i++)
    {
        var st = (await s.Grabber.NextFrameAsync()).Stats();
        stats.Add(st);
        Console.WriteLine($"  {i,3}: {st}");
    }
    var means = stats.Select(x => x.Mean).ToArray();
    Console.WriteLine($"frame-to-frame: mean {means.Average():F2}, stdev {StdDev(means):F3} levels");
}

static async Task Sweep(Session s)
{
    var range = s.Uvc.GetRange(UvcProp.Exposure)!.Value;
    var gain = s.Int("gain", 0);
    s.Uvc.Set(UvcProp.Gain, gain);
    s.LockAux();
    Console.WriteLine($"gain {gain}; exposure {range.Min}..{range.Max} (log2 seconds)");
    Console.WriteLine("  exp    seconds  settle    fps  stats                                                                   log2(lin/t)");
    var from = s.Int("from", range.Min);
    // Exposures longer than one frame period (-5 = 31 ms at 30 fps) drop the frame rate and have
    // wedged the Logitech 046D:0819 until a USB replug, so they need an explicit --to.
    var to = s.Int("to", Math.Min(range.Max, -5));
    for (var e = from; e <= to; e += Math.Max(1, range.Step))
    {
        s.Uvc.Set(UvcProp.Exposure, e);
        int settle;
        var sw = Stopwatch.StartNew();
        var frames = new List<LumaStats>();
        try
        {
            settle = await s.SettleAsync();
            sw.Restart();
            for (var i = 0; i < 6; i++) frames.Add((await s.Grabber.NextFrameAsync()).Stats());
        }
        catch (TimeoutException ex)
        {
            Console.WriteLine($"  {e,3}  {Math.Pow(2, e),9:F5}  stream stalled - {ex.Message}");
            break;
        }
        var fps = 5 / sw.Elapsed.TotalSeconds;
        var st = frames[^1];
        var lin = frames.Average(f => f.LinearMean);
        var ev = Math.Log2(Math.Max(lin, 1e-6)) - e;
        Console.WriteLine($"  {e,3}  {Math.Pow(2, e),9:F5}  {settle,6}  {fps,5:F1}  {st}  {ev,6:F2}");
    }
}

static async Task Feedback(Session s)
{
    var monitor = s.Opts.GetValueOrDefault("monitor") ?? await FirstMonitor();
    var original = await TwinkleClient.GetBrightnessAsync(monitor);
    Console.WriteLine($"monitor {monitor}: original brightness {original}");
    s.ApplyLock();
    await s.SettleAsync();
    try
    {
        foreach (var b in new[] { 0, 25, 50, 75, 100, 50, 0 })
        {
            await TwinkleClient.SetBrightnessAsync(monitor, b);
            await Task.Delay(2500); // DDC/CI plus panel backlight ramp
            s.Grabber.Flush();
            var frames = new List<LumaStats>();
            for (var i = 0; i < 10; i++) frames.Add((await s.Grabber.NextFrameAsync()).Stats());
            var readBack = await TwinkleClient.GetBrightnessAsync(monitor);
            Console.WriteLine($"  set {b,3} (reads {readBack,3}): mean {frames.Average(f => f.Mean),6:F2}  lin {frames.Average(f => f.LinearMean):F4}  med {frames[^1].Median,3}");
        }
    }
    finally
    {
        await TwinkleClient.SetBrightnessAsync(monitor, original);
        Console.WriteLine($"monitor brightness restored to {original}");
    }
}

static async Task AutoRead(Session s)
{
    var monitor = s.Opts.GetValueOrDefault("monitor") ?? await FirstMonitor();
    var original = await TwinkleClient.GetBrightnessAsync(monitor);
    s.Uvc.Set(UvcProp.Exposure, s.Uvc.GetRange(UvcProp.Exposure)!.Value.Default, UvcFlags.Auto);
    try
    {
        foreach (var b in new[] { 0, 100, 0, 100 })
        {
            await TwinkleClient.SetBrightnessAsync(monitor, b);
            for (var t = 0; t < 4; t++)
            {
                await Task.Delay(1000);
                s.Grabber.Flush();
                var st = (await s.Grabber.NextFrameAsync()).Stats();
                Console.WriteLine($"  brightness {b,3}  t+{t + 1}s  exposure readback {s.Uvc.Get(UvcProp.Exposure)}  gain {s.Uvc.Get(UvcProp.Gain)?.Value}  {st}");
            }
        }
    }
    finally
    {
        await TwinkleClient.SetBrightnessAsync(monitor, original);
        Console.WriteLine($"monitor brightness restored to {original}");
    }
}

static async Task Watch(Session s)
{
    s.ApplyLock();
    await s.SettleAsync();
    var interval = TimeSpan.FromSeconds(double.Parse(s.Opts.GetValueOrDefault("interval") ?? "1"));
    var exp = s.Uvc.Get(UvcProp.Exposure)!.Value.Value;
    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    Console.WriteLine("Ctrl+C to stop");
    while (!cts.IsCancellationRequested)
    {
        s.Grabber.Flush();
        var st = (await s.Grabber.NextFrameAsync()).Stats();
        Console.WriteLine($"{DateTime.Now:HH:mm:ss}  {st}  log2(lin/t) {Math.Log2(Math.Max(st.LinearMean, 1e-6)) - exp,6:F2}");
        try { await Task.Delay(interval, cts.Token); } catch (TaskCanceledException) { }
    }
}

static async Task Rate(Session s)
{
    if (s.Opts.ContainsKey("exposure")) s.ApplyLock();
    var seconds = s.Int("seconds", 20);
    var last = 0;
    for (var t = 1; t <= seconds; t++)
    {
        await Task.Delay(1000);
        var now = s.Grabber.FramesArrived;
        Console.WriteLine($"  t={t,3}s  frames/s {now - last,3}  total {now,5}  exposure {s.Uvc.Get(UvcProp.Exposure)}  gain {s.Uvc.Get(UvcProp.Gain)?.Value}");
        last = now;
    }
}

static async Task<string> FirstMonitor()
{
    var list = await TwinkleClient.ListAsync();
    return list.First().Key;
}

static double StdDev(double[] xs)
{
    var m = xs.Average();
    return Math.Sqrt(xs.Sum(x => (x - m) * (x - m)) / xs.Length);
}

sealed record Session(DeviceInformation Info, UvcDevice Uvc, FrameGrabber Grabber, Dictionary<string, string> Opts)
{
    public int Int(string key, int fallback) => Opts.TryGetValue(key, out var v) ? int.Parse(v) : fallback;

    /// <summary>Disable every automatic adjustment that would normalize the image.</summary>
    public void LockAux()
    {
        if (Uvc.Get(UvcProp.WhiteBalance) is { } wb) Uvc.Set(UvcProp.WhiteBalance, wb.Value, UvcFlags.Manual);
        if (Uvc.GetRange(UvcProp.BacklightCompensation) is not null) Uvc.Set(UvcProp.BacklightCompensation, 0);
    }

    public void ApplyLock()
    {
        var range = Uvc.GetRange(UvcProp.Exposure)!.Value;
        var exposure = Opts.GetValueOrDefault("exposure") ?? range.Default.ToString();
        if (exposure == "auto") { Uvc.Set(UvcProp.Exposure, range.Default, UvcFlags.Auto); return; }
        Uvc.Set(UvcProp.Exposure, int.Parse(exposure), UvcFlags.Manual);
        Uvc.Set(UvcProp.Gain, Int("gain", 0), UvcFlags.Manual);
        LockAux();
    }

    /// <summary>Reads frames until three consecutive means agree within one level; returns the count.</summary>
    public async Task<int> SettleAsync(int max = 60)
    {
        Grabber.Flush();
        var recent = new Queue<double>();
        for (var i = 1; i <= max; i++)
        {
            recent.Enqueue((await Grabber.NextFrameAsync()).Stats().Mean);
            if (recent.Count > 3) recent.Dequeue();
            if (recent.Count == 3 && recent.Max() - recent.Min() < 1.0) return i;
        }
        return max;
    }
}
