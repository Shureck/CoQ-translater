using System;
using System.IO;
using System.Net;
using System.Text;
using Newtonsoft.Json.Linq;

namespace RuLocalization
{
    /// <summary>
    /// Local translation provider backed by a Python service
    /// (for example Helsinki-NLP/opus-mt-en-ru served locally).
    /// </summary>
    public class LocalHuggingFaceProvider : ITranslationProvider
    {
        private readonly string _serviceUrl;
        private readonly int _timeoutMs;

        public LocalHuggingFaceProvider(string serviceUrl, int timeoutMs)
        {
            _serviceUrl = string.IsNullOrEmpty(serviceUrl)
                ? "http://127.0.0.1:5005/translate"
                : serviceUrl;
            _timeoutMs = Math.Max(1000, timeoutMs);
        }

        public string Translate(string text, string fromLang, string toLang)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            try
            {
                var payload = new JObject
                {
                    ["text"] = text,
                    ["source_lang"] = string.IsNullOrEmpty(fromLang) ? "en" : fromLang,
                    ["target_lang"] = string.IsNullOrEmpty(toLang) ? "ru" : toLang
                };

                var body = Encoding.UTF8.GetBytes(payload.ToString());
                var request = (HttpWebRequest)WebRequest.Create(_serviceUrl);
                request.Method = "POST";
                request.ContentType = "application/json";
                request.Timeout = _timeoutMs;
                request.ReadWriteTimeout = _timeoutMs;
                request.ContentLength = body.Length;

                using (var stream = request.GetRequestStream())
                    stream.Write(body, 0, body.Length);

                using (var response = (HttpWebResponse)request.GetResponse())
                using (var stream = response.GetResponseStream())
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    var json = reader.ReadToEnd();
                    var parsed = JObject.Parse(json);
                    return parsed["translation"]?.ToString();
                }
            }
            catch (Exception e)
            {
                MetricsManager.LogError($"RuLocalization LocalHF: {e.Message}");
                return null;
            }
        }
    }
}
