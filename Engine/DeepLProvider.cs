using System;
using System.IO;
using System.Net;
using System.Text;
using Newtonsoft.Json.Linq;

namespace RuLocalization
{
    /// <summary>
    /// DeepL API provider. Requires an API key (free tier: 500k chars/month).
    /// Superior Russian translation quality compared to Google Translate.
    /// </summary>
    public class DeepLProvider : ITranslationProvider
    {
        private readonly string _apiKey;
        private readonly string _baseUrl;

        public DeepLProvider(string apiKey)
        {
            _apiKey = apiKey;
            _baseUrl = apiKey != null && apiKey.EndsWith(":fx")
                ? "https://api-free.deepl.com/v2/translate"
                : "https://api.deepl.com/v2/translate";
        }

        public string Translate(string text, string fromLang, string toLang)
        {
            if (string.IsNullOrEmpty(_apiKey))
            {
                MetricsManager.LogError("RuLocalization DeepL: API ключ не задан");
                return null;
            }

            try
            {
                var sourceLang = fromLang.ToUpper();
                var targetLang = toLang.ToUpper();

                var postData = $"text={Uri.EscapeDataString(text)}&source_lang={sourceLang}&target_lang={targetLang}";
                var data = Encoding.UTF8.GetBytes(postData);

                var request = (HttpWebRequest)WebRequest.Create(_baseUrl);
                request.Method = "POST";
                request.ContentType = "application/x-www-form-urlencoded";
                request.Headers.Add("Authorization", $"DeepL-Auth-Key {_apiKey}");
                request.ContentLength = data.Length;
                request.Timeout = 15000;

                using (var reqStream = request.GetRequestStream())
                    reqStream.Write(data, 0, data.Length);

                using (var response = (HttpWebResponse)request.GetResponse())
                using (var stream = response.GetResponseStream())
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    var json = reader.ReadToEnd();
                    var obj = JObject.Parse(json);
                    return obj["translations"]?[0]?["text"]?.ToString();
                }
            }
            catch (Exception e)
            {
                MetricsManager.LogError($"RuLocalization DeepL: {e.Message}");
                return null;
            }
        }
    }
}
