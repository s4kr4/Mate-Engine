using System;

namespace VRMate
{
    public sealed class PcmAudio
    {
        public PcmAudio(int channels, int sampleRate, float[] samples)
        {
            Channels = channels;
            SampleRate = sampleRate;
            Samples = samples;
            FrameCount = samples.Length / channels;
        }

        public int Channels { get; }

        public int SampleRate { get; }

        public int FrameCount { get; }

        /// <summary>Interleaved samples in [-1, 1).</summary>
        public float[] Samples { get; }

        public double DurationSeconds
        {
            get { return (double)FrameCount / SampleRate; }
        }
    }

    /// <summary>
    /// Reads RIFF/WAVE PCM 16-bit mono or stereo. Every length field is checked against the bytes
    /// actually present, so broken input fails with an error instead of throwing.
    /// </summary>
    public static class WavDecoder
    {
        public const int MaxSampleRate = 384000;

        private const int HeaderSize = 12;
        private const int ChunkHeaderSize = 8;
        private const int MinFmtSize = 16;
        private const int PcmFormat = 1;

        public static bool TryDecodeBase64(string base64, out PcmAudio audio, out string error)
        {
            audio = null;
            if (string.IsNullOrEmpty(base64))
            {
                error = "audio: empty base64";
                return false;
            }

            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(base64);
            }
            catch (FormatException)
            {
                error = "audio: broken base64";
                return false;
            }

            return TryDecode(bytes, out audio, out error);
        }

        public static bool TryDecode(byte[] wav, out PcmAudio audio, out string error)
        {
            audio = null;
            try
            {
                return TryDecodeCore(wav, out audio, out error);
            }
            catch (Exception e)
            {
                // The checks below should make this unreachable; it keeps the never-throw contract.
                audio = null;
                error = "wav: unreadable (" + e.GetType().Name + ")";
                return false;
            }
        }

        private static bool TryDecodeCore(byte[] wav, out PcmAudio audio, out string error)
        {
            audio = null;
            if (wav == null || wav.Length < HeaderSize)
            {
                error = "wav: too short for a RIFF header";
                return false;
            }

            if (!HasId(wav, 0, "RIFF") || !HasId(wav, 8, "WAVE"))
            {
                error = "wav: not RIFF/WAVE";
                return false;
            }

            // The RIFF size field is ignored; chunks are walked up to the real end of the bytes.
            long position = HeaderSize;
            var fmtOffset = -1;
            var fmtSize = 0;
            var dataOffset = -1;
            var dataSize = 0;
            while (wav.Length - position >= ChunkHeaderSize)
            {
                var offset = (int)position;
                var size = ReadUInt32(wav, offset + 4);
                var bodyStart = position + ChunkHeaderSize;
                if (size > wav.Length - bodyStart)
                {
                    error = "wav: chunk '" + ChunkName(wav, offset) + "' is longer than the remaining bytes";
                    return false;
                }

                if (HasId(wav, offset, "fmt "))
                {
                    if (fmtOffset >= 0)
                    {
                        error = "wav: more than one fmt chunk";
                        return false;
                    }

                    fmtOffset = (int)bodyStart;
                    fmtSize = (int)size;
                }
                else if (HasId(wav, offset, "data"))
                {
                    if (dataOffset >= 0)
                    {
                        error = "wav: more than one data chunk";
                        return false;
                    }

                    dataOffset = (int)bodyStart;
                    dataSize = (int)size;
                }

                // Odd-sized chunks are followed by a pad byte, which may be missing at the very end.
                position = bodyStart + size + (size & 1);
            }

            if (fmtOffset < 0)
            {
                error = "wav: no fmt chunk";
                return false;
            }

            if (dataOffset < 0)
            {
                error = "wav: no data chunk";
                return false;
            }

            if (fmtSize < MinFmtSize)
            {
                error = "wav: fmt chunk is shorter than 16 bytes";
                return false;
            }

            var formatTag = ReadUInt16(wav, fmtOffset);
            var channels = ReadUInt16(wav, fmtOffset + 2);
            var sampleRate = ReadUInt32(wav, fmtOffset + 4);
            var blockAlign = ReadUInt16(wav, fmtOffset + 12);
            var bitsPerSample = ReadUInt16(wav, fmtOffset + 14);

            if (formatTag != PcmFormat)
            {
                error = "wav: unsupported format tag " + formatTag + " (PCM only)";
                return false;
            }

            if (bitsPerSample != 16)
            {
                error = "wav: unsupported bits per sample " + bitsPerSample + " (16 only)";
                return false;
            }

            if (channels != 1 && channels != 2)
            {
                error = "wav: unsupported channel count " + channels;
                return false;
            }

            if (sampleRate < 1 || sampleRate > MaxSampleRate)
            {
                error = "wav: sample rate " + sampleRate + " out of range";
                return false;
            }

            if (blockAlign != channels * 2)
            {
                error = "wav: block align " + blockAlign + " does not match " + channels + " channel(s)";
                return false;
            }

            var frames = dataSize / blockAlign;
            if (frames == 0)
            {
                error = "wav: no complete sample frame";
                return false;
            }

            var samples = new float[frames * channels];
            for (var i = 0; i < samples.Length; i++)
            {
                var at = dataOffset + i * 2;
                var value = (short)(wav[at] | (wav[at + 1] << 8));
                samples[i] = value / 32768f;
            }

            audio = new PcmAudio(channels, (int)sampleRate, samples);
            error = null;
            return true;
        }

        private static bool HasId(byte[] bytes, int offset, string id)
        {
            for (var i = 0; i < 4; i++)
            {
                if (bytes[offset + i] != id[i])
                {
                    return false;
                }
            }

            return true;
        }

        private static string ChunkName(byte[] bytes, int offset)
        {
            var chars = new char[4];
            for (var i = 0; i < 4; i++)
            {
                var b = bytes[offset + i];
                chars[i] = b >= 0x20 && b < 0x7F ? (char)b : '?';
            }

            return new string(chars);
        }

        private static int ReadUInt16(byte[] bytes, int offset)
        {
            return bytes[offset] | (bytes[offset + 1] << 8);
        }

        private static long ReadUInt32(byte[] bytes, int offset)
        {
            return (uint)(bytes[offset] | (bytes[offset + 1] << 8) | (bytes[offset + 2] << 16) | (bytes[offset + 3] << 24));
        }
    }
}
