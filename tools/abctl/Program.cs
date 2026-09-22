using AutoBrightness.Camera;

// Camera-only diagnostics for AutoBrightness.Core. Never changes monitor brightness.

var cmd = args.FirstOrDefault() ?? "help";
string? Opt(string name)
{
    var i = Array.IndexOf(args, "--" + name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

try
{
    var cameras = await CameraDevice.FindAllAsync();
    CameraDevice Pick() =>
        (Opt("camera") is { } want
            ? cameras.FirstOrDefault(c => c.Name.Contains(want, StringComparison.OrdinalIgnoreCase) || c.Key.Contains(want, StringComparison.OrdinalIgnoreCase))
            : cameras.FirstOrDefault(c => c.IsUsb))
        ?? throw new InvalidOperationException("no matching camera");

    switch (cmd)
    {
        case "cameras":
            foreach (var c in cameras)
            {
                var profile = CameraProfile.Load(c.Key);
                Console.WriteLine($"{c.Name,-28} {c.Key,-22} {(profile?.IsCalibrated == true ? $"calibrated {profile.CalibratedAt:g}" : "not calibrated")}");
            }
            break;

        case "calibrate":
        {
            var camera = Pick();
            Console.WriteLine($"Calibrating {camera.Name} ({camera.Key}). Keep the scene still.");
            var progress = new SyncProgress<CalibrationProgress>(p =>
                Console.WriteLine($"  [{p.Fraction,4:P0}] {p.Step}{(p.Detail is null ? "" : ": " + p.Detail)}"));
            var profile = await new Calibrator(camera).RunAsync(progress);
            profile.Save();
            Console.WriteLine();
            Console.WriteLine($"exposure {profile.ExposureMin}..{profile.ExposureMax} (start {profile.ExposureStart}), settle {profile.SettleFrames} frames");
            Console.WriteLine($"response: black {profile.Black:F1}, gamma {profile.Gamma:F3}; noise {profile.NoiseLevels:F2} levels");
            Console.WriteLine($"gain: {(profile.GainSupported ? string.Join(", ", profile.GainTable.Select(g => $"{g.Gain}=x{g.Factor:F2}")) : "not used")}");
            foreach (var note in profile.Notes) Console.WriteLine($"note: {note}");
            Console.WriteLine($"saved to {CameraProfile.DirectoryPath}");
            break;
        }

        case "measure":
        {
            var camera = Pick();
            var profile = CameraProfile.Load(camera.Key) ?? CameraProfile.Uncalibrated(camera.Key, camera.Name);
            var count = int.Parse(Opt("count") ?? "5");
            var interval = TimeSpan.FromSeconds(double.Parse(Opt("interval") ?? "3"));
            await using var meter = new LightMeter(camera, profile);
            Console.WriteLine($"{camera.Name}, {(profile.IsCalibrated ? "calibrated" : "UNCALIBRATED")} profile; each reading opens and closes the camera");
            for (var i = 0; i < count; i++)
            {
                var r = await meter.MeasureAsync();
                Console.WriteLine($"  {r.Time:HH:mm:ss}  EV {r.Ev,6:F2}  exposure {r.Exposure,3}  gain {r.Gain,3}  mean {r.Mean,5:F1}  clipped {r.Clipped,5:P1}  {r.Verdict,-9}  attempts {r.Attempts}  {r.Duration.TotalMilliseconds,5:F0} ms");
                if (i < count - 1) await Task.Delay(interval);
            }
            break;
        }

        case "frames":
        {
            // Frame timing after open and after an exposure change, to find where metering time goes.
            var camera = Pick();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await using var session = await CameraSession.OpenAsync(camera);
            Console.WriteLine($"open {sw.ElapsedMilliseconds} ms, {session.FrameRate:F0} fps, exposure now {session.Controls.Exposure}");
            async Task Time(string label, int n)
            {
                var t0 = sw.ElapsedMilliseconds;
                var times = new List<long>();
                for (var i = 0; i < n; i++) { await session.NextFrameAsync(); times.Add(sw.ElapsedMilliseconds - t0); }
                Console.WriteLine($"  {label,-28} {string.Join(" ", times)}");
            }
            var opened = DateTime.UtcNow;
            await Time("first frames (auto)", 10);
            Console.WriteLine($"  events {session.EventsRaised}, first event +{(session.FirstEventAt - opened)?.TotalMilliseconds:F0} ms after open returned, no bitmap {session.FramesWithoutBitmap}, failed {session.FramesFailed} {session.LastError?.Message}");
            foreach (var e in new[] { -5, -6, -8, -5 })
            {
                session.Controls.SetExposure(e);
                await Time($"after SetExposure({e})", 10);
            }
            session.Controls.TrySetGain(0);
            await Time("after TrySetGain(0)", 10);
            break;
        }

        case "startup":
        {
            // First-frame latency depending on the exposure mode the camera is in when the stream starts.
            var camera = Pick();
            async Task<long> FirstFrame(string label, Action<CameraSession>? before = null, bool restore = false)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                await using var s = await CameraSession.OpenAsync(camera);
                var open = sw.ElapsedMilliseconds;
                var mode = s.Controls.Save();
                await s.NextFrameAsync();
                Console.WriteLine($"  {label,-44} open {open,5} ms  first frame {sw.ElapsedMilliseconds,5} ms  (exposure {(mode.ExposureAuto ? "auto" : "manual")} {mode.Exposure}, gain {mode.Gain})");
                before?.Invoke(s);
                s.RestoreOnDispose = restore;
                return sw.ElapsedMilliseconds;
            }
            try
            {
                await FirstFrame("as found");
                await FirstFrame("as found, then lock exposure -6 and keep it", s => { s.Controls.SetExposure(-6); s.Controls.TrySetGain(0); });
                await FirstFrame("started with manual -6");
                await FirstFrame("started with manual -6 (again), then back to auto", s => s.Controls.RestoreAuto());
                await FirstFrame("started in auto");
            }
            finally
            {
                // Never leave the camera in manual exposure, even if interrupted.
                await using var s = await CameraSession.OpenAsync(camera, remember: false);
                s.Controls.RestoreAuto();
                s.RestoreOnDispose = false;
            }
            break;
        }

        case "simulate-crash":
        {
            // Lock exposure, then die without restoring, as a crash mid-measurement would.
            var camera = Pick();
            var s = await CameraSession.OpenAsync(camera);
            s.Controls.SetExposure(-6);
            await s.NextFrameAsync();
            Console.WriteLine($"left {camera.Name} in manual exposure {s.Controls.Exposure}; exiting without cleanup");
            Environment.Exit(3);
            break;
        }

        case "settle":
        {
            // Frame-by-frame means after opening and locking, as LightMeter does, to see when the image stops changing.
            var camera = Pick();
            for (var run = 1; run <= int.Parse(Opt("count") ?? "5"); run++)
            {
                await using var s = await CameraSession.OpenAsync(camera);
                var before = s.Controls.Save();
                s.Controls.LockImageProcessing();
                s.Controls.SetExposure(-5);
                var gainOk = s.Controls.TrySetGain(0);
                var means = new List<string>();
                for (var i = 0; i < 30; i++) means.Add((await s.NextFrameAsync()).Histogram(Roi.Full).Mean.ToString("0"));
                Console.WriteLine($"run {run}: opened with exposure {(before.ExposureAuto ? "auto" : "manual")} {before.Exposure}, gain {before.Gain}; set gain 0 ok={gainOk}, now exposure {s.Controls.Exposure} gain {s.Controls.Gain}");
                Console.WriteLine($"  frame means: {string.Join(" ", means)}");
                await Task.Delay(3000);
            }
            break;
        }

        case "recover":
            Console.WriteLine(await CameraRecovery.RecoverAsync() ? "restored camera settings left by an unclean exit" : "nothing to recover");
            break;

        default:
            Console.WriteLine("""
                abctl - camera diagnostics for AutoBrightness (never changes monitor brightness)

                  cameras                                  list cameras and calibration status
                  calibrate [--camera NAME]                run the calibration and save the profile
                  measure   [--camera NAME] [--count N] [--interval S]
                                                           take light readings, opening the camera for each
                """);
            break;
    }
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}

/// <summary>Reports synchronously so console output keeps its order.</summary>
sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
