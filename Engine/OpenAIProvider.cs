using System;
using System.IO;
using System.Net;
using System.Text;
using Newtonsoft.Json.Linq;

namespace RuLocalization
{
    /// <summary>
    /// OpenAI API provider. Best context-aware translation quality.
    /// Uses a system prompt tuned for Caves of Qud game text.
    /// </summary>
    public class OpenAIProvider : ITranslationProvider
    {
        private readonly string _apiKey;
        private readonly string _model;

        private const string SystemPrompt =
            "You are a translator for the game Caves of Qud (a sci-fi/fantasy roguelike). " +
            "Translate the following English game text to Russian. " +
            "Preserve all special markup: {{color|text}}, =variable=, <spice>, *placeholder*. " +
            "Keep proper nouns like character names transliterated. " +
            "Use a style appropriate for a dark sci-fi/fantasy RPG. " +
            "Return ONLY the translated text, no explanations.";

        public OpenAIProvider(string apiKey, string model = "gpt-4o-mini")
        {
            _apiKey = apiKey;
            _model = model;
        }

        public string Translate(string text, string fromLang, string toLang)
        {
            if (string.IsNullOrEmpty(_apiKey))
            {
                MetricsManager.LogError("RuLocalization OpenAI: API ключ не задан");
                return null;
            }

            try
            {
                var requestBody = new JObject
                {
                    ["model"] = _model,
                    ["messages"] = new JArray
                    {
                        new JObject { ["role"] = "system", ["content"] = SystemPrompt },
                        new JObject { ["role"] = "user", ["content"] = text }
                    },
                    ["temperature"] = 0.3,
                    ["max_tokens"] = 1024
                };

                var jsonData = Encoding.UTF8.GetBytes(requestBody.ToString());

                var request = (HttpWebRequest)WebRequest.Create("https://api.openai.com/v1/chat/completions");
                request.Method = "POST";
                request.ContentType = "application/json";
                request.Headers.Add("Authorization", $"Bearer {_apiKey}");
                request.ContentLength = jsonData.Length;
                request.Timeout = 30000;

                using (var reqStream = request.GetRequestStream())
                    reqStream.Write(jsonData, 0, jsonData.Length);

                using (var response = (HttpWebResponse)request.GetResponse())
                using (var stream = response.GetResponseStream())
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    var json = reader.ReadToEnd();
                    var obj = JObject.Parse(json);
                    return obj["choices"]?[0]?["message"]?["content"]?.ToString()?.Trim();
                }
            }
            catch (Exception e)
            {
                MetricsManager.LogError($"RuLocalization OpenAI: {e.Message}");
                return null;
            }
        }
    }
}
