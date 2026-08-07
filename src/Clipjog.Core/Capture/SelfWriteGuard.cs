using Clipjog.Core.Abstractions;

namespace Clipjog.Core.Capture;

/// <summary>
/// Recognises clipboard changes that we caused ourselves, so pasting a clip does not capture
/// it straight back as a new clip.
/// <para>
/// This replaces the original's <c>API.blockMonitoring()</c> / <c>ONCLIPBOARD</c> flag protocol
/// and its 200 ms time-difference heuristic (Clipjump.ahk:412). Both were timing-based, so both
/// had windows where a genuinely fast user copy got swallowed or a slow self-write got recorded.
/// Matching on content hash removes the timing question entirely: the bytes either are the ones
/// we just wrote, or they are not.
/// </para>
/// <para>
/// A short TTL still applies, for a real reason rather than as a fudge: re-copying the same text
/// an hour later is a legitimate new capture that should refresh the clip's position. The TTL
/// bounds suppression to the paste round-trip and nothing more.
/// </para>
/// </summary>
public sealed class SelfWriteGuard
{
    private readonly IClock _clock;
    private readonly TimeSpan _ttl;
    private readonly int _maxEntries;
    private readonly Dictionary<string, DateTimeOffset> _recent = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public SelfWriteGuard(IClock? clock = null, TimeSpan? ttl = null, int maxEntries = 32)
    {
        _clock = clock ?? SystemClock.Instance;
        _ttl = ttl ?? TimeSpan.FromSeconds(5);
        _maxEntries = maxEntries;
    }

    /// <summary>Call immediately before writing these bytes to the clipboard.</summary>
    public void NoteWrite(string contentHash)
    {
        if (string.IsNullOrEmpty(contentHash))
        {
            return;
        }

        lock (_gate)
        {
            Prune();
            _recent[contentHash] = _clock.UtcNow;
        }
    }

    /// <summary>
    /// True if an incoming clipboard change carrying this hash is one of our own recent writes
    /// and should not be captured. Consumes the entry - a single write yields a single
    /// notification, and leaving it behind would suppress a real user copy of the same content.
    /// </summary>
    public bool IsOwnWrite(string contentHash)
    {
        if (string.IsNullOrEmpty(contentHash))
        {
            return false;
        }

        lock (_gate)
        {
            Prune();

            if (!_recent.TryGetValue(contentHash, out var written))
            {
                return false;
            }

            if (_clock.UtcNow - written > _ttl)
            {
                _recent.Remove(contentHash);
                return false;
            }

            _recent.Remove(contentHash);
            return true;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _recent.Clear();
        }
    }

    private void Prune()
    {
        var now = _clock.UtcNow;

        if (_recent.Count > 0)
        {
            var stale = _recent
                .Where(kv => now - kv.Value > _ttl)
                .Select(static kv => kv.Key)
                .ToList();

            foreach (var key in stale)
            {
                _recent.Remove(key);
            }
        }

        // Hard ceiling so a pathological write loop cannot grow this without bound.
        while (_recent.Count >= _maxEntries)
        {
            var oldest = _recent.OrderBy(static kv => kv.Value).First().Key;
            _recent.Remove(oldest);
        }
    }
}
