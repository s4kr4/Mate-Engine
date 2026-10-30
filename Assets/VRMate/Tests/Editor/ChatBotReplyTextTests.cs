using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;

namespace VRMate.Tests
{
    /// <summary>
    /// サンプルの ChatBot（Assembly-CSharp）が AI の返事を ReplyBubbleWriter に任せていること、
    /// GetText() を入力欄（inputBubble）のほかから呼ばないことをソースで確かめる。
    /// 返事ごとの吹き出しの作り直しと文字列のつなぎ方は ReplyBubbleWriter が受け持ち、その振る舞いは ReplyBubbleWriterTests で確かめる。
    /// asmdef から Assembly-CSharp は参照できないので、ソースファイルを読み、コメントと文字列リテラルの中身を除いて調べる。
    /// 空白・改行・this. の違いは同じ形として扱う。
    /// </summary>
    public class ChatBotReplyTextTests
    {
        private const string RelativePath = "LLMUnity/Samples/ChatBot/ChatBot.cs";
        private const string WriterType = "ReplyBubbleWriter<Bubble>";

        // テストからの差し替え用。検査の検出力を ChatBot.cs を書き換えずに確かめるため、別のソースファイルを指せる
        private const string SourceOverrideVariable = "VRMATE_TEST_CHATBOT_SOURCE";

        private static readonly Regex WriterField = new Regex(@"(?<![\w.])ReplyBubbleWriter\s*<\s*Bubble\s*>\s+@?([A-Za-z_]\w*)\s*[;=]");

        /// <summary>ソースの一部（本文など）と、ソース全体の中での開始位置。</summary>
        private sealed class Span
        {
            public Span(string text, int start)
            {
                Text = text;
                Start = start;
            }

            public string Text { get; }

            public int Start { get; }
        }

        /// <summary>見つけたメソッドのパラメーター名の並びと本文（式本体なら式）。</summary>
        private sealed class Method
        {
            public Method(List<string> parameterNames, Span body)
            {
                ParameterNames = parameterNames;
                Body = body;
            }

            public List<string> ParameterNames { get; }

            public Span Body { get; }
        }

        private static string SourcePath()
        {
            var overridePath = System.Environment.GetEnvironmentVariable(SourceOverrideVariable);
            return string.IsNullOrEmpty(overridePath) ? Path.Combine(Application.dataPath, RelativePath) : overridePath;
        }

        /// <summary>ソースを読み、コメントを除き、文字列・文字リテラルの中身を _ に置き換えたもの（位置はそのまま）を返す。</summary>
        private static string ReadSource()
        {
            var path = SourcePath();
            Assert.That(File.Exists(path), Is.True, "ChatBot.cs が見つからない: " + path);

            var source = File.ReadAllText(path);
            Assert.That(source, Is.Not.Empty, "ChatBot.cs が空: " + path);

            var code = MaskCommentsAndLiterals(source);
            Assert.That(Regex.IsMatch(code, @"(?<![\w.])class\s+ChatBot\b"), Is.True, "ChatBot.cs に ChatBot クラスが無い: " + path);
            return code;
        }

        /// <summary>コメントを空白に、文字列・文字リテラルの中身を _ に置き換える。長さと改行の位置は変えない。</summary>
        private static string MaskCommentsAndLiterals(string source)
        {
            var result = new StringBuilder(source.Length);
            var i = 0;
            while (i < source.Length)
            {
                var c = source[i];
                var next = i + 1 < source.Length ? source[i + 1] : '\0';
                if (c == '/' && next == '/')
                {
                    while (i < source.Length && source[i] != '\n')
                    {
                        result.Append(source[i] == '\r' ? '\r' : ' ');
                        i++;
                    }

                    continue;
                }

                if (c == '/' && next == '*')
                {
                    var end = source.IndexOf("*/", i + 2, System.StringComparison.Ordinal);
                    end = end < 0 ? source.Length : end + 2;
                    for (; i < end; i++)
                    {
                        result.Append(source[i] == '\n' || source[i] == '\r' ? source[i] : ' ');
                    }

                    continue;
                }

                var verbatim = (c == '@' && next == '"') || (c == '@' && next == '$' && i + 2 < source.Length && source[i + 2] == '"');
                if (verbatim)
                {
                    while (source[i] != '"')
                    {
                        result.Append(source[i]);
                        i++;
                    }

                    result.Append('"');
                    i++;
                    while (i < source.Length)
                    {
                        if (source[i] == '"' && i + 1 < source.Length && source[i + 1] == '"')
                        {
                            result.Append("__");
                            i += 2;
                            continue;
                        }

                        if (source[i] == '"')
                        {
                            result.Append('"');
                            i++;
                            break;
                        }

                        result.Append(source[i] == '\n' || source[i] == '\r' ? source[i] : '_');
                        i++;
                    }

                    continue;
                }

                if (c == '"' || c == '\'')
                {
                    result.Append(c);
                    i++;
                    while (i < source.Length && source[i] != c && source[i] != '\n')
                    {
                        if (source[i] == '\\' && i + 1 < source.Length)
                        {
                            result.Append("__");
                            i += 2;
                            continue;
                        }

                        result.Append('_');
                        i++;
                    }

                    if (i < source.Length && source[i] == c)
                    {
                        result.Append(c);
                        i++;
                    }

                    continue;
                }

                result.Append(c);
                i++;
            }

            return result.ToString();
        }

        /// <summary>空白を詰め（識別子どうしの間は 1 つ残す）、this. を除く。</summary>
        private static string Squash(string code)
        {
            var squashed = Regex.Replace(code, @"\s+", " ").Trim();
            squashed = Regex.Replace(squashed, @" ?([^\w\s]) ?", "$1");
            return Regex.Replace(squashed, @"(?<![\w.])this\.", string.Empty);
        }

        /// <summary>code[open] の開き括弧に対応する閉じ括弧の位置。見つからなければテストを失敗させる。</summary>
        private static int Matching(string code, int open)
        {
            var opener = code[open];
            var closer = opener == '{' ? '}' : opener == '(' ? ')' : ']';
            var depth = 0;
            for (var i = open; i < code.Length; i++)
            {
                if (code[i] == opener)
                {
                    depth++;
                }
                else if (code[i] == closer)
                {
                    depth--;
                    if (depth == 0)
                    {
                        return i;
                    }
                }
            }

            Assert.Fail("対応する閉じ括弧が見つからない: " + code.Substring(open, System.Math.Min(80, code.Length - open)));
            return -1;
        }

        /// <summary>ソース全体での位置 index の、scope の本文の中での { } の深さ。</summary>
        private static int Depth(Span scope, int index)
        {
            var depth = 0;
            for (var i = scope.Start; i < index; i++)
            {
                var c = scope.Text[i - scope.Start];
                if (c == '{')
                {
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                }
            }

            return depth;
        }

        /// <summary>class name の本文（{ と } の内側）。ちょうど 1 つでなければテストを失敗させる。</summary>
        private static Span ClassBody(string code, string name)
        {
            var matches = new Regex(@"(?<![\w.])class\s+" + name + @"\b[^{;]*\{").Matches(code);
            Assert.That(matches.Count, Is.EqualTo(1), "class " + name + " の宣言がちょうど 1 つ見つからない");
            return BlockAt(code, matches[0].Index + matches[0].Length - 1);
        }

        private static Span BlockAt(string code, int open)
        {
            var close = Matching(code, open);
            return new Span(code.Substring(open + 1, close - open - 1), open + 1);
        }

        /// <summary>scope の直下から、戻り値が void で名前が name のメソッドをちょうど 1 つ探す。式本体（=&gt;）も受け付ける。</summary>
        private static Method FindVoidMethod(Span scope, string name)
        {
            var declaration = new Regex(@"(?<![\w.])void\s+@?" + name + @"\s*\(([^()]*)\)\s*(\{|=>)");
            var matches = declaration.Matches(scope.Text).Cast<Match>().Where(m => Depth(scope, scope.Start + m.Index) == 0).ToList();
            Assert.That(matches.Count, Is.EqualTo(1), "void " + name + " の宣言がちょうど 1 つ見つからない");

            var match = matches[0];
            var parameterNames = new List<string>();
            var parameterList = match.Groups[1].Value.Trim();
            if (parameterList.Length > 0)
            {
                foreach (var parameter in parameterList.Split(','))
                {
                    var parts = Squash(parameter).Split(' ');
                    Assert.That(parts.Length, Is.EqualTo(2), name + " のパラメーターが「型 名前」の形ではない: " + parameter.Trim());
                    parameterNames.Add(parts[1].TrimStart('@'));
                }
            }

            var marker = match.Groups[2];
            if (marker.Value == "{")
            {
                var body = BlockAt(scope.Text, marker.Index);
                return new Method(parameterNames, new Span(body.Text, scope.Start + body.Start));
            }

            var expressionStart = marker.Index + marker.Length;
            var end = expressionStart;
            var round = 0;
            while (end < scope.Text.Length && !(scope.Text[end] == ';' && round == 0))
            {
                round += scope.Text[end] == '(' ? 1 : scope.Text[end] == ')' ? -1 : 0;
                end++;
            }

            Assert.That(end, Is.LessThan(scope.Text.Length), name + " の式本体の終わりが見つからない");
            return new Method(parameterNames, new Span(scope.Text.Substring(expressionStart, end + 1 - expressionStart), scope.Start + expressionStart));
        }

        /// <summary>ChatBot の直下に宣言された ReplyBubbleWriter&lt;Bubble&gt; 型のフィールドの名前。1 つも無ければテストを失敗させる。</summary>
        private static List<string> WriterFieldNames(Span chatBot)
        {
            var names = WriterField.Matches(chatBot.Text).Cast<Match>()
                .Where(m => Depth(chatBot, chatBot.Start + m.Index) == 0)
                .Select(m => m.Groups[1].Value)
                .ToList();
            Assert.That(names, Is.Not.Empty, "ChatBot に " + WriterType + " 型のフィールドが無い");
            return names;
        }

        [Test]
        public void OnSpeechTextAppended_DelegatesToReplyBubbleWriter()
        {
            var chatBot = ClassBody(ReadSource(), "ChatBot");
            var writers = WriterFieldNames(chatBot);
            var method = FindVoidMethod(chatBot, "OnSpeechTextAppended");
            Assert.That(method.ParameterNames.Count, Is.EqualTo(3), "OnSpeechTextAppended のパラメーターが 3 つではない");

            var body = Squash(method.Body.Text);
            var arguments = string.Join(",", method.ParameterNames.Select(name => "@?" + Regex.Escape(name)));
            var receivers = string.Join("|", writers.Select(Regex.Escape));
            var delegation = new Regex(@"(?<![\w.@])@?(?:" + receivers + @")\??\.Append\(" + arguments + @"\)");

            var expected = writers[0] + ".Append(" + string.Join(", ", method.ParameterNames) + ")";
            Assert.That(delegation.IsMatch(body), Is.True, "OnSpeechTextAppended の本文に「" + expected + "」の呼び出しが無い:\n  " + body);
        }

        [Test]
        public void ChatBot_ReadsGetTextOnlyFromInputBubble()
        {
            var squashed = Squash(ReadSource());

            foreach (Match call in new Regex(@"(?<![\w@])@?GetText\(").Matches(squashed))
            {
                var from = System.Math.Max(0, call.Index - 40);
                var around = squashed.Substring(from, System.Math.Min(squashed.Length, call.Index + 40) - from);
                var receiver = Regex.IsMatch(squashed.Substring(0, call.Index), @"(?<![\w.@])@?inputBubble\??\.$");
                Assert.That(receiver, Is.True, "inputBubble のほかから GetText() を読んでいる: " + around);
            }
        }
    }
}
