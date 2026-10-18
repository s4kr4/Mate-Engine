using System;
using System.Collections.Generic;

namespace VRMate
{
    /// <summary>
    /// Decides whether a warning is new. Each key warns once; keys compare ordinally (no case folding
    /// or normalization) and null is the empty key. The brain may send any number of distinct bad
    /// names, so at most MaxKeys keys are remembered and every key after that stays silent.
    /// Not thread-safe; call from one thread.
    /// </summary>
    public sealed class WarnOnce
    {
        public const int MaxKeys = 256;

        private readonly HashSet<string> _seen = new HashSet<string>(StringComparer.Ordinal);

        public bool ShouldWarn(string key)
        {
            key = key ?? string.Empty;
            if (_seen.Contains(key) || _seen.Count >= MaxKeys)
            {
                return false;
            }

            _seen.Add(key);
            return true;
        }
    }
}
