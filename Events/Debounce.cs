using System;
using System.Collections.Generic;

namespace CS2Hooks.Events;

/// <summary>
/// Generic debounce bucket: batches rapid changes to the same key and fires
/// a single callback after <see cref="DebounceTime"/> of inactivity per key.
///
/// Thread-safety: not required — all callers run on the main ECS thread.
/// </summary>
internal sealed class Debounce<TKey, TValue> where TKey : notnull
{
    private struct Entry
    {
        public TValue   OriginalOld;
        public TValue   FinalNew;
        public DateTime LastTime;
    }

    private readonly Dictionary<TKey, Entry>      _pending      = new();
    private readonly TimeSpan                      _debounceTime;
    private readonly Action<TKey, TValue, TValue>  _onFire;

    internal Debounce(TimeSpan debounceTime, Action<TKey, TValue, TValue> onFire)
    {
        _debounceTime = debounceTime;
        _onFire       = onFire;
    }

    /// <summary>
    /// Record a change. Keeps the original old value from the first call in a sequence;
    /// always updates the final new value so the fired event spans the full range.
    /// </summary>
    internal void Record(TKey key, TValue oldValue, TValue newValue)
    {
        if (_pending.TryGetValue(key, out var existing))
            _pending[key] = new Entry { OriginalOld = existing.OriginalOld, FinalNew = newValue, LastTime = DateTime.Now };
        else
            _pending[key] = new Entry { OriginalOld = oldValue,            FinalNew = newValue, LastTime = DateTime.Now };
    }

    /// <summary>
    /// Fire events for any key that has been quiet for longer than the debounce window.
    /// Call this every frame from a GameSystemBase.OnUpdate.
    /// </summary>
    internal void Flush()
    {
        if (_pending.Count == 0) return;

        var now    = DateTime.Now;
        var toFire = new List<TKey>();

        foreach (var (key, entry) in _pending)
            if ((now - entry.LastTime) >= _debounceTime)
                toFire.Add(key);

        foreach (var key in toFire)
        {
            var entry = _pending[key];
            _pending.Remove(key);
            _onFire(key, entry.OriginalOld, entry.FinalNew);
        }
    }
}
