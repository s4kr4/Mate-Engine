using System;
using System.IO;
using System.Text;

namespace VRMate.Tests
{
    /// <summary>
    /// テスト用の WAV のバイト列を組み立てる。チャンクの順序・長さの値・詰め物を自由に決められる。
    /// </summary>
    internal sealed class WavBuilder
    {
        private readonly MemoryStream _body = new MemoryStream();
        private uint? _riffSize;

        /// <summary>RIFF ヘッダの全体長の値を、実際の長さと無関係な値にする。</summary>
        public WavBuilder RiffSize(uint value)
        {
            _riffSize = value;
            return this;
        }

        /// <summary>任意のチャンクを足す。declaredSize で長さの値を偽れる。pad が true なら奇数長のあとに詰め物を 1 バイト足す。</summary>
        public WavBuilder Chunk(string id, byte[] data, uint? declaredSize = null, bool pad = true)
        {
            var idBytes = Encoding.ASCII.GetBytes(id);
            if (idBytes.Length != 4)
            {
                throw new ArgumentException("chunk id must be 4 ASCII chars", nameof(id));
            }

            _body.Write(idBytes, 0, 4);
            WriteUInt32(_body, declaredSize ?? (uint)data.Length);
            _body.Write(data, 0, data.Length);
            if (pad && data.Length % 2 == 1)
            {
                _body.WriteByte(0);
            }

            return this;
        }

        public WavBuilder Fmt(
            int formatTag = 1,
            int channels = 1,
            int sampleRate = 24000,
            int bitsPerSample = 16,
            int? blockAlign = null,
            byte[] extra = null)
        {
            var align = blockAlign ?? channels * bitsPerSample / 8;
            var data = new MemoryStream();
            WriteUInt16(data, (ushort)formatTag);
            WriteUInt16(data, (ushort)channels);
            WriteUInt32(data, (uint)sampleRate);
            WriteUInt32(data, (uint)(sampleRate * align));
            WriteUInt16(data, (ushort)align);
            WriteUInt16(data, (ushort)bitsPerSample);
            if (extra != null)
            {
                data.Write(extra, 0, extra.Length);
            }

            return Chunk("fmt ", data.ToArray());
        }

        public WavBuilder Data(short[] samples)
        {
            return Chunk("data", Pcm16Bytes(samples));
        }

        public byte[] Build()
        {
            var output = new MemoryStream();
            output.Write(Encoding.ASCII.GetBytes("RIFF"), 0, 4);
            WriteUInt32(output, _riffSize ?? (uint)(4 + _body.Length));
            output.Write(Encoding.ASCII.GetBytes("WAVE"), 0, 4);
            var body = _body.ToArray();
            output.Write(body, 0, body.Length);
            return output.ToArray();
        }

        /// <summary>fmt → data の素直な PCM 16bit の WAV。</summary>
        public static byte[] Pcm16(short[] samples, int channels = 1, int sampleRate = 24000)
        {
            return new WavBuilder().Fmt(channels: channels, sampleRate: sampleRate).Data(samples).Build();
        }

        public static string Pcm16Base64(short[] samples, int channels = 1, int sampleRate = 24000)
        {
            return Convert.ToBase64String(Pcm16(samples, channels, sampleRate));
        }

        public static byte[] Pcm16Bytes(short[] samples)
        {
            var bytes = new byte[samples.Length * 2];
            for (var i = 0; i < samples.Length; i++)
            {
                bytes[i * 2] = (byte)(samples[i] & 0xFF);
                bytes[i * 2 + 1] = (byte)((samples[i] >> 8) & 0xFF);
            }

            return bytes;
        }

        private static void WriteUInt16(Stream stream, ushort value)
        {
            stream.WriteByte((byte)(value & 0xFF));
            stream.WriteByte((byte)((value >> 8) & 0xFF));
        }

        private static void WriteUInt32(Stream stream, uint value)
        {
            stream.WriteByte((byte)(value & 0xFF));
            stream.WriteByte((byte)((value >> 8) & 0xFF));
            stream.WriteByte((byte)((value >> 16) & 0xFF));
            stream.WriteByte((byte)((value >> 24) & 0xFF));
        }
    }
}
