using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace VRMate.Tests
{
    /// <summary>
    /// テストで JSON を読み書きするための補助。日時や小数を勝手に変換しない設定で読む。
    /// </summary>
    internal static class TestJson
    {
        /// <summary>JSON の文字列リテラル（引用符つき・エスケープ済み）にする。</summary>
        public static string Quote(string value)
        {
            return JsonConvert.ToString(value);
        }

        /// <summary>1 つの JSON object として読む。object でない・後ろに余計な内容がある場合はテストを失敗させる。</summary>
        public static JObject ReadObject(string json)
        {
            Assert.That(json, Is.Not.Null, "JSON が null");
            using (var reader = new JsonTextReader(new StringReader(json)))
            {
                reader.DateParseHandling = DateParseHandling.None;
                reader.FloatParseHandling = FloatParseHandling.Double;
                var token = JToken.ReadFrom(reader);
                Assert.That(reader.Read(), Is.False, "JSON の後ろに余計な内容がある: " + json);
                Assert.That(token.Type, Is.EqualTo(JTokenType.Object), "JSON が object でない: " + json);
                return (JObject)token;
            }
        }
    }
}
