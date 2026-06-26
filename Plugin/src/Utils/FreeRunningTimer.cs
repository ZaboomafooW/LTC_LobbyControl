using System;
using System.Diagnostics;

namespace LobbyControl.Utils;

public class FreeRunningTimer
{
    public bool Enabled => _stopwatch.IsRunning;
    public bool TimedOut => _stopwatch.Elapsed > _timeout;
    public TimeSpan Elapsed => _stopwatch.Elapsed;
    public TimeSpan Remaining => TimedOut ? TimeSpan.Zero : _timeout - _stopwatch.Elapsed;

    public void Start(TimeSpan timeout) {
        _timeout = timeout;
        _stopwatch.Restart();
    }
    public void Stop() => _stopwatch.Stop();

    private readonly Stopwatch _stopwatch = new Stopwatch();
    private TimeSpan _timeout = TimeSpan.Zero;
}
