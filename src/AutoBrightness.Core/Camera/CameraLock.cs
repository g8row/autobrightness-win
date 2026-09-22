namespace AutoBrightness.Camera;

/// <summary>
/// Serializes camera sessions across AutoBrightness processes (the app, a second copy, abctl). Two sessions
/// on one camera overwrite each other's exposure and gain mid-reading, which showed up as readings a stop apart.
/// A named mutex is released by Windows if its holder dies; because mutexes are thread-affine and sessions
/// are async, the mutex is owned by a dedicated thread for the lifetime of the lock.
/// </summary>
internal sealed class CameraLock : IDisposable
{
    private readonly ManualResetEventSlim _release = new(false);
    private readonly Thread _owner;

    private CameraLock(Thread owner) => _owner = owner;

    public static async Task<CameraLock> AcquireAsync(string cameraKey, TimeSpan timeout)
    {
        var name = @"Local\AutoBrightness.Camera." + string.Concat(cameraKey.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
        var acquired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        CameraLock? handle = null;
        var thread = new Thread(() =>
        {
            using var mutex = new Mutex(false, name);
            bool owned;
            try { owned = mutex.WaitOne(timeout); }
            catch (AbandonedMutexException) { owned = true; } // previous holder crashed; we own it now
            acquired.SetResult(owned);
            if (!owned) return;
            handle!._release.Wait();
            mutex.ReleaseMutex();
        }) { IsBackground = true, Name = "AutoBrightness camera lock" };
        handle = new CameraLock(thread);
        thread.Start();

        if (!await acquired.Task)
            throw new CameraException(CameraFailure.Unavailable, "Another AutoBrightness process is using the camera.");
        return handle;
    }

    public void Dispose()
    {
        _release.Set();
        _owner.Join(TimeSpan.FromSeconds(2));
    }
}
