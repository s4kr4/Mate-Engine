using System;
using System.Linq;
using System.Text;
using NUnit.Framework;

namespace VRMate.Tests
{
    public class WavDecoderTests
    {
        private static PcmAudio DecodeOk(byte[] wav)
        {
            PcmAudio audio;
            string error;
            var ok = WavDecoder.TryDecode(wav, out audio, out error);
            Assert.That(ok, Is.True, "読めない: " + error);
            return audio;
        }

        private static void AssertFails(byte[] wav, string because)
        {
            PcmAudio audio = null;
            string error = null;
            var ok = true;
            Assert.DoesNotThrow(() => ok = WavDecoder.TryDecode(wav, out audio, out error), because);
            Assert.That(ok, Is.False, because);
            Assert.That(audio, Is.Null, because + "（audio が null でない）");
            Assert.That(error, Is.Not.Null.And.Not.Empty, because + "（error が空）");
        }

        private static readonly short[] FiveSamples = { 0, 32767, -32768, 1, -1 };

        /// <summary>PCM 16bit モノラル 24000 Hz の fmt の中身（16 バイト）。</summary>
        private static byte[] Pcm16MonoFmtBody()
        {
            return new byte[] { 1, 0, 1, 0, 0xC0, 0x5D, 0, 0, 0x80, 0xBB, 0, 0, 2, 0, 16, 0 };
        }

        // ---- 正常系 ----

        [Test]
        public void TryDecode_ReadsMonoPcm16_WhenWavIsCanonical()
        {
            var audio = DecodeOk(WavBuilder.Pcm16(FiveSamples, channels: 1, sampleRate: 24000));

            Assert.That(audio.Channels, Is.EqualTo(1));
            Assert.That(audio.SampleRate, Is.EqualTo(24000));
            Assert.That(audio.FrameCount, Is.EqualTo(5));
            Assert.That(audio.Samples, Is.EqualTo(new[] { 0f, 32767f / 32768f, -1f, 1f / 32768f, -1f / 32768f }));
            Assert.That(audio.DurationSeconds, Is.EqualTo(5.0 / 24000.0).Within(1e-12));
        }

        [Test]
        public void TryDecode_ReadsLittleEndianSamples_WhenBytesAreAsymmetric()
        {
            // 0x0102 = 258、0x80FF = -32513
            var wav = new WavBuilder().Fmt().Chunk("data", new byte[] { 0x02, 0x01, 0xFF, 0x80 }).Build();

            var audio = DecodeOk(wav);

            Assert.That(audio.Samples, Is.EqualTo(new[] { 258f / 32768f, -32513f / 32768f }));
        }

        [Test]
        public void TryDecode_ReadsInterleavedStereo_WhenChannelsIsTwo()
        {
            var samples = new short[] { 100, -100, 200, -200, 300, -300 };

            var audio = DecodeOk(WavBuilder.Pcm16(samples, channels: 2, sampleRate: 44100));

            Assert.That(audio.Channels, Is.EqualTo(2));
            Assert.That(audio.SampleRate, Is.EqualTo(44100));
            Assert.That(audio.FrameCount, Is.EqualTo(3));
            Assert.That(audio.Samples, Is.EqualTo(samples.Select(s => s / 32768f).ToArray()));
            Assert.That(audio.DurationSeconds, Is.EqualTo(3.0 / 44100.0).Within(1e-12));
        }

        [TestCase(1)]
        [TestCase(8000)]
        [TestCase(22050)]
        [TestCase(384000)]
        public void TryDecode_AcceptsSampleRate_WhenWithinOneTo384000(int sampleRate)
        {
            var audio = DecodeOk(WavBuilder.Pcm16(FiveSamples, sampleRate: sampleRate));

            Assert.That(audio.SampleRate, Is.EqualTo(sampleRate));
            Assert.That(audio.DurationSeconds, Is.EqualTo(5.0 / sampleRate).Within(1e-12));
        }

        [Test]
        public void TryDecode_ReadsTwentySecondsOfAudio_WhenWavIsLarge()
        {
            var samples = Enumerable.Range(0, 24000 * 20).Select(i => (short)((i * 37) % 65536 - 32768)).ToArray();

            var audio = DecodeOk(WavBuilder.Pcm16(samples));

            Assert.That(audio.FrameCount, Is.EqualTo(480000));
            Assert.That(audio.Samples.Length, Is.EqualTo(480000));
            Assert.That(audio.Samples[479999], Is.EqualTo(samples[479999] / 32768f));
            Assert.That(audio.DurationSeconds, Is.EqualTo(20.0).Within(1e-9));
        }

        [Test]
        public void TryDecode_SkipsUnknownChunks_WhenTheyAppearBeforeBetweenAndAfter()
        {
            var wav = new WavBuilder()
                .Chunk("LIST", Encoding.ASCII.GetBytes("INFOISFT\u0004\0\0\0abc\0"))
                .Fmt()
                .Chunk("fact", new byte[] { 5, 0, 0, 0 })
                .Data(FiveSamples)
                .Chunk("junk", new byte[64])
                .Build();

            var audio = DecodeOk(wav);

            Assert.That(audio.FrameCount, Is.EqualTo(5));
            Assert.That(audio.Samples[1], Is.EqualTo(32767f / 32768f));
        }

        [Test]
        public void TryDecode_ReadsChunks_WhenDataComesBeforeFmt()
        {
            var wav = new WavBuilder().Data(FiveSamples).Chunk("LIST", new byte[4]).Fmt(sampleRate: 16000).Build();

            var audio = DecodeOk(wav);

            Assert.That(audio.SampleRate, Is.EqualTo(16000));
            Assert.That(audio.FrameCount, Is.EqualTo(5));
        }

        [TestCase(1)]
        [TestCase(3)]
        [TestCase(5)]
        public void TryDecode_SkipsPadByte_WhenUnknownChunkHasOddLength(int length)
        {
            var wav = new WavBuilder()
                .Fmt()
                .Chunk("odd ", Enumerable.Repeat((byte)0x7A, length).ToArray())
                .Data(FiveSamples)
                .Build();

            var audio = DecodeOk(wav);

            Assert.That(audio.FrameCount, Is.EqualTo(5));
            Assert.That(audio.Samples, Is.EqualTo(new[] { 0f, 32767f / 32768f, -1f, 1f / 32768f, -1f / 32768f }));
        }

        [Test]
        public void TryDecode_DropsTrailingPartialFrame_WhenMonoDataHasOddLength()
        {
            // 5 バイトの data（2 フレーム＋端数 1 バイト）。そのあとに詰め物と fmt が続く
            var wav = new WavBuilder()
                .Chunk("data", new byte[] { 1, 0, 2, 0, 9 })
                .Fmt()
                .Build();

            var audio = DecodeOk(wav);

            Assert.That(audio.FrameCount, Is.EqualTo(2));
            Assert.That(audio.Samples, Is.EqualTo(new[] { 1f / 32768f, 2f / 32768f }));
        }

        [Test]
        public void TryDecode_DropsTrailingPartialFrame_WhenStereoDataIsNotMultipleOfBlockAlign()
        {
            var wav = new WavBuilder()
                .Fmt(channels: 2)
                .Chunk("data", new byte[] { 1, 0, 2, 0, 3, 0 })
                .Build();

            var audio = DecodeOk(wav);

            Assert.That(audio.FrameCount, Is.EqualTo(1));
            Assert.That(audio.Samples, Is.EqualTo(new[] { 1f / 32768f, 2f / 32768f }));
        }

        [Test]
        public void TryDecode_AcceptsFmt_WhenFmtChunkHasExtensionBytes()
        {
            var wav = new WavBuilder().Fmt(extra: new byte[] { 0, 0 }).Data(FiveSamples).Build();

            Assert.That(DecodeOk(wav).FrameCount, Is.EqualTo(5));
        }

        [TestCase(0u)]
        [TestCase(4u)]
        [TestCase(0xFFFFFFFFu)]
        [TestCase(0x7FFFFFFFu)]
        public void TryDecode_IgnoresRiffSizeField_WhenItDisagreesWithActualLength(uint riffSize)
        {
            var wav = new WavBuilder().RiffSize(riffSize).Fmt().Data(FiveSamples).Build();

            Assert.That(DecodeOk(wav).FrameCount, Is.EqualTo(5));
        }

        // ---- 失敗 ----

        [Test]
        public void TryDecode_Fails_WhenInputIsNull()
        {
            AssertFails(null, "null");
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(4)]
        [TestCase(11)]
        public void TryDecode_Fails_WhenInputIsShorterThan12Bytes(int length)
        {
            var wav = WavBuilder.Pcm16(FiveSamples).Take(length).ToArray();

            AssertFails(wav, "length " + length);
        }

        [Test]
        public void TryDecode_Fails_WhenOnlyRiffHeaderExists()
        {
            AssertFails(new WavBuilder().Build(), "ヘッダだけ");
        }

        [TestCase("RIFX", "WAVE")]
        [TestCase("riff", "WAVE")]
        [TestCase("RIFF", "AVI ")]
        [TestCase("RIFF", "wave")]
        [TestCase("OggS", "WAVE")]
        public void TryDecode_Fails_WhenHeaderIsNotRiffWave(string riff, string wave)
        {
            var wav = WavBuilder.Pcm16(FiveSamples);
            Encoding.ASCII.GetBytes(riff).CopyTo(wav, 0);
            Encoding.ASCII.GetBytes(wave).CopyTo(wav, 8);

            AssertFails(wav, riff + "/" + wave);
        }

        [Test]
        public void TryDecode_Fails_WhenInputIsNotWavAtAll()
        {
            AssertFails(Encoding.UTF8.GetBytes("this is definitely not a wav file at all"), "テキスト");
        }

        [TestCase(3)]
        [TestCase(0)]
        [TestCase(2)]
        [TestCase(0xFFFE)]
        public void TryDecode_Fails_WhenFormatTagIsNotPcm(int formatTag)
        {
            AssertFails(new WavBuilder().Fmt(formatTag: formatTag).Data(FiveSamples).Build(), "formatTag " + formatTag);
        }

        [TestCase(8)]
        [TestCase(24)]
        [TestCase(32)]
        [TestCase(0)]
        public void TryDecode_Fails_WhenBitsPerSampleIsNot16(int bits)
        {
            var wav = new WavBuilder().Fmt(bitsPerSample: bits, blockAlign: Math.Max(1, bits / 8)).Data(FiveSamples).Build();

            AssertFails(wav, "bits " + bits);
        }

        [TestCase(0)]
        [TestCase(8)]
        [TestCase(15)]
        [TestCase(17)]
        [TestCase(24)]
        [TestCase(32)]
        [TestCase(65535)]
        public void TryDecode_Fails_WhenOnlyBitsPerSampleDiffersFromPcm16Mono(int bits)
        {
            // チャンネル数・blockAlign・サンプリング周波数・data は 16bit モノラルとして正しい
            var wav = new WavBuilder().Fmt(channels: 1, bitsPerSample: bits, blockAlign: 2).Data(FiveSamples).Build();

            AssertFails(wav, "bits " + bits + " blockAlign 2");
        }

        [TestCase(0)]
        [TestCase(3)]
        [TestCase(6)]
        [TestCase(65535)]
        public void TryDecode_Fails_WhenChannelsIsNotOneOrTwo(int channels)
        {
            var wav = new WavBuilder().Fmt(channels: channels, blockAlign: 2).Data(FiveSamples).Build();

            AssertFails(wav, "channels " + channels);
        }

        [TestCase(3)]
        [TestCase(4)]
        [TestCase(6)]
        [TestCase(8)]
        [TestCase(32767)]
        public void TryDecode_Fails_WhenChannelsIsNotOneOrTwoEvenIfBlockAlignMatchesChannels(int channels)
        {
            // blockAlign はそのチャンネル数の 16bit の値、data は 2 フレーム分ある
            var samples = Enumerable.Range(0, channels * 2).Select(i => (short)(i % 100)).ToArray();
            var wav = new WavBuilder().Fmt(channels: channels, blockAlign: channels * 2).Data(samples).Build();

            AssertFails(wav, "channels " + channels + " blockAlign " + channels * 2);
        }

        [TestCase(0)]
        [TestCase(384001)]
        [TestCase(-1)]
        [TestCase(int.MinValue)]
        public void TryDecode_Fails_WhenSampleRateIsOutOfRange(int sampleRate)
        {
            // -1 と int.MinValue は uint として 0xFFFFFFFF と 0x80000000 になる
            var wav = new WavBuilder().Fmt(sampleRate: sampleRate, blockAlign: 2).Data(FiveSamples).Build();

            AssertFails(wav, "sampleRate " + sampleRate);
        }

        [TestCase(1, 4)]
        [TestCase(1, 1)]
        [TestCase(1, 0)]
        [TestCase(2, 2)]
        [TestCase(2, 8)]
        public void TryDecode_Fails_WhenBlockAlignDoesNotMatchChannels(int channels, int blockAlign)
        {
            var wav = new WavBuilder().Fmt(channels: channels, blockAlign: blockAlign).Data(new short[] { 1, 2, 3, 4, 5, 6, 7, 8 }).Build();

            AssertFails(wav, "channels " + channels + " blockAlign " + blockAlign);
        }

        [Test]
        public void TryDecode_Fails_WhenFmtChunkIsShorterThan16Bytes()
        {
            var fmt = new byte[] { 1, 0, 1, 0, 0xC0, 0x5D, 0, 0, 0x80, 0xBB, 0, 0, 2, 0 };
            var wav = new WavBuilder().Chunk("fmt ", fmt).Data(FiveSamples).Build();

            AssertFails(wav, "fmt 14 バイト");
        }

        [Test]
        public void TryDecode_Fails_WhenFmtChunkIs15BytesEvenIfPadByteCompletesBitsPerSample()
        {
            // 15 バイト目までは 16bit モノラルの fmt と同じ。続く詰め物の 0 と合わせるとビット数 16 と同じ並びになる
            var fmt = Pcm16MonoFmtBody().Take(15).ToArray();
            var wav = new WavBuilder().Chunk("fmt ", fmt).Data(FiveSamples).Build();

            AssertFails(wav, "fmt 15 バイト");
        }

        [Test]
        public void TryDecode_Fails_WhenFmtChunkIs14BytesEvenIfNextChunkIdReadsAsBitsPerSample16()
        {
            // fmt の直後のチャンク ID の先頭 2 バイトが 0x10 0x00（ビット数 16 と同じ並び）
            var fmt = Pcm16MonoFmtBody().Take(14).ToArray();
            var nextId = new string(new[] { (char)0x10, (char)0x00, 'x', 'x' });
            var wav = new WavBuilder().Chunk("fmt ", fmt).Chunk(nextId, new byte[0]).Data(FiveSamples).Build();

            AssertFails(wav, "fmt 14 バイト＋ID が 0x10 0x00 で始まるチャンク");
        }

        [Test]
        public void TryDecode_Fails_WhenFmtChunkAppearsTwice()
        {
            var wav = new WavBuilder().Fmt().Data(FiveSamples).Fmt(sampleRate: 16000).Build();

            AssertFails(wav, "fmt が 2 つ");
        }

        [Test]
        public void TryDecode_Fails_WhenDataChunkAppearsTwice()
        {
            var wav = new WavBuilder().Fmt().Data(FiveSamples).Data(new short[] { 7, 8, 9 }).Build();

            AssertFails(wav, "data が 2 つ");
        }

        [TestCase("fmt ")]
        [TestCase("data")]
        public void TryDecode_Fails_WhenSecondChunkWithSameIdIsEmptyAndEndsExactlyAtEndOfBytes(string id)
        {
            // 末尾の 8 バイトはヘッダだけのチャンク（長さ 0）
            var wav = new WavBuilder().Fmt().Data(FiveSamples).Chunk(id, new byte[0]).Build();

            AssertFails(wav, "末尾に長さ 0 の '" + id + "'");
        }

        [Test]
        public void TryDecode_Fails_WhenFmtIsMissing()
        {
            AssertFails(new WavBuilder().Chunk("LIST", new byte[4]).Data(FiveSamples).Build(), "fmt なし");
        }

        [Test]
        public void TryDecode_Fails_WhenDataIsMissing()
        {
            AssertFails(new WavBuilder().Fmt().Chunk("LIST", new byte[4]).Build(), "data なし");
        }

        [Test]
        public void TryDecode_Fails_WhenDataIsEmpty()
        {
            AssertFails(new WavBuilder().Fmt().Chunk("data", new byte[0]).Build(), "data 0 バイト");
        }

        [Test]
        public void TryDecode_Fails_WhenDataIsShorterThanOneFrame()
        {
            AssertFails(new WavBuilder().Fmt(channels: 2).Chunk("data", new byte[] { 1, 0, 2 }).Build(), "1 フレーム未満");
        }

        [TestCase(11u)]
        [TestCase(12u)]
        [TestCase(1000u)]
        [TestCase(0x7FFFFFFFu)]
        [TestCase(0x80000000u)]
        [TestCase(0xFFFFFFFFu)]
        [TestCase(0xFFFFFFF8u)]
        public void TryDecode_Fails_WhenDataChunkLengthExceedsRemainingBytes(uint declared)
        {
            var wav = new WavBuilder().Fmt().Chunk("data", WavBuilder.Pcm16Bytes(FiveSamples), declaredSize: declared, pad: false).Build();

            AssertFails(wav, "data の長さ " + declared);
        }

        [TestCase(100u)]
        [TestCase(0x80000000u)]
        [TestCase(0xFFFFFFFFu)]
        [TestCase(0xFFFFFFF8u)]
        public void TryDecode_Fails_WhenUnknownChunkLengthExceedsRemainingBytes(uint declared)
        {
            var wav = new WavBuilder().Fmt().Chunk("LIST", new byte[4], declaredSize: declared).Data(FiveSamples).Build();

            AssertFails(wav, "LIST の長さ " + declared);
        }

        [TestCase(100u)]
        [TestCase(0xFFFFFFF8u)]
        public void TryDecode_Fails_WhenChunkAfterDataHasLengthExceedingRemainingBytes(uint declared)
        {
            var wav = new WavBuilder().Fmt().Data(FiveSamples).Chunk("LIST", new byte[4], declaredSize: declared).Build();

            AssertFails(wav, "data のあとの LIST の長さ " + declared);
        }

        [TestCase(0xFFFFFFFFu)]
        [TestCase(0x80000000u)]
        public void TryDecode_Fails_WhenFmtChunkLengthExceedsRemainingBytes(uint declared)
        {
            var wav = new WavBuilder().Chunk("fmt ", new byte[16], declaredSize: declared).Build();

            AssertFails(wav, "fmt の長さ " + declared);
        }

        [TestCase(17u)]
        [TestCase(100u)]
        [TestCase(0x7FFFFFFFu)]
        public void TryDecode_Fails_WhenFmtChunkAfterDataHasLengthExceedingRemainingBytes(uint declared)
        {
            // data が先にあり、末尾の fmt は中身 16 バイトが揃っていて長さの値だけが実際より長い
            var wav = new WavBuilder().Data(FiveSamples).Chunk("fmt ", Pcm16MonoFmtBody(), declaredSize: declared).Build();

            AssertFails(wav, "data のあとの fmt の長さ " + declared);
        }

        [Test]
        public void TryDecode_FailsWithoutThrowing_ForEveryProperPrefixOfValidWav()
        {
            var wav = new WavBuilder().Chunk("LIST", new byte[3]).Fmt().Data(FiveSamples).Build();

            for (var length = 0; length < wav.Length; length++)
            {
                AssertFails(wav.Take(length).ToArray(), "prefix " + length);
            }
        }

        [Test]
        public void TryDecode_NeverThrows_ForRandomByteCorruption()
        {
            var original = new WavBuilder().Chunk("LIST", new byte[5]).Fmt(channels: 2).Data(new short[] { 1, 2, 3, 4, 5, 6 }).Build();
            var random = new Random(1014);

            for (var round = 0; round < 5000; round++)
            {
                var wav = (byte[])original.Clone();
                var flips = 1 + random.Next(4);
                for (var f = 0; f < flips; f++)
                {
                    wav[random.Next(wav.Length)] = (byte)random.Next(256);
                }

                var audio = default(PcmAudio);
                string error = null;
                var ok = false;
                Assert.DoesNotThrow(() => ok = WavDecoder.TryDecode(wav, out audio, out error), "round " + round);
                if (ok)
                {
                    Assert.That(audio.Channels == 1 || audio.Channels == 2, Is.True, "round " + round);
                    Assert.That(audio.SampleRate, Is.InRange(1, 384000), "round " + round);
                    Assert.That(audio.FrameCount, Is.GreaterThan(0), "round " + round);
                    Assert.That(audio.Samples.Length, Is.EqualTo(audio.FrameCount * audio.Channels), "round " + round);
                    Assert.That(audio.Samples.All(s => s >= -1f && s < 1f), Is.True, "round " + round);
                }
                else
                {
                    Assert.That(error, Is.Not.Null.And.Not.Empty, "round " + round);
                }
            }
        }

        // ---- base64 ----

        [Test]
        public void TryDecodeBase64_ReadsWav_WhenBase64IsValid()
        {
            PcmAudio audio;
            string error;

            var ok = WavDecoder.TryDecodeBase64(WavBuilder.Pcm16Base64(FiveSamples, channels: 1, sampleRate: 48000), out audio, out error);

            Assert.That(ok, Is.True, error);
            Assert.That(audio.SampleRate, Is.EqualTo(48000));
            Assert.That(audio.FrameCount, Is.EqualTo(5));
            Assert.That(audio.Samples[2], Is.EqualTo(-1f));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("!!!!")]
        [TestCase("abc")]
        [TestCase("UklGRg=")]
        [TestCase("UklGRg==x")]
        [TestCase("ＵｋｌＧＲｇ＝＝")]
        [TestCase("2026-10-14T12:00:00Z")]
        public void TryDecodeBase64_Fails_WhenBase64IsBrokenOrEmpty(string base64)
        {
            PcmAudio audio;
            string error = null;
            var ok = true;

            Assert.DoesNotThrow(() => ok = WavDecoder.TryDecodeBase64(base64, out audio, out error));

            Assert.That(ok, Is.False);
            Assert.That(error, Is.Not.Null.And.Not.Empty);
        }

        [Test]
        public void TryDecodeBase64_Fails_WhenDecodedBytesAreNotWav()
        {
            PcmAudio audio;
            string error;

            var ok = WavDecoder.TryDecodeBase64(Convert.ToBase64String(Encoding.UTF8.GetBytes("not a wav, just some text")), out audio, out error);

            Assert.That(ok, Is.False);
            Assert.That(error, Is.Not.Null.And.Not.Empty);
        }

        [Test]
        public void TryDecodeBase64_Fails_WhenWavIsNotPcm16()
        {
            PcmAudio audio;
            string error;
            var wav = new WavBuilder().Fmt(formatTag: 3, bitsPerSample: 32, blockAlign: 4).Chunk("data", new byte[16]).Build();

            var ok = WavDecoder.TryDecodeBase64(Convert.ToBase64String(wav), out audio, out error);

            Assert.That(ok, Is.False);
            Assert.That(error, Is.Not.Null.And.Not.Empty);
        }
    }
}
