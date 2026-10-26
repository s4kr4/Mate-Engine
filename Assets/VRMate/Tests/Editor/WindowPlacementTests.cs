using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace VRMate.Tests
{
    /// <summary>
    /// 起動時に復元するウィンドウ位置の決定。中心 = (x + width/2, y + height/2) がいずれかの有効なモニターの閉区間上にあれば位置をそのまま返し、
    /// なければ中心から最も近いモニター（同じ距離なら番号が小さい方）へ、中心がその閉区間に入るよう最小限だけ平行移動する。
    /// ウィンドウ全体をモニターに収めることはしない。
    /// </summary>
    public class WindowPlacementTests
    {
        // 奇数にして、幅・高さの半分が整数にならない（768.5, 512.5）ようにする
        private const float W = 1537f;
        private const float H = 1025f;

        private const float Tolerance = 1e-3f;

        private static readonly ScreenRect Primary = new ScreenRect(0f, 0f, 1920f, 1080f);

        private static ScreenRect R(float x, float y, float width, float height)
        {
            return new ScreenRect(x, y, width, height);
        }

        private static IReadOnlyList<ScreenRect> Monitors(params ScreenRect[] monitors)
        {
            return monitors;
        }

        private static int Bits(float value)
        {
            return BitConverter.ToInt32(BitConverter.GetBytes(value), 0);
        }

        private static string Describe(IReadOnlyList<ScreenRect> monitors)
        {
            if (monitors == null)
            {
                return "null";
            }

            var parts = new List<string>();
            for (var i = 0; i < monitors.Count; i++)
            {
                var m = monitors[i];
                parts.Add("#" + i + "(" + m.X + ", " + m.Y + ", " + m.Width + ", " + m.Height + ")");
            }

            return "[" + string.Join(" ", parts) + "]";
        }

        private static bool IsValid(ScreenRect m)
        {
            return IsFinite(m.X) && IsFinite(m.Y) && IsFinite(m.Width) && IsFinite(m.Height) && m.Width > 0f && m.Height > 0f;
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        /// <summary>倍精度で中心を求め、モニターの閉区間上にあるか。</summary>
        private static bool CenterOn(ScreenRect m, double x, double y, double width, double height)
        {
            var cx = x + width / 2.0;
            var cy = y + height / 2.0;
            return m.X <= cx && cx <= (double)m.X + m.Width && m.Y <= cy && cy <= (double)m.Y + m.Height;
        }

        private static double DistanceToRect(ScreenRect m, double cx, double cy)
        {
            var dx = Math.Max(Math.Max((double)m.X - cx, 0.0), cx - ((double)m.X + m.Width));
            var dy = Math.Max(Math.Max((double)m.Y - cy, 0.0), cy - ((double)m.Y + m.Height));
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static void AssertKeeps(float x, float y, float width, float height, IReadOnlyList<ScreenRect> monitors, string because)
        {
            float rx;
            float ry;
            var restored = WindowPlacement.TryRestore(x, y, width, height, monitors, out rx, out ry);

            var where = because + "（位置 " + x + ", " + y + " / サイズ " + width + "x" + height + " / モニター " + Describe(monitors) + "）";
            Assert.That(restored, Is.True, where + ": 復元しなかった");
            Assert.That(Bits(rx), Is.EqualTo(Bits(x)), where + ": x が入力のままでない（実際 " + rx + "）");
            Assert.That(Bits(ry), Is.EqualTo(Bits(y)), where + ": y が入力のままでない（実際 " + ry + "）");
        }

        private static void AssertMoves(
            float x, float y, float width, float height, IReadOnlyList<ScreenRect> monitors,
            float expectedX, float expectedY, int expectedMonitor, string because)
        {
            float rx;
            float ry;
            var restored = WindowPlacement.TryRestore(x, y, width, height, monitors, out rx, out ry);

            var where = because + "（位置 " + x + ", " + y + " / サイズ " + width + "x" + height + " / モニター " + Describe(monitors) + "）";
            Assert.That(restored, Is.True, where + ": 復元しなかった");
            Assert.That(IsFinite(rx) && IsFinite(ry), Is.True, where + ": 位置が有限でない（実際 " + rx + ", " + ry + "）");
            Assert.That(rx, Is.EqualTo(expectedX).Within(Tolerance), where + ": x");
            Assert.That(ry, Is.EqualTo(expectedY).Within(Tolerance), where + ": y");
            Assert.That(
                CenterOn(monitors[expectedMonitor], rx, ry, width, height),
                Is.True,
                where + ": 返した位置 " + rx + ", " + ry + " の中心がモニター #" + expectedMonitor + " の上にない");
        }

        private static void AssertDeclines(float x, float y, float width, float height, IReadOnlyList<ScreenRect> monitors, string because)
        {
            float rx;
            float ry;
            var restored = WindowPlacement.TryRestore(x, y, width, height, monitors, out rx, out ry);

            Assert.That(
                restored,
                Is.False,
                because + "（位置 " + x + ", " + y + " / サイズ " + width + "x" + height + " / モニター " + Describe(monitors) + "）: 復元した（" + rx + ", " + ry + "）");
        }

        // ---- ScreenRect ----

        [Test]
        public void ScreenRect_KeepsEachValue()
        {
            var rect = new ScreenRect(-1920.5f, -200f, 1537f, 1025f);

            Assert.That(rect.X, Is.EqualTo(-1920.5f));
            Assert.That(rect.Y, Is.EqualTo(-200f));
            Assert.That(rect.Width, Is.EqualTo(1537f));
            Assert.That(rect.Height, Is.EqualTo(1025f));
        }

        // ---- 中心がモニター上にある ----

        [Test]
        public void TryRestore_KeepsPosition_WhenCenterIsInsideMonitor()
        {
            AssertKeeps(100f, 50f, W, H, Monitors(Primary), "中心 868.5, 562.5");
            AssertKeeps(0f, 0f, W, H, Monitors(Primary), "左上が原点");
            AssertKeeps(-700f, -500f, W, H, Monitors(Primary), "ウィンドウの左上がモニターの外にはみ出す");
            AssertKeeps(1100f, 500f, W, H, Monitors(Primary), "ウィンドウの右下がモニターの外にはみ出す");
        }

        [Test]
        public void TryRestore_KeepsPosition_WhenCenterIsOnMonitorEdge()
        {
            // 中心の x が 0 / 1920、y が 0 / 1080 ちょうど
            var xs = new[] { -768.5f, 1151.5f, 100f };
            var ys = new[] { -512.5f, 567.5f, 50f };

            foreach (var x in xs)
            {
                foreach (var y in ys)
                {
                    AssertKeeps(x, y, W, H, Monitors(Primary), "境界ちょうど");
                }
            }
        }

        [Test]
        public void TryRestore_ReturnsInputBits_WhenCenterIsOnMonitor()
        {
            // (x + 768.5) - 768.5 が x と同じビットにならない値を含める
            var negativeZero = BitConverter.ToSingle(BitConverter.GetBytes(unchecked((int)0x80000000)), 0);

            AssertKeeps(negativeZero, negativeZero, W, H, Monitors(Primary), "-0");
            AssertKeeps(100.3f, 0.1f, W, H, Monitors(Primary), "小数");
            AssertKeeps(-768.4999f, 567.4999f, W, H, Monitors(Primary), "境界のわずかに内側");
            AssertKeeps(1e-30f, -1e-30f, W, H, Monitors(Primary), "0 に近い値");

            // 片方の軸は境界ちょうど、もう片方は小数
            AssertKeeps(100.3f, -512.5f, W, H, Monitors(Primary), "上の境界ちょうど");
            AssertKeeps(100.3f, 567.5f, W, H, Monitors(Primary), "下の境界ちょうど");
            AssertKeeps(-768.5f, 0.1f, W, H, Monitors(Primary), "左の境界ちょうど");
            AssertKeeps(1151.5f, 0.1f, W, H, Monitors(Primary), "右の境界ちょうど");
        }

        [Test]
        public void TryRestore_KeepsPosition_WhenCenterIsOnNonPrimaryMonitor()
        {
            var left = R(-1920f, -200f, 1920f, 1080f);
            var above = R(0f, -1440f, 2560f, 1440f);

            AssertKeeps(-1500f, 0f, W, H, Monitors(Primary, left), "左にある負の座標のモニター");
            AssertKeeps(-1500f, 0f, W, H, Monitors(left, Primary), "左にある負の座標のモニターが先頭");
            AssertKeeps(500f, -1200f, W, H, Monitors(Primary, left, above), "上にあるモニター");
            AssertKeeps(1151.5f, 100f, W, H, Monitors(Primary, R(1920f, 0f, 1920f, 1080f)), "隣り合うモニターの境目");
            AssertKeeps(1300f, 100f, W, H, Monitors(Primary, R(1920f, 0f, 1920f, 1080f)), "隣の右のモニター");
            AssertKeeps(-768.5f, -512.5f, W, H, Monitors(left, above), "2 枚の角が接する点");
        }

        [Test]
        public void TryRestore_KeepsPosition_WhenCenterIsOnOverlappingOrNestedMonitors()
        {
            var overlapping = R(1000f, 500f, 1920f, 1080f);
            var nested = R(200f, 100f, 800f, 600f);

            AssertKeeps(500f, 200f, W, H, Monitors(Primary, overlapping), "重なった部分");
            AssertKeeps(1800f, 800f, W, H, Monitors(Primary, overlapping), "重なったモニターの片方だけ");
            AssertKeeps(-200f, -100f, W, H, Monitors(Primary, nested), "入れ子の内側");
            AssertKeeps(-200f, -100f, W, H, Monitors(nested, Primary), "入れ子の内側が先頭");
        }

        [Test]
        public void TryRestore_KeepsPosition_WhenWindowIsLargerThanMonitor()
        {
            AssertKeeps(-1000f, -900f, 4001f, 3001f, Monitors(Primary), "中心 1000.5, 600.5");
            AssertKeeps(-2000.5f, -1500.5f, 4001f, 3001f, Monitors(Primary), "中心が左上の角ちょうど");
        }

        // ---- 中心がどのモニターの上にもない ----

        [Test]
        public void TryRestore_MovesCenterOntoEdge_WhenCenterIsJustOutside()
        {
            AssertMoves(1152f, 50f, W, H, Monitors(Primary), 1151.5f, 50f, 0, "右に 0.5 外");
            AssertMoves(-769f, 50f, W, H, Monitors(Primary), -768.5f, 50f, 0, "左に 0.5 外");
            AssertMoves(100f, -513f, W, H, Monitors(Primary), 100f, -512.5f, 0, "上に 0.5 外");
            AssertMoves(100f, 568f, W, H, Monitors(Primary), 100f, 567.5f, 0, "下に 0.5 外");
            AssertMoves(1152f, 568f, W, H, Monitors(Primary), 1151.5f, 567.5f, 0, "右下に 0.5 外");
        }

        [Test]
        public void TryRestore_ClampsEachAxisIndependently_WhenCenterIsOutsideInAnyDirection()
        {
            AssertMoves(5000f, 50f, W, H, Monitors(Primary), 1151.5f, 50f, 0, "右");
            AssertMoves(-5000f, 50f, W, H, Monitors(Primary), -768.5f, 50f, 0, "左");
            AssertMoves(100f, -4000f, W, H, Monitors(Primary), 100f, -512.5f, 0, "上");
            AssertMoves(100f, 3000f, W, H, Monitors(Primary), 100f, 567.5f, 0, "下");
            AssertMoves(5000f, 3000f, W, H, Monitors(Primary), 1151.5f, 567.5f, 0, "右下");
            AssertMoves(-5000f, -4000f, W, H, Monitors(Primary), -768.5f, -512.5f, 0, "左上");
            AssertMoves(5000f, -4000f, W, H, Monitors(Primary), 1151.5f, -512.5f, 0, "右上");
            AssertMoves(-5000f, 3000f, W, H, Monitors(Primary), -768.5f, 567.5f, 0, "左下");
        }

        [Test]
        public void TryRestore_MovesCenterOntoMonitor_WhenPositionIsHuge()
        {
            AssertMoves(1e7f, 50f, W, H, Monitors(Primary), 1151.5f, 50f, 0, "x = 1e7");
            AssertMoves(-1e7f, -1e7f, W, H, Monitors(Primary), -768.5f, -512.5f, 0, "x = y = -1e7");
            AssertMoves(100f, 1e7f, W, H, Monitors(Primary), 100f, 567.5f, 0, "y = 1e7");
            AssertMoves(float.MaxValue, float.MaxValue, W, H, Monitors(Primary), 1151.5f, 567.5f, 0, "float.MaxValue");
            AssertMoves(-float.MaxValue, -float.MaxValue, W, H, Monitors(Primary), -768.5f, -512.5f, 0, "-float.MaxValue");
        }

        [Test]
        public void TryRestore_MovesCenterOntoNegativeCoordinateMonitor()
        {
            var negative = R(-1920f, -1080f, 1920f, 1080f);

            AssertMoves(-5000f, -200f, W, H, Monitors(negative), -2688.5f, -512.5f, 0, "左に外れた");
            AssertMoves(-1500f, -3000f, W, H, Monitors(negative), -1500f, -1592.5f, 0, "上に外れた");
        }

        [Test]
        public void TryRestore_DoesNotFitWholeWindowIntoMonitor()
        {
            AssertMoves(3000f, 0f, 4001f, 3001f, Monitors(Primary), -80.5f, -420.5f, 0, "モニターより大きいウィンドウ");
            AssertMoves(2000f, 50f, W, H, Monitors(Primary), 1151.5f, 50f, 0, "右半分ははみ出したまま");
        }

        /// <summary>
        /// 幅・高さが小数のとき、「端 - 半分」を float に丸めると中心が端のわずかに外へ出ることがある。
        /// 返した位置の中心は、それでも倍精度でモニターの閉区間上にある。
        /// 正の位置・負の位置のそれぞれで、内側が大きい向きと小さい向きの両方を含める。
        /// </summary>
        [Test]
        public void TryRestore_KeepsCenterOnMonitor_WhenFloatRoundingWouldPushItPastEdge()
        {
            var negative = R(-1920f, -1080f, 1920f, 1080f);
            var farLeft = R(-3920f, -1080f, 1920f, 1080f);
            var right = R(3000f, 0f, 1920f, 1080f);

            AssertMoves(5000f, 10f, 1000.7f, 1024f, Monitors(Primary), 1419.65f, 10f, 0, "右端、正の位置");
            AssertMoves(0f, 10f, 1000.1f, 1024f, Monitors(right), 2499.95f, 10f, 0, "左端、正の位置");
            AssertMoves(-9000f, -600f, 1000.1f, 1024f, Monitors(negative), -2420.05f, -600f, 0, "左端、負の位置");
            AssertMoves(0f, -600f, 1000.3f, 1024f, Monitors(farLeft), -2500.15f, -600f, 0, "右端、負の位置");
            AssertMoves(10f, 5000f, 1024f, 1000.2f, Monitors(Primary), 10f, 579.9f, 0, "下端、正の位置");
            AssertMoves(-1500f, -9000f, 1024f, 1000.1f, Monitors(negative), -1500f, -1580.05f, 0, "上端、負の位置");
            AssertMoves(5000f, 5000f, 1000.7f, 1000.2f, Monitors(Primary), 1419.65f, 579.9f, 0, "右下の角");
        }

        [Test]
        public void TryRestore_MovesToNearestMonitor_WhenCenterIsBetweenMonitors()
        {
            var right = R(3000f, 0f, 1920f, 1080f);

            // 中心 x 2600: 左のモニターまで 680、右のモニターまで 400
            AssertMoves(1831.5f, 50f, W, H, Monitors(Primary, right), 2231.5f, 50f, 1, "右の方が近い");
            AssertMoves(1831.5f, 50f, W, H, Monitors(right, Primary), 2231.5f, 50f, 0, "右の方が近い（右が先頭）");

            // 中心 x 2200: 左まで 280、右まで 800
            AssertMoves(1431.5f, 50f, W, H, Monitors(right, Primary), 1151.5f, 50f, 1, "左の方が近い");
        }

        [Test]
        public void TryRestore_MeasuresEuclideanDistanceToRectangle()
        {
            // 中心はどれも 0, 0
            var x = -768.5f;
            var y = -512.5f;

            // diagonal: dx 300, dy 400 → 500（マンハッタン 700、チェビシェフ 400）
            var diagonal = R(300f, 400f, 1000f, 1000f);
            // straight600: dy 600 → 600（マンハッタン 600）
            var straight600 = R(-500f, -1600f, 1000f, 1000f);
            // straight450: dy 450 → 450（チェビシェフ 450）
            var straight450 = R(-500f, 450f, 1000f, 1000f);

            AssertMoves(x, y, W, H, Monitors(straight600, diagonal), -468.5f, -112.5f, 1, "マンハッタン距離なら番号 0 になる");
            AssertMoves(x, y, W, H, Monitors(diagonal, straight450), -768.5f, -62.5f, 1, "チェビシェフ距離なら番号 0 になる");

            // big: 矩形までは 500、矩形の中心（2500, 0）までは 2500
            // small: 矩形までは 700、矩形の中心（-1200, 0）までは 1200
            var big = R(500f, -3000f, 4000f, 6000f);
            var small = R(-1700f, -100f, 1000f, 200f);

            AssertMoves(x, y, W, H, Monitors(small, big), -268.5f, -512.5f, 1, "モニターの中心までの距離なら番号 0 になる");
        }

        [Test]
        public void TryRestore_PrefersLowerIndex_WhenDistancesTie()
        {
            var x = -768.5f;
            var y = -512.5f;

            // どちらも中心から 100
            var right = R(100f, -500f, 1000f, 1000f);
            var left = R(-1100f, -500f, 1000f, 1000f);

            AssertMoves(x, y, W, H, Monitors(right, left), -668.5f, -512.5f, 0, "右が番号 0");
            AssertMoves(x, y, W, H, Monitors(left, right), -868.5f, -512.5f, 0, "左が番号 0");

            // 斜めの 500（dx 300, dy 400）と真横の 500
            var diagonal = R(300f, 400f, 1000f, 1000f);
            var side = R(-1500f, -500f, 1000f, 1000f);

            AssertMoves(x, y, W, H, Monitors(diagonal, side), -468.5f, -112.5f, 0, "斜めが番号 0");
            AssertMoves(x, y, W, H, Monitors(side, diagonal), -1268.5f, -512.5f, 0, "真横が番号 0");

            // 番号の大きい方が後ろに複数ある
            AssertMoves(x, y, W, H, Monitors(R(5000f, 5000f, 10f, 10f), left, right, left), -868.5f, -512.5f, 1, "同じ距離が 3 枚");
        }

        [Test]
        public void TryRestore_ChoosesNearestOfEightMonitors()
        {
            // 1920x1080 を 4 列 2 行、100 の隙間を空けて並べる（番号は行優先）
            var monitors = new ScreenRect[8];
            for (var row = 0; row < 2; row++)
            {
                for (var col = 0; col < 4; col++)
                {
                    monitors[row * 4 + col] = R(col * 2020f, row * 1180f, 1920f, 1080f);
                }
            }

            // 中心 3960, 1130: 列 1 まで 20、列 2 まで 80、行 0 と行 1 まで 50 ずつ → 番号 1 と 5 が同じ距離
            AssertMoves(3191.5f, 617.5f, W, H, monitors, 3171.5f, 567.5f, 1, "隙間の交点の近く");

            // 中心 8000, 2300: 番号 7 の右下の外
            AssertMoves(7231.5f, 1787.5f, W, H, monitors, 7211.5f, 1747.5f, 7, "全体の右下の外");

            // 中心 3990, 500: 列 1 と列 2 の隙間のちょうど真ん中 → 番号 1
            AssertMoves(3221.5f, -12.5f, W, H, monitors, 3171.5f, -12.5f, 1, "隙間の真ん中");

            // 中心が番号 6 の上
            AssertKeeps(4100f, 1300f, W, H, monitors, "番号 6 の上");
        }

        // ---- 無効なモニター ----

        /// <summary>有効でないモニター。どれも、無効と扱わないと中心 900, 600 を含むか最も近くなる位置に置く。</summary>
        private static readonly ScreenRect[] InvalidMonitors =
        {
            R(900f, 0f, 0f, 1080f),
            R(0f, 600f, 1920f, 0f),
            R(0f, 0f, 0f, 0f),
            R(2000f, 0f, -1500f, 1080f),
            R(0f, 1200f, 1920f, -1000f),
            R(1920f, 1080f, -1920f, -1080f),
            R(float.NaN, 0f, 1920f, 1080f),
            R(0f, float.NaN, 1920f, 1080f),
            R(0f, 0f, float.NaN, 1080f),
            R(0f, 0f, 1920f, float.NaN),
            R(float.PositiveInfinity, 0f, 1920f, 1080f),
            R(float.NegativeInfinity, 0f, 1920f, 1080f),
            R(0f, float.NegativeInfinity, 1920f, 1080f),
            R(0f, float.PositiveInfinity, 1920f, 1080f),
            R(0f, 0f, float.PositiveInfinity, 1080f),
            R(0f, 0f, 1920f, float.PositiveInfinity),
            R(0f, 0f, float.NegativeInfinity, 1080f),
            R(float.NegativeInfinity, float.NegativeInfinity, float.PositiveInfinity, float.PositiveInfinity),
            R(800f, 500f, float.Epsilon * -1f, 200f),
        };

        [Test]
        public void TryRestore_IgnoresInvalidMonitor_WhenValidMonitorRemains()
        {
            // 中心 900, 600。有効なのは右の離れたモニターだけ
            var valid = R(3000f, 0f, 1920f, 1080f);

            foreach (var invalid in InvalidMonitors)
            {
                AssertMoves(131.5f, 87.5f, W, H, Monitors(invalid, valid), 2231.5f, 87.5f, 1, "無効が先頭");
                AssertMoves(131.5f, 87.5f, W, H, Monitors(valid, invalid), 2231.5f, 87.5f, 0, "無効が後ろ");
            }
        }

        [Test]
        public void TryRestore_PrefersLowerIndexAmongValidMonitors_WhenInvalidMonitorsComeFirst()
        {
            var right = R(100f, -500f, 1000f, 1000f);
            var left = R(-1100f, -500f, 1000f, 1000f);

            foreach (var invalid in InvalidMonitors)
            {
                AssertMoves(-768.5f, -512.5f, W, H, Monitors(invalid, invalid, left, invalid, right), -868.5f, -512.5f, 2, "同じ距離の有効なモニターの手前に無効");
            }
        }

        [Test]
        public void TryRestore_Declines_WhenNoValidMonitor()
        {
            foreach (var invalid in InvalidMonitors)
            {
                AssertDeclines(131.5f, 87.5f, W, H, Monitors(invalid), "無効なモニター 1 枚");
            }

            AssertDeclines(131.5f, 87.5f, W, H, InvalidMonitors, "無効なモニターだけ");
            AssertDeclines(131.5f, 87.5f, W, H, Monitors(), "0 枚");
            AssertDeclines(131.5f, 87.5f, W, H, null, "null");
        }

        // ---- 復元しない入力 ----

        [TestCase(float.NaN, 50f)]
        [TestCase(float.PositiveInfinity, 50f)]
        [TestCase(float.NegativeInfinity, 50f)]
        [TestCase(100f, float.NaN)]
        [TestCase(100f, float.PositiveInfinity)]
        [TestCase(100f, float.NegativeInfinity)]
        [TestCase(float.NaN, float.NaN)]
        [TestCase(float.PositiveInfinity, float.NegativeInfinity)]
        public void TryRestore_Declines_WhenPositionIsNotFinite(float x, float y)
        {
            AssertDeclines(x, y, W, H, Monitors(Primary), "位置が有限でない");
            AssertDeclines(x, y, W, H, Monitors(Primary, R(-1920f, 0f, 1920f, 1080f)), "位置が有限でない、2 枚");
        }

        [TestCase(0f, H)]
        [TestCase(-0f, H)]
        [TestCase(-1f, H)]
        [TestCase(-1537f, H)]
        [TestCase(float.NaN, H)]
        [TestCase(float.PositiveInfinity, H)]
        [TestCase(float.NegativeInfinity, H)]
        [TestCase(W, 0f)]
        [TestCase(W, -1f)]
        [TestCase(W, float.NaN)]
        [TestCase(W, float.PositiveInfinity)]
        [TestCase(W, float.NegativeInfinity)]
        [TestCase(0f, 0f)]
        public void TryRestore_Declines_WhenWindowSizeIsNotPositiveFinite(float width, float height)
        {
            AssertDeclines(100f, 50f, width, height, Monitors(Primary), "中心がモニター上にある位置");
            AssertDeclines(5000f, 3000f, width, height, Monitors(Primary), "中心がモニター外にある位置");
        }

        [Test]
        public void TryRestore_Declines_WhenNegativeZeroWidth()
        {
            var negativeZero = BitConverter.ToSingle(BitConverter.GetBytes(unchecked((int)0x80000000)), 0);

            AssertDeclines(100f, 50f, negativeZero, H, Monitors(Primary), "幅 -0");
            AssertDeclines(100f, 50f, W, negativeZero, Monitors(Primary), "高さ -0");
        }

        // ---- 不変条件 ----

        /// <summary>
        /// 構成と位置を総当たりして、返した位置での中心が有効なモニター上にあること、
        /// 保存位置の中心がすでにモニター上ならビットまで変えないこと、
        /// 動かした場合は移動量が中心から最も近い有効なモニターまでの距離に等しいことを確かめる。
        /// </summary>
        [Test]
        public void TryRestore_PutsCenterOnValidMonitorWithMinimalMove_ForManyLayouts()
        {
            var layouts = new[]
            {
                Monitors(Primary),
                Monitors(Primary, R(-1920f, -200f, 1920f, 1080f)),
                Monitors(R(-2560f, -1440f, 2560f, 1440f), Primary, R(1920f, 300f, 1080f, 1920f)),
                Monitors(Primary, R(1000f, 500f, 1920f, 1080f), R(200f, 100f, 800f, 600f)),
                Monitors(R(0f, 0f, 0f, 1080f), R(5000f, -3000f, 1280f, 720f), R(float.NaN, 0f, 10f, 10f), Primary),
                Monitors(
                    R(0f, 0f, 1920f, 1080f), R(2020f, 0f, 1920f, 1080f), R(4040f, 0f, 1920f, 1080f), R(6060f, 0f, 1920f, 1080f),
                    R(0f, 1180f, 1920f, 1080f), R(2020f, 1180f, 1920f, 1080f), R(4040f, 1180f, 1920f, 1080f), R(6060f, 1180f, 1920f, 1080f)),
            };
            var sizes = new[]
            {
                new[] { W, H }, new[] { 768f, 512f }, new[] { 4001f, 3001f }, new[] { 1f, 1f },
                new[] { 1000.7f, 1000.2f }, new[] { 1000.1f, 1000.3f },
            };
            var random = new Random(20261026);
            var checkedCount = 0;

            foreach (var monitors in layouts)
            {
                foreach (var size in sizes)
                {
                    for (var i = 0; i < 400; i++)
                    {
                        // 整数の位置と、0.25 刻みの位置を混ぜる
                        var x = (float)(random.Next(-20000, 20000) + (i % 2 == 0 ? 0 : random.Next(0, 4) * 0.25));
                        var y = (float)(random.Next(-20000, 20000) + (i % 2 == 0 ? 0 : random.Next(0, 4) * 0.25));
                        var width = size[0];
                        var height = size[1];

                        double cx = x + width / 2.0;
                        double cy = y + height / 2.0;
                        var alreadyOn = false;
                        var nearest = double.PositiveInfinity;
                        foreach (var m in monitors)
                        {
                            if (!IsValid(m))
                            {
                                continue;
                            }

                            alreadyOn |= CenterOn(m, x, y, width, height);
                            nearest = Math.Min(nearest, DistanceToRect(m, cx, cy));
                        }

                        float rx;
                        float ry;
                        var restored = WindowPlacement.TryRestore(x, y, width, height, monitors, out rx, out ry);

                        var where = "位置 " + x + ", " + y + " / サイズ " + width + "x" + height + " / モニター " + Describe(monitors);
                        Assert.That(restored, Is.True, where + ": 復元しなかった");

                        var landed = false;
                        foreach (var m in monitors)
                        {
                            landed |= IsValid(m) && CenterOn(m, rx, ry, width, height);
                        }

                        Assert.That(landed, Is.True, where + ": 返した位置 " + rx + ", " + ry + " の中心が有効なモニター上にない");

                        if (alreadyOn)
                        {
                            Assert.That(Bits(rx), Is.EqualTo(Bits(x)), where + ": x を変えた（実際 " + rx + "）");
                            Assert.That(Bits(ry), Is.EqualTo(Bits(y)), where + ": y を変えた（実際 " + ry + "）");
                        }
                        else
                        {
                            var dx = (double)rx - x;
                            var dy = (double)ry - y;
                            Assert.That(Math.Sqrt(dx * dx + dy * dy), Is.EqualTo(nearest).Within(1e-2), where + ": 移動量が最も近いモニターまでの距離でない");
                        }

                        checkedCount++;
                    }
                }
            }

            Assert.That(checkedCount, Is.EqualTo(layouts.Length * sizes.Length * 400));
        }
    }

    /// <summary>
    /// ウィンドウ位置を保存する時機。Begin で与えた位置を保存済みとし、保存済みと異なる位置が SettleSeconds 以上続けて観測され、
    /// そのときのウィンドウサイズが保存中のサイズ状態に対応していれば保存する。終了時は待たずに同じ条件を見る。
    /// </summary>
    public class PlacementSaverTests
    {
        [Test]
        public void SettleSeconds_IsOneSecond()
        {
            Assert.That(PlacementSaver.SettleSeconds, Is.EqualTo(1f));
        }

        // ---- Begin 前 ----

        [Test]
        public void Tick_ReturnsFalse_BeforeBegin()
        {
            var saver = new PlacementSaver();

            for (var i = 0; i <= 40; i++)
            {
                var now = i * 0.25f;
                var x = i < 20 ? 100f : 300f;
                Assert.That(saver.Tick(now, x, 200f, true), Is.False, "now " + now);
            }
        }

        [Test]
        public void ShouldSaveOnQuit_ReturnsFalse_BeforeBegin()
        {
            var saver = new PlacementSaver();

            Assert.That(saver.ShouldSaveOnQuit(100f, 200f, true), Is.False);
            saver.Tick(0f, 100f, 200f, true);
            saver.Tick(5f, 100f, 200f, true);
            Assert.That(saver.ShouldSaveOnQuit(100f, 200f, true), Is.False);
            Assert.That(saver.ShouldSaveOnQuit(-5f, 7f, true), Is.False);
        }

        // ---- 位置が動かない ----

        [Test]
        public void Tick_ReturnsFalse_WhilePositionStaysAtBegin()
        {
            var saver = new PlacementSaver();
            saver.Begin(100f, 200f);

            for (var i = 0; i <= 400; i++)
            {
                var now = 3f + i * 0.25f;
                Assert.That(saver.Tick(now, 100f, 200f, true), Is.False, "now " + now);
            }
        }

        // ---- 落ち着く前と後 ----

        [Test]
        public void Tick_ReturnsTrue_WhenNewPositionHasStayedForSettleSeconds()
        {
            var saver = new PlacementSaver();
            saver.Begin(100f, 200f);

            Assert.That(saver.Tick(1000f, 100f, 200f, true), Is.False, "まだ動いていない");
            Assert.That(saver.Tick(1000.25f, 140f, 200f, true), Is.False, "動いた直後");
            Assert.That(saver.Tick(1000.75f, 140f, 200f, true), Is.False, "0.5 秒");
            Assert.That(saver.Tick(1001.1875f, 140f, 200f, true), Is.False, "0.9375 秒");
            Assert.That(saver.Tick(1001.25f, 140f, 200f, true), Is.True, "1 秒ちょうど");
        }

        [Test]
        public void Tick_ReturnsFalse_JustBeforeSettleSeconds()
        {
            var saver = new PlacementSaver();
            saver.Begin(0f, 0f);

            Assert.That(saver.Tick(2f, 10f, 0f, true), Is.False, "動いた直後");
            Assert.That(saver.Tick(2.999f, 10f, 0f, true), Is.False, "0.999 秒");
            Assert.That(saver.Tick(3f, 10f, 0f, true), Is.True, "1 秒ちょうど");
        }

        [Test]
        public void Tick_ReturnsTrue_WhenOnlyOneAxisChanged()
        {
            var onlyY = new PlacementSaver();
            onlyY.Begin(100f, 200f);
            Assert.That(onlyY.Tick(0f, 100f, 201f, true), Is.False, "y だけ動いた直後");
            Assert.That(onlyY.Tick(1f, 100f, 201f, true), Is.True, "y だけ動いて 1 秒");

            var onlyX = new PlacementSaver();
            onlyX.Begin(100f, 200f);
            Assert.That(onlyX.Tick(0f, 99f, 200f, true), Is.False, "x だけ動いた直後");
            Assert.That(onlyX.Tick(1f, 99f, 200f, true), Is.True, "x だけ動いて 1 秒");
        }

        [Test]
        public void Tick_ReturnsTrue_WhenNextObservationComesLongAfterMove()
        {
            var saver = new PlacementSaver();
            saver.Begin(0f, 0f);

            Assert.That(saver.Tick(0.5f, -300f, -40f, true), Is.False, "動いた直後");
            Assert.That(saver.Tick(30f, -300f, -40f, true), Is.True, "次の観測が 29.5 秒後");
        }

        [Test]
        public void Tick_MeasuresFromFirstObservation_NotFromBegin()
        {
            var saver = new PlacementSaver();
            saver.Begin(0f, 0f);

            Assert.That(saver.Tick(0f, 0f, 0f, true), Is.False);
            Assert.That(saver.Tick(5f, 0f, 0f, true), Is.False, "Begin の位置のまま 5 秒");
            Assert.That(saver.Tick(5.5f, 10f, 0f, true), Is.False, "動いた直後");
            Assert.That(saver.Tick(6.25f, 10f, 0f, true), Is.False, "動いてから 0.75 秒");
            Assert.That(saver.Tick(6.5f, 10f, 0f, true), Is.True, "動いてから 1 秒");
        }

        [Test]
        public void Tick_RestartsWait_WhenPositionChangesWhileWaiting()
        {
            var saver = new PlacementSaver();
            saver.Begin(0f, 0f);

            Assert.That(saver.Tick(0f, 10f, 0f, true), Is.False, "A を観測");
            Assert.That(saver.Tick(0.75f, 20f, 0f, true), Is.False, "B に動いた");
            Assert.That(saver.Tick(1f, 20f, 0f, true), Is.False, "最初の移動から 1 秒、B は 0.25 秒");
            Assert.That(saver.Tick(1.5f, 20f, 0f, true), Is.False, "B は 0.75 秒");
            Assert.That(saver.Tick(1.75f, 20f, 0f, true), Is.True, "B は 1 秒");
        }

        [Test]
        public void Tick_RestartsWait_WhenOnlyYChangesWhileWaiting()
        {
            var saver = new PlacementSaver();
            saver.Begin(0f, 0f);

            Assert.That(saver.Tick(0f, 5f, 5f, true), Is.False, "A を観測");
            Assert.That(saver.Tick(0.75f, 5f, 7f, true), Is.False, "y だけ動いて B");
            Assert.That(saver.Tick(1f, 5f, 7f, true), Is.False, "最初の移動から 1 秒、B は 0.25 秒");
            Assert.That(saver.Tick(1.5f, 5f, 7f, true), Is.False, "B は 0.75 秒");
            Assert.That(saver.Tick(1.75f, 5f, 7f, true), Is.True, "B は 1 秒");
        }

        [Test]
        public void Tick_RestartsWait_WhenOnlyXChangesWhileWaiting()
        {
            var saver = new PlacementSaver();
            saver.Begin(0f, 0f);

            Assert.That(saver.Tick(0f, 5f, 5f, true), Is.False, "A を観測");
            Assert.That(saver.Tick(0.75f, 7f, 5f, true), Is.False, "x だけ動いて B");
            Assert.That(saver.Tick(1f, 7f, 5f, true), Is.False, "最初の移動から 1 秒、B は 0.25 秒");
            Assert.That(saver.Tick(1.75f, 7f, 5f, true), Is.True, "B は 1 秒");
        }

        [Test]
        public void Tick_RestartsWait_WhenPositionReturnsToEarlierUnsavedPosition()
        {
            var saver = new PlacementSaver();
            saver.Begin(0f, 0f);

            Assert.That(saver.Tick(0f, 10f, 0f, true), Is.False, "A を観測");
            Assert.That(saver.Tick(0.5f, 20f, 0f, true), Is.False, "B に動いた");
            Assert.That(saver.Tick(1f, 10f, 0f, true), Is.False, "A に戻った");
            Assert.That(saver.Tick(1.5f, 10f, 0f, true), Is.False, "最初に A を観測してから 1.5 秒、戻ってからは 0.5 秒");
            Assert.That(saver.Tick(1.9375f, 10f, 0f, true), Is.False, "戻ってから 0.9375 秒");
            Assert.That(saver.Tick(2f, 10f, 0f, true), Is.True, "戻ってから 1 秒");
        }

        [Test]
        public void Tick_RestartsWait_WhenMovingContinuouslyEveryFrame()
        {
            var saver = new PlacementSaver();
            saver.Begin(0f, 0f);

            var now = 0f;
            for (var frame = 1; frame <= 300; frame++)
            {
                now = frame / 64f;
                Assert.That(saver.Tick(now, frame, 0f, true), Is.False, "毎フレーム動いている frame " + frame);
            }

            Assert.That(saver.Tick(now + 0.5f, 300f, 0f, true), Is.False, "止まってから 0.5 秒");
            Assert.That(saver.Tick(now + 1f, 300f, 0f, true), Is.True, "止まってから 1 秒");
        }

        // ---- 同じ位置を二度保存しない ----

        [Test]
        public void Tick_ReturnsTrueOnlyOnce_ForSettledPosition()
        {
            var saver = new PlacementSaver();
            saver.Begin(0f, 0f);

            saver.Tick(0f, 10f, 0f, true);
            Assert.That(saver.Tick(1f, 10f, 0f, true), Is.True, "保存");

            for (var i = 1; i <= 40; i++)
            {
                var now = 1f + i * 0.25f;
                Assert.That(saver.Tick(now, 10f, 0f, true), Is.False, "保存した位置のまま now " + now);
            }
        }

        [Test]
        public void Tick_ReturnsFalse_WhenPositionReturnsToSavedPosition()
        {
            var saver = new PlacementSaver();
            saver.Begin(0f, 0f);

            Assert.That(saver.Tick(0f, 10f, 0f, true), Is.False, "動いた");
            Assert.That(saver.Tick(0.5f, 0f, 0f, true), Is.False, "Begin の位置に戻った");
            Assert.That(saver.Tick(5f, 0f, 0f, true), Is.False, "Begin の位置のまま 4.5 秒");
            Assert.That(saver.Tick(50f, 0f, 0f, true), Is.False, "Begin の位置のまま 49.5 秒");
        }

        [Test]
        public void Tick_RemembersLatestSavedPosition()
        {
            var saver = new PlacementSaver();
            saver.Begin(0f, 0f);

            saver.Tick(0f, 10f, 0f, true);
            Assert.That(saver.Tick(1f, 10f, 0f, true), Is.True, "A を保存");

            Assert.That(saver.Tick(1.5f, 20f, 0f, true), Is.False, "B に動いた直後");
            Assert.That(saver.Tick(2.4375f, 20f, 0f, true), Is.False, "B は 0.9375 秒");
            Assert.That(saver.Tick(2.5f, 20f, 0f, true), Is.True, "B は 1 秒");

            Assert.That(saver.Tick(3f, 10f, 0f, true), Is.False, "前に保存した A に戻った");
            Assert.That(saver.Tick(4f, 10f, 0f, true), Is.True, "A は保存済みでなくなっているので 1 秒で保存");
            Assert.That(saver.Tick(4.5f, 20f, 0f, true), Is.False, "B に戻った");
            Assert.That(saver.Tick(5.5f, 20f, 0f, true), Is.True, "B も 1 秒で保存");

            Assert.That(saver.Tick(6f, 0f, 0f, true), Is.False, "Begin の位置に戻った直後");
            Assert.That(saver.Tick(7f, 0f, 0f, true), Is.True, "Begin の位置も保存済みでなくなっている");
        }

        // ---- ウィンドウサイズ ----

        [Test]
        public void Tick_ReturnsFalse_WhileSizeDoesNotMatch()
        {
            var saver = new PlacementSaver();
            saver.Begin(0f, 0f);

            for (var i = 0; i <= 40; i++)
            {
                var now = i * 0.25f;
                Assert.That(saver.Tick(now, 10f, 0f, false), Is.False, "サイズが違う now " + now);
            }
        }

        [Test]
        public void Tick_ReturnsTrue_WhenSizeMatchesAgainAfterPositionSettled()
        {
            var saver = new PlacementSaver();
            saver.Begin(0f, 0f);

            Assert.That(saver.Tick(0f, 10f, 0f, false), Is.False, "動いた直後、サイズが違う");
            Assert.That(saver.Tick(2.5f, 10f, 0f, false), Is.False, "2.5 秒、サイズが違う");
            Assert.That(saver.Tick(3f, 10f, 0f, true), Is.True, "3 秒、サイズが合った（サイズが違った間も位置は落ち着いている）");
            Assert.That(saver.Tick(4f, 10f, 0f, true), Is.False, "保存済み");
        }

        [Test]
        public void Tick_WaitsSettleSeconds_WhenSizeMatchesBeforePositionSettled()
        {
            var saver = new PlacementSaver();
            saver.Begin(0f, 0f);

            Assert.That(saver.Tick(0f, 10f, 0f, false), Is.False, "動いた直後、サイズが違う");
            Assert.That(saver.Tick(0.5f, 10f, 0f, true), Is.False, "0.5 秒、サイズが合った");
            Assert.That(saver.Tick(1f, 10f, 0f, true), Is.True, "1 秒");
        }

        [Test]
        public void Tick_DoesNotRecordPosition_WhenSizeDoesNotMatch()
        {
            var saver = new PlacementSaver();
            saver.Begin(0f, 0f);

            Assert.That(saver.Tick(0f, 10f, 0f, false), Is.False);
            Assert.That(saver.Tick(5f, 10f, 0f, false), Is.False, "サイズが違うので保存しない");
            Assert.That(saver.Tick(5.5f, 0f, 0f, true), Is.False, "Begin の位置に戻った");
            Assert.That(saver.Tick(10f, 0f, 0f, true), Is.False, "保存済みは Begin の位置のまま");
            Assert.That(saver.Tick(10.5f, 10f, 0f, true), Is.False, "再び動いた直後");
            Assert.That(saver.Tick(11.5f, 10f, 0f, true), Is.True, "動いてから 1 秒");
        }

        // ---- 終了時 ----

        [Test]
        public void ShouldSaveOnQuit_ReturnsTrueWithoutWaiting_WhenPositionDiffersAndSizeMatches()
        {
            var saver = new PlacementSaver();
            saver.Begin(0f, 0f);

            Assert.That(saver.ShouldSaveOnQuit(10f, 0f, true), Is.True, "観測なしで動いた位置");

            var waiting = new PlacementSaver();
            waiting.Begin(0f, 0f);
            Assert.That(waiting.Tick(0f, 0f, 5f, true), Is.False, "動いた直後");
            Assert.That(waiting.ShouldSaveOnQuit(0f, 5f, true), Is.True, "落ち着く前に終了");
        }

        [Test]
        public void ShouldSaveOnQuit_ReturnsFalse_WhenPositionIsSaved()
        {
            var atBegin = new PlacementSaver();
            atBegin.Begin(100f, 200f);
            Assert.That(atBegin.ShouldSaveOnQuit(100f, 200f, true), Is.False, "Begin の位置");

            var afterTick = new PlacementSaver();
            afterTick.Begin(0f, 0f);
            afterTick.Tick(0f, 10f, 0f, true);
            Assert.That(afterTick.Tick(1f, 10f, 0f, true), Is.True, "保存");
            Assert.That(afterTick.ShouldSaveOnQuit(10f, 0f, true), Is.False, "Tick で保存した位置");
        }

        [Test]
        public void ShouldSaveOnQuit_ReturnsFalse_WhenSizeDoesNotMatch()
        {
            var saver = new PlacementSaver();
            saver.Begin(0f, 0f);

            Assert.That(saver.ShouldSaveOnQuit(10f, 0f, false), Is.False, "サイズが違う");
            Assert.That(saver.ShouldSaveOnQuit(10f, 0f, true), Is.True, "サイズが違ったときは保存済みにしない");
        }

        [Test]
        public void ShouldSaveOnQuit_RecordsPosition_WhenReturningTrue()
        {
            var saver = new PlacementSaver();
            saver.Begin(0f, 0f);

            Assert.That(saver.ShouldSaveOnQuit(10f, 0f, true), Is.True, "1 回目");
            Assert.That(saver.ShouldSaveOnQuit(10f, 0f, true), Is.False, "同じ位置の 2 回目");
            Assert.That(saver.Tick(0f, 10f, 0f, true), Is.False, "保存した位置を観測");
            Assert.That(saver.Tick(5f, 10f, 0f, true), Is.False, "保存した位置のまま 5 秒");
            Assert.That(saver.ShouldSaveOnQuit(0f, 0f, true), Is.True, "Begin の位置は保存済みでなくなっている");
        }

        // ---- 有限でない位置 ----

        [TestCase(float.PositiveInfinity, 5f)]
        [TestCase(float.NegativeInfinity, 5f)]
        [TestCase(float.NaN, 5f)]
        [TestCase(5f, float.PositiveInfinity)]
        [TestCase(5f, float.NegativeInfinity)]
        [TestCase(5f, float.NaN)]
        public void Tick_NeverSaves_WhenPositionIsNotFinite(float x, float y)
        {
            var saver = new PlacementSaver();
            saver.Begin(0f, 0f);

            for (var i = 0; i <= 40; i++)
            {
                var now = i * 0.25f;
                Assert.That(saver.Tick(now, x, y, true), Is.False, "now " + now);
            }

            Assert.That(saver.ShouldSaveOnQuit(x, y, true), Is.False, "終了時");
        }

        [TestCase(float.PositiveInfinity, 5f)]
        [TestCase(float.NegativeInfinity, 5f)]
        [TestCase(float.NaN, 5f)]
        [TestCase(5f, float.PositiveInfinity)]
        [TestCase(5f, float.NegativeInfinity)]
        [TestCase(5f, float.NaN)]
        public void ShouldSaveOnQuit_ReturnsFalse_WhenPositionIsNotFinite(float x, float y)
        {
            var saver = new PlacementSaver();
            saver.Begin(0f, 0f);

            Assert.That(saver.ShouldSaveOnQuit(x, y, true), Is.False, "有限でない位置");
            Assert.That(saver.ShouldSaveOnQuit(10f, 0f, true), Is.True, "有限でない位置は保存済みにしない");
        }

        // ---- 観測の持ち越し ----

        [Test]
        public void Begin_DiscardsPositionObservedBefore()
        {
            var saver = new PlacementSaver();
            saver.Begin(0f, 0f);

            Assert.That(saver.Tick(0f, 10f, 0f, true), Is.False, "A を観測");
            saver.Begin(20f, 0f);
            Assert.That(saver.Tick(1f, 10f, 0f, true), Is.False, "Begin し直した後に A を観測");
            Assert.That(saver.Tick(1.75f, 10f, 0f, true), Is.False, "Begin し直してから 0.75 秒");
            Assert.That(saver.Tick(2f, 10f, 0f, true), Is.True, "Begin し直した後の観測から 1 秒");
        }

        [Test]
        public void Tick_RestartsWait_AfterSavingOnQuit()
        {
            var saver = new PlacementSaver();
            saver.Begin(0f, 0f);

            Assert.That(saver.Tick(0f, 10f, 0f, true), Is.False, "A を観測");
            Assert.That(saver.ShouldSaveOnQuit(20f, 0f, true), Is.True, "終了時に B を保存");
            Assert.That(saver.Tick(1f, 10f, 0f, true), Is.False, "保存の後に A を観測");
            Assert.That(saver.Tick(1.75f, 10f, 0f, true), Is.False, "A は 0.75 秒");
            Assert.That(saver.Tick(2f, 10f, 0f, true), Is.True, "A は 1 秒");
        }
    }
}
