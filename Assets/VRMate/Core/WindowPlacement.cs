using System;
using System.Collections.Generic;

namespace VRMate
{
    /// <summary>A rectangle in the window system's coordinates: its top-left corner (X, Y) and its size.</summary>
    public readonly struct ScreenRect
    {
        public ScreenRect(float x, float y, float width, float height)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }

        public float X { get; }

        public float Y { get; }

        public float Width { get; }

        public float Height { get; }
    }

    /// <summary>
    /// Decides where a saved window goes at startup. The window's center is (x + width/2, y + height/2),
    /// worked out in double. If it lies on a valid monitor (edges included), the saved position is kept
    /// bit for bit. Otherwise the window moves just enough for its center to land on the monitor nearest
    /// to it (Euclidean distance to the rectangle; the lower index on a tie). The whole window is not
    /// fitted into the monitor: a transparent window hanging off the screen is normal use, and fitting it
    /// would move the avatar from where its owner put it. Works in whatever coordinates the caller uses,
    /// as long as the position and the monitors share them.
    /// </summary>
    public static class WindowPlacement
    {
        /// <summary>
        /// Returns false, leaving the window where it is, when the position is not finite, the size is
        /// not positive and finite, or no monitor is valid. A monitor is valid when all four values are
        /// finite and both its width and height are positive. monitors may be null.
        /// </summary>
        public static bool TryRestore(
            float x, float y, float width, float height, IReadOnlyList<ScreenRect> monitors,
            out float restoredX, out float restoredY)
        {
            restoredX = x;
            restoredY = y;

            if (monitors == null || !IsFinite(x) || !IsFinite(y) || !IsPositiveFinite(width) || !IsPositiveFinite(height))
            {
                return false;
            }

            var halfWidth = width / 2.0;
            var halfHeight = height / 2.0;
            var centerX = x + halfWidth;
            var centerY = y + halfHeight;

            var hasNearest = false;
            var nearest = default(ScreenRect);
            var nearestDistance = 0.0;

            for (var i = 0; i < monitors.Count; i++)
            {
                var monitor = monitors[i];
                if (!IsValid(monitor))
                {
                    continue;
                }

                var dx = Outside(centerX, monitor.X, (double)monitor.X + monitor.Width);
                var dy = Outside(centerY, monitor.Y, (double)monitor.Y + monitor.Height);
                if (dx == 0.0 && dy == 0.0)
                {
                    return true;
                }

                // Squared distances compare the same as distances and stay exact for pixel coordinates,
                // so equal distances really tie and the lower index wins.
                var distance = dx * dx + dy * dy;
                if (!hasNearest || distance < nearestDistance)
                {
                    hasNearest = true;
                    nearest = monitor;
                    nearestDistance = distance;
                }
            }

            if (!hasNearest)
            {
                return false;
            }

            restoredX = MoveOnto(x, centerX, halfWidth, nearest.X, (double)nearest.X + nearest.Width);
            restoredY = MoveOnto(y, centerY, halfHeight, nearest.Y, (double)nearest.Y + nearest.Height);
            return true;
        }

        /// <summary>How far the center lies outside [min, max] on one axis; 0 when it is on it.</summary>
        private static double Outside(double center, double min, double max)
        {
            if (center < min)
            {
                return min - center;
            }

            return center > max ? center - max : 0.0;
        }

        /// <summary>
        /// The new position on one axis. An axis whose center already lies on the monitor keeps its value.
        /// Otherwise the position is derived from the clamped center (clamp - half) rather than by adding
        /// the shift to the position: for a position like 1e7 the float sum loses the shift's low bits and
        /// leaves the center off the monitor.
        /// </summary>
        private static float MoveOnto(float position, double center, double half, double min, double max)
        {
            if (center >= min && center <= max)
            {
                return position;
            }

            var target = center < min ? min : max;
            var moved = (float)(target - half);

            // Rounding to float may leave the center a hair past the edge; step one float inwards.
            if (moved + half < min)
            {
                moved = NextFloat(moved, true);
            }
            else if (moved + half > max)
            {
                moved = NextFloat(moved, false);
            }

            return moved;
        }

        private static float NextFloat(float value, bool up)
        {
            if (value == 0f)
            {
                return up ? float.Epsilon : -float.Epsilon;
            }

            var bits = BitConverter.ToInt32(BitConverter.GetBytes(value), 0);
            // For a positive float the next one up has the next larger bit pattern; for a negative one, smaller.
            bits += (value > 0f) == up ? 1 : -1;
            return BitConverter.ToSingle(BitConverter.GetBytes(bits), 0);
        }

        private static bool IsValid(ScreenRect monitor)
        {
            return IsFinite(monitor.X) && IsFinite(monitor.Y) && IsPositiveFinite(monitor.Width) && IsPositiveFinite(monitor.Height);
        }

        private static bool IsPositiveFinite(float value)
        {
            // NaN fails the comparison; -0 is not greater than 0.
            return value > 0f && !float.IsPositiveInfinity(value);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }

    /// <summary>
    /// Decides when to save the window's position. Begin marks the restored position as saved; before it,
    /// nothing is ever saved. Tick saves a position that differs from the saved one once it has been seen
    /// unchanged for SettleSeconds, timed from when it was first seen, and only while the window has the
    /// size of the saved size state (sizeMatches); a position seen at another size is not recorded. At quit
    /// the same check runs without waiting. A position that is saved becomes the saved one. Positions that
    /// are not finite are never saved. Positions compare with float ==.
    /// Not thread-safe; call from one thread.
    /// </summary>
    public sealed class PlacementSaver
    {
        public const float SettleSeconds = 1f;

        private bool _begun;
        private float _savedX;
        private float _savedY;

        private bool _hasPending;
        private float _pendingX;
        private float _pendingY;
        private float _pendingSince;

        public void Begin(float x, float y)
        {
            _begun = true;
            _savedX = x;
            _savedY = y;
            _hasPending = false;
        }

        /// <summary>now is a clock in seconds that never goes back. Returns true when the position should be saved now.</summary>
        public bool Tick(float now, float x, float y, bool sizeMatches)
        {
            if (!_begun)
            {
                return false;
            }

            if (IsSaved(x, y))
            {
                _hasPending = false;
                return false;
            }

            if (!_hasPending || x != _pendingX || y != _pendingY)
            {
                _hasPending = true;
                _pendingX = x;
                _pendingY = y;
                _pendingSince = now;
                return false;
            }

            if (!sizeMatches || now - _pendingSince < SettleSeconds || !IsFinite(x) || !IsFinite(y))
            {
                return false;
            }

            MarkSaved(x, y);
            return true;
        }

        /// <summary>Returns true when the position should be saved before the application quits.</summary>
        public bool ShouldSaveOnQuit(float x, float y, bool sizeMatches)
        {
            if (!_begun || !sizeMatches || IsSaved(x, y) || !IsFinite(x) || !IsFinite(y))
            {
                return false;
            }

            MarkSaved(x, y);
            return true;
        }

        private bool IsSaved(float x, float y)
        {
            return x == _savedX && y == _savedY;
        }

        private void MarkSaved(float x, float y)
        {
            _savedX = x;
            _savedY = y;
            _hasPending = false;
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
