using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace VRMate.Tests
{
    public class SpeechSequencerTests
    {
        private static readonly string ValidAudio = WavBuilder.Pcm16Base64(new short[] { 0, 1000, -1000, 2000, -2000, 0 }, channels: 1, sampleRate: 24000);
        private static readonly string StereoAudio = WavBuilder.Pcm16Base64(new short[] { 1, 2, 3, 4 }, channels: 2, sampleRate: 48000);
        private static readonly string NotWavAudio = Convert.ToBase64String(Encoding.UTF8.GetBytes("not a wav file, only text"));

        private static SayMessage Say(long id, long turn, string text, string audioJson = null, string visemesJson = null)
        {
            var json = "{\"type\":\"say\",\"id\":" + id + ",\"turn\":" + turn + ",\"text\":" + TestJson.Quote(text)
                + (audioJson != null ? ",\"audio\":" + audioJson : string.Empty)
                + (visemesJson != null ? ",\"visemes\":" + visemesJson : string.Empty)
                + "}";
            var say = BrainProtocol.Parse(json) as SayMessage;
            Assert.That(say, Is.Not.Null, "テスト用の say を作れない: " + json);
            return say;
        }

        private static SayMessage TextSay(long id, long turn = 1, string text = null)
        {
            return Say(id, turn, text ?? "text " + id);
        }

        private static SayMessage AudioSay(long id, long turn = 1, string text = null, string visemesJson = null)
        {
            return Say(id, turn, text ?? "audio " + id, TestJson.Quote(ValidAudio), visemesJson);
        }

        private static SayMessage BrokenAudioSay(long id, string audioJson, long turn = 1, string text = null)
        {
            return Say(id, turn, text ?? "broken " + id, audioJson);
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
                return "AppendText(c" + append.Connection + ",t" + append.Turn + "," + append.Text + ")";
            }

            var send = action as SendMessage;
            if (send != null)
            {
                return "Send(" + send.Json + ")";
            }

            return action == null ? "null" : action.GetType().Name;
        }

        private static void AssertPlay(SpeechAction action, long id, string because)
        {
            Assert.That(action, Is.InstanceOf<PlayAudio>(), because);
            Assert.That(((PlayAudio)action).Id, Is.EqualTo(id), because);
            Assert.That(((PlayAudio)action).Audio, Is.Not.Null, because);
        }

        private static void AssertAppend(SpeechAction action, long turn, string text, string because)
        {
            Assert.That(action, Is.InstanceOf<AppendText>(), because);
            Assert.That(((AppendText)action).Turn, Is.EqualTo(turn), because);
            Assert.That(((AppendText)action).Text, Is.EqualTo(text), because);
        }

        private static JObject AssertSend(SpeechAction action, string type, long id, string because)
        {
            Assert.That(action, Is.InstanceOf<SendMessage>(), because);
            var json = TestJson.ReadObject(((SendMessage)action).Json);
            Assert.That((string)json["type"], Is.EqualTo(type), because);
            Assert.That(json["id"].Type, Is.EqualTo(JTokenType.Integer), because);
            Assert.That((long)json["id"], Is.EqualTo(id), because);
            return json;
        }

        private static void AssertStarted(SpeechAction action, long id, string because)
        {
            var json = AssertSend(action, "started", id, because);
            Assert.That(json.Properties().Select(p => p.Name), Is.EquivalentTo(new[] { "type", "id" }), because);
        }

        private static void AssertPlayed(SpeechAction action, long id, string because)
        {
            var json = AssertSend(action, "played", id, because);
            Assert.That(json["error"], Is.Null, because + "（error が付いている）");
        }

        private static void AssertPlayedWithError(SpeechAction action, long id, string because)
        {
            var json = AssertSend(action, "played", id, because);
            Assert.That(json["error"], Is.Not.Null, because + "（error が無い）");
            Assert.That(json["error"].Type, Is.EqualTo(JTokenType.String), because);
            Assert.That((string)json["error"], Is.Not.Empty, because);
        }

        // ---- 文字だけの say ----

        [Test]
        public void OnSay_AppendsTextThenSendsStartedAndPlayed_WhenSayHasNoAudio()
        {
            var sequencer = new SpeechSequencer();

            sequencer.OnSay(TextSay(1, turn: 3, text: "こんにちは😀"));
            var actions = Drain(sequencer);

            Assert.That(actions.Count, Is.EqualTo(3), Describe(actions));
            AssertAppend(actions[0], 3, "こんにちは😀", Describe(actions));
            AssertStarted(actions[1], 1, Describe(actions));
            AssertPlayed(actions[2], 1, Describe(actions));
        }

        [Test]
        public void OnSay_ProcessesTextOnlySaysBackToBack_WhenSeveralArrive()
        {
            var sequencer = new SpeechSequencer();

            sequencer.OnSay(TextSay(1));
            sequencer.OnSay(TextSay(2));
            var actions = Drain(sequencer);

            Assert.That(actions.Count, Is.EqualTo(6), Describe(actions));
            AssertAppend(actions[0], 1, "text 1", Describe(actions));
            AssertStarted(actions[1], 1, Describe(actions));
            AssertPlayed(actions[2], 1, Describe(actions));
            AssertAppend(actions[3], 1, "text 2", Describe(actions));
            AssertStarted(actions[4], 2, Describe(actions));
            AssertPlayed(actions[5], 2, Describe(actions));
        }

        [Test]
        public void DrainActions_ReturnsEmpty_WhenCalledTwice()
        {
            var sequencer = new SpeechSequencer();
            sequencer.OnSay(TextSay(1));
            Drain(sequencer);

            Assert.That(Drain(sequencer), Is.Empty);
        }

        [Test]
        public void DrainActions_ReturnsEmpty_WhenNothingHappened()
        {
            Assert.That(Drain(new SpeechSequencer()), Is.Empty);
        }

        // ---- 音声つきの say ----

        [Test]
        public void OnSay_PlaysAudioThenAppendsTextThenSendsStarted_WhenAudioIsDecodable()
        {
            var sequencer = new SpeechSequencer();

            sequencer.OnSay(AudioSay(10, turn: 2, text: "声つき"));
            var actions = Drain(sequencer);

            Assert.That(actions.Count, Is.EqualTo(3), Describe(actions));
            AssertPlay(actions[0], 10, Describe(actions));
            AssertAppend(actions[1], 2, "声つき", Describe(actions));
            AssertStarted(actions[2], 10, Describe(actions));
            var play = (PlayAudio)actions[0];
            Assert.That(play.Audio.Channels, Is.EqualTo(1));
            Assert.That(play.Audio.SampleRate, Is.EqualTo(24000));
            Assert.That(play.Audio.FrameCount, Is.EqualTo(6));
            Assert.That(play.Audio.Samples[1], Is.EqualTo(1000f / 32768f));
            Assert.That(play.Timeline, Is.Null);
        }

        [Test]
        public void OnSay_PassesDecodedStereoAudio_WhenAudioIsStereo()
        {
            var sequencer = new SpeechSequencer();

            sequencer.OnSay(Say(4, 1, "stereo", TestJson.Quote(StereoAudio)));
            var play = Drain(sequencer).OfType<PlayAudio>().Single();

            Assert.That(play.Audio.Channels, Is.EqualTo(2));
            Assert.That(play.Audio.SampleRate, Is.EqualTo(48000));
            Assert.That(play.Audio.FrameCount, Is.EqualTo(2));
        }

        [Test]
        public void OnSay_BuildsTimelineFromVisemes_WhenVisemesArePresent()
        {
            var sequencer = new SpeechSequencer();

            sequencer.OnSay(AudioSay(10, visemesJson: "[{\"t\":0,\"v\":\"ee\"},{\"t\":1,\"v\":\"sil\"}]"));
            var play = Drain(sequencer).OfType<PlayAudio>().Single();

            Assert.That(play.Timeline, Is.Not.Null);
            var weights = play.Timeline.Evaluate(0.5);
            Assert.That(weights.Ee, Is.EqualTo(1f).Within(1e-5));
            Assert.That(weights.Aa, Is.EqualTo(0f).Within(1e-5));
        }

        [Test]
        public void OnSay_PlaysWithoutTimeline_WhenVisemesWereRejected()
        {
            var sequencer = new SpeechSequencer();

            sequencer.OnSay(AudioSay(10, visemesJson: "[{\"t\":0.5,\"v\":\"aa\"},{\"t\":0.1,\"v\":\"sil\"}]"));
            var actions = Drain(sequencer);

            Assert.That(actions.Count, Is.EqualTo(3), Describe(actions));
            AssertPlay(actions[0], 10, Describe(actions));
            Assert.That(((PlayAudio)actions[0]).Timeline, Is.Null);
        }

        [Test]
        public void OnSay_QueuesSay_WhileAnotherIsPlaying()
        {
            var sequencer = new SpeechSequencer();
            sequencer.OnSay(AudioSay(1));
            Drain(sequencer);

            sequencer.OnSay(AudioSay(2));
            sequencer.OnSay(TextSay(3));

            Assert.That(Drain(sequencer), Is.Empty);
        }

        [Test]
        public void OnPlaybackFinished_SendsPlayedAndStartsNext_WhenIdMatchesPlayingSay()
        {
            var sequencer = new SpeechSequencer();
            sequencer.OnSay(AudioSay(1));
            sequencer.OnSay(AudioSay(2, turn: 1, text: "second"));
            Drain(sequencer);

            sequencer.OnPlaybackFinished(1);
            var actions = Drain(sequencer);

            Assert.That(actions.Count, Is.EqualTo(4), Describe(actions));
            AssertPlayed(actions[0], 1, Describe(actions));
            AssertPlay(actions[1], 2, Describe(actions));
            AssertAppend(actions[2], 1, "second", Describe(actions));
            AssertStarted(actions[3], 2, Describe(actions));
        }

        [Test]
        public void OnPlaybackFinished_RunsQueuedTextOnlySaysUntilNextAudio()
        {
            var sequencer = new SpeechSequencer();
            sequencer.OnSay(AudioSay(1));
            sequencer.OnSay(TextSay(2));
            sequencer.OnSay(TextSay(3));
            sequencer.OnSay(AudioSay(4));
            Drain(sequencer);

            sequencer.OnPlaybackFinished(1);
            var actions = Drain(sequencer);

            Assert.That(actions.Count, Is.EqualTo(10), Describe(actions));
            AssertPlayed(actions[0], 1, Describe(actions));
            AssertAppend(actions[1], 1, "text 2", Describe(actions));
            AssertStarted(actions[2], 2, Describe(actions));
            AssertPlayed(actions[3], 2, Describe(actions));
            AssertAppend(actions[4], 1, "text 3", Describe(actions));
            AssertStarted(actions[5], 3, Describe(actions));
            AssertPlayed(actions[6], 3, Describe(actions));
            AssertPlay(actions[7], 4, Describe(actions));
            AssertAppend(actions[8], 1, "audio 4", Describe(actions));
            AssertStarted(actions[9], 4, Describe(actions));
        }

        [Test]
        public void OnPlaybackFinished_DoesNothing_WhenIdDoesNotMatchPlayingSay()
        {
            var sequencer = new SpeechSequencer();
            sequencer.OnSay(AudioSay(5));
            sequencer.OnSay(AudioSay(6));
            Drain(sequencer);

            sequencer.OnPlaybackFinished(6);
            sequencer.OnPlaybackFinished(4);
            sequencer.OnPlaybackFinished(-5);

            Assert.That(Drain(sequencer), Is.Empty);
        }

        [Test]
        public void OnPlaybackFinished_DoesNothing_WhenNothingIsPlaying()
        {
            var sequencer = new SpeechSequencer();

            sequencer.OnPlaybackFinished(1);
            sequencer.OnPlaybackFinished(0);

            Assert.That(Drain(sequencer), Is.Empty);
        }

        [Test]
        public void OnPlaybackFinished_DoesNothing_WhenCalledTwiceForSameId()
        {
            var sequencer = new SpeechSequencer();
            sequencer.OnSay(AudioSay(1));
            sequencer.OnPlaybackFinished(1);
            Drain(sequencer);

            sequencer.OnPlaybackFinished(1);

            Assert.That(Drain(sequencer), Is.Empty);
        }

        [Test]
        public void OnPlaybackFinished_DoesNothing_ForTextOnlySayId()
        {
            var sequencer = new SpeechSequencer();
            sequencer.OnSay(TextSay(1));
            Drain(sequencer);

            sequencer.OnPlaybackFinished(1);

            Assert.That(Drain(sequencer), Is.Empty);
        }

        // ---- 再生できない音声 ----

        [Test]
        public void OnSay_AppendsTextAndSendsPlayedWithErrorWithoutStarted_WhenAudioCannotBeDecoded()
        {
            var cases = new[]
            {
                TestJson.Quote("!!! not base64 !!!"),
                TestJson.Quote(NotWavAudio),
                TestJson.Quote(string.Empty),
                "123",
                "{}",
                "true",
                TestJson.Quote(Convert.ToBase64String(new WavBuilder().Fmt(formatTag: 3, bitsPerSample: 32, blockAlign: 4).Chunk("data", new byte[8]).Build())),
            };

            foreach (var audioJson in cases)
            {
                var sequencer = new SpeechSequencer();

                sequencer.OnSay(BrokenAudioSay(7, audioJson, turn: 2, text: "読めない声"));
                var actions = Drain(sequencer);

                var because = "audio=" + audioJson + " " + Describe(actions);
                Assert.That(actions.Count, Is.EqualTo(2), because);
                AssertAppend(actions[0], 2, "読めない声", because);
                AssertPlayedWithError(actions[1], 7, because);
            }
        }

        [Test]
        public void OnSay_ContinuesWithNextSay_AfterUndecodableAudio()
        {
            var sequencer = new SpeechSequencer();
            sequencer.OnSay(AudioSay(1));
            sequencer.OnSay(BrokenAudioSay(2, TestJson.Quote(NotWavAudio)));
            sequencer.OnSay(AudioSay(3));
            Drain(sequencer);

            sequencer.OnPlaybackFinished(1);
            var actions = Drain(sequencer);

            Assert.That(actions.Count, Is.EqualTo(6), Describe(actions));
            AssertPlayed(actions[0], 1, Describe(actions));
            AssertAppend(actions[1], 1, "broken 2", Describe(actions));
            AssertPlayedWithError(actions[2], 2, Describe(actions));
            AssertPlay(actions[3], 3, Describe(actions));
            AssertAppend(actions[4], 1, "audio 3", Describe(actions));
            AssertStarted(actions[5], 3, Describe(actions));
        }

        // ---- 待ちの上限 ----

        [Test]
        public void OnSay_DropsSayAndSendsPlayedWithError_WhenDefaultPendingLimitOfFourIsFull()
        {
            var sequencer = new SpeechSequencer();
            sequencer.OnSay(AudioSay(1));
            for (var id = 2; id <= 5; id++)
            {
                sequencer.OnSay(AudioSay(id));
            }

            Drain(sequencer);

            sequencer.OnSay(AudioSay(6));
            var actions = Drain(sequencer);

            Assert.That(actions.Count, Is.EqualTo(1), Describe(actions));
            AssertPlayedWithError(actions[0], 6, Describe(actions));
        }

        [Test]
        public void OnSay_NeverPlaysDroppedSay_AfterQueueDrains()
        {
            var sequencer = new SpeechSequencer(maxPending: 2);
            sequencer.OnSay(AudioSay(1));
            sequencer.OnSay(AudioSay(2));
            sequencer.OnSay(AudioSay(3));
            sequencer.OnSay(TextSay(4));
            var all = Drain(sequencer);

            for (var id = 1; id <= 3; id++)
            {
                sequencer.OnPlaybackFinished(id);
                all.AddRange(Drain(sequencer));
            }

            var played = all.OfType<PlayAudio>().Select(p => p.Id).ToArray();
            Assert.That(played, Is.EqualTo(new long[] { 1, 2, 3 }), Describe(all));
            Assert.That(all.OfType<AppendText>().Any(a => a.Text == "text 4"), Is.False, Describe(all));
            Assert.That(all.OfType<SendMessage>().Count(m => m.Json.Contains("\"started\"") && (long)TestJson.ReadObject(m.Json)["id"] == 4), Is.EqualTo(0), Describe(all));
        }

        [Test]
        public void OnSay_AcceptsSayAgain_AfterPendingSpaceFreesUp()
        {
            var sequencer = new SpeechSequencer(maxPending: 1);
            sequencer.OnSay(AudioSay(1));
            sequencer.OnSay(AudioSay(2));
            sequencer.OnSay(AudioSay(3));
            Drain(sequencer);

            sequencer.OnPlaybackFinished(1);
            Drain(sequencer);
            sequencer.OnSay(AudioSay(4));

            Assert.That(Drain(sequencer), Is.Empty, "2 が再生中で、4 は待ちに入る");
            sequencer.OnPlaybackFinished(2);
            var actions = Drain(sequencer);
            AssertPlayed(actions[0], 2, Describe(actions));
            AssertPlay(actions[1], 4, Describe(actions));
        }

        [Test]
        public void OnSay_DropsEverySayWhilePlaying_WhenMaxPendingIsZero()
        {
            var sequencer = new SpeechSequencer(maxPending: 0);
            sequencer.OnSay(AudioSay(1));
            Drain(sequencer);

            sequencer.OnSay(TextSay(2));
            var actions = Drain(sequencer);

            Assert.That(actions.Count, Is.EqualTo(1), Describe(actions));
            AssertPlayedWithError(actions[0], 2, Describe(actions));
        }

        // ---- 取り消しと切断 ----

        [Test]
        public void OnCancel_StopsAudioAndDropsQueue_WhenPlaying()
        {
            var sequencer = new SpeechSequencer();
            sequencer.OnSay(AudioSay(1));
            sequencer.OnSay(AudioSay(2));
            sequencer.OnSay(TextSay(3));
            Drain(sequencer);

            sequencer.OnCancel();
            var actions = Drain(sequencer);

            Assert.That(actions.Count, Is.EqualTo(1), Describe(actions));
            Assert.That(actions[0], Is.InstanceOf<StopAudio>());
        }

        [Test]
        public void OnCancel_SendsNothingLater_ForCancelledIds()
        {
            var sequencer = new SpeechSequencer();
            sequencer.OnSay(AudioSay(1));
            sequencer.OnSay(AudioSay(2));
            Drain(sequencer);
            sequencer.OnCancel();
            Drain(sequencer);

            sequencer.OnPlaybackFinished(1);
            sequencer.OnPlaybackFinished(2);

            Assert.That(Drain(sequencer), Is.Empty);
        }

        [Test]
        public void OnCancel_DoesNothing_WhenIdle()
        {
            var sequencer = new SpeechSequencer();
            sequencer.OnSay(TextSay(1));
            Drain(sequencer);

            sequencer.OnCancel();
            sequencer.OnCancel();

            Assert.That(Drain(sequencer), Is.Empty);
        }

        [Test]
        public void OnSay_PlaysNormally_AfterCancel()
        {
            var sequencer = new SpeechSequencer();
            sequencer.OnSay(AudioSay(1));
            sequencer.OnSay(AudioSay(2));
            sequencer.OnCancel();
            Drain(sequencer);

            sequencer.OnSay(AudioSay(3));
            var actions = Drain(sequencer);

            Assert.That(actions.Count, Is.EqualTo(3), Describe(actions));
            AssertPlay(actions[0], 3, Describe(actions));
            sequencer.OnPlaybackFinished(1);
            Assert.That(Drain(sequencer), Is.Empty, "取り消した 1 の終わりは無視される");
            sequencer.OnPlaybackFinished(3);
            var finished = Drain(sequencer);
            Assert.That(finished.Count, Is.EqualTo(1), Describe(finished));
            AssertPlayed(finished[0], 3, Describe(finished));
        }

        [Test]
        public void OnCancel_FreesPendingSpace_ForLaterSays()
        {
            var sequencer = new SpeechSequencer(maxPending: 1);
            sequencer.OnSay(AudioSay(1));
            sequencer.OnSay(AudioSay(2));
            sequencer.OnCancel();
            sequencer.OnSay(AudioSay(3));
            sequencer.OnSay(AudioSay(4));
            var actions = Drain(sequencer);

            Assert.That(actions.OfType<SendMessage>().Any(m => m.Json.Contains("\"played\"")), Is.False, Describe(actions));
        }

        [Test]
        public void OnDisconnected_StopsAudioAndDropsQueue_WhenPlaying()
        {
            var sequencer = new SpeechSequencer();
            sequencer.OnSay(AudioSay(1));
            sequencer.OnSay(AudioSay(2));
            Drain(sequencer);

            sequencer.OnDisconnected();
            var actions = Drain(sequencer);
            sequencer.OnPlaybackFinished(1);
            sequencer.OnPlaybackFinished(2);

            Assert.That(actions.Count, Is.EqualTo(1), Describe(actions));
            Assert.That(actions[0], Is.InstanceOf<StopAudio>());
            Assert.That(Drain(sequencer), Is.Empty);
        }

        [Test]
        public void OnDisconnected_DoesNothing_WhenIdle()
        {
            var sequencer = new SpeechSequencer();

            sequencer.OnDisconnected();

            Assert.That(Drain(sequencer), Is.Empty);
        }

        [Test]
        public void OnSay_PlaysNormally_AfterDisconnected()
        {
            var sequencer = new SpeechSequencer();
            sequencer.OnSay(AudioSay(1));
            sequencer.OnDisconnected();
            Drain(sequencer);

            sequencer.OnSay(TextSay(1, text: "again"));
            var actions = Drain(sequencer);

            Assert.That(actions.Count, Is.EqualTo(3), Describe(actions));
            AssertAppend(actions[0], 1, "again", Describe(actions));
            AssertStarted(actions[1], 1, Describe(actions));
            AssertPlayed(actions[2], 1, Describe(actions));
        }

        [Test]
        public void OnCancel_IgnoresFinishOfCancelledId_EvenWhenSameIdArrivesAgain()
        {
            // 再接続のあとに頭脳が同じ id を使い直しても、新しい say は普通に扱う
            var sequencer = new SpeechSequencer();
            sequencer.OnSay(AudioSay(1));
            sequencer.OnDisconnected();
            Drain(sequencer);

            sequencer.OnSay(AudioSay(1));
            Drain(sequencer);
            sequencer.OnPlaybackFinished(1);
            var actions = Drain(sequencer);

            Assert.That(actions.Count, Is.EqualTo(1), Describe(actions));
            AssertPlayed(actions[0], 1, Describe(actions));
        }

        // ---- 接続の番号 ----
        // 吹き出しは接続の番号と turn の両方が同じ AppendText をまとめる。
        // 頭脳を起動し直すと turn は数え直しになるので、切断のたびに接続の番号が変わる必要がある。

        private static readonly SayKind[] AllSayKinds = { SayKind.TextOnly, SayKind.Audio, SayKind.BrokenAudio };

        private static SayMessage SayOfKind(SayKind kind, long id, long turn, string text)
        {
            switch (kind)
            {
                case SayKind.TextOnly:
                    return TextSay(id, turn, text);
                case SayKind.Audio:
                    return AudioSay(id, turn, text);
                default:
                    return BrokenAudioSay(id, TestJson.Quote(NotWavAudio), turn, text);
            }
        }

        /// <summary>何も再生していない sequencer に say を渡し、その say の AppendText を返す。</summary>
        private static AppendText AppendOf(SpeechSequencer sequencer, SayMessage say)
        {
            Assert.That(sequencer.IsPlaying, Is.False, "say を渡す前に再生を終わらせておく");
            sequencer.OnSay(say);
            var actions = Drain(sequencer);
            var appends = actions.OfType<AppendText>().ToList();
            Assert.That(appends.Count, Is.EqualTo(1), Describe(actions));
            Assert.That(appends[0].Text, Is.EqualTo(say.Text), Describe(actions));
            return appends[0];
        }

        [Test]
        public void AppendText_HasSameConnection_ForEverySayKindAndTurn_WhenNotDisconnected()
        {
            var sequencer = new SpeechSequencer();
            var appends = new List<AppendText>();

            appends.Add(AppendOf(sequencer, TextSay(1, turn: 1)));
            appends.Add(AppendOf(sequencer, AudioSay(2, turn: 2)));
            sequencer.OnPlaybackFinished(2);
            Drain(sequencer);
            appends.Add(AppendOf(sequencer, BrokenAudioSay(3, TestJson.Quote(NotWavAudio), turn: 3)));
            appends.Add(AppendOf(sequencer, TextSay(4, turn: 1)));
            sequencer.OnSay(AudioSay(5, turn: 4));
            sequencer.OnSay(TextSay(6, turn: 5));
            sequencer.OnPlaybackFinished(5);
            appends.AddRange(Drain(sequencer).OfType<AppendText>());

            Assert.That(appends.Count, Is.EqualTo(6), Describe(appends));
            Assert.That(appends.Select(a => a.Connection).Distinct().Count(), Is.EqualTo(1), Describe(appends));
        }

        [Test]
        public void AppendText_HasDifferentConnection_WhenSameTurnArrivesAfterDisconnect()
        {
            foreach (var turn in new[] { long.MinValue, -1L, 0L, 1L, long.MaxValue })
            {
                var sequencer = new SpeechSequencer();
                var before = AppendOf(sequencer, TextSay(1, turn: turn, text: "前の頭脳"));

                sequencer.OnDisconnected();
                Drain(sequencer);
                var after = AppendOf(sequencer, TextSay(1, turn: turn, text: "新しい頭脳"));

                var because = "turn=" + turn + " " + Describe(new SpeechAction[] { before, after });
                Assert.That(after.Turn, Is.EqualTo(before.Turn), because);
                Assert.That(after.Connection, Is.Not.EqualTo(before.Connection), because);
            }
        }

        [Test]
        public void AppendText_HasDifferentConnection_AfterDisconnect_ForEverySayKindBeforeAndAfter()
        {
            foreach (var kindBefore in AllSayKinds)
            {
                foreach (var kindAfter in AllSayKinds)
                {
                    var sequencer = new SpeechSequencer();
                    var before = AppendOf(sequencer, SayOfKind(kindBefore, 1, 1, "前の頭脳"));

                    sequencer.OnDisconnected();
                    Drain(sequencer);
                    var after = AppendOf(sequencer, SayOfKind(kindAfter, 1, 1, "新しい頭脳"));

                    var because = kindBefore + " -> " + kindAfter + " " + Describe(new SpeechAction[] { before, after });
                    Assert.That(after.Connection, Is.Not.EqualTo(before.Connection), because);
                }
            }
        }

        [Test]
        public void AppendText_HasDifferentConnection_AfterDisconnect_WhetherIdleOrPlayingOrWaiting()
        {
            foreach (var state in new[] { "再生していない", "再生中で待ちなし", "再生中で待ちあり" })
            {
                var sequencer = new SpeechSequencer();
                sequencer.OnSay(AudioSay(1, turn: 1));
                if (state == "再生していない")
                {
                    sequencer.OnPlaybackFinished(1);
                }
                else if (state == "再生中で待ちあり")
                {
                    sequencer.OnSay(TextSay(2, turn: 1));
                    sequencer.OnSay(AudioSay(3, turn: 1));
                }

                var earlier = Drain(sequencer).OfType<AppendText>().ToList();
                Assert.That(earlier.Count, Is.EqualTo(1), state + " " + Describe(earlier));

                sequencer.OnDisconnected();
                Drain(sequencer);
                sequencer.OnSay(AudioSay(1, turn: 1, text: "新しい頭脳の声"));
                sequencer.OnSay(TextSay(2, turn: 1, text: "新しい頭脳の文字"));
                sequencer.OnPlaybackFinished(1);
                var later = Drain(sequencer).OfType<AppendText>().ToList();

                var because = state + " " + Describe(earlier.Concat(later));
                Assert.That(later.Select(a => a.Text), Is.EqualTo(new[] { "新しい頭脳の声", "新しい頭脳の文字" }), because + "（切断で捨てた say が出ている）");
                Assert.That(later[0].Connection, Is.Not.EqualTo(earlier[0].Connection), because);
                Assert.That(later[1].Connection, Is.EqualTo(later[0].Connection), because);
            }
        }

        [Test]
        public void AppendText_HasConnectionDifferentFromEveryEarlierConnection_WhenDisconnectedRepeatedly()
        {
            var sequencer = new SpeechSequencer();
            var appends = new List<AppendText>();

            appends.Add(AppendOf(sequencer, TextSay(1, turn: 1, text: "1 つ目の接続")));
            sequencer.OnDisconnected();
            appends.Add(AppendOf(sequencer, AudioSay(1, turn: 1, text: "2 つ目の接続")));
            sequencer.OnDisconnected();
            sequencer.OnDisconnected();
            Drain(sequencer);
            appends.Add(AppendOf(sequencer, TextSay(1, turn: 1, text: "4 つ目の接続")));
            sequencer.OnDisconnected();
            appends.Add(AppendOf(sequencer, BrokenAudioSay(1, TestJson.Quote(NotWavAudio), turn: 1, text: "5 つ目の接続")));
            sequencer.OnDisconnected();
            sequencer.OnDisconnected();
            sequencer.OnDisconnected();
            appends.Add(AppendOf(sequencer, TextSay(1, turn: 1, text: "8 つ目の接続")));

            Assert.That(appends.Select(a => a.Connection).Distinct().Count(), Is.EqualTo(appends.Count), Describe(appends));
        }

        [Test]
        public void AppendText_KeepsConnection_AcrossCancel()
        {
            var sequencer = new SpeechSequencer();
            var appends = new List<AppendText>();

            appends.Add(AppendOf(sequencer, TextSay(1, turn: 1)));
            sequencer.OnCancel();
            appends.Add(AppendOf(sequencer, AudioSay(2, turn: 1)));
            sequencer.OnCancel();
            Drain(sequencer);
            appends.Add(AppendOf(sequencer, AudioSay(3, turn: 1)));
            sequencer.OnSay(TextSay(4, turn: 1));
            sequencer.OnCancel();
            Drain(sequencer);
            appends.Add(AppendOf(sequencer, BrokenAudioSay(5, TestJson.Quote(NotWavAudio), turn: 1)));
            sequencer.OnCancel();
            sequencer.OnCancel();
            appends.Add(AppendOf(sequencer, TextSay(6, turn: 1)));

            Assert.That(appends.Count, Is.EqualTo(5), Describe(appends));
            Assert.That(appends.Select(a => a.Connection).Distinct().Count(), Is.EqualTo(1), Describe(appends));
        }

        // ---- 不変条件（ランダムな操作列） ----

        [Test]
        public void Sequencer_KeepsOrderingOnceOnlyAndConnectionInvariants_ForRandomOperationSequences()
        {
            for (var seed = 0; seed < 400; seed++)
            {
                RunRandomScenario(seed);
            }
        }

        private enum SayKind
        {
            TextOnly,
            Audio,
            BrokenAudio,
        }

        private static void RunRandomScenario(int seed)
        {
            var random = new Random(seed);
            var maxPending = random.Next(0, 6);
            var sequencer = new SpeechSequencer(maxPending);
            var log = new List<string>();
            var kinds = new Dictionary<long, SayKind>();
            var startedCount = new Dictionary<long, int>();
            var playedCount = new Dictionary<long, int>();
            var outstanding = new HashSet<long>();
            var cancelled = new HashSet<long>();
            var lastAppendedId = 0L;
            long? playing = null;
            var nextId = 1L;
            var disconnects = 0;
            var connectionAfterDisconnects = new Dictionary<int, long>();

            Action<string> check = context =>
            {
                foreach (var action in sequencer.DrainActions())
                {
                    var because = "seed " + seed + " maxPending " + maxPending + " after " + context + " action " + Describe(action) + " log: " + string.Join(" ", log);
                    log.Add(Describe(action));

                    var play = action as PlayAudio;
                    if (play != null)
                    {
                        Assert.That(playing.HasValue, Is.False, because + "（同時に 2 つ再生）");
                        Assert.That(kinds.ContainsKey(play.Id), Is.True, because + "（受け取っていない id）");
                        Assert.That(kinds[play.Id], Is.EqualTo(SayKind.Audio), because);
                        Assert.That(cancelled.Contains(play.Id), Is.False, because + "（取り消した say を再生）");
                        playing = play.Id;
                        continue;
                    }

                    if (action is StopAudio)
                    {
                        Assert.That(playing.HasValue, Is.True, because + "（再生していないのに止めた）");
                        playing = null;
                        continue;
                    }

                    var append = action as AppendText;
                    if (append != null)
                    {
                        // ランダムな場面では turn = id にしてあるので、吹き出しに足す順で開始の順を確かめられる
                        Assert.That(append.Turn, Is.GreaterThan(lastAppendedId), because + "（受け取った順と違う）");
                        Assert.That(cancelled.Contains(append.Turn), Is.False, because + "（取り消した say を開始）");
                        Assert.That(append.Text, Is.EqualTo("say " + append.Turn), because);
                        long connection;
                        if (connectionAfterDisconnects.TryGetValue(disconnects, out connection))
                        {
                            Assert.That(append.Connection, Is.EqualTo(connection), because + "（同じ接続の中で番号が変わった）");
                        }
                        else
                        {
                            Assert.That(connectionAfterDisconnects.Values, Has.No.Member(append.Connection), because + "（前の接続と同じ番号）");
                            connectionAfterDisconnects[disconnects] = append.Connection;
                        }

                        lastAppendedId = append.Turn;
                        continue;
                    }

                    var send = action as SendMessage;
                    Assert.That(send, Is.Not.Null, because);
                    var json = TestJson.ReadObject(send.Json);
                    var type = (string)json["type"];
                    var id = (long)json["id"];
                    Assert.That(kinds.ContainsKey(id), Is.True, because + "（受け取っていない id）");
                    Assert.That(cancelled.Contains(id), Is.False, because + "（取り消した say に返事）");
                    if (type == "started")
                    {
                        Assert.That(kinds[id], Is.Not.EqualTo(SayKind.BrokenAudio), because + "（再生できない say に started）");
                        Assert.That(startedCount.ContainsKey(id), Is.False, because + "（started が 2 回）");
                        Assert.That(playedCount.ContainsKey(id), Is.False, because + "（played のあとに started）");
                        Assert.That(lastAppendedId, Is.EqualTo(id), because + "（吹き出しに足す前に started）");
                        startedCount[id] = 1;
                    }
                    else
                    {
                        Assert.That(type, Is.EqualTo("played"), because);
                        Assert.That(playedCount.ContainsKey(id), Is.False, because + "（played が 2 回）");
                        var hasError = json["error"] != null;
                        if (kinds[id] == SayKind.BrokenAudio)
                        {
                            Assert.That(hasError, Is.True, because + "（再生できない say の played に error が無い）");
                        }

                        if (!hasError)
                        {
                            Assert.That(startedCount.ContainsKey(id), Is.True, because + "（成功の played の前に started が無い）");
                        }

                        if (playing == id)
                        {
                            playing = null;
                        }

                        playedCount[id] = 1;
                        outstanding.Remove(id);
                    }
                }
            };

            var steps = 10 + random.Next(50);
            for (var step = 0; step < steps; step++)
            {
                var op = random.Next(100);
                if (op < 45)
                {
                    var id = nextId++;
                    var kindRoll = random.Next(10);
                    var kind = kindRoll < 3 ? SayKind.TextOnly : kindRoll < 8 ? SayKind.Audio : SayKind.BrokenAudio;
                    kinds[id] = kind;
                    outstanding.Add(id);
                    var text = "say " + id;
                    SayMessage say;
                    switch (kind)
                    {
                        case SayKind.TextOnly:
                            say = Say(id, id, text);
                            break;
                        case SayKind.Audio:
                            say = Say(id, id, text, TestJson.Quote(ValidAudio), random.Next(2) == 0 ? null : "[{\"t\":0,\"v\":\"aa\"},{\"t\":0.1,\"v\":\"sil\"}]");
                            break;
                        default:
                            say = Say(id, id, text, random.Next(2) == 0 ? TestJson.Quote(NotWavAudio) : "42");
                            break;
                    }

                    log.Add("say(" + id + "," + kind + ")");
                    sequencer.OnSay(say);
                    check("say " + id);
                }
                else if (op < 75)
                {
                    long target;
                    var roll = random.Next(4);
                    if (roll < 2 && playing.HasValue)
                    {
                        target = playing.Value;
                    }
                    else if (roll == 2 && nextId > 1)
                    {
                        target = 1 + random.Next((int)nextId - 1);
                    }
                    else
                    {
                        target = random.Next(-2, (int)nextId + 3);
                    }

                    log.Add("finished(" + target + ")");
                    sequencer.OnPlaybackFinished(target);
                    check("finished " + target);
                }
                else if (op < 82)
                {
                    log.Add("drain");
                    check("drain");
                }
                else
                {
                    var disconnect = op >= 92;
                    log.Add(disconnect ? "disconnected" : "cancel");
                    var wasPlaying = playing.HasValue;
                    if (disconnect)
                    {
                        sequencer.OnDisconnected();
                        disconnects++;
                    }
                    else
                    {
                        sequencer.OnCancel();
                    }

                    // 取り消しの時点で返事の済んでいない say は、すべて再生中か待ちのどちらか
                    foreach (var id in outstanding)
                    {
                        cancelled.Add(id);
                    }

                    outstanding.Clear();
                    var before = log.Count;
                    check(disconnect ? "disconnected" : "cancel");
                    var emitted = log.Skip(before).ToList();
                    var because = "seed " + seed + " log: " + string.Join(" ", log);
                    if (wasPlaying)
                    {
                        Assert.That(emitted, Is.EqualTo(new[] { "StopAudio" }), because);
                    }
                    else
                    {
                        Assert.That(emitted, Is.Empty, because);
                    }
                }
            }

            // 最後まで再生を終わらせると、取り消していない say にはすべて played が返る
            for (var guard = 0; guard < 1000 && playing.HasValue; guard++)
            {
                var id = playing.Value;
                log.Add("finished(" + id + ")");
                sequencer.OnPlaybackFinished(id);
                check("final finished " + id);
            }

            Assert.That(playing.HasValue, Is.False, "seed " + seed);
            Assert.That(outstanding, Is.Empty, "seed " + seed + " 返事の無い say: " + string.Join(",", outstanding) + " log: " + string.Join(" ", log));
            foreach (var id in kinds.Keys)
            {
                var ended = playedCount.ContainsKey(id) ? 1 : 0;
                var wasCancelled = cancelled.Contains(id) ? 1 : 0;
                Assert.That(ended + wasCancelled, Is.EqualTo(1), "seed " + seed + " id " + id + " log: " + string.Join(" ", log));
            }
        }
    }
}
