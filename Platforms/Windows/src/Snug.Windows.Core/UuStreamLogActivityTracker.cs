namespace Snug.Windows.Core;

/// <summary>
/// Treats append growth in the current GameViewerServer streamer log as a
/// low-confidence activity hint. A locked file is not activity: UU can hold
/// the handle for the whole server lifetime.
/// </summary>
public sealed class UuStreamLogActivityTracker
{
    public static readonly TimeSpan GrowthWindow = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan Freshness = TimeSpan.FromSeconds(2);
    // GameViewerServer keeps writing a small heartbeat line every ~3 seconds even with no
    // session. That alone satisfies "two growth events within the window" and would make the
    // log-fallback signal permanently true, seeding phantom sessions. Require the total bytes
    // appended inside the window to clear a floor so periodic heartbeats stay inert.
    public const long MinimumGrowthBytes = 1024;

    private string? _source;
    private long? _lastLength;
    private DateTimeOffset? _lastSampleAt;
    private readonly Queue<(DateTimeOffset At, long Bytes)> _growthEvents = new();
    private long _windowGrowthBytes;

    public bool Observe(string source, long length, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(source) || length < 0)
        {
            Reset();
            return false;
        }

        if (!string.Equals(_source, source, StringComparison.OrdinalIgnoreCase))
        {
            Reset();
            _source = source;
            _lastLength = length;
            _lastSampleAt = now;
            return false;
        }

        if (_lastSampleAt is { } previousSample && now < previousSample)
        {
            Reset();
            _source = source;
            _lastLength = length;
            _lastSampleAt = now;
            return false;
        }

        _lastSampleAt = now;
        if (_lastLength is not { } previousLength)
        {
            _lastLength = length;
            return false;
        }

        if (length < previousLength)
        {
            _lastLength = length;
            _growthEvents.Clear();
            _windowGrowthBytes = 0;
            return false;
        }

        if (length > previousLength)
        {
            _lastLength = length;
            _growthEvents.Enqueue((now, length - previousLength));
            _windowGrowthBytes += length - previousLength;
            while (_growthEvents.TryPeek(out var oldest) && now - oldest.At > GrowthWindow)
            {
                _windowGrowthBytes -= oldest.Bytes;
                _growthEvents.Dequeue();
            }
        }

        if (_growthEvents.Count < 2 || _windowGrowthBytes < MinimumGrowthBytes)
            return false;

        var last = _growthEvents.Last().At;

        var age = now - last;
        return age >= TimeSpan.Zero && age <= Freshness;
    }

    public void Reset()
    {
        _source = null;
        _lastLength = null;
        _lastSampleAt = null;
        _growthEvents.Clear();
        _windowGrowthBytes = 0;
    }
}