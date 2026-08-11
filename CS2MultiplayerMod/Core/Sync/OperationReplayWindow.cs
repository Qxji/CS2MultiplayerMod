using System;
using System.Collections.Generic;

namespace CS2MultiplayerMod.Core.Sync
{
    /// <summary>
    /// Bounded-by-time idempotence window for reliable operation replays. Keys are remembered only
    /// after commit/drain; an operation that failed before commit therefore remains retryable.
    /// </summary>
    public sealed class OperationReplayWindow<TKey>
    {
        private readonly Dictionary<TKey, long> _completed;

        public OperationReplayWindow() : this(null) { }

        public OperationReplayWindow(IEqualityComparer<TKey> comparer)
        {
            _completed = new Dictionary<TKey, long>(comparer ?? EqualityComparer<TKey>.Default);
        }

        public int Count => _completed.Count;

        public bool Contains(TKey key, long now)
        {
            long expires;
            if (!_completed.TryGetValue(key, out expires)) return false;
            if (expires > now) return true;
            _completed.Remove(key);
            return false;
        }

        public void Remember(TKey key, long now, long duration)
        {
            if (duration <= 0) throw new ArgumentOutOfRangeException(nameof(duration));
            long expires = now > long.MaxValue - duration ? long.MaxValue : now + duration;
            _completed[key] = expires;
        }

        public void Prune(long now)
        {
            if (_completed.Count == 0) return;
            var expired = new List<TKey>();
            foreach (KeyValuePair<TKey, long> pair in _completed)
                if (pair.Value <= now) expired.Add(pair.Key);
            for (int i = 0; i < expired.Count; i++) _completed.Remove(expired[i]);
        }

        public void Clear() => _completed.Clear();
    }
}
