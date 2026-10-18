using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace VRMate.Tests
{
    /// <summary>
    /// say の列に、表情とモーションの動作（SetExpression / PlayMotion / StopMotion）がどう並ぶか。
    /// </summary>
    public class SpeechSequencerCueTests
    {
        private static readonly string ValidAudio = WavBuilder.Pcm16Base64(new short[] { 0, 1000, -1000, 2000, -2000, 0 }, channels: 1, sampleRate: 24000);
        private static readonly string NotWavAudio = Convert.ToBase64String(Encoding.UTF8.GetBytes("not a wav file, only text"));

        private enum SayKind
        {
            TextOnly,
            Audio,
            BrokenAudio,
            NonStringAudio,
        }

        private static readonly SayKind[] AllSayKinds = { SayKind.TextOnly, SayKind.Audio, SayKind.BrokenAudio, SayKind.NonStringAudio };

        private static SayMessage Say(SayKind kind, long id, string expression = null, string motion = null, long turn = 1, string text = null)
        {
            text = text ?? "say " + id;
            switch (kind)
            {
                case SayKind.TextOnly:
                    return new SayMessage(id, turn, text, false, null, null, expression, motion);
                case SayKind.Audio:
                    return new SayMessage(id, turn, text, true, ValidAudio, null, expression, motion);
                case SayKind.BrokenAudio:
                    return new SayMessage(id, turn, text, true, NotWavAudio, null, expression, motion);
                default:
                    // audio が文字列でなかった say
                    return new SayMessage(id, turn, text, true, null, null, expression, motion);
            }
        }

        private static List<SpeechAction> Drain(SpeechSequencer sequencer)
        {
            var actions = sequencer.DrainActions();
            Assert.That(actions, Is.Not.Null);
            return actions.ToList();
        }

        private static string Describe(IEnumerable<SpeechAction> actions)
        {
            return "[" + string.Join(", ", actions.Select(Describe)) + "]";
        }

        private static string Describe(SpeechAction action)
        {
            var play = action as PlayAudio;
            if (play != null)
            {
                return "PlayAudio(" + play.Id + ")";
            }

            if (action is StopAudio)
            {
                return "StopAudio";
            }

            var append = action as AppendText;
            if (append != null)
            {
                return "AppendText(" + append.Text + ")";
            }

            var send = action as SendMessage;
            if (send != null)
            {
                var json = TestJson.ReadObject(send.Json);
                return (string)json["type"] + "(" + (long)json["id"] + (json["error"] != null ? ",error" : string.Empty) + ")";
            }

            var expression = action as SetExpression;
            if (expression != null)
            {
                return "SetExpression(" + expression.Name + ")";
            }

            var motion = action as PlayMotion;
            if (motion != null)
            {
                return "PlayMotion(" + motion.Name + ")";
            }

            if (action is StopMotion)
            {
                return "StopMotion";
            }

            return action == null ? "null" : action.GetType().Name;
        }

        private static void AssertActions(IReadOnlyList<SpeechAction> actions, params string[] expected)
        {
            Assert.That(actions.Select(Describe).ToArray(), Is.EqualTo(expected), Describe(actions));
        }

        // ---- say の再生を始めるとき ----

        [Test]
        public void OnSay_EmitsExpressionThenMotionRightBeforeAppendText_WhenTextOnlySayHasBoth()
        {
            var sequencer = new SpeechSequencer();

            sequencer.OnSay(Say(SayKind.TextOnly, 1, "happy", "wave"));

            AssertActions(Drain(sequencer), "SetExpression(happy)", "PlayMotion(wave)", "AppendText(say 1)", "started(1)", "played(1)");
        }

        [Test]
        public void OnSay_EmitsCuesAfterPlayAudioAndRightBeforeAppendText_WhenAudioSayHasBoth()
        {
            var sequencer = new SpeechSequencer();

            sequencer.OnSay(Say(SayKind.Audio, 2, "sad", "bow"));

            AssertActions(Drain(sequencer), "PlayAudio(2)", "SetExpression(sad)", "PlayMotion(bow)", "AppendText(say 2)", "started(2)");
        }

        [Test]
        public void OnSay_EmitsCuesRightBeforeAppendText_WhenAudioCannotBeDecoded()
        {
            foreach (var kind in new[] { SayKind.BrokenAudio, SayKind.NonStringAudio })
            {
                var sequencer = new SpeechSequencer();

                sequencer.OnSay(Say(kind, 3, "angry", "stomp"));

                var actions = Drain(sequencer);
                Assert.That(actions.Take(3).Select(Describe).ToArray(), Is.EqualTo(new[] { "SetExpression(angry)", "PlayMotion(stomp)", "AppendText(say 3)" }), kind + " " + Describe(actions));
                Assert.That(actions.Count, Is.EqualTo(4), kind + " " + Describe(actions));
                Assert.That(Describe(actions[3]), Is.EqualTo("played(3,error)"), kind + " " + Describe(actions));
            }
        }

        [Test]
        public void OnSay_EmitsOnlyExpression_WhenSayHasExpressionOnly()
        {
            foreach (var kind in AllSayKinds)
            {
                var sequencer = new SpeechSequencer();

                sequencer.OnSay(Say(kind, 1, expression: "relaxed"));

                var actions = Drain(sequencer);
                var appendIndex = actions.FindIndex(a => a is AppendText);
                Assert.That(appendIndex, Is.GreaterThan(0), kind + " " + Describe(actions));
                Assert.That(Describe(actions[appendIndex - 1]), Is.EqualTo("SetExpression(relaxed)"), kind + " " + Describe(actions));
                Assert.That(actions.Count(a => a is SetExpression), Is.EqualTo(1), kind + " " + Describe(actions));
                Assert.That(actions.Any(a => a is PlayMotion || a is StopMotion), Is.False, kind + " " + Describe(actions));
            }
        }

        [Test]
        public void OnSay_EmitsOnlyMotion_WhenSayHasMotionOnly()
        {
            foreach (var kind in AllSayKinds)
            {
                var sequencer = new SpeechSequencer();

                sequencer.OnSay(Say(kind, 1, motion: "VRMA_02"));

                var actions = Drain(sequencer);
                var appendIndex = actions.FindIndex(a => a is AppendText);
                Assert.That(appendIndex, Is.GreaterThan(0), kind + " " + Describe(actions));
                Assert.That(Describe(actions[appendIndex - 1]), Is.EqualTo("PlayMotion(VRMA_02)"), kind + " " + Describe(actions));
                Assert.That(actions.Count(a => a is PlayMotion), Is.EqualTo(1), kind + " " + Describe(actions));
                Assert.That(actions.Any(a => a is SetExpression || a is StopMotion), Is.False, kind + " " + Describe(actions));
            }
        }

        [Test]
        public void OnSay_EmitsNoCueActions_WhenSayHasNeither()
        {
            foreach (var kind in AllSayKinds)
            {
                var sequencer = new SpeechSequencer();

                sequencer.OnSay(Say(kind, 1));

                var actions = Drain(sequencer);
                Assert.That(actions.Any(a => a is SetExpression || a is PlayMotion || a is StopMotion), Is.False, kind + " " + Describe(actions));
            }
        }

        [Test]
        public void OnSay_EmitsNeutralExpression_WhenSayExpressionIsNeutral()
        {
            var sequencer = new SpeechSequencer();

            sequencer.OnSay(Say(SayKind.TextOnly, 1, "neutral"));

            AssertActions(Drain(sequencer), "SetExpression(neutral)", "AppendText(say 1)", "started(1)", "played(1)");
        }

        [Test]
        public void OnSay_EmitsQueuedSayCuesOnlyWhenItStarts_WhenSayWaitsBehindPlayingSay()
        {
            var sequencer = new SpeechSequencer();
            sequencer.OnSay(Say(SayKind.Audio, 1));
            Drain(sequencer);

            sequencer.OnSay(Say(SayKind.Audio, 2, "surprised", "jump"));
            sequencer.OnSay(Say(SayKind.TextOnly, 3, "sad"));

            Assert.That(Drain(sequencer), Is.Empty, "待っている say の表情・モーションは、始まるまで出さない");

            sequencer.OnPlaybackFinished(1);
            AssertActions(Drain(sequencer), "played(1)", "PlayAudio(2)", "SetExpression(surprised)", "PlayMotion(jump)", "AppendText(say 2)", "started(2)");

            sequencer.OnPlaybackFinished(2);
            AssertActions(Drain(sequencer), "played(2)", "SetExpression(sad)", "AppendText(say 3)", "started(3)", "played(3)");
        }

        [Test]
        public void OnSay_EmitsEachSayCuesBeforeItsOwnText_WhenTextOnlySaysRunBackToBack()
        {
            var sequencer = new SpeechSequencer();
            sequencer.OnSay(Say(SayKind.Audio, 1));
            sequencer.OnSay(Say(SayKind.TextOnly, 2, "happy"));
            sequencer.OnSay(Say(SayKind.BrokenAudio, 3, motion: "wave"));
            sequencer.OnSay(Say(SayKind.TextOnly, 4, "angry", "stomp"));
            Drain(sequencer);

            sequencer.OnPlaybackFinished(1);

            AssertActions(
                Drain(sequencer),
                "played(1)",
                "SetExpression(happy)",
                "AppendText(say 2)",
                "started(2)",
                "played(2)",
                "PlayMotion(wave)",
                "AppendText(say 3)",
                "played(3,error)",
                "SetExpression(angry)",
                "PlayMotion(stomp)",
                "AppendText(say 4)",
                "started(4)",
                "played(4)");
        }

        [Test]
        public void OnSay_NeverEmitsCues_WhenSayIsRefusedBecauseTooManyAreWaiting()
        {
            var sequencer = new SpeechSequencer(maxPending: 1);
            sequencer.OnSay(Say(SayKind.Audio, 1));
            sequencer.OnSay(Say(SayKind.Audio, 2));
            Drain(sequencer);

            sequencer.OnSay(Say(SayKind.TextOnly, 3, "happy", "wave"));
            var refused = Drain(sequencer);

            AssertActions(refused, "played(3,error)");

            sequencer.OnPlaybackFinished(1);
            sequencer.OnPlaybackFinished(2);
            var later = Drain(sequencer);
            Assert.That(later.Any(a => a is SetExpression || a is PlayMotion), Is.False, Describe(later));

            sequencer.OnCancel();
            Assert.That(Drain(sequencer), Is.Empty, "断った say の表情・モーションは出していないので、戻すものも無い");
        }

        [Test]
        public void OnCancel_NeverEmitsCuesOfDroppedSays_AndOnlyStopsAudio_WhenOnlyWaitingSaysHadCues()
        {
            foreach (var disconnect in new[] { false, true })
            {
                var sequencer = new SpeechSequencer();
                sequencer.OnSay(Say(SayKind.Audio, 1));
                sequencer.OnSay(Say(SayKind.TextOnly, 2, "happy", "wave"));
                sequencer.OnSay(Say(SayKind.Audio, 3, "sad", "bow"));
                Drain(sequencer);

                if (disconnect)
                {
                    sequencer.OnDisconnected();
                }
                else
                {
                    sequencer.OnCancel();
                }

                AssertActions(Drain(sequencer), "StopAudio");

                sequencer.OnPlaybackFinished(1);
                sequencer.OnPlaybackFinished(3);
                Assert.That(Drain(sequencer), Is.Empty, disconnect ? "disconnect" : "cancel");
            }
        }

        // ---- 単独の表情・モーション ----

        [Test]
        public void OnExpression_EmitsSetExpressionImmediately_WhenIdle()
        {
            foreach (var name in new[] { "happy", "angry", "sad", "relaxed", "surprised", "neutral" })
            {
                var sequencer = new SpeechSequencer();

                sequencer.OnExpression(name);

                AssertActions(Drain(sequencer), "SetExpression(" + name + ")");
            }
        }

        [Test]
        public void OnMotion_EmitsPlayMotionImmediately_WhenIdle()
        {
            var sequencer = new SpeechSequencer();

            sequencer.OnMotion("VRMA_02");

            AssertActions(Drain(sequencer), "PlayMotion(VRMA_02)");
        }

        [Test]
        public void OnExpressionAndOnMotion_EmitImmediatelyWithoutTouchingTheQueue_WhenSayIsPlayingAndOthersWait()
        {
            var sequencer = new SpeechSequencer();
            sequencer.OnSay(Say(SayKind.Audio, 1));
            sequencer.OnSay(Say(SayKind.Audio, 2));
            Drain(sequencer);

            sequencer.OnExpression("happy");
            sequencer.OnMotion("wave");

            AssertActions(Drain(sequencer), "SetExpression(happy)", "PlayMotion(wave)");
            Assert.That(sequencer.IsPlaying, Is.True);

            sequencer.OnPlaybackFinished(1);
            AssertActions(Drain(sequencer), "played(1)", "PlayAudio(2)", "AppendText(say 2)", "started(2)");
        }

        [Test]
        public void OnExpression_EmitsNothing_WhenNameIsNotAnExpression()
        {
            var names = new[] { null, string.Empty, "Happy", " happy", "happy ", "joy", "custom", "wave", "happy" + (char)0, "ｈａｐｐｙ" };
            var sequencer = new SpeechSequencer();

            foreach (var name in names)
            {
                sequencer.OnExpression(name);
            }

            Assert.That(Drain(sequencer), Is.Empty);
            sequencer.OnCancel();
            Assert.That(Drain(sequencer), Is.Empty, "出していない表情は戻さない");
        }

        [Test]
        public void OnMotion_EmitsNothing_WhenNameIsNotAWellFormedMotion()
        {
            var names = new[] { null, string.Empty, "..", "../wave", "a/b", "a\\b", "C:wave", "wave.vrma", "NUL", "con", "COM1", " wave", "wave" + (char)0, "ｗａｖｅ", new string('a', 65) };
            var sequencer = new SpeechSequencer();

            foreach (var name in names)
            {
                sequencer.OnMotion(name);
            }

            Assert.That(Drain(sequencer), Is.Empty);
            sequencer.OnDisconnected();
            Assert.That(Drain(sequencer), Is.Empty, "出していないモーションは止めない");
        }

        // ---- 取り消し・切断で戻す ----

        private enum Attached
        {
            Nothing,
            ExpressionOnly,
            MotionOnly,
            Both,
        }

        private enum Via
        {
            Say,
            Standalone,
        }

        private static void Attach(SpeechSequencer sequencer, Attached attached, Via via)
        {
            var expression = attached == Attached.ExpressionOnly || attached == Attached.Both ? "happy" : null;
            var motion = attached == Attached.MotionOnly || attached == Attached.Both ? "wave" : null;
            if (via == Via.Say)
            {
                sequencer.OnSay(Say(SayKind.TextOnly, 100, expression, motion));
                return;
            }

            if (expression != null)
            {
                sequencer.OnExpression(expression);
            }

            if (motion != null)
            {
                sequencer.OnMotion(motion);
            }
        }

        private static string[] ExpectedRestore(bool playing, Attached attached)
        {
            var expected = new List<string>();
            if (playing)
            {
                expected.Add("StopAudio");
            }

            if (attached == Attached.ExpressionOnly || attached == Attached.Both)
            {
                expected.Add("SetExpression(neutral)");
            }

            if (attached == Attached.MotionOnly || attached == Attached.Both)
            {
                expected.Add("StopMotion");
            }

            return expected.ToArray();
        }

        [Test]
        public void OnCancelAndOnDisconnected_RestoreOnlyWhatWasEmitted_ForEveryAttachedStateAndPlayback()
        {
            foreach (var disconnect in new[] { false, true })
            {
                foreach (var playing in new[] { false, true })
                {
                    foreach (Attached attached in Enum.GetValues(typeof(Attached)))
                    {
                        foreach (Via via in Enum.GetValues(typeof(Via)))
                        {
                            var sequencer = new SpeechSequencer();
                            Attach(sequencer, attached, via);
                            if (playing)
                            {
                                sequencer.OnSay(Say(SayKind.Audio, 1));
                                sequencer.OnSay(Say(SayKind.TextOnly, 2, "sad", "bow"));
                            }

                            Drain(sequencer);

                            if (disconnect)
                            {
                                sequencer.OnDisconnected();
                            }
                            else
                            {
                                sequencer.OnCancel();
                            }

                            var because = (disconnect ? "disconnect" : "cancel") + " playing=" + playing + " attached=" + attached + " via=" + via;
                            var actions = Drain(sequencer);
                            Assert.That(actions.Select(Describe).ToArray(), Is.EqualTo(ExpectedRestore(playing, attached)), because + " " + Describe(actions));
                        }
                    }
                }
            }
        }

        [Test]
        public void OnCancel_RestoresExpressionAndStopsMotion_WhenPlayingSayBroughtThem()
        {
            var sequencer = new SpeechSequencer();
            sequencer.OnSay(Say(SayKind.Audio, 1, "angry", "stomp"));
            Drain(sequencer);

            sequencer.OnCancel();

            AssertActions(Drain(sequencer), "StopAudio", "SetExpression(neutral)", "StopMotion");
        }

        [Test]
        public void OnCancel_RestoresOnlyOnce_WhenCalledTwice()
        {
            foreach (var disconnect in new[] { false, true })
            {
                var sequencer = new SpeechSequencer();
                sequencer.OnExpression("sad");
                sequencer.OnMotion("bow");
                Drain(sequencer);

                if (disconnect)
                {
                    sequencer.OnDisconnected();
                    Drain(sequencer);
                    sequencer.OnDisconnected();
                }
                else
                {
                    sequencer.OnCancel();
                    Drain(sequencer);
                    sequencer.OnCancel();
                }

                Assert.That(Drain(sequencer), Is.Empty, disconnect ? "disconnect" : "cancel");
            }
        }

        [Test]
        public void OnCancel_DoesNotRestoreExpression_AfterNeutralWasEmitted()
        {
            var ways = new Action<SpeechSequencer>[]
            {
                s => s.OnExpression("neutral"),
                s => s.OnSay(Say(SayKind.TextOnly, 5, "neutral")),
                s => s.OnCancel(),
            };

            for (var i = 0; i < ways.Length; i++)
            {
                var sequencer = new SpeechSequencer();
                sequencer.OnSay(Say(SayKind.TextOnly, 4, "happy"));
                ways[i](sequencer);
                Drain(sequencer);

                sequencer.OnCancel();

                Assert.That(Drain(sequencer), Is.Empty, "neutral を出した経路 " + i);
            }
        }

        [Test]
        public void OnCancel_RestoresExpression_WhenAnotherExpressionFollowsNeutral()
        {
            var sequencer = new SpeechSequencer();
            sequencer.OnExpression("happy");
            sequencer.OnExpression("neutral");
            sequencer.OnSay(Say(SayKind.TextOnly, 1, "surprised"));
            Drain(sequencer);

            sequencer.OnCancel();

            AssertActions(Drain(sequencer), "SetExpression(neutral)");
        }

        [Test]
        public void OnCancel_DoesNothing_WhenOnlyNeutralWasEverEmitted()
        {
            var sequencer = new SpeechSequencer();
            sequencer.OnExpression("neutral");
            sequencer.OnSay(Say(SayKind.TextOnly, 1, "neutral"));
            Drain(sequencer);

            sequencer.OnCancel();
            sequencer.OnDisconnected();

            Assert.That(Drain(sequencer), Is.Empty);
        }

        [Test]
        public void OnCancel_StopsMotionOnce_WhenSeveralMotionsWereEmitted()
        {
            var sequencer = new SpeechSequencer();
            sequencer.OnMotion("wave");
            sequencer.OnSay(Say(SayKind.TextOnly, 1, motion: "bow"));
            sequencer.OnMotion("jump");
            Drain(sequencer);

            sequencer.OnCancel();

            AssertActions(Drain(sequencer), "StopMotion");
        }

        [Test]
        public void OnCancel_StopsMotionAgain_WhenMotionIsPlayedAfterEarlierStop()
        {
            var sequencer = new SpeechSequencer();
            sequencer.OnMotion("wave");
            sequencer.OnCancel();
            Drain(sequencer);
            sequencer.OnMotion("bow");
            sequencer.OnExpression("happy");
            Drain(sequencer);

            sequencer.OnDisconnected();

            AssertActions(Drain(sequencer), "SetExpression(neutral)", "StopMotion");
        }

        [Test]
        public void OnSay_PlaysWithCuesNormally_AfterCancelRestored()
        {
            var sequencer = new SpeechSequencer();
            sequencer.OnSay(Say(SayKind.Audio, 1, "happy", "wave"));
            sequencer.OnCancel();
            Drain(sequencer);

            sequencer.OnSay(Say(SayKind.Audio, 2, "sad", "bow"));

            AssertActions(Drain(sequencer), "PlayAudio(2)", "SetExpression(sad)", "PlayMotion(bow)", "AppendText(say 2)", "started(2)");
        }

        // ---- 不変条件（ランダムな操作列） ----

        private static readonly string[] RandomExpressions = { null, null, "happy", "angry", "sad", "relaxed", "surprised", "neutral" };
        private static readonly string[] RandomMotions = { null, null, "wave", "bow", "VRMA_02" };
        private static readonly string[] RandomBadNames = { null, string.Empty, "Happy", "../x", "NUL", "a.b", "joy" };

        [Test]
        public void Sequencer_KeepsCueOrderingAndRestoreInvariants_ForRandomOperationSequences()
        {
            for (var seed = 0; seed < 400; seed++)
            {
                RunRandomScenario(seed);
            }
        }

        private static void RunRandomScenario(int seed)
        {
            var random = new Random(seed);
            var maxPending = random.Next(0, 4);
            var sequencer = new SpeechSequencer(maxPending);
            var log = new List<string>();
            var says = new Dictionary<long, SayMessage>();
            var appended = new HashSet<long>();
            var refusedOrCancelled = new HashSet<long>();
            var expressionApplied = false;
            var motionApplied = false;
            var playing = false;
            var nextId = 1L;

            Func<string, List<SpeechAction>> drainChecked = context =>
            {
                var actions = Drain(sequencer);
                var because = "seed " + seed + " after " + context + " " + Describe(actions) + " log: " + string.Join(" ", log);
                log.Add(context + "=>" + Describe(actions));

                var cueCount = 0;
                var expectedCueCount = 0;
                for (var i = 0; i < actions.Count; i++)
                {
                    var action = actions[i];
                    if (action is SetExpression || action is PlayMotion)
                    {
                        cueCount++;
                    }

                    var append = action as AppendText;
                    if (append != null)
                    {
                        // ランダムな場面では turn = id
                        var say = says[append.Turn];
                        Assert.That(refusedOrCancelled.Contains(say.Id), Is.False, because + "（捨てた say を始めた）");
                        var expected = new List<string>();
                        if (say.Expression != null)
                        {
                            expected.Add("SetExpression(" + say.Expression + ")");
                        }

                        if (say.Motion != null)
                        {
                            expected.Add("PlayMotion(" + say.Motion + ")");
                        }

                        var before = actions.Skip(i - expected.Count).Take(expected.Count).Select(Describe).ToArray();
                        Assert.That(i >= expected.Count && before.SequenceEqual(expected), Is.True, because + "（AppendText の直前に、その say の表情 → モーションが無い）");
                        if (i - expected.Count - 1 >= 0)
                        {
                            var previous = actions[i - expected.Count - 1];
                            Assert.That(previous is SetExpression || previous is PlayMotion, Is.False, because + "（余計な表情・モーションがある）");
                        }

                        appended.Add(say.Id);
                        expectedCueCount += expected.Count;
                        continue;
                    }

                    var setExpression = action as SetExpression;
                    if (setExpression != null)
                    {
                        Assert.That(CueNames.IsExpression(setExpression.Name), Is.True, because);
                        expressionApplied = setExpression.Name != "neutral";
                        continue;
                    }

                    var playMotion = action as PlayMotion;
                    if (playMotion != null)
                    {
                        Assert.That(CueNames.IsMotion(playMotion.Name), Is.True, because);
                        motionApplied = true;
                        continue;
                    }

                    if (action is StopMotion)
                    {
                        motionApplied = false;
                        continue;
                    }

                    if (action is PlayAudio)
                    {
                        playing = true;
                        continue;
                    }

                    if (action is StopAudio)
                    {
                        playing = false;
                        continue;
                    }

                    var send = action as SendMessage;
                    Assert.That(send, Is.Not.Null, because);
                    var json = TestJson.ReadObject(send.Json);
                    if ((string)json["type"] == "played" && json["error"] != null && !appended.Contains((long)json["id"]))
                    {
                        refusedOrCancelled.Add((long)json["id"]);
                    }

                    if ((string)json["type"] == "played" && json["error"] == null)
                    {
                        playing = false;
                    }
                }

                if (context.StartsWith("say", StringComparison.Ordinal) || context.StartsWith("finished", StringComparison.Ordinal))
                {
                    Assert.That(cueCount, Is.EqualTo(expectedCueCount), because + "（始めた say のもの以外の表情・モーションがある）");
                }

                return actions;
            };

            var steps = 10 + random.Next(50);
            for (var step = 0; step < steps; step++)
            {
                var op = random.Next(100);
                if (op < 35)
                {
                    var id = nextId++;
                    var kind = AllSayKinds[random.Next(AllSayKinds.Length)];
                    var say = Say(kind, id, RandomExpressions[random.Next(RandomExpressions.Length)], RandomMotions[random.Next(RandomMotions.Length)], turn: id);
                    says[id] = say;
                    sequencer.OnSay(say);
                    drainChecked("say " + id + " " + kind + " " + say.Expression + "/" + say.Motion);
                }
                else if (op < 55)
                {
                    sequencer.OnPlaybackFinished(nextId - 1 - random.Next(3));
                    drainChecked("finished");
                }
                else if (op < 65)
                {
                    var name = random.Next(3) == 0 ? RandomBadNames[random.Next(RandomBadNames.Length)] : RandomExpressions[2 + random.Next(RandomExpressions.Length - 2)];
                    sequencer.OnExpression(name);
                    var actions = drainChecked("expression " + name);
                    var expected = CueNames.IsExpression(name) ? new[] { "SetExpression(" + name + ")" } : new string[0];
                    Assert.That(actions.Select(Describe).ToArray(), Is.EqualTo(expected), "seed " + seed + " " + string.Join(" ", log));
                }
                else if (op < 75)
                {
                    var name = random.Next(3) == 0 ? RandomBadNames[random.Next(RandomBadNames.Length)] : RandomMotions[2 + random.Next(RandomMotions.Length - 2)];
                    sequencer.OnMotion(name);
                    var actions = drainChecked("motion " + name);
                    var expected = CueNames.IsMotion(name) ? new[] { "PlayMotion(" + name + ")" } : new string[0];
                    Assert.That(actions.Select(Describe).ToArray(), Is.EqualTo(expected), "seed " + seed + " " + string.Join(" ", log));
                }
                else
                {
                    var disconnect = op >= 88;
                    var expected = new List<string>();
                    if (playing)
                    {
                        expected.Add("StopAudio");
                    }

                    if (expressionApplied)
                    {
                        expected.Add("SetExpression(neutral)");
                    }

                    if (motionApplied)
                    {
                        expected.Add("StopMotion");
                    }

                    foreach (var id in says.Keys.Where(k => !appended.Contains(k)))
                    {
                        refusedOrCancelled.Add(id);
                    }

                    if (disconnect)
                    {
                        sequencer.OnDisconnected();
                    }
                    else
                    {
                        sequencer.OnCancel();
                    }

                    var actions = drainChecked(disconnect ? "disconnected" : "cancel");
                    Assert.That(actions.Select(Describe).ToArray(), Is.EqualTo(expected.ToArray()), "seed " + seed + " " + string.Join(" ", log));
                    Assert.That(expressionApplied || motionApplied, Is.False, "seed " + seed);
                }
            }
        }
    }
}
