using System;
using System.Linq;
using System.Text;
using NUnit.Framework;

namespace VRMate.Tests
{
    public class MessageAssemblerTests
    {
        private static AssembleResult AppendText(MessageAssembler assembler, byte[] bytes, bool end)
        {
            return assembler.Append(bytes, 0, bytes.Length, true, end);
        }

        [Test]
        public void DefaultMaxBytes_Is8MiB()
        {
            Assert.That(MessageAssembler.DefaultMaxBytes, Is.EqualTo(8 * 1024 * 1024));
        }

        [Test]
        public void Append_ReturnsText_WhenSingleFrameEndsMessage()
        {
            var assembler = new MessageAssembler(1024);

            var result = AppendText(assembler, Encoding.UTF8.GetBytes("{\"type\":\"cancel\"}"), true);

            Assert.That(result.Status, Is.EqualTo(AssembleStatus.Text));
            Assert.That(result.Text, Is.EqualTo("{\"type\":\"cancel\"}"));
        }

        [Test]
        public void Append_ReturnsPendingThenJoinedText_WhenMessageIsFragmented()
        {
            var assembler = new MessageAssembler(1024);

            var first = AppendText(assembler, Encoding.UTF8.GetBytes("hel"), false);
            var second = AppendText(assembler, Encoding.UTF8.GetBytes("lo, "), false);
            var third = AppendText(assembler, Encoding.UTF8.GetBytes("world"), true);

            Assert.That(first.Status, Is.EqualTo(AssembleStatus.Pending));
            Assert.That(second.Status, Is.EqualTo(AssembleStatus.Pending));
            Assert.That(third.Status, Is.EqualTo(AssembleStatus.Text));
            Assert.That(third.Text, Is.EqualTo("hello, world"));
        }

        [Test]
        public void Append_ReturnsEmptyText_WhenMessageHasNoBytes()
        {
            var assembler = new MessageAssembler(1024);

            var result = assembler.Append(new byte[0], 0, 0, true, true);

            Assert.That(result.Status, Is.EqualTo(AssembleStatus.Text));
            Assert.That(result.Text, Is.EqualTo(string.Empty));
        }

        [Test]
        public void Append_DecodesUtf8_WhenFragmentBoundarySplitsMultibyteCharacters()
        {
            var text = "aあ😀é👨" + (char)0x200D + "👩" + (char)0x200D + "👧z";
            var bytes = Encoding.UTF8.GetBytes(text);

            for (var split = 0; split <= bytes.Length; split++)
            {
                var assembler = new MessageAssembler(1024);
                var first = assembler.Append(bytes, 0, split, true, false);
                var second = assembler.Append(bytes, split, bytes.Length - split, true, true);

                Assert.That(first.Status, Is.EqualTo(AssembleStatus.Pending), "split " + split);
                Assert.That(second.Status, Is.EqualTo(AssembleStatus.Text), "split " + split);
                Assert.That(second.Text, Is.EqualTo(text), "split " + split);
            }
        }

        [Test]
        public void Append_DecodesUtf8_WhenEveryFragmentIsOneByte()
        {
            var text = "日本語と😀の混ざった文";
            var bytes = Encoding.UTF8.GetBytes(text);
            var assembler = new MessageAssembler(1024);
            var last = default(AssembleResult);

            for (var i = 0; i < bytes.Length; i++)
            {
                last = assembler.Append(bytes, i, 1, true, i == bytes.Length - 1);
                if (i < bytes.Length - 1)
                {
                    Assert.That(last.Status, Is.EqualTo(AssembleStatus.Pending), "byte " + i);
                }
            }

            Assert.That(last.Status, Is.EqualTo(AssembleStatus.Text));
            Assert.That(last.Text, Is.EqualTo(text));
        }

        [Test]
        public void Append_ReadsOnlyGivenRange_WhenBufferHasBytesAroundIt()
        {
            var assembler = new MessageAssembler(1024);
            var buffer = Encoding.UTF8.GetBytes("XXXXhelloYYYY");

            var result = assembler.Append(buffer, 4, 5, true, true);

            Assert.That(result.Status, Is.EqualTo(AssembleStatus.Text));
            Assert.That(result.Text, Is.EqualTo("hello"));
        }

        [Test]
        public void Append_KeepsEarlierFragments_WhenCallerReusesTheSameBuffer()
        {
            var assembler = new MessageAssembler(1024);
            var buffer = new byte[8];

            Encoding.ASCII.GetBytes("abcd").CopyTo(buffer, 0);
            var first = assembler.Append(buffer, 0, 4, true, false);
            Encoding.ASCII.GetBytes("WXYZ").CopyTo(buffer, 0);
            var second = assembler.Append(buffer, 0, 4, true, true);

            Assert.That(first.Status, Is.EqualTo(AssembleStatus.Pending));
            Assert.That(second.Text, Is.EqualTo("abcdWXYZ"));
        }

        [Test]
        public void Append_ReplacesInvalidUtf8WithReplacementCharacter_WithoutThrowing()
        {
            var assembler = new MessageAssembler(1024);
            var bytes = new byte[] { (byte)'a', 0xFF, (byte)'b', 0xC3, 0x28, (byte)'c', 0xED, 0xA0, 0x80, (byte)'d', 0xE3 };

            var result = default(AssembleResult);
            Assert.DoesNotThrow(() => result = assembler.Append(bytes, 0, bytes.Length, true, true));

            Assert.That(result.Status, Is.EqualTo(AssembleStatus.Text));
            Assert.That(result.Text, Does.StartWith("a�"));
            Assert.That(result.Text, Does.Contain("b"));
            Assert.That(result.Text, Does.Contain("c"));
            Assert.That(result.Text, Does.Contain("d"));
            Assert.That(result.Text, Does.EndWith("�"));
            Assert.That(result.Text.Any(char.IsSurrogate), Is.False, "不正な UTF-8 からサロゲートを作らない");
        }

        [Test]
        public void Append_ReturnsBinaryWithoutContent_WhenMessageIsBinary()
        {
            var assembler = new MessageAssembler(1024);
            var bytes = Encoding.UTF8.GetBytes("{\"type\":\"cancel\"}");

            var first = assembler.Append(bytes, 0, 5, false, false);
            var second = assembler.Append(bytes, 5, bytes.Length - 5, false, true);

            Assert.That(first.Status, Is.EqualTo(AssembleStatus.Pending));
            Assert.That(second.Status, Is.EqualTo(AssembleStatus.Binary));
            Assert.That(second.Text, Is.Null);
        }

        [Test]
        public void Append_StartsFreshMessage_AfterBinaryMessage()
        {
            var assembler = new MessageAssembler(1024);
            assembler.Append(new byte[] { 1, 2, 3 }, 0, 3, false, true);

            var result = AppendText(assembler, Encoding.UTF8.GetBytes("next"), true);

            Assert.That(result.Status, Is.EqualTo(AssembleStatus.Text));
            Assert.That(result.Text, Is.EqualTo("next"));
        }

        [Test]
        public void Append_StartsFreshMessage_AfterTextMessage()
        {
            var assembler = new MessageAssembler(1024);
            AppendText(assembler, Encoding.UTF8.GetBytes("first"), true);

            var result = AppendText(assembler, Encoding.UTF8.GetBytes("second"), true);

            Assert.That(result.Text, Is.EqualTo("second"));
        }

        [Test]
        public void Append_AcceptsMessage_WhenSizeIsExactlyMaxBytes()
        {
            var assembler = new MessageAssembler(10);

            var first = AppendText(assembler, Encoding.ASCII.GetBytes("123456"), false);
            var second = AppendText(assembler, Encoding.ASCII.GetBytes("7890"), true);

            Assert.That(first.Status, Is.EqualTo(AssembleStatus.Pending));
            Assert.That(second.Status, Is.EqualTo(AssembleStatus.Text));
            Assert.That(second.Text, Is.EqualTo("1234567890"));
        }

        [Test]
        public void Append_ReturnsTooLarge_WhenSingleFrameExceedsMaxBytesByOne()
        {
            var assembler = new MessageAssembler(10);

            var result = AppendText(assembler, Encoding.ASCII.GetBytes("12345678901"), true);

            Assert.That(result.Status, Is.EqualTo(AssembleStatus.TooLarge));
        }

        [Test]
        public void Append_ReturnsTooLarge_AsSoonAsTotalExceedsMaxBytesBeforeEndOfMessage()
        {
            var assembler = new MessageAssembler(10);

            var first = AppendText(assembler, Encoding.ASCII.GetBytes("123456"), false);
            var second = AppendText(assembler, Encoding.ASCII.GetBytes("78901"), false);

            Assert.That(first.Status, Is.EqualTo(AssembleStatus.Pending));
            Assert.That(second.Status, Is.EqualTo(AssembleStatus.TooLarge));
        }

        [Test]
        public void Append_ReturnsTooLarge_WhenBinaryMessageExceedsMaxBytes()
        {
            var assembler = new MessageAssembler(10);

            var result = assembler.Append(new byte[11], 0, 11, false, true);

            Assert.That(result.Status, Is.EqualTo(AssembleStatus.TooLarge));
        }

        [Test]
        public void Append_AcceptsNextMessage_AfterTooLarge()
        {
            var assembler = new MessageAssembler(10);
            AppendText(assembler, Encoding.ASCII.GetBytes("123456"), false);
            AppendText(assembler, Encoding.ASCII.GetBytes("78901"), false);

            var result = AppendText(assembler, Encoding.ASCII.GetBytes("0123456789"), true);

            Assert.That(result.Status, Is.EqualTo(AssembleStatus.Text));
            Assert.That(result.Text, Is.EqualTo("0123456789"));
        }

        [Test]
        public void Append_AcceptsMessageOfExactlyDefaultMax_WhenFedIn64KiBFrames()
        {
            var assembler = new MessageAssembler(MessageAssembler.DefaultMaxBytes);
            var chunk = Enumerable.Repeat((byte)'x', 64 * 1024).ToArray();
            var chunks = MessageAssembler.DefaultMaxBytes / chunk.Length;
            var last = default(AssembleResult);

            for (var i = 0; i < chunks; i++)
            {
                last = AppendText(assembler, chunk, i == chunks - 1);
            }

            Assert.That(last.Status, Is.EqualTo(AssembleStatus.Text));
            Assert.That(last.Text.Length, Is.EqualTo(MessageAssembler.DefaultMaxBytes));
        }

        [Test]
        public void Append_ReturnsTooLarge_WhenDefaultMaxIsExceededByOneByte()
        {
            var assembler = new MessageAssembler(MessageAssembler.DefaultMaxBytes);
            var chunk = Enumerable.Repeat((byte)'x', 64 * 1024).ToArray();
            var chunks = MessageAssembler.DefaultMaxBytes / chunk.Length;

            for (var i = 0; i < chunks; i++)
            {
                Assert.That(AppendText(assembler, chunk, false).Status, Is.EqualTo(AssembleStatus.Pending), "chunk " + i);
            }

            var result = AppendText(assembler, new[] { (byte)'x' }, true);

            Assert.That(result.Status, Is.EqualTo(AssembleStatus.TooLarge));
        }
    }
}
