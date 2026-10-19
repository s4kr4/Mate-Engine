using System;
using System.Collections.Generic;

namespace VRMate
{
    /// <summary>
    /// Decides which file problems to log after each reload of a folder. A problem is logged by name
    /// once while it stays in the folder; one that goes away and comes back is new again. The state is
    /// the set of problems present now, counted afresh on every reload, never a history of keys: a
    /// history needs a size limit, and whatever falls past the limit would never be named.
    /// At most MaxListedPerReport problems are named per reload, oldest first, so a folder full of
    /// bad files never floods the log; the rest are counted in one line and named on later reloads.
    /// Not thread-safe; call from one thread.
    /// </summary>
    public sealed class FileProblemLog
    {
        public const int MaxListedPerReport = 8;

        private static readonly StringComparer KeyComparer = StringComparer.Ordinal;

        // The problems present at the last End: when each started being present, and whether it was named.
        private Dictionary<string, Present> _present = new Dictionary<string, Present>(KeyComparer);

        // This reload's problems in the order they were added; the first message for a key wins.
        private readonly List<string> _keys = new List<string>();
        private readonly Dictionary<string, string> _messages = new Dictionary<string, string>(KeyComparer);

        private long _nextOrder;

        /// <summary>Starts a reload: drops the problems added since the last End (or Begin).</summary>
        public void Begin()
        {
            _keys.Clear();
            _messages.Clear();
        }

        /// <summary>A problem present in the folder now. A null key is the empty key; a null message is empty text.</summary>
        public void Add(string key, string message)
        {
            key = key ?? string.Empty;
            if (_messages.ContainsKey(key))
            {
                return;
            }

            _keys.Add(key);
            _messages.Add(key, message ?? string.Empty);
        }

        /// <summary>
        /// Ends the reload. Problems that are gone are forgotten; up to MaxListedPerReport problems not
        /// named yet are returned as their messages, those present longest first (problems new in this
        /// reload in the order they were added), followed by one line counting any others.
        /// </summary>
        public IReadOnlyList<string> End()
        {
            var present = new Dictionary<string, Present>(KeyComparer);
            var unlisted = new List<KeyValuePair<string, Present>>();
            foreach (var key in _keys)
            {
                Present entry;
                if (!_present.TryGetValue(key, out entry))
                {
                    entry = new Present(_nextOrder++);
                }

                present.Add(key, entry);
                if (!entry.Listed)
                {
                    unlisted.Add(new KeyValuePair<string, Present>(key, entry));
                }
            }

            unlisted.Sort((left, right) => left.Value.Order.CompareTo(right.Value.Order));
            var lines = new List<string>();
            for (var i = 0; i < unlisted.Count && i < MaxListedPerReport; i++)
            {
                unlisted[i].Value.Listed = true;
                lines.Add(_messages[unlisted[i].Key]);
            }

            var rest = unlisted.Count - lines.Count;
            if (rest > 0)
            {
                lines.Add(rest + " more file problems are not listed yet; they are listed on the next reloads");
            }

            _present = present;
            _keys.Clear();
            _messages.Clear();
            return lines;
        }

        private sealed class Present
        {
            public Present(long order)
            {
                Order = order;
            }

            /// <summary>Increases with each new appearance, so older problems sort first.</summary>
            public long Order { get; }

            public bool Listed { get; set; }
        }
    }
}
