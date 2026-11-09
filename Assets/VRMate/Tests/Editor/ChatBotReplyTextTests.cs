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
    /// 音声入力の表示（listen を入力欄のプレースホルダーに、heard をユーザーの吹き出しに）も VoiceInputDisplay に任せていること、
    /// つまり Update で Attach と Tick を、OnDestroy で Detach を呼び、IVoiceInputView を実装する入れ子クラスが受け取った文を吹き出しと入力欄へそのまま渡すことを確かめる。
    /// 購読の回数・解除・文が変わったときだけ書く判断は VoiceInputDisplay が受け持ち、その振る舞いは VoiceInputDisplayTests で確かめる。
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

        private const string DisplayType = "VoiceInputDisplay";

        // 接続していないときの文は VoiceInputDisplay（InputPlaceholder）が持つ
        private const string NotConnectedText = "頭脳につながっていません";

        private static readonly Regex DisplayField = new Regex(@"(?<![\w.])(?:VRMate\s*\.\s*)?VoiceInputDisplay\s+@?([A-Za-z_]\w*)\s*[;=]");

        private static readonly Regex IsConnectedRead = new Regex(@"(?<![\w@])@?IsConnected(?!\w)");

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

        /// <summary>ChatBot の直下に宣言された VoiceInputDisplay 型のフィールドの名前。1 つも無ければテストを失敗させる。</summary>
        private static List<string> DisplayFieldNames(Span chatBot)
        {
            var names = DisplayField.Matches(chatBot.Text).Cast<Match>()
                .Where(m => Depth(chatBot, chatBot.Start + m.Index) == 0)
                .Select(m => m.Groups[1].Value)
                .ToList();
            Assert.That(names, Is.Not.Empty, "ChatBot に " + DisplayType + " 型のフィールドが無い");
            return names;
        }

        /// <summary>Squash した本文で、フィールドのどれかを受け手にした「名前.method(」の呼び出しに合う正規表現の文字列。?. も受け付ける。</summary>
        private static string CallOn(List<string> fields, string method)
        {
            var receivers = string.Join("|", fields.Select(Regex.Escape));
            return @"(?<![\w.@])@?(?:" + receivers + @")\??\.@?" + method + @"\(";
        }

        /// <summary>
        /// Squash した本文 body の位置 index が、{ } の外にある文の先頭か。直前が本文の始まり・}・; のどれかなら文の先頭とみなす。
        /// if・else・while などの直後（波かっこなしの条件の中）や、ラムダの中は文の先頭ではない。
        /// </summary>
        private static bool IsTopLevelStatement(string body, int index)
        {
            if (Depth(new Span(body, 0), index) != 0)
            {
                return false;
            }

            var before = body.Substring(0, index).TrimEnd();
            return before.Length == 0 || before.EndsWith("}") || before.EndsWith(";");
        }

        /// <summary>
        /// Tick の引数 argument（Squash 済み）が IsConnected を読む式か。名前付き引数も受け付ける。
        /// 引数が 1 つの名前なら、同じ本文 body で IsConnected を読む式から初期化したローカル変数（bool か var）でもよい。
        /// </summary>
        private static bool ReadsIsConnected(string body, string argument)
        {
            var value = Regex.Replace(argument, @"^@?[A-Za-z_]\w*:(?!:)", string.Empty);
            if (IsConnectedRead.IsMatch(value))
            {
                return true;
            }

            var local = Regex.Match(value, @"^@?([A-Za-z_]\w*)$");
            if (!local.Success)
            {
                return false;
            }

            var declaration = new Regex(@"(?<![\w.@])(?:bool|var) ?@?" + Regex.Escape(local.Groups[1].Value) + @"=([^;]*);");
            return declaration.Matches(body).Cast<Match>().Any(m => IsConnectedRead.IsMatch(m.Groups[1].Value));
        }

        /// <summary>Squash した本文 body の文の先頭で、tick（「フィールド.Tick(」）を IsConnected を読む引数で呼んでいるか。</summary>
        private static bool TicksWithConnection(string body, Regex tick)
        {
            foreach (Match call in tick.Matches(body))
            {
                if (!IsTopLevelStatement(body, call.Index))
                {
                    continue;
                }

                var open = call.Index + call.Length - 1;
                var close = Matching(body, open);
                if (ReadsIsConnected(body, body.Substring(open + 1, close - open - 1)))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Squash した Update の本文の文の先頭で呼んでいる、ChatBot の直下の引数なしの void メソッドの本文（Squash 済み）。</summary>
        private static List<string> TopLevelHelperBodies(Span chatBot, string update)
        {
            var bodies = new List<string>();
            foreach (Match call in new Regex(@"(?<![\w.@])@?([A-Za-z_]\w*)\(\)").Matches(update))
            {
                if (!IsTopLevelStatement(update, call.Index))
                {
                    continue;
                }

                var name = call.Groups[1].Value;
                var declarations = new Regex(@"(?<![\w.])void\s+@?" + name + @"\s*\(([^()]*)\)\s*(\{|=>)").Matches(chatBot.Text).Cast<Match>()
                    .Where(m => Depth(chatBot, chatBot.Start + m.Index) == 0)
                    .ToList();
                if (declarations.Count != 1 || declarations[0].Groups[1].Value.Trim().Length > 0)
                {
                    continue;
                }

                bodies.Add(Squash(FindVoidMethod(chatBot, name).Body.Text));
            }

            return bodies;
        }

        /// <summary>ChatBot の直下にある、IVoiceInputView を実装する入れ子クラスの本文。ちょうど 1 つでなければテストを失敗させる。クラス名は問わない。</summary>
        private static Span VoiceInputViewBody(Span chatBot)
        {
            var declarations = new Regex(@"(?<![\w.])class\s+@?[A-Za-z_]\w*\s*:([^{;]*)\{").Matches(chatBot.Text).Cast<Match>()
                .Where(m => Depth(chatBot, chatBot.Start + m.Index) == 0)
                .Where(m => Regex.IsMatch(m.Groups[1].Value, @"(?<!\w)IVoiceInputView(?!\w)"))
                .ToList();
            Assert.That(declarations.Count, Is.EqualTo(1), "ChatBot の中に IVoiceInputView を実装するクラスがちょうど 1 つ見つからない");

            var open = declarations[0].Index + declarations[0].Length - 1;
            var body = BlockAt(chatBot.Text, open);
            return new Span(body.Text, chatBot.Start + body.Start);
        }

        /// <summary>入れ子の view の、パラメーターがちょうど 1 つの void メソッド name。パラメーター名と Squash した本文を返す。</summary>
        private static KeyValuePair<string, string> ViewMethod(Span view, string name)
        {
            var method = FindVoidMethod(view, name);
            Assert.That(method.ParameterNames.Count, Is.EqualTo(1), name + " のパラメーターが 1 つではない");
            return new KeyValuePair<string, string>(method.ParameterNames[0], Squash(method.Body.Text));
        }

        /// <summary>コメントだけを除いたソース（文字列・文字リテラルの中身は残す）。</summary>
        private static string ReadSourceKeepingLiterals()
        {
            var source = File.ReadAllText(SourcePath());
            var masked = MaskCommentsAndLiterals(source);
            var result = new StringBuilder(source.Length);
            for (var i = 0; i < source.Length; i++)
            {
                result.Append(masked[i] == '_' ? source[i] : masked[i]);
            }

            return result.ToString();
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

        // ---- 音声入力の表示は VoiceInputDisplay に任せる ----

        /// <summary>
        /// Update は、本文の直下（条件の中でない文）で VoiceInputDisplay のフィールドの Attach(BrainLink.Instance) と、
        /// IsConnected を読む式を渡す Tick を呼ぶ。Tick は、Update の本文の直下で呼ぶ引数なしの補助メソッドの本文の直下でもよい。
        /// </summary>
        [Test]
        public void Update_AttachesBrainLinkAndTicksWithConnection_InTopLevelStatements()
        {
            var chatBot = ClassBody(ReadSource(), "ChatBot");
            var fields = DisplayFieldNames(chatBot);
            var update = Squash(FindVoidMethod(chatBot, "Update").Body.Text);

            var attach = new Regex(CallOn(fields, "Attach") + @"(?:source:)?(?:VRMate\.)?BrainLink\.Instance\)");
            var attached = attach.Matches(update).Cast<Match>().Any(call => IsTopLevelStatement(update, call.Index));
            Assert.That(attached, Is.True, "Update の本文の直下（条件の中でない文）に「" + fields[0] + ".Attach(BrainLink.Instance)」が無い:\n  " + update);

            var tick = new Regex(CallOn(fields, "Tick"));
            var ticked = TicksWithConnection(update, tick) || TopLevelHelperBodies(chatBot, update).Any(helper => TicksWithConnection(helper, tick));
            Assert.That(ticked, Is.True, "Update（または Update の直下で呼ぶ補助メソッド）の本文の直下に、IsConnected を読む式を渡す「" + fields[0] + ".Tick(...)」が無い:\n  " + update);
        }

        /// <summary>OnDestroy は VoiceInputDisplay のフィールドの Detach() を呼ぶ。</summary>
        [Test]
        public void OnDestroy_DetachesVoiceInputDisplay()
        {
            var chatBot = ClassBody(ReadSource(), "ChatBot");
            var fields = DisplayFieldNames(chatBot);
            var onDestroy = Squash(FindVoidMethod(chatBot, "OnDestroy").Body.Text);

            var detach = new Regex(CallOn(fields, "Detach") + @"\)");
            Assert.That(detach.IsMatch(onDestroy), Is.True, "OnDestroy の本文に「" + fields[0] + ".Detach()」の呼び出しが無い:\n  " + onDestroy);
        }

        /// <summary>ChatBot は OnDestroy のほかでは Detach を呼ばない（パネルを閉じても heard は吹き出しになる）。</summary>
        [Test]
        public void ChatBot_CallsDetachOnlyFromOnDestroy()
        {
            var chatBot = ClassBody(ReadSource(), "ChatBot");
            var onDestroy = FindVoidMethod(chatBot, "OnDestroy").Body;

            var outside = new List<string>();
            foreach (Match call in new Regex(@"(?<![\w@])@?Detach\s*\(").Matches(chatBot.Text))
            {
                var index = chatBot.Start + call.Index;
                if (index >= onDestroy.Start && index < onDestroy.Start + onDestroy.Text.Length)
                {
                    continue;
                }

                var from = System.Math.Max(0, call.Index - 60);
                outside.Add(Squash(chatBot.Text.Substring(from, System.Math.Min(chatBot.Text.Length, call.Index + 20) - from)));
            }

            Assert.That(outside, Is.Empty, "OnDestroy のほかで Detach を呼んでいる:\n  " + string.Join("\n  ", outside));
        }

        /// <summary>IVoiceInputView を実装する ChatBot の入れ子クラスの AddPlayerBubble は、受け取った文をそのまま AddBubble(文, true) に渡す。</summary>
        [Test]
        public void VoiceInputView_AddPlayerBubble_PassesTextAsIsToAddBubbleAsPlayer()
        {
            var view = VoiceInputViewBody(ClassBody(ReadSource(), "ChatBot"));
            var method = ViewMethod(view, "AddPlayerBubble");

            var text = "@?" + Regex.Escape(method.Key);
            var addBubble = new Regex(@"(?<![\w@])@?AddBubble\((?:(?:message:)?" + text + @",(?:isPlayerMessage:)?true|isPlayerMessage:true,message:" + text + @")\)");
            Assert.That(addBubble.IsMatch(method.Value), Is.True, "AddPlayerBubble の本文に「AddBubble(" + method.Key + ", true)」の呼び出しが無い:\n  " + method.Value);
        }

        /// <summary>IVoiceInputView を実装する ChatBot の入れ子クラスの SetPlaceholder は、受け取った文をそのまま SetPlaceHolderText(文) に渡す。</summary>
        [Test]
        public void VoiceInputView_SetPlaceholder_PassesTextAsIsToSetPlaceHolderText()
        {
            var view = VoiceInputViewBody(ClassBody(ReadSource(), "ChatBot"));
            var method = ViewMethod(view, "SetPlaceholder");

            var setPlaceHolderText = new Regex(@"(?<![\w@])@?SetPlaceHolderText\((?:text:)?@?" + Regex.Escape(method.Key) + @"\)");
            Assert.That(setPlaceHolderText.IsMatch(method.Value), Is.True, "SetPlaceholder の本文に「SetPlaceHolderText(" + method.Key + ")」の呼び出しが無い:\n  " + method.Value);
        }

        /// <summary>IVoiceInputView を実装する ChatBot の入れ子クラスの AddPlayerBubble と SetPlaceholder は、SendInput も TrySend も呼ばない（聞き取った文を頭脳へ送り返さない）。</summary>
        [Test]
        public void VoiceInputView_DoesNotSendToBrain()
        {
            var view = VoiceInputViewBody(ClassBody(ReadSource(), "ChatBot"));
            var send = new Regex(@"(?<![\w@])@?(?:SendInput|TrySend)(?!\w)");

            foreach (var name in new[] { "AddPlayerBubble", "SetPlaceholder" })
            {
                var body = ViewMethod(view, name).Value;
                Assert.That(send.IsMatch(body), Is.False, name + " が SendInput か TrySend を呼んでいる:\n  " + body);
            }
        }

        /// <summary>ChatBot は BrainLink のイベント（Heard・ListenChanged・Disconnected）を自分で購読しない。</summary>
        [Test]
        public void ChatBot_DoesNotSubscribeBrainLinkEventsItself()
        {
            var squashed = Squash(ReadSource());

            var subscriptions = new Regex(@"(?<![\w@])@?(?:Heard|ListenChanged|Disconnected)(?:\+=|-=)").Matches(squashed).Cast<Match>()
                .Select(m => squashed.Substring(System.Math.Max(0, m.Index - 30), System.Math.Min(squashed.Length, m.Index + 50) - System.Math.Max(0, m.Index - 30)))
                .ToList();
            Assert.That(subscriptions, Is.Empty, "ChatBot が BrainLink のイベントを自分で購読・解除している:\n  " + string.Join("\n  ", subscriptions));
        }

        /// <summary>ChatBot は「頭脳につながっていません」の文を自前で持たない（コメントは除く）。</summary>
        [Test]
        public void ChatBot_DoesNotHoldNotConnectedText()
        {
            ReadSource();
            var code = ReadSourceKeepingLiterals();

            Assert.That(code.Contains(NotConnectedText), Is.False, "ChatBot.cs のコードに「" + NotConnectedText + "」の文がある");
        }
    }
}
