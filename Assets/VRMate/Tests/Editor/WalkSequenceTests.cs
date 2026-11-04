using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;

namespace VRMate.Tests
{
    /// <summary>
    /// 歩きの判断。歩き出せる状態が待ち時間だけ途切れずに続き、待機が切れ目に来たら、左か右へ距離 D だけ表示領域を横に動かし、
    /// 体の進む向きの端が歩き始めたモニターの端に着いたらそこで止まる。歩き出せる状態が崩れたらそのフレームから動かさず、重みを 0.3 秒で 0 に戻す。
    /// 歩きごとに倍率 k（0.5〜0.75）を選び、表示領域の速さもクリップの時刻の進みも k 倍にする。
    /// クリップの時刻は歩き出しの時刻から始まり、自然に終わるときは待機に近い時刻で重みを下げ始める。
    /// テストでは Runtime と同じく、毎フレームの StepX だけ体の矩形を横に動かして次のフレームの入力にする。
    /// 乱数は呼ばれる順（待ち時間 → 距離 → 向き → 倍率）に決め打ちの値を返す。各テストの仕様文は Description に書く。
    /// </summary>
    public class WalkSequenceTests
    {
        private const float Frame = 1f / 60f;

        // フレームの境目で 1〜2 フレームずれてよい量
        private const float Slack = 2.5f * Frame;

        // 全速 250 px/秒（本家の速さ）になる 1 m の px
        private const float Px = 500f;

        private const float WeightTolerance = 1e-4f;
        private const double StepTolerance = 1e-3;
        private const double EdgeTolerance = 1e-2;

        private static readonly ScreenRect Primary = new ScreenRect(0f, 0f, 1920f, 1080f);

        // 体を中ほど（X = 1870、幅 100）に置くと左右の隙間がどちらも 1870 px で、どの距離でも左右どちらへも歩ける
        private static readonly ScreenRect Wide = new ScreenRect(0f, 0f, 3840f, 1080f);

        private static readonly ScreenRect Small = new ScreenRect(0f, 0f, 800f, 600f);
        private static readonly ScreenRect RightOfPrimary = new ScreenRect(1920f, 0f, 1920f, 1080f);
        private static readonly ScreenRect LeftOfPrimary = new ScreenRect(-1920f, 0f, 1920f, 1080f);

        // ---- ハーネス ----

        private static IReadOnlyList<ScreenRect> Monitors(params ScreenRect[] monitors)
        {
            return monitors;
        }

        private static Func<double> Constant(double value)
        {
            return () => value;
        }

        private static float MaxDelta
        {
            get { return (float)WalkSequence.MaxDeltaSeconds; }
        }

        private static float Clamped(float delta)
        {
            if (!(delta > 0f) || float.IsInfinity(delta))
            {
                return 0f;
            }

            return Math.Min(delta, MaxDelta);
        }

        /// <summary>倍率 1 のときの全速の速さ（px/秒）= FootSpeed × px × sin(TurnDegrees°)。</summary>
        private static double FullSpeed(float px)
        {
            return (double)WalkSequence.FootSpeed * px * Math.Sin((double)WalkSequence.TurnDegrees * Math.PI / 180.0);
        }

        /// <summary>倍率 k の歩きの全速の速さ（px/秒）= FootSpeed × k × px × sin(TurnDegrees°)。</summary>
        private static double FullSpeed(float px, double k)
        {
            return FullSpeed(px) * k;
        }

        /// <summary>倍率 = MinSpeedScale + r × (MaxSpeedScale − MinSpeedScale)。</summary>
        private static double ScaleFor(double r)
        {
            return (double)WalkSequence.MinSpeedScale + r * ((double)WalkSequence.MaxSpeedScale - (double)WalkSequence.MinSpeedScale);
        }

        /// <summary>止め始めてから止まるまでに全速で進む距離 = 全速 × FadeOut ÷ 2。</summary>
        private static double StoppingDistance(float px, double k)
        {
            return FullSpeed(px, k) * (float)WalkSequence.FadeOut / 2.0;
        }

        private static double WaitFor(double r)
        {
            return (double)WalkSequence.MinWaitSeconds + r * ((double)WalkSequence.MaxWaitSeconds - (double)WalkSequence.MinWaitSeconds);
        }

        private static double DistanceFor(double r)
        {
            return (double)WalkSequence.MinDistance + r * ((double)WalkSequence.MaxDistance - (double)WalkSequence.MinDistance);
        }

        /// <summary>距離か端のどちらで止まったかにかかわらず、止まった位置が狙いの手前に収まる幅（全速で 3 フレーム分）。</summary>
        private static double ArrivalTolerance(float px)
        {
            return FullSpeed(px) * 3.0 * Frame;
        }

        /// <summary>進む向きの隙間。左 = 体の左端 − モニターの左端、右 = モニターの右端 − 体の右端。</summary>
        private static double Gap(ScreenRect body, ScreenRect monitor, int direction)
        {
            return direction < 0
                ? (double)body.X - monitor.X
                : ((double)monitor.X + monitor.Width) - ((double)body.X + body.Width);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static bool IsValidMonitor(ScreenRect m)
        {
            return IsFinite(m.X) && IsFinite(m.Y) && IsFinite(m.Width) && IsFinite(m.Height) && m.Width > 0f && m.Height > 0f;
        }

        private static bool IsValidBody(ScreenRect b)
        {
            return IsFinite(b.X) && IsFinite(b.Y) && IsFinite(b.Width) && IsFinite(b.Height) && b.Width > 0f;
        }

        private static bool HasValidMonitor(IReadOnlyList<ScreenRect> monitors)
        {
            return monitors != null && monitors.Any(IsValidMonitor);
        }

        private static double Outside(double value, double low, double high)
        {
            return value < low ? low - value : (value > high ? value - high : 0.0);
        }

        /// <summary>
        /// 歩き始めのモニター: 体の中心を含む有効なモニター（端を含む、添字の小さい方）。どれも含まなければ中心からの距離が最も近いもの（同じなら添字の小さい方）。
        /// </summary>
        private static ScreenRect? StartMonitor(ScreenRect body, IReadOnlyList<ScreenRect> monitors)
        {
            if (monitors == null)
            {
                return null;
            }

            var cx = (double)body.X + body.Width / 2.0;
            var cy = (double)body.Y + body.Height / 2.0;
            ScreenRect? nearest = null;
            var nearestDistance = double.MaxValue;
            for (var i = 0; i < monitors.Count; i++)
            {
                var m = monitors[i];
                if (!IsValidMonitor(m))
                {
                    continue;
                }

                var dx = Outside(cx, m.X, (double)m.X + m.Width);
                var dy = Outside(cy, m.Y, (double)m.Y + m.Height);
                if (dx == 0.0 && dy == 0.0)
                {
                    return m;
                }

                var distance = Math.Sqrt(dx * dx + dy * dy);
                if (distance < nearestDistance)
                {
                    nearestDistance = distance;
                    nearest = m;
                }
            }

            return nearest;
        }

        /// <summary>呼ばれた順に決め打ちの値を返し、尽きたら fallback を返す。</summary>
        private sealed class ScriptedRandom
        {
            private readonly Queue<double> _values;
            private readonly double _fallback;

            public ScriptedRandom(double fallback, params double[] values)
            {
                _fallback = fallback;
                _values = new Queue<double>(values);
            }

            public double Next()
            {
                return _values.Count > 0 ? _values.Dequeue() : _fallback;
            }
        }

        /// <summary>1 回の Advance の入力と出力。</summary>
        private struct FrameLog
        {
            public ScreenRect Body;
            public IReadOnlyList<ScreenRect> Monitors;
            public float Px;
            public float Delta;
            public double WalkedBefore;
            public float PrevWeight;
            public float PrevTime;
            public bool PrevWalking;
            public bool PrevHoldIdle;
            public bool IdleAtBoundary;
            public float StepX;
            public float Weight;
            public float Time;
            public int Direction;
            public bool IsWalking;
            public bool HoldIdle;
            public bool Started;
        }

        /// <summary>WalkSequence と、Runtime の代わりに体の矩形を StepX だけ横に動かす窓。</summary>
        private sealed class Rig
        {
            public Rig(Func<double> random, IReadOnlyList<ScreenRect> monitors, float x, float width, float y = 300f, float height = 400f)
            {
                Walk = new WalkSequence(random);
                MonitorList = monitors;
                X = x;
                Width = width;
                Y = y;
                Height = height;
            }

            public WalkSequence Walk { get; }

            public double X;
            public float Y;
            public float Width;
            public float Height;
            public float PixelsPerMeter = Px;
            public bool CanWalk = true;
            public bool Talking;
            public IReadOnlyList<ScreenRect> MonitorList;

            // 既定は自作の待機がないとき（いつも切れ目にある）と、待機に近い時刻の一覧がないとき（0 秒から始め、残りだけで止め始める）
            public bool IdleAtBoundary = true;
            public float ClipLength = 1f;
            public float StartTime;
            public IReadOnlyList<float> RestTimes;

            /// <summary>null でなければ体の矩形の代わりにこれを渡す（不正な矩形を渡すとき）。</summary>
            public ScreenRect? BodyOverride;

            /// <summary>丸めた delta の和（秒）。</summary>
            public double Clock;

            /// <summary>いまの歩きで動いた量（符号付き、px）。</summary>
            public double Walked;

            public int Walks;

            public ScreenRect Body
            {
                get { return new ScreenRect((float)X, Y, Width, Height); }
            }

            public FrameLog Step(float delta)
            {
                var log = new FrameLog
                {
                    Body = BodyOverride ?? Body,
                    Monitors = MonitorList,
                    Px = PixelsPerMeter,
                    Delta = Clamped(delta),
                    PrevWeight = Walk.Weight,
                    PrevTime = Walk.Time,
                    PrevWalking = Walk.IsWalking,
                    PrevHoldIdle = Walk.HoldIdle,
                    IdleAtBoundary = IdleAtBoundary,
                };

                Walk.Advance(delta, new WalkInput(CanWalk, Talking, PixelsPerMeter, log.Body, MonitorList, IdleAtBoundary, ClipLength, StartTime, RestTimes));

                log.StepX = Walk.StepX;
                log.Weight = Walk.Weight;
                log.Time = Walk.Time;
                log.Direction = Walk.Direction;
                log.IsWalking = Walk.IsWalking;
                log.HoldIdle = Walk.HoldIdle;
                log.Started = Walk.IsWalking && !log.PrevWalking;
                if (log.Started)
                {
                    Walks++;
                    Walked = 0.0;
                }

                log.WalkedBefore = Walked;
                Walked += Walk.StepX;
                X += Walk.StepX;
                Clock += log.Delta;
                return log;
            }

            /// <summary>done が true になったフレームで止め、そのフレームまでの記録を返す。seconds 以内に来なければ null。</summary>
            public List<FrameLog> RunUntil(Func<FrameLog, bool> done, double seconds, float delta = Frame)
            {
                var logs = new List<FrameLog>();
                var end = Clock + seconds;
                while (Clock < end)
                {
                    var log = Step(delta);
                    logs.Add(log);
                    if (done(log))
                    {
                        return logs;
                    }
                }

                return null;
            }

            public List<FrameLog> Run(double seconds, float delta = Frame)
            {
                var logs = new List<FrameLog>();
                var end = Clock + seconds;
                while (Clock < end)
                {
                    logs.Add(Step(delta));
                }

                return logs;
            }

            /// <summary>歩き出すまで進める。歩き出したフレームの記録を返す。</summary>
            public FrameLog StartWalk(double seconds = 40.0, float delta = Frame)
            {
                var logs = RunUntil(l => l.Started, seconds, delta);
                Assert.That(logs, Is.Not.Null, seconds + " 秒以内に歩き出さない");
                return logs[logs.Count - 1];
            }

            /// <summary>歩き終えて重みが 0 に戻るまで進め、歩き出したフレームの次からの記録を返す。</summary>
            public List<FrameLog> FinishWalk(double seconds = 60.0, float delta = Frame)
            {
                var logs = RunUntil(l => !l.IsWalking && l.Weight == 0f, seconds, delta);
                Assert.That(logs, Is.Not.Null, seconds + " 秒以内に歩き終えない");
                return logs;
            }
        }

        private static void AssertNeverStarts(Rig rig, double seconds, string because)
        {
            var logs = rig.Run(seconds);
            for (var i = 0; i < logs.Count; i++)
            {
                var log = logs[i];
                Assert.That(log.IsWalking, Is.False, because + "（" + i + " フレーム目に歩き出した）");
                Assert.That(log.StepX, Is.EqualTo(0f), because + "（" + i + " フレーム目に動いた）");
                Assert.That(log.Weight, Is.EqualTo(0f), because + "（" + i + " フレーム目に重みがある）");
                Assert.That(log.HoldIdle, Is.False, because + "（" + i + " フレーム目に待機の押さえを求めた）");
            }
        }

        /// <summary>
        /// 速さ = 向き × 倍率 × 全速 × 重み（今フレームの更新後）× 丸めた delta。ただし |StepX| は残り = min(D − 歩いた距離, 進む向きの隙間)（0 未満なら 0）を超えない。
        /// </summary>
        private static void AssertSpeed(FrameLog log, ScreenRect monitor, double distance, double k, string because)
        {
            var expected = log.Direction * FullSpeed(log.Px, k) * log.Weight * log.Delta;
            var remaining = Math.Max(0.0, Math.Min(distance - Math.Abs(log.WalkedBefore), Gap(log.Body, monitor, log.Direction)));
            if (Math.Abs(expected) < remaining - StepTolerance)
            {
                Assert.That((double)log.StepX, Is.EqualTo(expected).Within(StepTolerance), because + "（向き × 0.5 × k × px × sin(90°) × 重み × delta）");
                return;
            }

            Assert.That(Math.Abs((double)log.StepX), Is.EqualTo(Math.Min(Math.Abs(expected), remaining)).Within(StepTolerance), because + "（残りで押さえられる）");
            if (log.StepX != 0f)
            {
                Assert.That(Math.Sign(log.StepX), Is.EqualTo(log.Direction), because + "（向きと逆に動いた）");
            }
        }

        // ---- 定数 ----

        [Test]
        [Description("定数は計画の値（待ち 15〜30 秒・距離 400〜1000 px・足の速さ 0.5 m/s・向き 90°・入り 0.25 秒・終わり 0.3 秒・話し終えの 1.5 秒・1 回の Advance は 1/15 秒まで）である")]
        public void Constants_MatchThePlan()
        {
            Assert.That((double)WalkSequence.MinWaitSeconds, Is.EqualTo(15.0));
            Assert.That((double)WalkSequence.MaxWaitSeconds, Is.EqualTo(30.0));
            Assert.That((double)WalkSequence.MinDistance, Is.EqualTo(400.0));
            Assert.That((double)WalkSequence.MaxDistance, Is.EqualTo(1000.0));
            Assert.That((double)WalkSequence.FootSpeed, Is.EqualTo(0.5));
            Assert.That((double)WalkSequence.TurnDegrees, Is.EqualTo(90.0));
            Assert.That((float)WalkSequence.FadeIn, Is.EqualTo(0.25f));
            Assert.That((float)WalkSequence.FadeOut, Is.EqualTo(0.3f));
            Assert.That((float)WalkSequence.TalkHoldSeconds, Is.EqualTo(1.5f));
            Assert.That((float)WalkSequence.MaxDeltaSeconds, Is.EqualTo(1f / 15f).Within(1e-7f));
        }

        [Test]
        [Description("倍率の定数は計画の値（0.5〜0.75）である")]
        public void SpeedScaleConstants_MatchThePlan()
        {
            Assert.That((double)WalkSequence.MinSpeedScale, Is.EqualTo(0.5));
            Assert.That((double)WalkSequence.MaxSpeedScale, Is.EqualTo(0.75));
        }

        [Test]
        [Description("話し終えてから歩き出せるまでの秒は、待機の話し終えの保持（IdleSequence.TalkHoldSeconds）と同じ値である")]
        public void TalkHoldSeconds_EqualsIdleTalkHold()
        {
            Assert.That((float)WalkSequence.TalkHoldSeconds, Is.EqualTo(IdleSequence.TalkHoldSeconds));
        }

        // ---- 作る・入力・出力 ----

        [Test]
        [Description("乱数が null なら作れない（ArgumentNullException）")]
        public void Constructor_ThrowsArgumentNullException_WhenRandomIsNull()
        {
            Assert.Throws<ArgumentNullException>(() => new WalkSequence(null));
        }

        [Test]
        [Description("歩きの入力は、コンストラクタで渡した値（歩ける・話している・1 m の px・体の矩形・モニターの一覧・待機が切れ目にある・クリップの長さ・歩き出しの時刻・待機に近い時刻の一覧）をそのまま持つ")]
        public void WalkInput_KeepsEachValue()
        {
            var body = new ScreenRect(1.5f, -2.5f, 30.25f, 40.75f);
            var monitors = Monitors(Primary, RightOfPrimary);
            var restTimes = new[] { 0.75f, 0.25f };

            var input = new WalkInput(true, false, 123.4f, body, monitors, true, 1.25f, 0.375f, restTimes);
            var other = new WalkInput(false, true, -1f, body, null, false, float.NaN, -2f, null);

            Assert.That(input.CanWalk, Is.True);
            Assert.That(input.Talking, Is.False);
            Assert.That(input.PixelsPerMeter, Is.EqualTo(123.4f));
            Assert.That(input.Body.X, Is.EqualTo(1.5f));
            Assert.That(input.Body.Y, Is.EqualTo(-2.5f));
            Assert.That(input.Body.Width, Is.EqualTo(30.25f));
            Assert.That(input.Body.Height, Is.EqualTo(40.75f));
            Assert.That(input.Monitors, Is.SameAs(monitors));
            Assert.That(other.CanWalk, Is.False);
            Assert.That(other.Talking, Is.True);
            Assert.That(other.PixelsPerMeter, Is.EqualTo(-1f));
            Assert.That(other.Monitors, Is.Null);
            Assert.That(input.IdleAtBoundary, Is.True);
            Assert.That(input.ClipLength, Is.EqualTo(1.25f));
            Assert.That(input.StartTime, Is.EqualTo(0.375f));
            Assert.That(input.RestTimes, Is.SameAs(restTimes));
            Assert.That(other.IdleAtBoundary, Is.False);
            Assert.That(other.ClipLength, Is.NaN);
            Assert.That(other.StartTime, Is.EqualTo(-2f));
            Assert.That(other.RestTimes, Is.Null);
        }

        [Test]
        [Description("作った直後は歩いておらず、重み 0・横の移動 0・向きは右（+1）で、待機の押さえを求めない")]
        public void New_IsNotWalking_AndFacesRight()
        {
            var walk = new WalkSequence(Constant(0.0));

            Assert.That(walk.IsWalking, Is.False);
            Assert.That(walk.Weight, Is.EqualTo(0f));
            Assert.That(walk.StepX, Is.EqualTo(0f));
            Assert.That(walk.Direction, Is.EqualTo(1));
            Assert.That(walk.HoldIdle, Is.False);
        }

        [Test]
        [Description("出力は重み・向き・時刻・横の移動・歩いているか・待機を押さえてほしいか だけで、表示領域を縦に動かす出力を持たない")]
        public void Outputs_HaveNoVerticalMovement()
        {
            var names = typeof(WalkSequence).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name).ToArray();

            Assert.That(names, Is.EquivalentTo(new[] { "Weight", "Direction", "Time", "StepX", "IsWalking", "HoldIdle" }));
        }

        // ---- 待ち ----

        [Test]
        [Description("一度も話していなければ、歩き出せる状態が待ち時間（乱数 0 なら 15 秒）続いたところで歩き出す")]
        public void Advance_StartsWalking_AfterMinWait_WhenRandomIsZero()
        {
            var rig = new Rig(Constant(0.0), Monitors(Wide), 1870f, 100f);

            rig.StartWalk();

            Assert.That(rig.Clock, Is.InRange(15.0 - Slack, 15.0 + Slack));
        }

        [Test]
        [Description("乱数が 1 に近ければ、待ち時間は 30 秒近くになる")]
        public void Advance_StartsWalking_AfterNearlyMaxWait_WhenRandomIsNearlyOne()
        {
            var rig = new Rig(Constant(0.999999), Monitors(Wide), 1870f, 100f);

            rig.StartWalk();

            Assert.That(rig.Clock, Is.InRange(WaitFor(0.999999) - Slack, WaitFor(0.999999) + Slack));
        }

        [Test]
        [Description("待ち時間は最初の Advance で引いた乱数 r から 15 + r × (30 − 15) 秒で決まる")]
        public void Advance_DrawsWaitFirst()
        {
            var random = new ScriptedRandom(0.0, 0.5, 0.0, 0.0);
            var rig = new Rig(random.Next, Monitors(Wide), 1870f, 100f);

            rig.StartWalk();

            Assert.That(rig.Clock, Is.InRange(22.5 - Slack, 22.5 + Slack));
        }

        [Test]
        [Description("歩き出せる状態が途中で 1 フレームでも途切れると、待ちの経過は 0 に戻る")]
        public void Advance_RestartsWait_WhenStateBreaksForOneFrame()
        {
            var rig = new Rig(Constant(0.0), Monitors(Wide), 1870f, 100f);
            rig.Run(10.0);
            rig.CanWalk = false;
            rig.Step(Frame);
            rig.CanWalk = true;
            var resumed = rig.Clock;

            rig.StartWalk();

            Assert.That(rig.Clock - resumed, Is.InRange(15.0 - Slack, 15.0 + Slack));
        }

        [Test]
        [Description("途切れたあと待ちを始め直すときは、待ち時間を引き直す")]
        public void Advance_RedrawsWait_WhenWaitStartsAgainAfterBreak()
        {
            var random = new ScriptedRandom(0.0, 0.0, 1.0);
            var rig = new Rig(random.Next, Monitors(Wide), 1870f, 100f);
            rig.Run(10.0);
            rig.CanWalk = false;
            rig.Step(Frame);
            rig.CanWalk = true;
            var resumed = rig.Clock;

            rig.StartWalk(60.0);

            Assert.That(rig.Clock - resumed, Is.InRange(30.0 - Slack, 30.0 + Slack));
        }

        [Test]
        [Description("1/15 秒を超えるフレームは 1/15 秒として待ちを数える")]
        public void Advance_CountsLongFramesAsMaxDelta_WhileWaiting()
        {
            var rig = new Rig(Constant(0.0), Monitors(Wide), 1870f, 100f);
            var frames = 0;
            while (!rig.Walk.IsWalking && frames < 400)
            {
                rig.Step(1f);
                frames++;
            }

            // 15 秒 ÷ (1/15 秒) = 225 フレーム
            Assert.That(frames, Is.InRange(224, 227));
        }

        [Test]
        [Description("歩き出せる状態の続いた時間が待ち時間にちょうど達したフレームで歩き出す（1/16 秒ずつなら、15 秒の待ちは 240 フレーム目）")]
        public void Advance_StartsWalking_OnTheFrameTheWaitIsExactlyReached()
        {
            // 1/16 秒は 2 進で割り切れるので、240 回足すとちょうど 15 秒になる
            const float Sixteenth = 0.0625f;
            var rig = new Rig(Constant(0.0), Monitors(Wide), 1870f, 100f);
            var frames = 0;
            while (!rig.Walk.IsWalking && frames < 300)
            {
                rig.Step(Sixteenth);
                frames++;
            }

            Assert.That(frames, Is.EqualTo(240));
        }

        [TestCase(0f)]
        [TestCase(-0.5f)]
        [TestCase(float.NaN)]
        [TestCase(float.PositiveInfinity)]
        [TestCase(float.NegativeInfinity)]
        [Description("0 以下・NaN・無限の delta は 0 秒として扱い、待ちも重みも時刻も進まず、動かない")]
        public void Advance_TreatsNonPositiveOrNonFiniteDeltaAsZero(float delta)
        {
            var rig = new Rig(Constant(0.0), Monitors(Wide), 1870f, 100f);
            rig.Run(14.9);
            for (var i = 0; i < 1000; i++)
            {
                rig.Step(delta);
            }

            Assert.That(rig.Walk.IsWalking, Is.False, "待ちが進んだ");
            rig.StartWalk();
            Assert.That(rig.Clock, Is.InRange(15.0 - Slack, 15.0 + Slack), "待ちは丸めた delta の和で数える");

            rig.Run(0.1);
            var weight = rig.Walk.Weight;
            var time = rig.Walk.Time;
            var direction = rig.Walk.Direction;
            for (var i = 0; i < 10; i++)
            {
                var log = rig.Step(delta);
                Assert.That(log.StepX, Is.EqualTo(0f), "動いた");
                Assert.That(log.Weight, Is.EqualTo(weight), "重みが変わった");
                Assert.That(log.Time, Is.EqualTo(time), "時刻が進んだ");
                Assert.That(log.Direction, Is.EqualTo(direction));
                Assert.That(log.IsWalking, Is.True, "歩きが終わった");
            }
        }

        // ---- 待機の切れ目と押さえ ----

        [Test]
        [Description("待ちが終わっても待機が切れ目になければ歩き出さず、切れ目に来たフレームで歩き出す")]
        public void Advance_StartsOnTheFrameIdleReachesBoundary_AfterTheWait()
        {
            var rig = new Rig(Constant(0.0), Monitors(Wide), 1870f, 100f) { IdleAtBoundary = false };
            AssertNoWalk(rig.Run(20.0), "切れ目にない");

            rig.IdleAtBoundary = true;
            var log = rig.Step(Frame);

            Assert.That(log.Started, Is.True, "切れ目に来たフレームで歩き出さない");
        }

        [Test]
        [Description("待ちが終わる前に待機が切れ目にあっただけでは、待ちが終わったあと切れ目になければ歩き出さない")]
        public void Advance_DoesNotStart_WhenIdleWasAtBoundaryOnlyBeforeTheWaitEnded()
        {
            var rig = new Rig(Constant(0.0), Monitors(Wide), 1870f, 100f) { IdleAtBoundary = true };
            rig.Run(10.0);
            rig.IdleAtBoundary = false;

            AssertNoWalk(rig.Run(30.0), "待ちの途中だけ切れ目にあった");
        }

        [Test]
        [Description("待ちが終わるまでは、待機の押さえを求めない")]
        public void Advance_DoesNotHoldIdle_BeforeTheWaitEnds()
        {
            var rig = new Rig(Constant(0.0), Monitors(Wide), 1870f, 100f) { IdleAtBoundary = false };

            var logs = rig.Run(15.0 - Slack);

            Assert.That(logs.All(l => !l.HoldIdle), Is.True, "待ちの途中で押さえを求めた");
        }

        [Test]
        [Description("待ちが終わって待機の切れ目を待つ間は、待機の押さえを求める")]
        public void Advance_HoldsIdle_WhileWaitingForTheBoundaryAfterTheWait()
        {
            var rig = new Rig(Constant(0.0), Monitors(Wide), 1870f, 100f) { IdleAtBoundary = false };
            rig.Run(15.0 + Slack);

            var logs = rig.Run(10.0);

            Assert.That(logs.All(l => l.HoldIdle && !l.IsWalking && l.Weight == 0f), Is.True, "切れ目を待つ間に押さえを求めない・歩き出した");
        }

        [Test]
        [Description("待機が切れ目にないまま待ちにちょうど達したフレームから、待機の押さえを求める（1/16 秒ずつなら、15 秒の待ちは 240 フレーム目から）")]
        public void Advance_HoldsIdle_FromTheFrameTheWaitIsExactlyReached()
        {
            // 1/16 秒は 2 進で割り切れるので、240 回足すとちょうど 15 秒になる
            const float Sixteenth = 0.0625f;
            var rig = new Rig(Constant(0.0), Monitors(Wide), 1870f, 100f) { IdleAtBoundary = false };

            var logs = new List<FrameLog>();
            for (var i = 0; i < 241; i++)
            {
                logs.Add(rig.Step(Sixteenth));
            }

            Assert.That(logs.Take(239).All(l => !l.HoldIdle), Is.True, "待ちが終わる前に押さえを求めた");
            Assert.That(logs[239].HoldIdle, Is.True, "待ちにちょうど達したフレーム（240 フレーム目）で押さえを求めない");
            Assert.That(logs[240].HoldIdle, Is.True, "待ちを過ぎたフレーム");
            Assert.That(logs.All(l => !l.IsWalking), Is.True, "切れ目にないのに歩き出した");
        }

        // 重みが 0 のまま歩き出すのは、歩き出したフレームの delta が 0 のときと、進む向きの隙間が止まるまでの距離（18.75 px）以下で最初のフレームから止め始めるとき
        [TestCase("歩き出したフレームの delta が 0")]
        [TestCase("隙間が止まるまでの距離より小さい")]
        [Description("歩き出したフレームは、重みが 0 のままでも待機の押さえを求める")]
        public void Advance_HoldsIdle_OnTheStartFrame_EvenAtWeightZero(string kind)
        {
            FrameLog start;
            if (kind == "歩き出したフレームの delta が 0")
            {
                var rig = new Rig(Constant(0.0), Monitors(Wide), 1870f, 100f) { IdleAtBoundary = false };
                rig.Run(15.0 + Slack);
                rig.IdleAtBoundary = true;
                start = rig.Step(0f);
            }
            else
            {
                // 幅 800 のモニターに幅 790 の体。隙間は左右とも 5 px で、右へ歩き出してすぐ止め始める
                var rig = new Rig(Constant(0.0), Monitors(Small), 5f, 790f, 100f, 300f);
                start = rig.StartWalk();
            }

            Assert.That(start.Started, Is.True, kind + ": 前提: このフレームで歩き出していない");
            Assert.That(start.Weight, Is.EqualTo(0f), kind + ": 前提: 重みが 0 でない");
            Assert.That(start.HoldIdle, Is.True, kind + ": 歩き出したフレームで押さえを求めない");
        }

        [Test]
        [Description("歩きの重みが 0 より大きい間は、待機の押さえを求め、歩き終えて重みが 0 に戻ったフレームから求めない")]
        public void Advance_HoldsIdle_WhileWeightIsPositive_AndReleasesWhenItReturnsToZero()
        {
            var rig = new Rig(Constant(0.0), Monitors(Wide), 1870f, 100f);
            var start = rig.StartWalk();
            var logs = rig.FinishWalk();
            var after = rig.Run(5.0);

            Assert.That(start.HoldIdle, Is.True, "歩き出したフレーム");
            for (var i = 0; i < logs.Count; i++)
            {
                Assert.That(logs[i].HoldIdle, Is.EqualTo(logs[i].Weight > 0f), i + " フレーム目（重み " + logs[i].Weight + "）");
            }

            Assert.That(after.All(l => !l.HoldIdle), Is.True, "歩き終えたあとの待ちで押さえを求めた");
        }

        [TestCase("CanWalk が false")]
        [TestCase("話し始め")]
        [Description("歩いている間に歩き出せる状態が崩れても、重みを戻す間は待機の押さえを求め、重みが 0 に戻ったら求めない")]
        public void Advance_HoldsIdle_WhileFadingOutAfterBreak(string kind)
        {
            var rig = new Rig(Constant(0.0), Monitors(Wide), 1870f, 100f);
            rig.StartWalk();
            rig.Run(0.5);
            Break(rig, kind);

            var logs = rig.RunUntil(l => l.Weight == 0f, 1.0);

            Assert.That(logs, Is.Not.Null, "重みが 0 に戻らない");
            for (var i = 0; i < logs.Count; i++)
            {
                Assert.That(logs[i].HoldIdle, Is.EqualTo(logs[i].Weight > 0f), kind + ": " + i + " フレーム目（重み " + logs[i].Weight + "）");
            }
        }

        [TestCase("CanWalk が false")]
        [TestCase("話し始め")]
        [TestCase("モニターが null")]
        [Description("待機の切れ目を待つ間に歩き出せる状態が崩れたら、そのフレームから押さえを求めず、待ちを始め直して待ち時間を引き直す")]
        public void Advance_ReleasesHoldAndWaitsAgain_WhenStateBreaksWhileWaitingForTheBoundary(string kind)
        {
            // 最初の待ち 15 秒、引き直した待ち 30 秒
            var random = new ScriptedRandom(0.0, 0.0, 1.0);
            var rig = new Rig(random.Next, Monitors(Wide), 1870f, 100f) { IdleAtBoundary = false };
            rig.Run(17.0);
            Assert.That(rig.Walk.HoldIdle, Is.True, "前提: 切れ目を待っている");

            Break(rig, kind);
            var broken = rig.Step(Frame);
            Assert.That(broken.HoldIdle, Is.False, kind + ": 崩れたフレームで押さえを求めた");

            rig.CanWalk = true;
            rig.Talking = false;
            rig.MonitorList = Monitors(Wide);
            rig.IdleAtBoundary = true;
            var resumed = rig.Clock;
            var waiting = rig.RunUntil(l => l.Started, 60.0);

            Assert.That(waiting, Is.Not.Null, kind + ": 歩き出さない");
            Assert.That(waiting.Take(waiting.Count - 1).All(l => !l.HoldIdle), Is.True, kind + ": 待ちを始め直す間に押さえを求めた");
            var hold = kind == "話し始め" ? (double)WalkSequence.TalkHoldSeconds : 0.0;
            Assert.That(rig.Clock - resumed, Is.InRange(hold + 30.0 - Slack, hold + 30.0 + Slack), kind + ": 引き直した待ち時間で歩き出していない");
        }

        [Test]
        [Description("体の幅がモニターの幅以上で歩き出せなければ、待機が切れ目にあっても押さえを求めない")]
        public void Advance_DoesNotHoldIdle_WhenTheWalkCannotStartAtTheBoundary()
        {
            var rig = new Rig(Constant(0.0), Monitors(Primary), 0f, 1920f);

            AssertNeverStarts(rig, 70.0, "体の幅がモニターと同じ");
        }

        [Test]
        [Description("待機が切れ目にあるかどうかが毎フレーム入れ替わっても、歩き出すのは待ちが終わったあとの切れ目にあるフレームだけである")]
        public void Advance_StartsOnlyOnBoundaryFrames_WhenTheBoundaryFlickers()
        {
            var rig = new Rig(Constant(0.0), Monitors(Wide), 1870f, 100f);
            var starts = 0;
            for (var i = 0; i < 60 * 80; i++)
            {
                rig.IdleAtBoundary = i % 7 == 3;
                var log = rig.Step(Frame);
                if (log.Started)
                {
                    starts++;
                    Assert.That(log.IdleAtBoundary, Is.True, i + " フレーム目: 切れ目にないフレームで歩き出した");
                }
            }

            Assert.That(starts, Is.GreaterThanOrEqualTo(2), "歩き出した回数が少なすぎる");
        }

        private static void AssertNoWalk(List<FrameLog> logs, string because)
        {
            for (var i = 0; i < logs.Count; i++)
            {
                Assert.That(logs[i].IsWalking, Is.False, because + "（" + i + " フレーム目に歩き出した）");
                Assert.That(logs[i].StepX, Is.EqualTo(0f), because + "（" + i + " フレーム目に動いた）");
                Assert.That(logs[i].Weight, Is.EqualTo(0f), because + "（" + i + " フレーム目に重みがある）");
            }
        }

        // ---- 歩き出せる状態 ----

        [Test]
        [Description("CanWalk が false の間は歩き出さない")]
        public void Advance_DoesNotStart_WhileCanWalkIsFalse()
        {
            var rig = new Rig(Constant(0.0), Monitors(Wide), 1870f, 100f) { CanWalk = false };

            AssertNeverStarts(rig, 60.0, "CanWalk が false");
        }

        [Test]
        [Description("話している間は歩き出さない")]
        public void Advance_DoesNotStart_WhileTalking()
        {
            var rig = new Rig(Constant(0.0), Monitors(Wide), 1870f, 100f) { Talking = true };

            AssertNeverStarts(rig, 60.0, "話している");
        }

        [Test]
        [Description("話し終えてから 1.5 秒の間は歩き出せる状態にならず、そのあと待ち時間がたってから歩き出す")]
        public void Advance_WaitsTalkHoldBeforeWaitCounts_AfterTalkingEnds()
        {
            var rig = new Rig(Constant(0.0), Monitors(Wide), 1870f, 100f) { Talking = true };
            rig.Run(20.0);
            rig.Talking = false;
            var ended = rig.Clock;

            rig.StartWalk();

            Assert.That(rig.Clock - ended, Is.InRange(16.5 - Slack, 16.5 + Slack), "1.5 秒 + 待ち 15 秒");
        }

        [Test]
        [Description("話し終えの 1.5 秒は、ほかの条件が崩れている間も数える")]
        public void Advance_CountsTalkHold_WhileOtherConditionsAreBroken()
        {
            var rig = new Rig(Constant(0.0), Monitors(Wide), 1870f, 100f) { Talking = true };
            rig.Run(5.0);
            rig.Talking = false;
            rig.CanWalk = false;
            rig.Run(5.0);
            rig.CanWalk = true;
            var resumed = rig.Clock;

            rig.StartWalk();

            Assert.That(rig.Clock - resumed, Is.InRange(15.0 - Slack, 15.0 + Slack), "話し終えの 1.5 秒は CanWalk が false の間に過ぎている");
        }

        [TestCase(0f)]
        [TestCase(-1f)]
        [TestCase(float.NaN)]
        [TestCase(float.PositiveInfinity)]
        [Description("1 m の px が正の有限でなければ歩き出さない")]
        public void Advance_DoesNotStart_WhenPixelsPerMeterIsNotPositiveFinite(float px)
        {
            var rig = new Rig(Constant(0.0), Monitors(Wide), 1870f, 100f) { PixelsPerMeter = px };

            AssertNeverStarts(rig, 60.0, "px = " + px);
        }

        [TestCase("X が NaN")]
        [TestCase("Y が NaN")]
        [TestCase("幅が NaN")]
        [TestCase("高さが NaN")]
        [TestCase("X が無限")]
        [TestCase("幅が無限")]
        [TestCase("幅が 0")]
        [TestCase("幅が負")]
        [Description("体の矩形の 4 値が有限でないか、幅が正でなければ歩き出さない")]
        public void Advance_DoesNotStart_WhenBodyIsInvalid(string kind)
        {
            var rig = new Rig(Constant(0.0), Monitors(Wide), 1870f, 100f) { BodyOverride = InvalidBody(kind) };

            AssertNeverStarts(rig, 60.0, kind);
        }

        private static ScreenRect InvalidBody(string kind)
        {
            switch (kind)
            {
                case "X が NaN":
                    return new ScreenRect(float.NaN, 300f, 100f, 400f);
                case "Y が NaN":
                    return new ScreenRect(1870f, float.NaN, 100f, 400f);
                case "幅が NaN":
                    return new ScreenRect(1870f, 300f, float.NaN, 400f);
                case "高さが NaN":
                    return new ScreenRect(1870f, 300f, 100f, float.NaN);
                case "X が無限":
                    return new ScreenRect(float.NegativeInfinity, 300f, 100f, 400f);
                case "幅が無限":
                    return new ScreenRect(1870f, 300f, float.PositiveInfinity, 400f);
                case "幅が 0":
                    return new ScreenRect(1870f, 300f, 0f, 400f);
                case "幅が負":
                    return new ScreenRect(1970f, 300f, -100f, 400f);
                default:
                    throw new ArgumentException(kind);
            }
        }

        [TestCase("null")]
        [TestCase("空")]
        [TestCase("すべて不正")]
        [Description("有効なモニター（4 値が有限で幅・高さが正）が 1 枚もなければ歩き出さない")]
        public void Advance_DoesNotStart_WhenNoMonitorIsValid(string kind)
        {
            var rig = new Rig(Constant(0.0), InvalidMonitors(kind), 1870f, 100f);

            AssertNeverStarts(rig, 60.0, kind);
        }

        private static IReadOnlyList<ScreenRect> InvalidMonitors(string kind)
        {
            switch (kind)
            {
                case "null":
                    return null;
                case "空":
                    return Monitors();
                case "すべて不正":
                    return Monitors(
                        new ScreenRect(0f, 0f, 0f, 1080f),
                        new ScreenRect(0f, 0f, 3840f, 0f),
                        new ScreenRect(0f, 0f, -3840f, 1080f),
                        new ScreenRect(float.NaN, 0f, 3840f, 1080f),
                        new ScreenRect(0f, 0f, float.PositiveInfinity, 1080f));
                default:
                    throw new ArgumentException(kind);
            }
        }

        // 幅 1920 のモニター。中央に置くと左右の隙間がどちらも 0 以下になるので、片側だけ隙間が正になる置き方も含める
        // （幅を見なければ、その正の隙間の側へ歩き出す）
        [TestCase("幅がモニターと同じ・中央", 1920f, 0f)]
        [TestCase("幅がモニターより広い・中央", 2500f, -290f)]
        [TestCase("幅がモニターと同じ・左の隙間 50", 1920f, 50f)]
        [TestCase("幅がモニターと同じ・右の隙間 47", 1920f, -47f)]
        [TestCase("幅がモニターより広い・左の隙間 100", 2000f, 100f)]
        [TestCase("幅がモニターより広い・右の隙間 100", 2000f, -180f)]
        [Description("体の幅がモニターの幅以上なら、片側に隙間があっても歩き出さない")]
        public void Advance_DoesNotStart_WhenBodyIsAsWideAsMonitorOrWider(string label, float width, float x)
        {
            var rig = new Rig(Constant(0.0), Monitors(Primary), x, width);

            AssertNeverStarts(rig, 70.0, label);
        }

        // ---- 距離と向き ----

        [TestCase(0.0, -1)]
        [TestCase(0.999999, 1)]
        [Description("距離は歩き出すときの乱数 r から 400 + r × (1000 − 400) px で決まり、その距離を超えずに歩き切る")]
        public void Advance_WalksDistanceFromRandom(double r, int direction)
        {
            var rig = new Rig(Constant(r), Monitors(Wide), 1870f, 100f);
            rig.StartWalk(60.0);
            rig.FinishWalk();

            var distance = DistanceFor(r);
            Assert.That(rig.Walk.Direction, Is.EqualTo(direction));
            Assert.That(Math.Abs(rig.Walked), Is.LessThanOrEqualTo(distance + StepTolerance), "距離を超えた");
            Assert.That(Math.Abs(rig.Walked), Is.GreaterThanOrEqualTo(distance - ArrivalTolerance(Px)), "距離の手前で止まった");
        }

        [Test]
        [Description("歩き出すときは、距離 → 向き → 倍率 の順に乱数を引く")]
        public void Advance_DrawsDistanceThenDirectionThenSpeedScale()
        {
            var random = new ScriptedRandom(0.0, 0.0, 0.5, 0.9, 0.2);
            var rig = new Rig(random.Next, Monitors(Wide), 1870f, 100f);
            rig.StartWalk();
            var logs = rig.FinishWalk();

            Assert.That(rig.Walk.Direction, Is.EqualTo(1), "向きは 3 番目の乱数 0.9");
            Assert.That(rig.Walked, Is.InRange(700.0 - ArrivalTolerance(Px), 700.0 + StepTolerance), "距離は 2 番目の乱数 0.5 → 700 px");
            AssertTimeRate(logs, ScaleFor(0.2), "倍率は 4 番目の乱数 0.2 → 0.55");
        }

        /// <summary>重みが正の間の各フレームで、時刻が 倍率 × 丸めた delta ずつ進むこと。</summary>
        private static void AssertTimeRate(List<FrameLog> logs, double k, string because)
        {
            var checkedFrames = 0;
            for (var i = 0; i < logs.Count; i++)
            {
                if (logs[i].PrevWeight > 0f || logs[i].Weight > 0f)
                {
                    Assert.That((double)logs[i].Time - logs[i].PrevTime, Is.EqualTo(k * logs[i].Delta).Within(WeightTolerance), because + "（" + i + " フレーム目）");
                    checkedFrames++;
                }
            }

            Assert.That(checkedFrames, Is.GreaterThan(10), because + "（確かめたフレームが少なすぎる）");
        }

        [TestCase(0.0, -1)]
        [TestCase(0.4999999, -1)]
        [TestCase(0.5, 1)]
        [TestCase(0.9999999, 1)]
        [Description("左右どちらの隙間も距離以上なら、向きの乱数が 0.5 未満で左、0.5 以上で右へ歩く")]
        public void Advance_ChoosesDirectionByRandom_WhenBothSidesHaveRoom(double r, int direction)
        {
            var random = new ScriptedRandom(0.0, 0.0, 0.0, r);
            var rig = new Rig(random.Next, Monitors(Wide), 1870f, 100f);

            var start = rig.StartWalk();
            rig.Run(0.5);

            Assert.That(start.Direction, Is.EqualTo(direction));
            Assert.That(Math.Sign(rig.Walked), Is.EqualTo(direction), "向きへ動いていない");
        }

        [TestCase(1500f, 0.99, -1)]
        [TestCase(300f, 0.0, 1)]
        [Description("片側の隙間だけが距離以上なら、乱数によらずその向きへ歩く")]
        public void Advance_WalksTowardTheOnlySideWithRoom(float x, double r, int direction)
        {
            var rig = new Rig(Constant(r), Monitors(Primary), x, 100f);

            var start = rig.StartWalk(60.0);
            rig.FinishWalk();

            Assert.That(start.Direction, Is.EqualTo(direction));
            Assert.That(Math.Abs(rig.Walked), Is.GreaterThanOrEqualTo(DistanceFor(r) - ArrivalTolerance(Px)), "距離を歩き切っていない");
        }

        [Test]
        [Description("片側だけが許されるときは向きの乱数を引かず、距離の次の乱数を倍率に、その次を歩き終えたあとの待ち時間に使う")]
        public void Advance_DoesNotDrawDirection_WhenOnlyOneSideHasRoom()
        {
            // 待ち 15 秒 → 距離 400 px → 倍率 0.3 → 0.575 → 次の待ち 0.8 → 27 秒
            var random = new ScriptedRandom(0.0, 0.0, 0.0, 0.3, 0.8);
            var rig = new Rig(random.Next, Monitors(Primary), 300f, 100f);
            rig.StartWalk();
            var logs = rig.FinishWalk();
            var ended = rig.Clock;

            rig.StartWalk(60.0);

            AssertTimeRate(logs, ScaleFor(0.3), "倍率");
            Assert.That(rig.Clock - ended, Is.InRange(27.0 - Slack, 27.0 + Slack), "次の待ち時間");
        }

        [TestCase(390f, -1)]
        [TestCase(310f, 1)]
        [Description("どちらの隙間も距離に足りないときは、隙間の大きい方へ歩く")]
        public void Advance_WalksTowardLargerGap_WhenNeitherSideHasRoom(float x, int direction)
        {
            // 幅 800 のモニターに幅 100 の体。距離は 400 px、隙間は 390 と 310
            var rig = new Rig(Constant(0.0), Monitors(Small), x, 100f, 100f, 300f);

            var start = rig.StartWalk();

            Assert.That(start.Direction, Is.EqualTo(direction));
        }

        [Test]
        [Description("どちらの隙間も距離に足りず、左右の隙間が同じなら右へ歩く")]
        public void Advance_WalksRight_WhenNeitherSideHasRoomAndGapsAreEqual()
        {
            // 乱数 0 を向きに使えば左になる
            var rig = new Rig(Constant(0.0), Monitors(Small), 350f, 100f, 100f, 300f);

            var start = rig.StartWalk();

            Assert.That(start.Direction, Is.EqualTo(1));
        }

        // 幅 1000 のモニターに幅 100 の体、距離 400 px。片側の隙間がちょうど 400、反対側が 500。
        // ちょうどの側が許されなければ、向きの乱数によらず 500 の側へ歩く
        [TestCase("左の隙間がちょうど距離", 400f, 0.1, -1)]
        [TestCase("右の隙間がちょうど距離", 500f, 0.9, 1)]
        [Description("隙間がちょうど距離に等しい向きも、距離以上の向きとして許される")]
        public void Advance_AllowsSideWhoseGapEqualsDistance(string label, float x, double directionRandom, int direction)
        {
            var random = new ScriptedRandom(0.0, 0.0, 0.0, directionRandom);
            var rig = new Rig(random.Next, Monitors(new ScreenRect(0f, 0f, 1000f, 600f)), x, 100f, 100f, 300f);

            var start = rig.StartWalk();

            Assert.That(start.Direction, Is.EqualTo(direction), label);
        }

        [TestCase(double.NaN, 0.0)]
        [TestCase(-3.0, 0.0)]
        [TestCase(double.NegativeInfinity, 0.0)]
        [TestCase(5.0, 1.0)]
        [TestCase(double.PositiveInfinity, 1.0)]
        [Description("乱数が NaN なら 0、[0, 1] の外なら近い方の端に丸めて、待ち時間・距離・向き・倍率を決める")]
        public void Advance_ClampsRandom(double r, double clamped)
        {
            var rig = new Rig(Constant(r), Monitors(Wide), 1870f, 100f);

            var start = rig.StartWalk(60.0);
            Assert.That(rig.Clock, Is.InRange(WaitFor(clamped) - Slack, WaitFor(clamped) + Slack), "待ち時間");
            Assert.That(start.Direction, Is.EqualTo(clamped < 0.5 ? -1 : 1), "向き");
            var logs = rig.FinishWalk();
            Assert.That(Math.Abs(rig.Walked), Is.InRange(DistanceFor(clamped) - ArrivalTolerance(Px), DistanceFor(clamped) + StepTolerance), "距離");
            AssertTimeRate(logs, ScaleFor(clamped), "倍率");
        }

        // ---- 倍率 ----

        [TestCase(0.0)]
        [TestCase(0.37)]
        [TestCase(0.999999)]
        [Description("倍率は歩き出すときの乱数 r から 0.5 + r × (0.75 − 0.5) で決まり、その歩きの間ずっと時刻は 1 秒に倍率の秒ずつ進む")]
        public void Advance_AdvancesTimeBySpeedScale_DrawnFromRandom(double r)
        {
            var rig = new Rig(Constant(r), Monitors(Wide), 1870f, 100f);
            rig.StartWalk(60.0);

            var logs = rig.FinishWalk();

            AssertTimeRate(logs, ScaleFor(r), "r = " + r);
        }

        [Test]
        [Description("倍率は歩きごとに選び直す")]
        public void Advance_DrawsSpeedScaleAgainForEachWalk()
        {
            // 1 回目: 待ち 0 → 距離 0 → 向き 0（左）→ 倍率 0。2 回目: 待ち 0 → 距離 0 → 向き 0.9（右）→ 倍率 1
            var random = new ScriptedRandom(0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.9, 1.0);
            var rig = new Rig(random.Next, Monitors(Wide), 1870f, 100f);
            rig.StartWalk();
            var first = rig.FinishWalk();

            rig.StartWalk();
            var second = rig.FinishWalk();

            AssertTimeRate(first, (double)WalkSequence.MinSpeedScale, "1 回目");
            AssertTimeRate(second, (double)WalkSequence.MaxSpeedScale, "2 回目");
        }

        // ---- 歩く間 ----

        [Test]
        [Description("歩き出すと重みは 1 フレームごとに delta ÷ 0.25 ずつ上がり、0.25 秒で 1 になる")]
        public void Advance_RaisesWeightOverFadeIn()
        {
            var rig = new Rig(Constant(0.99), Monitors(Wide), 1800f, 100f);
            var start = rig.StartWalk();
            Assert.That(start.Weight, Is.InRange(0f, Frame / (float)WalkSequence.FadeIn + WeightTolerance), "歩き出したフレーム");

            var logs = rig.Run((float)WalkSequence.FadeIn + Slack);
            foreach (var log in logs)
            {
                var expected = Math.Min(1f, log.PrevWeight + log.Delta / (float)WalkSequence.FadeIn);
                Assert.That(log.Weight, Is.EqualTo(expected).Within(WeightTolerance));
            }

            Assert.That(rig.Walk.Weight, Is.EqualTo(1f).Within(WeightTolerance));
        }

        [TestCase("1/60 秒・500 px")]
        [TestCase("1/144 秒・123.4 px")]
        [TestCase("1/30 秒・px が毎フレーム変わる")]
        [TestCase("1/15 秒を超えるフレームを含む")]
        [Description("歩いている間の横の移動は、どのフレームでも 向き × 0.5 × 倍率 × 1 m の px × sin(90°) × 重み × 丸めた delta に等しい（残りで押さえられるフレームを除く）")]
        public void Advance_MovesAtFootSpeedTimesScaleTimesPixelsPerMeterTimesWeight(string pattern)
        {
            var rig = new Rig(Constant(0.99), Monitors(Wide), 1800f, 100f);
            rig.PixelsPerMeter = PatternPx(pattern, 0);
            rig.StartWalk(40.0, PatternDelta(pattern, 0));
            var distance = DistanceFor(0.99);
            var k = ScaleFor(0.99);

            var frames = 0;
            for (var i = 1; i < 100000 && rig.Walk.Weight > 0f; i++)
            {
                rig.PixelsPerMeter = PatternPx(pattern, i);
                var log = rig.Step(PatternDelta(pattern, i));
                AssertSpeed(log, Wide, distance, k, pattern + " の " + i + " フレーム目");
                frames++;
            }

            Assert.That(rig.Walk.Weight, Is.EqualTo(0f), "歩き終えない");
            Assert.That(frames, Is.GreaterThan(30), "歩いたフレームが少なすぎる");
        }

        private static float PatternDelta(string pattern, int i)
        {
            switch (pattern)
            {
                case "1/60 秒・500 px":
                    return 1f / 60f;
                case "1/144 秒・123.4 px":
                    return 1f / 144f;
                case "1/30 秒・px が毎フレーム変わる":
                    return 1f / 30f;
                case "1/15 秒を超えるフレームを含む":
                    return i % 3 == 0 ? 0.2f : 1f / 60f;
                default:
                    throw new ArgumentException(pattern);
            }
        }

        private static float PatternPx(string pattern, int i)
        {
            switch (pattern)
            {
                case "1/144 秒・123.4 px":
                    return 123.4f;
                case "1/30 秒・px が毎フレーム変わる":
                    return 300f + 400f * ((i * 37) % 100) / 100f;
                default:
                    return Px;
            }
        }

        [Test]
        [Description("待機に近い時刻の一覧がないとき、残りが 全速（倍率込み）× 0.3 ÷ 2 以下になったら、重みは 1 フレームごとに delta ÷ 0.3 ずつ下がって 0 になり、その間も動く")]
        public void Advance_LowersWeightOverFadeOut_WhenRemainingReachesHalfFadeOutDistance()
        {
            var rig = new Rig(Constant(0.99), Monitors(Wide), 1800f, 100f);
            rig.StartWalk();
            var logs = rig.FinishWalk();
            var distance = DistanceFor(0.99);
            var threshold = StoppingDistance(Px, ScaleFor(0.99));
            var oneFrame = FullSpeed(Px, ScaleFor(0.99)) * Frame * 1.01;

            var endFrame = logs.FindIndex(l => l.Weight < l.PrevWeight);
            Assert.That(endFrame, Is.GreaterThan(0), "重みが下がらない");
            for (var i = 0; i < endFrame; i++)
            {
                var remaining = distance - Math.Abs(logs[i].WalkedBefore);
                Assert.That(remaining, Is.GreaterThan(threshold - oneFrame), i + " フレーム目: 残りが閾値を過ぎても終わりに入らない");
            }

            var entered = distance - Math.Abs(logs[endFrame].WalkedBefore);
            Assert.That(entered, Is.LessThanOrEqualTo(threshold + oneFrame), "残りが閾値より大きいうちに終わりに入った");

            var movedWhileEnding = 0;
            for (var i = endFrame; i < logs.Count; i++)
            {
                var expected = Math.Max(0f, logs[i].PrevWeight - logs[i].Delta / (float)WalkSequence.FadeOut);
                Assert.That(logs[i].Weight, Is.EqualTo(expected).Within(WeightTolerance), i + " フレーム目");
                if (logs[i].StepX != 0f)
                {
                    movedWhileEnding++;
                }
            }

            Assert.That(movedWhileEnding, Is.GreaterThan(0), "終わりの間に動かない");
        }

        [Test]
        [Description("時刻は歩き出しの時刻から 倍率 × 丸めた delta ずつ進み、クリップの長さを越えても剰余を取らず、次の歩きではそのときの歩き出しの時刻から始まる")]
        public void Advance_AdvancesTimeFromStartTime()
        {
            var k = ScaleFor(0.99);
            var rig = new Rig(Constant(0.99), Monitors(Wide), 1800f, 100f) { ClipLength = 1.2f, StartTime = 0.4f, RestTimes = new[] { 0.4f } };
            var first = rig.StartWalk();
            Assert.That((double)first.Time, Is.InRange(0.4, 0.4 + k * first.Delta + WeightTolerance), "歩き出したフレーム");

            var logs = rig.FinishWalk();
            AssertTimeRate(logs, k, "1 回目");
            Assert.That(logs.Max(l => l.Time), Is.GreaterThan(2f * rig.ClipLength), "クリップの長さで剰余を取った");

            rig.X = 1800.0;
            rig.StartTime = 0.9f;
            var second = rig.StartWalk();
            Assert.That((double)second.Time, Is.InRange(0.9, 0.9 + k * second.Delta + WeightTolerance), "次の歩きの始まり");
        }

        // ClipLength 1 で 0.6 を歩き出しの時刻に、0.1 と 0.5 を待機に近い時刻にして、どれか 1 つを不正にする。
        // 一覧を使えば、0.5 を越えたフレーム（残り約 41 px）で止め始めるので、残りだけの規則（18.75 px）と区別できる
        [TestCase("クリップの長さが 0", 0f, 0.6f, "有効")]
        [TestCase("クリップの長さが負", -1f, 0.6f, "有効")]
        [TestCase("クリップの長さが NaN", float.NaN, 0.6f, "有効")]
        [TestCase("クリップの長さが無限", float.PositiveInfinity, 0.6f, "有効")]
        [TestCase("歩き出しの時刻が NaN", 1f, float.NaN, "有効")]
        [TestCase("歩き出しの時刻が負", 1f, -0.1f, "有効")]
        [TestCase("歩き出しの時刻がクリップの長さと同じ", 1f, 1f, "有効")]
        [TestCase("歩き出しの時刻がクリップの長さより大きい", 1f, 1.6f, "有効")]
        [TestCase("待機に近い時刻の一覧が null", 1f, 0.6f, "null")]
        [TestCase("待機に近い時刻の一覧が空", 1f, 0.6f, "空")]
        [Description("クリップの長さ・歩き出しの時刻・待機に近い時刻の一覧のどれかが不正なら、時刻は 0 から始まり、残りが 全速 × 0.3 ÷ 2 以下になったフレームで止め始める")]
        public void Advance_StartsAtZeroAndStopsByRemaining_WhenClipInputsAreInvalid(string label, float clipLength, float startTime, string restKind)
        {
            // 待ち 15 秒・距離 400 px・左・倍率 0.5
            var rig = new Rig(Constant(0.0), Monitors(Wide), 1870f, 100f)
            {
                ClipLength = clipLength,
                StartTime = startTime,
                RestTimes = restKind == "null" ? null : (restKind == "空" ? new float[0] : new[] { 0.1f, 0.5f }),
            };

            var start = rig.StartWalk();
            var logs = rig.FinishWalk();

            Assert.That((double)start.Time, Is.InRange(0.0, 0.5 * start.Delta + WeightTolerance), label + ": 歩き出したフレームの時刻");
            AssertEndsByRemaining(logs, DistanceFor(0.0), 0.5, label);
        }

        /// <summary>止め始めたフレームが、残りが 全速 × 0.3 ÷ 2 を 1 フレーム分の誤差で切ったフレームであること。</summary>
        private static void AssertEndsByRemaining(List<FrameLog> logs, double distance, double k, string because)
        {
            var threshold = StoppingDistance(Px, k);
            var oneFrame = FullSpeed(Px, k) * Frame * 1.01;
            var endFrame = logs.FindIndex(l => l.Weight < l.PrevWeight);
            Assert.That(endFrame, Is.GreaterThan(0), because + ": 重みが下がらない");
            for (var i = 0; i < endFrame; i++)
            {
                var remaining = distance - Math.Abs(logs[i].WalkedBefore);
                Assert.That(remaining, Is.GreaterThan(threshold - oneFrame), because + ": " + i + " フレーム目で残りが閾値を過ぎても止め始めない");
            }

            var entered = distance - Math.Abs(logs[endFrame].WalkedBefore);
            Assert.That(entered, Is.LessThanOrEqualTo(threshold + oneFrame), because + ": 残り " + entered.ToString("F2") + " px で止め始めた（閾値 " + threshold + " px）");
        }

        // ---- 待機に近い時刻で止め始める ----

        // ClipLength 1・倍率 0.5（クリップは 1 秒に 0.5 秒進む）・全速 125 px/秒・距離 493.75 px・止まるまでの距離 18.75 px。
        // 待機に近い時刻 0.2 と 0.5 は間が 0.3 と 0.7 で非対称。0.2 から歩き出すと、時刻 0.5（約 0.6 秒）・1.2（2.0 秒）・1.5（2.6 秒）で越える。
        //   0.5 を越えたとき: 残り約 433 > 次（1.2）までの 0.7 × 250 + 18.75 = 193.75 → 続ける
        //   1.2 を越えたとき: 残り約 259 > 次（1.5）までの 0.3 × 250 + 18.75 = 93.75 → 続ける
        //   1.5 を越えたとき: 残り約 185 は、次（2.2）までの 175 より大きく、止まるまでの距離を足した 193.75 より小さい → 止め始める
        // 和でなく次までの距離だけで比べると 1.5 では続け、残りが 18.75 px になる時刻 2.16 ごろ（待機に近い時刻ではない）で止め始める
        [TestCase("小さい順", new[] { 0.2f, 0.5f })]
        [TestCase("並びが乱れている", new[] { 0.5f, 0.2f })]
        [TestCase("NaN・負・クリップの長さ以上を含む", new[] { 1.3f, 0.2f, float.NaN, -0.4f, 0.5f, 7f })]
        [Description("自然に終わるときは、時刻が待機に近い時刻を越えたフレームで、次の待機に近い時刻まで進む距離と止まるまでの距離の和が残りより大きければ、次までの距離だけなら残りに届かなくても、そこで重みを下げ始める")]
        public void Advance_StartsSlowingDownAtRestTime_WhenTheNextRestTimeIsOutOfReach(string label, float[] restTimes)
        {
            // 待ち 15 秒・距離 400 + 0.15625 × 600 = 493.75 px・左・倍率 0.5
            const double DistanceRandom = 0.15625;
            var random = new ScriptedRandom(0.0, 0.0, DistanceRandom, 0.0, 0.0);
            var rig = new Rig(random.Next, Monitors(Wide), 1870f, 100f) { ClipLength = 1f, StartTime = 0.2f, RestTimes = restTimes };
            var start = rig.StartWalk();
            var logs = rig.FinishWalk();

            Assert.That((double)start.Time, Is.InRange(0.2, 0.2 + 0.5 * start.Delta + WeightTolerance), label + ": 歩き出しの時刻");
            var endFrame = logs.FindIndex(l => l.Weight < l.PrevWeight);
            Assert.That(endFrame, Is.GreaterThan(0), label + ": 重みが下がらない");
            Assert.That((double)logs[endFrame].Time, Is.InRange(1.5 - WeightTolerance, 1.5 + 2.0 * 0.5 * Frame + WeightTolerance), label + ": 止め始めた時刻が 1.5（待機に近い時刻 0.5）を越えたフレームでない");
            var remaining = DistanceFor(DistanceRandom) - Math.Abs(logs[endFrame].WalkedBefore);
            Assert.That(remaining, Is.InRange(0.7 * FullSpeed(Px) + StepTolerance, 0.7 * FullSpeed(Px) + StoppingDistance(Px, 0.5)), label + ": 止め始めたときの残りが、次までの距離 175 px と、止まるまでの距離との和 193.75 px の間にない");
            Assert.That(Math.Abs(rig.Walked), Is.LessThanOrEqualTo(DistanceFor(DistanceRandom) + StepTolerance), label + ": 距離を超えた");
            Assert.That(Math.Abs(rig.Walked), Is.InRange(320.0, 340.0), label + ": 止まった位置が 1.5 から止まるまでの距離（約 309 + 18.75 px）にない");
        }

        // ClipLength 1・待機に近い時刻 0.2 と 0.7（間はどちらも 0.5、次までの距離 0.5 × 250 = 125 px）・倍率 0.5・距離 493.75 px。
        // 0.2 から歩き出すと、時刻 0.7（1 秒）・1.2（2 秒）・1.7（3 秒）で越え、そのときの残りは約 384・259・135 px。
        // 1.7 での残り約 135 は 125 < 残り ≤ 125 + 18.75 に入るので、そこで止め始める
        [Test]
        [Description("越えたときの残りが、次の待機に近い時刻まで進む距離より大きく、止まるまでの距離との和以下なら、その待機に近い時刻で止め始め、止まる位置は狙った位置を越えず、次までの距離と止まるまでの距離の和より手前にはならない")]
        public void Advance_StartsSlowingDownAtRestTime_WhenOnlyTheStoppingDistanceIsOutOfReach()
        {
            const double DistanceRandom = 0.15625;
            var random = new ScriptedRandom(0.0, 0.0, DistanceRandom, 0.0, 0.0);
            var rig = new Rig(random.Next, Monitors(Wide), 1870f, 100f) { ClipLength = 1f, StartTime = 0.2f, RestTimes = new[] { 0.2f, 0.7f } };
            rig.StartWalk();
            var logs = rig.FinishWalk();

            var distance = DistanceFor(DistanceRandom);
            var toNext = 0.5 * FullSpeed(Px);
            var stopping = StoppingDistance(Px, 0.5);
            var endFrame = logs.FindIndex(l => l.Weight < l.PrevWeight);
            Assert.That(endFrame, Is.GreaterThan(0), "重みが下がらない");
            var crossed = 1.0 + (double)0.7f;
            var end = logs[endFrame];
            Assert.That(end.PrevTime < crossed && end.Time >= crossed, Is.True, "止め始めたのが 1.7（待機に近い時刻 0.7）を越えたフレームでない（" + end.PrevTime + " → " + end.Time + "）");
            var remaining = distance - Math.Abs(end.WalkedBefore);
            Assert.That(remaining, Is.InRange(toNext + StepTolerance, toNext + stopping), "止め始めたときの残りが、次までの距離 125 px と、止まるまでの距離との和 143.75 px の間にない");
            Assert.That(Math.Abs(rig.Walked), Is.LessThanOrEqualTo(distance + StepTolerance), "狙った位置を越えた");
            Assert.That(Math.Abs(rig.Walked), Is.GreaterThanOrEqualTo(distance - (toNext + stopping)), "狙った位置より、次までの距離と止まるまでの距離の和を超えて手前で止まった");
        }

        // ClipLength 2・待機に近い時刻 0.2 と 0.205・倍率 0.5・全速 250 px/秒。0.2 から歩き出すと、最初のフレーム（時刻 0.2 → 約 0.208）で 0.205 を越え、
        // 次は 2.2 なので、比べる和は (2.2 − 0.205) × 250 + 止まるまでの距離 ≈ 517.5 px。最初のフレームの残りは距離そのものなので、
        // 距離の乱数を選んで、残りを和とちょうど等しく（double で一致）、または 1 ulp だけ小さくする
        [TestCase("残りが和とちょうど等しい", 0, false)]
        [TestCase("残りが和より 1 ulp 小さい", -1, true)]
        [Description("越えたときの次の待機に近い時刻まで進む距離と止まるまでの距離の和が残りとちょうど等しければ止め始めず、残りが和より小さければそのフレームで止め始める")]
        public void Advance_StartsSlowingDownAtRestTime_OnlyWhenTheSumIsGreaterThanRemaining(string label, int ulps, bool slowsDown)
        {
            var sum = (((double)2f + (double)0.2f) - (double)0.205f) * FullSpeed(Px) + StoppingDistance(Px, 0.5);
            var target = Offset(sum, ulps);
            var distanceRandom = DistanceRandomFor(target);
            var random = new ScriptedRandom(0.0, 0.0, distanceRandom, 0.0, 0.0);
            var rig = new Rig(random.Next, Monitors(Wide), 1870f, 100f) { ClipLength = 2f, StartTime = 0.2f, RestTimes = new[] { 0.2f, 0.205f } };

            var start = rig.StartWalk();

            Assert.That((double)start.PrevTime, Is.LessThan((double)0.205f), label + ": 前提: 歩き出したフレームの前の時刻が 0.205 より前でない");
            Assert.That((double)start.Time, Is.GreaterThanOrEqualTo((double)0.205f), label + ": 前提: 歩き出したフレームで 0.205 を越えていない");
            if (slowsDown)
            {
                Assert.That(start.Weight, Is.EqualTo(0f), label + ": 和が残りより大きいのに止め始めない");
                Assert.That(rig.Step(Frame).IsWalking, Is.False, label + ": 止め始めた次のフレームで歩きが終わらない");
            }
            else
            {
                Assert.That(start.Weight, Is.GreaterThan(0f), label + ": 和が残りと等しいのに止め始めた");
                Assert.That(rig.Run(1.0).All(l => l.IsWalking && l.Weight >= l.PrevWeight), Is.True, label + ": 次の 1 秒の間に止め始めた");
            }
        }

        /// <summary>value から ulps（負なら小さい方へ）だけ隣の double。value は正。</summary>
        private static double Offset(double value, int ulps)
        {
            return BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(value) + ulps);
        }

        /// <summary>距離 = MinDistance + r × (MaxDistance − MinDistance) が double で distance とちょうど等しくなる r。</summary>
        private static double DistanceRandomFor(double distance)
        {
            var estimate = (distance - (double)WalkSequence.MinDistance) / ((double)WalkSequence.MaxDistance - (double)WalkSequence.MinDistance);
            for (var i = 0; i <= 64; i++)
            {
                foreach (var candidate in new[] { Offset(estimate, i), Offset(estimate, -i) })
                {
                    if (DistanceFor(candidate) == distance)
                    {
                        return candidate;
                    }
                }
            }

            Assert.Fail("距離 " + distance.ToString("R") + " にちょうどなる乱数が見つからない");
            return double.NaN;
        }

        // ClipLength 1・待機に近い時刻 0.99 だけ・歩き出しの時刻 0.01・倍率 0.5・距離 400 px。1/15 秒ずつ進めると時刻は 1 フレームに 1/30 進み、
        // 0.9767 → 1.01 のフレームで 0.99 と周期の境目 1.0 を同時に越える。そのときの残り約 166 < 1 周先までの 250 + 18.75 → 止め始める
        [Test]
        [Description("1 フレームで待機に近い時刻とクリップの周期の境目を同時に越えても、そのフレームで止め始めを判定する")]
        public void Advance_StartsSlowingDownAtRestTime_WhenTheFrameAlsoCrossesTheClipLength()
        {
            var rig = new Rig(Constant(0.0), Monitors(Wide), 1870f, 100f) { ClipLength = 1f, StartTime = 0.01f, RestTimes = new[] { 0.99f } };
            rig.StartWalk(40.0, MaxDelta);
            var logs = rig.FinishWalk(60.0, MaxDelta);

            var endFrame = logs.FindIndex(l => l.Weight < l.PrevWeight);
            Assert.That(endFrame, Is.GreaterThan(0), "重みが下がらない");
            var end = logs[endFrame];
            Assert.That(end.PrevTime < (double)0.99f && end.Time >= 1.0, Is.True, "止め始めたのが 0.99 と 1.0 を同時に越えたフレームでない（" + end.PrevTime + " → " + end.Time + "）");
            Assert.That(Math.Abs(rig.Walked), Is.LessThanOrEqualTo(DistanceFor(0.0) + StepTolerance), "距離を超えた");
        }

        [Test]
        [Description("待機に近い時刻が 1 つだけなら、越えたときの次はクリップ 1 周先の同じ時刻として比べる")]
        public void Advance_UsesOnePeriodAhead_WhenThereIsOnlyOneRestTime()
        {
            // 0.3 から歩き出し、1.3（2.0 秒）で越える。残り約 166 < 1 周 1.0 × 250 + 18.75 = 268.75 → 止め始める
            var rig = new Rig(Constant(0.0), Monitors(Wide), 1870f, 100f) { ClipLength = 1f, StartTime = 0.3f, RestTimes = new[] { 0.3f } };
            rig.StartWalk();
            var logs = rig.FinishWalk();

            var endFrame = logs.FindIndex(l => l.Weight < l.PrevWeight);
            Assert.That(endFrame, Is.GreaterThan(0), "重みが下がらない");
            Assert.That((double)logs[endFrame].Time, Is.InRange(1.3 - WeightTolerance, 1.3 + 2.0 * 0.5 * Frame + WeightTolerance), "止め始めた時刻が 1.3 を越えたフレームでない");
        }

        [Test]
        [Description("歩いている間にどの待機に近い時刻も越えないうちに残りが少なくなれば、残りが 全速 × 0.3 ÷ 2 以下になったフレームで止め始める")]
        public void Advance_StopsByRemaining_WhenNoRestTimeIsCrossedBeforeTheEnd()
        {
            // クリップ 10 秒・待機に近い時刻 0.3 だけ。次に越えるのは 20 秒後で、400 px は約 3.3 秒で歩き終える
            var rig = new Rig(Constant(0.0), Monitors(Wide), 1870f, 100f) { ClipLength = 10f, StartTime = 0.3f, RestTimes = new[] { 0.3f } };
            rig.StartWalk();
            var logs = rig.FinishWalk();

            AssertEndsByRemaining(logs, DistanceFor(0.0), 0.5, "越えない");
        }

        [Test]
        [Description("待機に近い時刻で止め始めても、体の進む向きの端は歩き始めたモニターの端を越えない")]
        public void Advance_StaysInsideMonitor_WhenSlowingDownAtRestTimeNearTheEdge()
        {
            // 幅 800 のモニターに幅 100 の体。隙間は左 300・右 400、距離 400 → 右へ。待機に近い時刻は 0.2 と 0.5
            var rig = new Rig(Constant(0.0), Monitors(Small), 300f, 100f, 100f, 300f) { ClipLength = 1f, StartTime = 0.2f, RestTimes = new[] { 0.2f, 0.5f } };
            var start = rig.StartWalk();
            var logs = rig.FinishWalk();

            Assert.That(start.Direction, Is.EqualTo(1));
            foreach (var log in logs)
            {
                Assert.That((double)log.Body.X + log.Body.Width + log.StepX, Is.LessThanOrEqualTo(800.0 + EdgeTolerance), "右端を越えた");
            }
        }

        [Test]
        [Description("歩き終えて重みが 0 に戻ったあとも、向きは直前の歩きの値のまま")]
        public void Advance_KeepsDirection_AfterWalkEnds()
        {
            var rig = new Rig(Constant(0.0), Monitors(Wide), 1870f, 100f);
            rig.StartWalk();
            rig.FinishWalk();

            var logs = rig.Run(5.0);

            Assert.That(logs.All(l => l.Direction == -1 && l.Weight == 0f && l.StepX == 0f), Is.True);
        }

        // ---- 端 ----

        [Test]
        [Description("体の進む向きの端がモニターの端に着いたら、その歩きは端を越えずに止まる")]
        public void Advance_StopsAtMonitorEdge()
        {
            // 隙間は左 900・右 920 で、どちらも距離 994 に足りないので右へ
            var rig = new Rig(Constant(0.99), Monitors(Primary), 900f, 100f);
            rig.StartWalk();
            var logs = rig.FinishWalk();

            Assert.That(rig.Walk.Direction, Is.EqualTo(1));
            foreach (var log in logs)
            {
                Assert.That((double)log.Body.X + log.Body.Width + log.StepX, Is.LessThanOrEqualTo(1920.0 + EdgeTolerance), "右端を越えた");
            }

            Assert.That(rig.X + rig.Width, Is.InRange(1920.0 - ArrivalTolerance(Px), 1920.0 + EdgeTolerance), "端の手前で止まった");
        }

        [Test]
        [Description("端で止まった次の歩きは、通常の規則なら逆を選ぶ位置からでも反対向きになる")]
        public void Advance_WalksOppositeWay_AfterStoppingAtEdge()
        {
            var rig = new Rig(Constant(0.99), Monitors(Primary), 900f, 100f);
            rig.StartWalk();
            rig.FinishWalk();
            Assert.That(rig.Walk.Direction, Is.EqualTo(1));

            // 隙間は左 200・右 1620。通常の規則なら右だけが距離 994 以上
            rig.X = 200.0;
            var next = rig.StartWalk();

            Assert.That(next.Direction, Is.EqualTo(-1));
        }

        // 右端で止まったあと、体を左端の近くへ移す。通常の規則なら右だけが距離 994 以上
        [TestCase("左へはみ出している（左の隙間 −50）", -50f)]
        [TestCase("左端に接している（左の隙間ちょうど 0）", 0f)]
        [Description("端で止まった次でも、反対向きの隙間が正でなければ（ちょうど 0 を含む）通常の規則で向きを決める")]
        public void Advance_UsesNormalRule_AfterEdgeStop_WhenOppositeGapIsNotPositive(string label, float x)
        {
            var rig = new Rig(Constant(0.99), Monitors(Primary), 900f, 100f);
            rig.StartWalk();
            rig.FinishWalk();
            Assert.That(rig.Walk.Direction, Is.EqualTo(1), "最初の歩きが右端で止まっていない");

            rig.X = x;
            var next = rig.StartWalk();
            var logs = rig.FinishWalk();

            Assert.That(next.Direction, Is.EqualTo(1), label);
            Assert.That(next.StepX >= 0f && logs.All(l => l.StepX >= 0f), Is.True, label + ": 左へ動いた");
        }

        [Test]
        [Description("距離を歩き終えて止まったときは端の扱いをせず、次の向きは通常の規則で決まる")]
        public void Advance_UsesNormalRule_AfterStoppingByDistance()
        {
            var rig = new Rig(Constant(0.99), Monitors(Wide), 1800f, 100f);
            rig.StartWalk();
            rig.FinishWalk();
            Assert.That(rig.Walk.Direction, Is.EqualTo(1));

            rig.X = 1800.0;
            var next = rig.StartWalk();

            Assert.That(next.Direction, Is.EqualTo(1), "左右どちらも許され、向きの乱数 0.99 は右");
        }

        [Test]
        [Description("横に並ぶ 2 枚のモニターでは、歩き始めたモニターの端（隣のモニターとの境目）で止まる")]
        public void Advance_StopsAtBoundaryBetweenSideBySideMonitors()
        {
            // 右のモニターの上で、隙間は左 980・右 840 → 左へ
            var rig = new Rig(Constant(0.99), Monitors(Primary, RightOfPrimary), 2900f, 100f);
            rig.StartWalk();
            var logs = rig.FinishWalk();

            Assert.That(rig.Walk.Direction, Is.EqualTo(-1));
            foreach (var log in logs)
            {
                Assert.That((double)log.Body.X + log.StepX, Is.GreaterThanOrEqualTo(1920.0 - EdgeTolerance), "境目を越えた");
            }

            Assert.That(rig.X, Is.InRange(1920.0 - EdgeTolerance, 1920.0 + ArrivalTolerance(Px)));
        }

        [Test]
        [Description("主モニターの左にある負の座標のモニターでも、そのモニターの端で止まる")]
        public void Advance_StopsAtEdgeOfMonitorWithNegativeCoordinates()
        {
            // 左のモニターの上で、隙間は左 840・右 980 → 右へ、境目 0 で止まる
            var rig = new Rig(Constant(0.99), Monitors(Primary, LeftOfPrimary), -1080f, 100f);
            rig.StartWalk();
            var logs = rig.FinishWalk();

            Assert.That(rig.Walk.Direction, Is.EqualTo(1));
            foreach (var log in logs)
            {
                Assert.That((double)log.Body.X + log.Body.Width + log.StepX, Is.LessThanOrEqualTo(EdgeTolerance), "境目を越えた");
            }

            Assert.That(rig.X + rig.Width, Is.InRange(-ArrivalTolerance(Px), EdgeTolerance));
        }

        [Test]
        [Description("体の中心がどのモニターにもないときは、中心に最も近いモニターの端を使う")]
        public void Advance_UsesNearestMonitor_WhenBodyCenterIsOnNoMonitor()
        {
            // 中心 (1900, 1500)。主モニターまで 420、右のモニターまで約 420.5 → 主モニター。
            // 主モニターなら隙間は左 1850・右 −30 → 左。右のモニターなら左 −70・右 1890 → 右
            var rig = new Rig(Constant(0.99), Monitors(Primary, RightOfPrimary), 1850f, 100f, 1300f, 400f);

            var start = rig.StartWalk();
            var logs = rig.FinishWalk();

            Assert.That(start.Direction, Is.EqualTo(-1));
            Assert.That(start.StepX <= 0f && logs.All(l => l.StepX <= 0f), Is.True, "右へ動いた");
        }

        // 中心 (1920, 1500) は主モニターからも右のモニターからも 420 離れている。
        // 主モニターなら隙間は左 1870・右 −50 → 左。右のモニターなら左 −50・右 1870 → 右
        [TestCase("主モニターが添字 0", true, -1)]
        [TestCase("右のモニターが添字 0", false, 1)]
        [Description("体の中心がどのモニターにもなく、最も近いモニターが 2 枚あるときは、添字の小さい方の端を使う")]
        public void Advance_UsesLowerIndexMonitor_WhenTwoMonitorsAreEquallyNearest(string label, bool primaryFirst, int direction)
        {
            var monitors = primaryFirst ? Monitors(Primary, RightOfPrimary) : Monitors(RightOfPrimary, Primary);
            var rig = new Rig(Constant(0.99), monitors, 1870f, 100f, 1300f, 400f);

            var start = rig.StartWalk();
            var logs = rig.FinishWalk();

            Assert.That(start.Direction, Is.EqualTo(direction), label);
            Assert.That(logs.All(l => Math.Sign(l.StepX) != -direction), Is.True, label + ": 逆へ動いた");
        }

        [Test]
        [Description("体の中心がモニターの境目にあるときは、添字の小さい方のモニターを使う")]
        public void Advance_UsesLowerIndexMonitor_WhenBodyCenterIsOnSharedEdge()
        {
            // 中心 x = 1920。添字 0 の主モニターなら隙間は左 1870・右 −50 → 左。添字 1 の右のモニターなら右へ
            var rig = new Rig(Constant(0.99), Monitors(Primary, RightOfPrimary), 1870f, 100f);

            var start = rig.StartWalk();

            Assert.That(start.Direction, Is.EqualTo(-1));
        }

        [Test]
        [Description("不正なモニターは選ばず、有効なモニターの端を使う")]
        public void Advance_IgnoresInvalidMonitors()
        {
            // 不正な広いモニターを使えば右へ 994 px 歩いて 1994 まで行く
            var monitors = Monitors(new ScreenRect(0f, 0f, 5000f, float.NaN), new ScreenRect(0f, 0f, 5000f, 0f), Primary);
            var rig = new Rig(Constant(0.99), monitors, 900f, 100f);
            rig.StartWalk();
            rig.FinishWalk();

            Assert.That(rig.X + rig.Width, Is.InRange(1920.0 - ArrivalTolerance(Px), 1920.0 + EdgeTolerance));
        }

        [Test]
        [Description("歩いている間にモニターの一覧が変わっても、歩き始めたモニターの端で止まる")]
        public void Advance_KeepsStartMonitor_WhileWalking()
        {
            var rig = new Rig(Constant(0.99), Monitors(Primary), 900f, 100f);
            rig.StartWalk();
            rig.Run(1.0);
            rig.MonitorList = Monitors(Wide);
            rig.FinishWalk();

            Assert.That(rig.X + rig.Width, Is.InRange(1920.0 - ArrivalTolerance(Px), 1920.0 + EdgeTolerance));
        }

        [TestCase("左へはみ出す", -30f, 1)]
        [TestCase("右へはみ出す", 730f, -1)]
        [TestCase("左端に接する", 0f, 1)]
        [TestCase("右端に接する", 700f, -1)]
        [Description("歩き始めに体がモニターの端に接しているか、はみ出していれば、その側へは動かない")]
        public void Advance_NeverMovesTowardSideAlreadyAtOrPastEdge(string label, float x, int direction)
        {
            // 幅 800 のモニターに幅 100 の体。距離は 400 px
            var rig = new Rig(Constant(0.0), Monitors(Small), x, 100f, 100f, 300f);

            var start = rig.StartWalk();
            var logs = rig.FinishWalk();

            Assert.That(start.Direction, Is.EqualTo(direction), label);
            Assert.That(Math.Sign(start.StepX) != -direction && logs.All(l => Math.Sign(l.StepX) != -direction), Is.True, label + ": 端の側へ動いた");
        }

        [Test]
        [Description("左右の隙間がどちらも終わりに入る残り（全速 × 0.3 ÷ 2）より小さくても、歩き出したフレームは歩いていることになり、そのあと端を越えずに歩き終える")]
        public void Advance_StartsWalkingAndStaysInside_WhenGapsAreShorterThanEndingLength()
        {
            // 幅 800 のモニターに幅 780 の体。隙間は左右とも 10 px（倍率 0.5 で終わりに入る残り 18.75 px より小さい）→ 右
            var rig = new Rig(Constant(0.0), Monitors(Small), 10f, 780f, 100f, 300f);
            var start = rig.StartWalk();
            var logs = rig.FinishWalk();

            Assert.That(start.IsWalking, Is.True, "歩き出したフレームで歩いていない");
            Assert.That(start.Direction, Is.EqualTo(1));
            Assert.That(rig.X + rig.Width, Is.LessThanOrEqualTo(800.0 + EdgeTolerance), "右端を越えた");
            Assert.That(start.StepX >= 0f && logs.All(l => l.StepX >= 0f), Is.True, "左へ動いた");
        }

        [Test]
        [Description("進む向きの隙間が終わりに入る残り（全速 × 0.3 ÷ 2）より大きく距離より小さければ、隙間の分だけ動いて端で止まる")]
        public void Advance_MovesTheWholeGapAndStopsAtEdge_WhenGapIsBetweenEndingLengthAndDistance()
        {
            // 幅 800 のモニターに幅 600 の体。隙間は左右とも 100 px（18.75 < 100 < 距離 400）→ 右
            var rig = new Rig(Constant(0.0), Monitors(Small), 100f, 600f, 100f, 300f);
            var start = rig.StartWalk();
            var logs = rig.FinishWalk();

            Assert.That(start.Direction, Is.EqualTo(1));
            foreach (var log in logs)
            {
                Assert.That((double)log.Body.X + log.Body.Width + log.StepX, Is.LessThanOrEqualTo(800.0 + EdgeTolerance), "右端を越えた");
            }

            Assert.That(rig.Walked, Is.InRange(100.0 - ArrivalTolerance(Px), 100.0 + EdgeTolerance), "隙間の分だけ動いていない");
        }

        // ---- 止まる ----

        [TestCase("CanWalk が false")]
        [TestCase("話し始め")]
        [TestCase("px が 0")]
        [TestCase("px が NaN")]
        [TestCase("体の幅が NaN")]
        [TestCase("体の幅が 0")]
        [TestCase("モニターが null")]
        [TestCase("モニターが空")]
        [TestCase("モニターがすべて不正")]
        [Description("歩いている間に歩き出せる状態が崩れると、そのフレームから動かず、重みは delta ÷ 0.3 ずつ下がって 0.3 秒で 0 に戻り、向きは保つ")]
        public void Advance_StopsMovingAndFadesOut_WhenStateBreaksWhileWalking(string kind)
        {
            var rig = new Rig(Constant(0.99), Monitors(Wide), 1800f, 100f);
            rig.StartWalk();
            rig.Run(0.5);
            Assert.That(rig.Walk.Weight, Is.EqualTo(1f).Within(WeightTolerance), "全速で歩いていない");
            var direction = rig.Walk.Direction;
            Break(rig, kind);

            var logs = rig.Run((float)WalkSequence.FadeOut + Frame);
            for (var i = 0; i < logs.Count; i++)
            {
                var log = logs[i];
                Assert.That(log.StepX, Is.EqualTo(0f), kind + ": " + i + " フレーム目に動いた");
                Assert.That(log.IsWalking, Is.False, kind + ": " + i + " フレーム目");
                Assert.That(log.Weight, Is.EqualTo(Math.Max(0f, log.PrevWeight - log.Delta / (float)WalkSequence.FadeOut)).Within(WeightTolerance), kind + ": " + i + " フレーム目の重み");
                Assert.That(log.Direction, Is.EqualTo(direction), kind + ": 向きが変わった");
            }

            Assert.That(rig.Walk.Weight, Is.EqualTo(0f), kind + ": 0.3 秒で 0 に戻らない");
        }

        private static void Break(Rig rig, string kind)
        {
            switch (kind)
            {
                case "CanWalk が false":
                    rig.CanWalk = false;
                    break;
                case "話し始め":
                    rig.Talking = true;
                    break;
                case "px が 0":
                    rig.PixelsPerMeter = 0f;
                    break;
                case "px が NaN":
                    rig.PixelsPerMeter = float.NaN;
                    break;
                case "体の幅が NaN":
                    rig.BodyOverride = new ScreenRect((float)rig.X, rig.Y, float.NaN, rig.Height);
                    break;
                case "体の幅が 0":
                    rig.BodyOverride = new ScreenRect((float)rig.X, rig.Y, 0f, rig.Height);
                    break;
                case "モニターが null":
                    rig.MonitorList = null;
                    break;
                case "モニターが空":
                    rig.MonitorList = Monitors();
                    break;
                case "モニターがすべて不正":
                    rig.MonitorList = InvalidMonitors("すべて不正");
                    break;
                default:
                    throw new ArgumentException(kind);
            }
        }

        [Test]
        [Description("崩れた状態がすぐ戻っても、重みが 0 になるまで動かず歩き出さず、待ちは重みが 0 になってから数える")]
        public void Advance_WaitsForWeightZeroBeforeCountingWait_WhenStateReturnsDuringFadeOut()
        {
            var rig = new Rig(Constant(0.0), Monitors(Wide), 1870f, 100f);
            rig.StartWalk();
            rig.Run(0.5);
            rig.CanWalk = false;
            rig.Step(Frame);
            rig.CanWalk = true;

            var fading = rig.RunUntil(l => l.Weight == 0f, 1.0);
            Assert.That(fading, Is.Not.Null, "重みが 0 に戻らない");
            Assert.That(fading.All(l => l.StepX == 0f && !l.IsWalking), Is.True, "重みが下がる間に動いた・歩き出した");
            Assert.That(fading.Select(l => l.Weight).Zip(fading.Select(l => l.PrevWeight), (w, p) => w < p).All(x => x), Is.True, "重みが下がり続けない");
            var zero = rig.Clock;

            rig.StartWalk();

            Assert.That(rig.Clock - zero, Is.InRange(15.0 - Slack, 15.0 + Slack));
        }

        [Test]
        [Description("条件が崩れて重みを戻す間も、時刻は 倍率 × 丸めた delta ずつ進む")]
        public void Advance_AdvancesTime_WhileFadingOutAfterBreak()
        {
            var rig = new Rig(Constant(0.99), Monitors(Wide), 1800f, 100f);
            rig.StartWalk();
            rig.Run(0.5);
            rig.Talking = true;

            var logs = rig.RunUntil(l => l.Weight == 0f, 1.0);

            Assert.That(logs, Is.Not.Null);
            foreach (var log in logs)
            {
                Assert.That((double)log.Time - log.PrevTime, Is.EqualTo(ScaleFor(0.99) * log.Delta).Within(WeightTolerance));
            }
        }

        // ---- 不変条件 ----

        [TestCase("1 枚・中ほど")]
        [TestCase("1 枚・左端に接する")]
        [TestCase("1 枚・右へはみ出す")]
        [TestCase("横に 2 枚")]
        [TestCase("負の座標を含む・中心が境目")]
        [TestCase("高さの違う 3 枚")]
        [TestCase("中心がどのモニターにもない")]
        [TestCase("狭いモニター・両側の隙間がわずか")]
        [TestCase("不正なモニターを含む")]
        [TestCase("体がモニターより広い")]
        [Description("モニターの並び・歩き始めの位置・体の大きさ・出来事・フレームの長さ・乱数・待機の切れ目・クリップの時刻の入力を変えて長く動かしても、動くのは重みが正で歩いている間だけで、動いたあとの体の進む向きの端は歩き始めたモニターの端を越えず、歩き出すのは待機が切れ目にあるフレームだけで、重みが正の間は待機の押さえを求める")]
        public void Advance_KeepsInvariants_OverLongRandomRuns(string layout)
        {
            var totals = new Totals();
            for (var seed = 1; seed <= 3; seed++)
            {
                RunLongly(layout, seed, totals);
            }

            Assert.That(totals.Walks, Is.GreaterThanOrEqualTo(3), layout + ": 歩き出した回数が少なすぎて確かめになっていない");
            Assert.That(totals.MovingFrames, Is.GreaterThanOrEqualTo(100), layout + ": 動いたフレームが少なすぎる");
            Assert.That(totals.BrokenFrames, Is.GreaterThan(0), layout + ": 状態が崩れたフレームを通っていない");
            Assert.That(totals.BoundaryWaitFrames, Is.GreaterThan(0), layout + ": 切れ目を待つフレームを通っていない");
            Assert.That(totals.WalksFromStartTime, Is.GreaterThan(0), layout + ": 歩き出しの時刻から始まる歩きを通っていない");
            Assert.That(totals.WalksFromZero, Is.GreaterThan(0), layout + ": 不正な入力で 0 から始まる歩きを通っていない");
            if (layout == "狭いモニター・両側の隙間がわずか")
            {
                Assert.That(totals.EdgeArrivals, Is.GreaterThan(0), layout + ": 端に着いたフレームを通っていない");
            }
        }

        private sealed class Totals
        {
            public int Walks;
            public int MovingFrames;
            public int EdgeArrivals;
            public int BrokenFrames;
            public int BoundaryWaitFrames;
            public int WalksFromStartTime;
            public int WalksFromZero;
        }

        /// <summary>クリップの長さ・歩き出しの時刻・待機に近い時刻の一覧の組。Valid は、時刻を歩き出しの時刻から始めるべき組かどうか。</summary>
        private sealed class ClipInputs
        {
            public float Length;
            public float Start;
            public float[] Rests;
            public bool Valid;
        }

        /// <summary>
        /// ほとんどは正しい組（並びの乱れ・NaN・範囲外の値を混ぜた一覧を含む）。ときどきクリップの長さ・歩き出しの時刻・一覧のどれかが不正な組。
        /// 一覧に正しい値が 1 つもない組は作らない（その扱いは計画が決めていない）。
        /// </summary>
        private static ClipInputs NoisyClipInputs(Random random)
        {
            var length = 0.4f + (float)random.NextDouble() * 3f;
            var count = 1 + random.Next(4);
            var rests = new List<float>();
            for (var i = 0; i < count; i++)
            {
                rests.Add((float)random.NextDouble() * length * 0.999f);
            }

            if (random.Next(3) == 0)
            {
                rests.Insert(random.Next(rests.Count + 1), float.NaN);
                rests.Insert(random.Next(rests.Count + 1), -0.25f);
                rests.Insert(random.Next(rests.Count + 1), length + 0.5f);
            }

            var start = rests.Where(r => r >= 0f && r < length).First();
            var inputs = new ClipInputs { Length = length, Start = start, Rests = rests.ToArray(), Valid = true };
            switch (random.Next(12))
            {
                case 0:
                    inputs.Length = float.NaN;
                    inputs.Valid = false;
                    break;
                case 1:
                    inputs.Length = 0f;
                    inputs.Valid = false;
                    break;
                case 2:
                    inputs.Start = length;
                    inputs.Valid = false;
                    break;
                case 3:
                    inputs.Start = float.NaN;
                    inputs.Valid = false;
                    break;
                case 4:
                    inputs.Rests = null;
                    inputs.Valid = false;
                    break;
                case 5:
                    inputs.Rests = new float[0];
                    inputs.Valid = false;
                    break;
            }

            return inputs;
        }

        private sealed class Layout
        {
            public IReadOnlyList<ScreenRect> Monitors;
            public ScreenRect Body;
        }

        private static Layout LayoutNamed(string layout)
        {
            switch (layout)
            {
                case "1 枚・中ほど":
                    return new Layout { Monitors = Monitors(Primary), Body = new ScreenRect(900f, 300f, 100f, 400f) };
                case "1 枚・左端に接する":
                    return new Layout { Monitors = Monitors(Primary), Body = new ScreenRect(0f, 300f, 100f, 400f) };
                case "1 枚・右へはみ出す":
                    return new Layout { Monitors = Monitors(Primary), Body = new ScreenRect(1880f, 300f, 100f, 400f) };
                case "横に 2 枚":
                    return new Layout { Monitors = Monitors(Primary, RightOfPrimary), Body = new ScreenRect(1900f, 300f, 100f, 400f) };
                case "負の座標を含む・中心が境目":
                    return new Layout { Monitors = Monitors(LeftOfPrimary, Primary), Body = new ScreenRect(-60f, 300f, 120f, 400f) };
                case "高さの違う 3 枚":
                    return new Layout
                    {
                        Monitors = Monitors(new ScreenRect(-1280f, 200f, 1280f, 720f), Primary, new ScreenRect(1920f, -300f, 2560f, 1440f)),
                        Body = new ScreenRect(1000f, 300f, 150f, 400f),
                    };
                case "中心がどのモニターにもない":
                    return new Layout { Monitors = Monitors(Primary, new ScreenRect(1920f, 400f, 1920f, 1080f)), Body = new ScreenRect(500f, -800f, 100f, 400f) };
                case "狭いモニター・両側の隙間がわずか":
                    return new Layout { Monitors = Monitors(Small), Body = new ScreenRect(10f, 100f, 780f, 300f) };
                case "不正なモニターを含む":
                    return new Layout
                    {
                        Monitors = Monitors(new ScreenRect(0f, 0f, 0f, 1080f), new ScreenRect(float.NaN, 0f, 1920f, 1080f), Primary, new ScreenRect(5000f, 0f, -100f, 100f)),
                        Body = new ScreenRect(900f, 300f, 100f, 400f),
                    };
                case "体がモニターより広い":
                    return new Layout { Monitors = Monitors(Primary), Body = new ScreenRect(-100f, 300f, 2120f, 400f) };
                default:
                    throw new ArgumentException(layout);
            }
        }

        /// <summary>乱数源: ほとんどは [0,1)、ときどき NaN・範囲外・0・0.5。</summary>
        private static Func<double> NoisyRandom(int seed)
        {
            var random = new Random(seed);
            return () =>
            {
                var pick = random.Next(100);
                if (pick < 3)
                {
                    return double.NaN;
                }

                if (pick < 5)
                {
                    return -0.5;
                }

                if (pick < 7)
                {
                    return 1.5;
                }

                if (pick < 8)
                {
                    return 0.0;
                }

                if (pick < 9)
                {
                    return 0.5;
                }

                return random.NextDouble();
            };
        }

        private static float NoisyDelta(Random random)
        {
            var pick = random.Next(100);
            if (pick < 70)
            {
                return 1f / 60f;
            }

            if (pick < 80)
            {
                return 1f / 30f;
            }

            if (pick < 85)
            {
                return 1f / 144f;
            }

            if (pick < 90)
            {
                return 0.2f;
            }

            if (pick < 91)
            {
                return 1f / 15f;
            }

            if (pick < 94)
            {
                return 0f;
            }

            if (pick < 96)
            {
                return -0.01f;
            }

            if (pick < 98)
            {
                return float.NaN;
            }

            return float.PositiveInfinity;
        }

        private static void RunLongly(string layout, int seed, Totals totals)
        {
            const double Seconds = 1000.0;
            var setup = LayoutNamed(layout);
            var events = new Random(seed * 7919 + layout.Length);
            var rig = new Rig(NoisyRandom(seed * 104729 + layout.Length), setup.Monitors, setup.Body.X, setup.Body.Width, setup.Body.Y, setup.Body.Height);
            var widest = setup.Monitors.Where(IsValidMonitor).Max(m => m.Width);
            var validMonitors = setup.Monitors.Where(IsValidMonitor).ToArray();

            var breakKind = -1;
            var breakUntil = 0.0;
            var nextBreak = Exponential(events, 60.0);
            var nextResize = Exponential(events, 30.0);
            var nextDrag = Exponential(events, 40.0);
            var dragUntil = -1.0;
            var lastTalking = double.NaN;
            double? readySince = 0.0;
            ScreenRect? startMonitor = null;
            var frame = 0;

            // 待機の切れ目: seed 1 はいつも切れ目にある（自作の待機がない）。ほかは、切れ目にある・ない の区間が入れ替わる
            var alwaysAtBoundary = seed == 1;
            var boundaryUntil = 0.0;
            var clip = NoisyClipInputs(events);
            var nextClip = Exponential(events, 20.0);

            // 切れ目を待っているはずかを見るための待ちの始まり。切れ目にあるのに歩き出さなかったフレームでも始め直す（歩き出せずに待ちへ戻ったかもしれない）
            double? holdCheckSince = 0.0;
            var walkScale = double.NaN;

            while (rig.Clock < Seconds)
            {
                frame++;

                // 出来事を決める
                rig.CanWalk = true;
                rig.Talking = false;
                rig.BodyOverride = null;
                rig.MonitorList = setup.Monitors;
                if (alwaysAtBoundary)
                {
                    rig.IdleAtBoundary = true;
                }
                else if (rig.Clock >= boundaryUntil)
                {
                    rig.IdleAtBoundary = events.Next(10) < 3;
                    boundaryUntil = rig.Clock + (rig.IdleAtBoundary ? Exponential(events, 0.3) : Exponential(events, 4.0));
                }

                if (rig.Clock >= nextClip && rig.Walk.Weight == 0f)
                {
                    clip = NoisyClipInputs(events);
                    nextClip = rig.Clock + Exponential(events, 20.0);
                }

                rig.ClipLength = clip.Length;
                rig.StartTime = clip.Start;
                rig.RestTimes = clip.Rests;
                if (breakKind < 0 && rig.Clock >= nextBreak)
                {
                    breakKind = events.Next(8);
                    breakUntil = rig.Clock + 0.05 + events.NextDouble() * 3.0;
                    nextBreak = breakUntil + Exponential(events, 60.0);
                }

                if (breakKind >= 0 && rig.Clock >= breakUntil)
                {
                    breakKind = -1;
                }

                if (rig.Clock >= nextResize)
                {
                    rig.PixelsPerMeter = 80f + (float)events.NextDouble() * 820f;
                    rig.Width = 20f + (float)events.NextDouble() * (widest * 1.2f - 20f);
                    nextResize = rig.Clock + Exponential(events, 30.0);
                }

                if (rig.Clock >= nextDrag && rig.Walk.Weight == 0f)
                {
                    // ドラッグ: 待機から外れ、そのあいだに体が別の位置（モニターの外を含む）へ移る
                    var target = validMonitors[events.Next(validMonitors.Length)];
                    rig.X = target.X - 200.0 + events.NextDouble() * (target.Width + 400.0 - rig.Width);
                    dragUntil = rig.Clock + 0.2;
                    nextDrag = rig.Clock + Exponential(events, 40.0);
                }

                if (rig.Clock < dragUntil)
                {
                    rig.CanWalk = false;
                }

                var px = rig.PixelsPerMeter;
                switch (breakKind)
                {
                    case 0:
                        rig.CanWalk = false;
                        break;
                    case 1:
                        rig.Talking = true;
                        break;
                    case 2:
                        rig.PixelsPerMeter = 0f;
                        break;
                    case 3:
                        rig.PixelsPerMeter = float.NaN;
                        break;
                    case 4:
                        rig.BodyOverride = new ScreenRect((float)rig.X, rig.Y, float.NaN, rig.Height);
                        break;
                    case 5:
                        rig.MonitorList = null;
                        break;
                    case 6:
                        rig.MonitorList = Monitors();
                        break;
                    case 7:
                        rig.PixelsPerMeter = float.NegativeInfinity;
                        break;
                }

                var log = rig.Step(NoisyDelta(events));
                rig.PixelsPerMeter = px;

                var walkable = rig.CanWalk && !rig.Talking && log.Px > 0f && !float.IsInfinity(log.Px) && IsValidBody(log.Body) && HasValidMonitor(log.Monitors);
                if (rig.Talking)
                {
                    lastTalking = rig.Clock;
                }

                var holdOver = double.IsNaN(lastTalking) || rig.Clock - lastTalking >= (float)WalkSequence.TalkHoldSeconds;

                // 毎フレーム 150 万回近く確かめるので、メッセージは破れたときだけ組み立てる
                void Require(bool ok, string what)
                {
                    if (!ok)
                    {
                        Assert.Fail(layout + " seed " + seed + " frame " + frame + " t " + rig.Clock.ToString("F3") + ": " + what + " " + Describe(log));
                    }
                }

                // 出力の範囲
                Require(!float.IsNaN(log.Weight) && log.Weight >= 0f && log.Weight <= 1f, "Weight が 0〜1 にない");
                Require(IsFinite(log.StepX), "StepX が有限でない");
                Require(IsFinite(log.Time), "Time が有限でない");
                Require(log.Direction == -1 || log.Direction == 1, "Direction が −1 / +1 でない");

                // 重みは跳ばない
                var maxRate = 1f / Math.Min((float)WalkSequence.FadeIn, (float)WalkSequence.FadeOut);
                Require(Math.Abs(log.Weight - log.PrevWeight) <= log.Delta * maxRate + WeightTolerance, "重みが跳んだ");

                // 歩き出すのは、重みが 0 に戻っていて、歩き出せる状態が 15 秒以上続いたあとだけ
                if (log.Started)
                {
                    Require(log.PrevWeight == 0f, "重みが 0 に戻る前に歩き出した");
                    Require(walkable, "歩き出せる状態でないのに歩き出した");
                    Require(readySince.HasValue, "待ちが始まっていないのに歩き出した");
                    Require(rig.Clock - readySince.Value >= (double)WalkSequence.MinWaitSeconds - 3.0 * MaxDelta, "待ちが 15 秒に満たない（待ちの始まり " + readySince.Value + "）");
                    startMonitor = StartMonitor(log.Body, log.Monitors);
                    Require(startMonitor.HasValue, "歩き始めのモニターがない");
                    Require(log.IdleAtBoundary, "待機が切れ目にないのに歩き出した");
                    totals.Walks++;

                    // 歩き出したフレームの時刻 = 歩き出しの時刻（不正な組なら 0）+ 倍率 × delta
                    var from = clip.Valid ? clip.Start : 0f;
                    var advanced = (double)log.Time - from;
                    Require(advanced >= -WeightTolerance && advanced <= (double)WalkSequence.MaxSpeedScale * log.Delta + WeightTolerance, "歩き出したフレームの時刻が " + from + " から始まっていない（" + log.Time + "）");
                    totals.WalksFromStartTime += clip.Valid ? 1 : 0;
                    totals.WalksFromZero += clip.Valid ? 0 : 1;
                    walkScale = log.Delta > 0f ? advanced / log.Delta : double.NaN;
                }

                // 時刻は 1 秒に倍率の秒ずつ進み、倍率は 0.5〜0.75 で、その歩きの間は変わらない
                if (!log.Started && log.Delta > 0f && (log.PrevWeight > 0f || log.Weight > 0f))
                {
                    var rate = ((double)log.Time - log.PrevTime) / log.Delta;
                    Require(rate >= (double)WalkSequence.MinSpeedScale - 2e-3 && rate <= (double)WalkSequence.MaxSpeedScale + 2e-3, "時刻の進みが倍率 0.5〜0.75 にない（" + rate + "）");
                    if (double.IsNaN(walkScale))
                    {
                        walkScale = rate;
                    }

                    Require(Math.Abs(rate - walkScale) <= 2e-3, "歩きの途中で時刻の進みが変わった（" + walkScale + " → " + rate + "）");
                }

                // 重みが正の間は待機の押さえを求め、歩き出せる状態が崩れて重みが 0 なら求めない
                if (log.Weight > 0f)
                {
                    Require(log.HoldIdle, "重みが正なのに待機の押さえを求めない");
                }
                else if (!walkable)
                {
                    Require(!log.HoldIdle, "歩き出せる状態が崩れて重みが 0 なのに待機の押さえを求めた");
                }
                else if (log.HoldIdle && !log.IsWalking)
                {
                    totals.BoundaryWaitFrames++;
                    Require(holdOver && readySince.HasValue && rig.Clock - readySince.Value >= (double)WalkSequence.MinWaitSeconds - 3.0 * MaxDelta, "待ちが終わる前に待機の押さえを求めた");
                }

                // 待ちが 30 秒を越えても切れ目がなければ、切れ目を待っている
                if (walkable && holdOver && log.Weight == 0f && !log.IsWalking && !log.IdleAtBoundary && holdCheckSince.HasValue
                    && rig.Clock - holdCheckSince.Value >= (double)WalkSequence.MaxWaitSeconds + 3.0 * MaxDelta)
                {
                    Require(log.HoldIdle, "待ちが終わって切れ目を待っているのに待機の押さえを求めない");
                }

                // 崩れたフレームからは動かず、重みは 0.3 秒の割合で下がる
                if (!walkable)
                {
                    totals.BrokenFrames++;
                    Require(log.StepX == 0f, "状態が崩れているのに動いた");
                    Require(!log.IsWalking, "状態が崩れているのに歩いている");
                    if (log.PrevWeight > 0f)
                    {
                        var expected = Math.Max(0f, log.PrevWeight - log.Delta / (float)WalkSequence.FadeOut);
                        Require(Math.Abs(log.Weight - expected) <= WeightTolerance, "重みの戻り方が delta ÷ 0.3 ずつでない");
                    }
                }

                // 動くのは重みが正で歩いている間だけ、向きのとおりに、全速 × 重み × delta まで、歩き始めたモニターの端まで
                if (log.StepX != 0f)
                {
                    totals.MovingFrames++;
                    Require(log.IsWalking, "歩いていないのに動いた");
                    Require(log.Weight > 0f, "重み 0 で動いた");
                    Require(Math.Sign(log.StepX) == log.Direction, "向きと逆に動いた");
                    var scale = double.IsNaN(walkScale) ? (double)WalkSequence.MaxSpeedScale : walkScale;
                    Require(Math.Abs((double)log.StepX) <= FullSpeed(log.Px, scale) * log.Weight * log.Delta + StepTolerance, "速すぎる（倍率 " + scale + "）");
                    Require(startMonitor.HasValue, "歩き出していないのに動いた");
                    var monitor = startMonitor.Value;
                    if (log.StepX > 0f)
                    {
                        var right = (double)log.Body.X + log.Body.Width + log.StepX;
                        var bound = (double)monitor.X + monitor.Width;
                        Require(right <= bound + EdgeTolerance, "右端が歩き始めたモニターの右端 " + bound + " を越えた");
                        if (bound - right <= 1.0)
                        {
                            totals.EdgeArrivals++;
                        }
                    }
                    else
                    {
                        var left = (double)log.Body.X + log.StepX;
                        Require(left >= monitor.X - EdgeTolerance, "左端が歩き始めたモニターの左端 " + monitor.X + " を越えた");
                        if (left - monitor.X <= 1.0)
                        {
                            totals.EdgeArrivals++;
                        }
                    }
                }

                // 待ちの始まりを数える（崩れたら消え、重みが 0 で歩いていなければ始まる）
                if (!walkable || !holdOver || log.IsWalking || log.Weight > 0f)
                {
                    readySince = null;
                    holdCheckSince = null;
                }
                else
                {
                    if (!readySince.HasValue)
                    {
                        readySince = rig.Clock;
                    }

                    if (!holdCheckSince.HasValue || log.IdleAtBoundary)
                    {
                        holdCheckSince = rig.Clock;
                    }
                }

                if (!log.IsWalking && log.Weight == 0f)
                {
                    walkScale = double.NaN;
                }
            }
        }

        private static string Describe(FrameLog log)
        {
            return "[体 X " + log.Body.X + " 幅 " + log.Body.Width + "、px " + log.Px + "、delta " + log.Delta
                + "、StepX " + log.StepX + "、重み " + log.PrevWeight + " → " + log.Weight + "、向き " + log.Direction
                + "、歩いている " + log.PrevWalking + " → " + log.IsWalking + "、時刻 " + log.PrevTime + " → " + log.Time
                + "、切れ目 " + log.IdleAtBoundary + "、押さえ " + log.PrevHoldIdle + " → " + log.HoldIdle + "]";
        }

        private static double Exponential(Random random, double mean)
        {
            return -mean * Math.Log(1.0 - random.NextDouble());
        }
    }
}
