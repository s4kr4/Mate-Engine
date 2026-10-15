using System;
using System.Collections.Generic;

namespace VRMate
{
    public struct MouthWeights
    {
        public MouthWeights(float aa, float ih, float ou, float ee, float oh)
        {
            Aa = aa;
            Ih = ih;
            Ou = ou;
            Ee = ee;
            Oh = oh;
        }

        public static MouthWeights Closed
        {
            get { return default(MouthWeights); }
        }

        public float Aa { get; }

        public float Ih { get; }

        public float Ou { get; }

        public float Ee { get; }

        public float Oh { get; }
    }

    /// <summary>
    /// Turns viseme keys into mouth weights. Each key holds its shape until the next key; on entering
    /// a key the mouth blends linearly from the previous key's shape (silence before the first key)
    /// over BlendSeconds. The weights are always in [0, 1] and sum to at most 1.
    /// </summary>
    public sealed class VisemeTimeline
    {
        public const double DefaultBlendSeconds = 0.05;

        private readonly VisemeKey[] _keys;
        private readonly double _blendSeconds;

        public VisemeTimeline(IReadOnlyList<VisemeKey> keys, double blendSeconds = DefaultBlendSeconds)
        {
            if (keys == null)
            {
                throw new ArgumentNullException(nameof(keys));
            }

            _keys = new VisemeKey[keys.Count];
            for (var i = 0; i < _keys.Length; i++)
            {
                _keys[i] = keys[i];
            }

            // Non-positive (or NaN) blend switches shapes instantly.
            _blendSeconds = blendSeconds > 0 ? blendSeconds : 0;
        }

        public MouthWeights Evaluate(double time)
        {
            if (_keys.Length == 0 || double.IsNaN(time) || time < 0 || time < _keys[0].Time)
            {
                return MouthWeights.Closed;
            }

            var index = FindKeyAt(time);
            var current = _keys[index];
            var previous = index == 0 ? VisemeShape.Sil : _keys[index - 1].Shape;

            double progress;
            if (_blendSeconds <= 0)
            {
                progress = 1;
            }
            else
            {
                progress = (time - current.Time) / _blendSeconds;
                if (double.IsNaN(progress))
                {
                    progress = 1;
                }

                progress = Math.Max(0, Math.Min(1, progress));
            }

            return new MouthWeights(
                Weight(VisemeShape.Aa, previous, current.Shape, progress),
                Weight(VisemeShape.Ih, previous, current.Shape, progress),
                Weight(VisemeShape.Ou, previous, current.Shape, progress),
                Weight(VisemeShape.Ee, previous, current.Shape, progress),
                Weight(VisemeShape.Oh, previous, current.Shape, progress));
        }

        private static float Weight(VisemeShape shape, VisemeShape previous, VisemeShape current, double progress)
        {
            var weight = 0.0;
            if (previous == shape)
            {
                weight += 1 - progress;
            }

            if (current == shape)
            {
                weight += progress;
            }

            return (float)Math.Max(0, Math.Min(1, weight));
        }

        /// <summary>The last key whose time is at or before the given time (the first key is at or before it).</summary>
        private int FindKeyAt(double time)
        {
            var low = 0;
            var high = _keys.Length - 1;
            while (low < high)
            {
                var mid = low + (high - low + 1) / 2;
                if (_keys[mid].Time <= time)
                {
                    low = mid;
                }
                else
                {
                    high = mid - 1;
                }
            }

            return low;
        }
    }
}
