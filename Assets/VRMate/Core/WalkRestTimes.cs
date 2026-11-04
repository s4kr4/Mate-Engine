using System;
using System.Collections.Generic;

namespace VRMate
{
    /// <summary>
    /// Where in the walk clip the pose is close to the idle's, from the clip sampled at a fixed interval:
    /// differences[i] is how far the clip's pose at i × interval is from the idle pose, and the clip
    /// repeats, so the last sample is next to the first. The walk starts at the smallest difference
    /// and starts slowing down at a rest time, so the feet are near the idle pose where they join it.
    /// </summary>
    public static class WalkRestTimes
    {
        /// <summary>A local minimum counts as a rest time when it is within this share of the range above the smallest difference.</summary>
        public const float RestShare = 0.25f;

        /// <summary>
        /// startTime is the time of the smallest difference (the earlier on a tie). restTimes is cleared,
        /// then gets, in increasing order, the times of the local minima (seen periodically; of a flat
        /// bottom, its first sample) whose difference is at most smallest + RestShare × (largest −
        /// smallest); with no minimum (all equal, or one sample) it gets startTime alone. NaN, infinite
        /// and negative differences are not used: their neighbours are compared with the next usable
        /// ones. False, with startTime NaN and restTimes empty, when interval is not positive and finite
        /// or no difference is usable.
        /// </summary>
        public static bool TryFind(IReadOnlyList<float> differences, float interval, List<float> restTimes, out float startTime)
        {
            if (restTimes == null)
            {
                throw new ArgumentNullException("restTimes");
            }

            restTimes.Clear();
            startTime = float.NaN;
            if (differences == null || !(interval > 0f) || float.IsPositiveInfinity(interval))
            {
                return false;
            }

            var usable = 0;
            var smallest = 0f;
            var largest = 0f;
            for (var i = 0; i < differences.Count; i++)
            {
                var difference = differences[i];
                if (!IsUsable(difference))
                {
                    continue;
                }

                if (usable == 0 || difference < smallest)
                {
                    smallest = difference;
                    startTime = i * interval;
                }

                if (usable == 0 || difference > largest)
                {
                    largest = difference;
                }

                usable++;
            }

            if (usable == 0)
            {
                return false;
            }

            AddMinima(differences, usable, interval, smallest + RestShare * (largest - smallest), restTimes);
            if (restTimes.Count == 0)
            {
                restTimes.Add(startTime);
            }

            restTimes.Sort();
            return true;
        }

        /// <summary>
        /// Walks the usable samples as runs of equal values, starting just after a change so that no run
        /// is split by the wrap. A run lower than both of its neighbours is a minimum, taken at its first
        /// sample. Steps over the unusable samples in place rather than listing the usable ones: the
        /// caller may search every frame, and this allocates nothing.
        /// </summary>
        private static void AddMinima(IReadOnlyList<float> differences, int usable, float interval, float threshold, List<float> restTimes)
        {
            var position = -1;
            for (var i = 0; i < differences.Count; i++)
            {
                if (IsUsable(differences[i]) && differences[Previous(differences, i)] != differences[i])
                {
                    position = i;
                    break;
                }
            }

            if (position < 0)
            {
                // All equal (or one sample): no minimum.
                return;
            }

            var seen = 0;
            while (seen < usable)
            {
                var value = differences[position];
                var before = differences[Previous(differences, position)];
                var length = 1;
                var after = Next(differences, position);
                while (length < usable && differences[after] == value)
                {
                    after = Next(differences, after);
                    length++;
                }

                if (value < before && value < differences[after] && value <= threshold)
                {
                    restTimes.Add(position * interval);
                }

                position = after;
                seen += length;
            }
        }

        /// <summary>The usable sample before index, wrapping; index itself must be usable.</summary>
        private static int Previous(IReadOnlyList<float> differences, int index)
        {
            var count = differences.Count;
            var i = index;
            do
            {
                i = (i + count - 1) % count;
            }
            while (!IsUsable(differences[i]));

            return i;
        }

        /// <summary>The usable sample after index, wrapping; index itself must be usable.</summary>
        private static int Next(IReadOnlyList<float> differences, int index)
        {
            var count = differences.Count;
            var i = index;
            do
            {
                i = (i + 1) % count;
            }
            while (!IsUsable(differences[i]));

            return i;
        }

        private static bool IsUsable(float difference)
        {
            // NaN fails the comparison.
            return difference >= 0f && !float.IsPositiveInfinity(difference);
        }
    }
}
