using System;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace RuLocalization
{
    public class GoogleTranslateProvider : ITranslationProvider
    {
        private static readonly Regex HtmlTagRegex = new Regex(@"<[^>]+>", RegexOptions.Compiled);

        static GoogleTranslateProvider()
        {
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11;
                ServicePointManager.ServerCertificateValidationCallback = AcceptAllCertificates;
            }
            catch
            {
            }
        }

        private static bool AcceptAllCertificates(object sender, X509Certificate cert, X509Chain chain, SslPolicyErrors errors)
        {
            return true;
        }

        public string Translate(string text, string fromLang, string toLang)
        {
            if (string.IsNullOrEmpty(text)) return text;

            try
            {
                var encoded = Uri.EscapeDataString(text);
                var url = $"https://translate.googleapis.com/translate_a/single?client=gtx&sl={fromLang}&tl={toLang}&dt=t&q={encoded}";

                var request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "GET";
                request.UserAgent = "Mozilla/5.0";
                request.Timeout = 10000;

                using (var response = (HttpWebResponse)request.GetResponse())
                using (var stream = response.GetResponseStream())
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    var json = reader.ReadToEnd();
                    return ParseResponse(json);
                }
            }
            catch (Exception e)
            {
                MetricsManager.LogError($"RuLocalization Google: {e.Message}");
                return null;
            }
        }

        private string ParseResponse(string json)
        {
            try
            {
                var arr = JArray.Parse(json);
                var sentences = arr[0];
                var sb = new StringBuilder();

                foreach (var sentence in sentences)
                {
                    var translated = sentence[0]?.ToString();
                    if (!string.IsNullOrEmpty(translated))
                        sb.Append(translated);
                }

                var result = sb.ToString();
                result = HtmlTagRegex.Replace(result, "");
                return result.Trim();
            }
            catch
            {
                return null;
            }
        }
    }
}
