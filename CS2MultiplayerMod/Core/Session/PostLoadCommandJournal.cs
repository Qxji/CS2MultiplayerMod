using System.Collections.Generic;
using CS2MultiplayerMod.Core.Protocol.Messages;

namespace CS2MultiplayerMod.Core.Session
{
    /// <summary>
    /// Bounded ordered journal for commands received after a world snapshot's causal cut but before
    /// the replacement world is ready. Overflow invalidates the complete suffix: replaying only a
    /// prefix would create a world that is neither the snapshot nor the host's current state.
    /// </summary>
    internal sealed class PostLoadCommandJournal
    {
        private const int MessageOverheadBytes = 18;

        private readonly int _maxCount;
        private readonly int _maxBytes;
        private readonly List<SimulationCommandMessage> _entries =
            new List<SimulationCommandMessage>();
        private int _bytes;

        public PostLoadCommandJournal(int maxCount, int maxBytes)
        {
            if (maxCount <= 0) throw new System.ArgumentOutOfRangeException(nameof(maxCount));
            if (maxBytes <= 0) throw new System.ArgumentOutOfRangeException(nameof(maxBytes));
            _maxCount = maxCount;
            _maxBytes = maxBytes;
        }

        public int Count => _entries.Count;
        public int Bytes => _bytes;
        public bool Overflowed { get; private set; }

        public bool TryAppend(SimulationCommandMessage command)
        {
            if (command == null) return false;
            if (Overflowed) return false;

            int bodyBytes = command.Body != null ? command.Body.Length : 0;
            long nextBytes = (long)_bytes + MessageOverheadBytes + bodyBytes;
            if (_entries.Count >= _maxCount || nextBytes > _maxBytes)
            {
                _entries.Clear();
                _bytes = 0;
                Overflowed = true;
                return false;
            }

            _entries.Add(command);
            _bytes = (int)nextBytes;
            return true;
        }

        /// <summary>
        /// Take the complete suffix in receive order. Returns false after overflow and deliberately
        /// returns no partial entries; the caller must obtain a fresh snapshot instead.
        /// </summary>
        public bool TryTakeAll(out List<SimulationCommandMessage> commands)
        {
            if (Overflowed)
            {
                commands = null;
                Clear();
                return false;
            }

            commands = new List<SimulationCommandMessage>(_entries);
            Clear();
            return true;
        }

        public void Clear()
        {
            _entries.Clear();
            _bytes = 0;
            Overflowed = false;
        }
    }
}
