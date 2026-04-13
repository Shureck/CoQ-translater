using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RuLocalization
{
    public enum TranslationKind
    {
        Message,
        Popup,
        Description
    }

    public class TranslationEngine
    {
        private static TranslationEngine _instance;
        public static TranslationEngine Instance => _instance ?? (_instance = new TranslationEngine());

        private readonly ConcurrentDictionary<string, string> _cache = new ConcurrentDictionary<string, string>();
        private readonly ConcurrentQueue<PendingTranslation> _pendingQueue = new ConcurrentQueue<PendingTranslation>();
        private ITranslationProvider _provider;
        private string _cachePath;
        private string _configPath;
        private string _translationsDir;
        private TranslationConfig _config = new TranslationConfig();
        private Thread _workerThread;
        private volatile bool _running;
        private int _cacheHits;
        private int _apiCalls;
        private int _pendingCount;
        private DateTime _lastSave = DateTime.MinValue;

        [ThreadStatic]
        private static int _translateDepth;

        private Dictionary<string, NameEntry> _nameEntries =
            new Dictionary<string, NameEntry>(StringComparer.OrdinalIgnoreCase);

        private Dictionary<string, string> _uiStrings =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private Dictionary<string, string> _descriptionPhrases =
            new Dictionary<string, string>(StringComparer.Ordinal);

        private List<KeyValuePair<string, string>> _prefixReplacements =
            new List<KeyValuePair<string, string>>();

        public int CacheCount => _cache.Count;
        public int PendingCount => _pendingCount;
        public int CacheHits => _cacheHits;
        public int ApiCalls => _apiCalls;

        public TranslationConfig Config => _config;

        public void Initialize(string modPath)
        {
            _cachePath = Path.Combine(modPath, "Cache");
            _configPath = Path.Combine(modPath, "config.json");
            _translationsDir = Path.Combine(modPath, "Translations");

            if (!Directory.Exists(_cachePath))
                Directory.CreateDirectory(_cachePath);

            LoadConfig();
            LoadGlossaries();
            LoadCache();
            InitializeProvider();
            StartWorker();

            var prov = _provider != null ? _config.Provider : "none";
            MetricsManager.LogInfo(
                $"RuLocalization: кэш {_cache.Count}, имён {_nameEntries.Count}, префиксов {_prefixReplacements.Count}, UI-строк {_uiStrings.Count}, провайдер: {prov}" +
                (_provider is GoogleTranslateProvider ? " (бесплатный translate.googleapis.com gtx)" : ""));
        }

        public string Translate(string text, TranslationKind kind = TranslationKind.Message)
        {
            if (string.IsNullOrEmpty(text) || text.Length < 2)
                return text;

            if (!IsKindEnabled(kind))
                return text;

            if (IsNonTranslatable(text))
                return text;

            var uiOrExact = TryUiOrExactPhrase(text);
            if (uiOrExact != null)
                return uiOrExact;

            var key = NormalizeKey(text);

            if (_cache.TryGetValue(key, out var cached))
            {
                Interlocked.Increment(ref _cacheHits);
                return RestoreFormatting(text, cached);
            }

            var useImmediate =
                _provider != null &&
                ((kind == TranslationKind.Popup && _config.PopupUseImmediateTranslate) ||
                 (kind == TranslationKind.Message && _config.MessageUseImmediateTranslate));

            if (useImmediate)
                return TranslateImmediateInternal(text, text, key);

            var partial = ApplyPrefixReplacements(text);
            EnqueueTranslation(key, text);
            return partial != text ? partial : text;
        }

        /// <summary>Popup / blocking UI: optional synchronous MT.</summary>
        public string TranslatePopup(string text)
        {
            return Translate(text, TranslationKind.Popup);
        }

        public string TranslateName(string text)
        {
            if (string.IsNullOrEmpty(text) || !_config.TranslateNames)
                return text;

            _translateDepth++;
            try
            {
                if (_translateDepth > 8)
                    return text;

                var stripped = ColorTagParser.StripTags(text).Trim();
                if (string.IsNullOrEmpty(stripped))
                    return text;

                var entry = GetNameEntry(stripped);
                if (entry != null && !string.IsNullOrEmpty(entry.Nom))
                    return ColorTagParser.TransferTags(text, entry.Nom);

                if (IsNonTranslatable(text))
                    return text;

                var key = NormalizeKey(stripped);
                if (_cache.TryGetValue(key, out var cached))
                    return RestoreFormatting(text, cached);

                if (_provider != null)
                {
                    var t = TranslateImmediateInternal(text, text, key);
                    if (t != text)
                        return t;
                }

                EnqueueTranslation(key, stripped);
                return text;
            }
            finally
            {
                _translateDepth--;
            }
        }

        public NameEntry GetNameEntry(string word)
        {
            if (string.IsNullOrEmpty(word))
                return null;
            var k = word.Trim();
            if (_nameEntries.TryGetValue(k, out var e))
                return e;
            if (_nameEntries.TryGetValue(k.ToLowerInvariant(), out e))
                return e;
            return null;
        }

        public string TranslateImmediate(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length < 2)
                return text;

            var uiOrExact = TryUiOrExactPhrase(text);
            if (uiOrExact != null)
                return uiOrExact;

            var key = NormalizeKey(text);
            return TranslateImmediateInternal(text, text, key);
        }

        private string TranslateImmediateInternal(string original, string forMt, string key)
        {
            if (_cache.TryGetValue(key, out var cached))
                return RestoreFormatting(original, cached);

            if (_provider == null)
                return original;

            try
            {
                var stripped = ColorTagParser.StripTags(forMt);
                var translated = _provider.Translate(stripped, "en", "ru");
                if (!string.IsNullOrEmpty(translated) && translated != stripped)
                {
                    _cache[key] = translated;
                    Interlocked.Increment(ref _apiCalls);
                    MaybeThrottleApi();
                    return RestoreFormatting(original, translated);
                }
            }
            catch (Exception e)
            {
                MetricsManager.LogError("RuLocalization: ошибка перевода", e);
            }

            return original;
        }

        public void Shutdown()
        {
            _running = false;
            SaveCache();
        }

        private bool IsKindEnabled(TranslationKind kind)
        {
            switch (kind)
            {
                case TranslationKind.Message:
                    return _config.TranslateMessages;
                case TranslationKind.Popup:
                    return _config.TranslatePopups;
                case TranslationKind.Description:
                    return _config.TranslateDescriptions;
                default:
                    return true;
            }
        }

        private string TryUiOrExactPhrase(string text)
        {
            var trimmed = text.Trim();
            if (_uiStrings.TryGetValue(trimmed, out var ui))
                return ColorTagParser.TransferTags(text, ui);
            var strippedOnce = ColorTagParser.StripTags(trimmed);
            if (_uiStrings.TryGetValue(strippedOnce, out ui))
                return ColorTagParser.TransferTags(text, ui);

            if (_descriptionPhrases.TryGetValue(trimmed, out var desc))
                return ColorTagParser.TransferTags(text, desc);
            if (_descriptionPhrases.TryGetValue(strippedOnce, out desc))
                return ColorTagParser.TransferTags(text, desc);

            return null;
        }

        private string ApplyPrefixReplacements(string text)
        {
            if (string.IsNullOrEmpty(text) || _prefixReplacements.Count == 0)
                return text;
            var working = text;
            foreach (var kv in _prefixReplacements)
            {
                if (string.IsNullOrEmpty(kv.Key))
                    continue;
                if (working.IndexOf(kv.Key, StringComparison.Ordinal) >= 0)
                    working = working.Replace(kv.Key, kv.Value);
            }
            return working;
        }

        private void EnqueueTranslation(string key, string original)
        {
            if (_provider == null)
                return;

            _pendingQueue.Enqueue(new PendingTranslation { Key = key, Original = original });
            Interlocked.Increment(ref _pendingCount);
        }

        private void StartWorker()
        {
            if (_provider == null)
                return;

            _running = true;
            _workerThread = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = "RuLocalization-Worker"
            };
            _workerThread.Start();
        }

        private void WorkerLoop()
        {
            var batch = new List<PendingTranslation>();
            while (_running)
            {
                batch.Clear();

                while (batch.Count < _config.BatchSize && _pendingQueue.TryDequeue(out var item))
                {
                    if (!_cache.ContainsKey(item.Key))
                        batch.Add(item);
                    Interlocked.Decrement(ref _pendingCount);
                }

                if (batch.Count > 0)
                    ProcessBatch(batch);

                if ((DateTime.UtcNow - _lastSave).TotalSeconds > 60)
                {
                    SaveCache();
                    _lastSave = DateTime.UtcNow;
                }

                Thread.Sleep(batch.Count > 0 ? _config.BatchDelayMs : 500);
            }
        }

        private void ProcessBatch(List<PendingTranslation> batch)
        {
            foreach (var item in batch)
            {
                if (_cache.ContainsKey(item.Key))
                    continue;

                try
                {
                    var stripped = ColorTagParser.StripTags(item.Original);
                    var translated = _provider.Translate(stripped, "en", "ru");

                    if (!string.IsNullOrEmpty(translated) && translated != stripped)
                    {
                        _cache[item.Key] = translated;
                        Interlocked.Increment(ref _apiCalls);
                        MaybeThrottleApi();
                    }
                }
                catch (Exception e)
                {
                    MetricsManager.LogError($"RuLocalization: ошибка перевода '{item.Key}'", e);
                    Thread.Sleep(2000);
                }
            }
        }

        private void MaybeThrottleApi()
        {
            var d = _config.ApiMinDelayMs;
            if (d > 0)
                Thread.Sleep(d);
        }

        private string NormalizeKey(string text)
        {
            return ColorTagParser.StripTags(text).Trim();
        }

        private string RestoreFormatting(string original, string translated)
        {
            return ColorTagParser.TransferTags(original, translated);
        }

        private bool IsNonTranslatable(string text)
        {
            if (text.Length > 2000)
                return true;

            bool hasLetter = false;
            foreach (char c in text)
            {
                if (char.IsLetter(c))
                {
                    hasLetter = true;
                    if (c >= 'а' && c <= 'я' || c >= 'А' && c <= 'Я' || c == 'ё' || c == 'Ё')
                        return true;
                    break;
                }
            }
            return !hasLetter;
        }

        private void LoadGlossaries()
        {
            _nameEntries.Clear();
            _uiStrings.Clear();
            _descriptionPhrases.Clear();
            _prefixReplacements.Clear();

            TryLoadJsonDict(Path.Combine(_translationsDir, "ui_strings.json"), _uiStrings);

            var namesPath = Path.Combine(_translationsDir, "names.json");
            if (File.Exists(namesPath))
            {
                try
                {
                    var json = File.ReadAllText(namesPath, Encoding.UTF8);
                    var raw = JObject.Parse(json);
                    foreach (var p in raw.Properties())
                    {
                        var entry = p.Value.ToObject<NameEntry>();
                        if (entry != null)
                            _nameEntries[p.Name] = entry;
                    }
                }
                catch (Exception e)
                {
                    MetricsManager.LogError("RuLocalization: names.json", e);
                }
            }

            MergePrefixFile(Path.Combine(_translationsDir, "patterns.json"));
            MergePrefixFile(Path.Combine(_translationsDir, "messages.json"));

            TryLoadJsonDict(Path.Combine(_translationsDir, "descriptions.json"), _descriptionPhrases);

            _prefixReplacements = _prefixReplacements
                .OrderByDescending(kv => kv.Key.Length)
                .ToList();
        }

        private void MergePrefixFile(string path)
        {
            if (!File.Exists(path))
                return;
            try
            {
                var json = File.ReadAllText(path, Encoding.UTF8);
                var dict = JsonConvert.DeserializeObject<Dictionary<string, string>>(json);
                if (dict == null)
                    return;
                foreach (var kv in dict)
                    _prefixReplacements.Add(new KeyValuePair<string, string>(kv.Key, kv.Value));
            }
            catch (Exception e)
            {
                MetricsManager.LogError("RuLocalization: " + path, e);
            }
        }

        private static void TryLoadJsonDict(string path, Dictionary<string, string> target)
        {
            if (!File.Exists(path))
                return;
            try
            {
                var json = File.ReadAllText(path, Encoding.UTF8);
                var dict = JsonConvert.DeserializeObject<Dictionary<string, string>>(json);
                if (dict == null)
                    return;
                foreach (var kv in dict)
                    target[kv.Key] = kv.Value;
            }
            catch (Exception e)
            {
                MetricsManager.LogError("RuLocalization: " + path, e);
            }
        }

        private void LoadConfig()
        {
            _config = new TranslationConfig();

            if (File.Exists(_configPath))
            {
                try
                {
                    var json = File.ReadAllText(_configPath, Encoding.UTF8);
                    _config = JsonConvert.DeserializeObject<TranslationConfig>(json) ?? new TranslationConfig();
                }
                catch (Exception e)
                {
                    MetricsManager.LogError("RuLocalization: ошибка загрузки конфига", e);
                }
            }
            else
            {
                SaveConfig();
            }
        }

        private void SaveConfig()
        {
            try
            {
                var json = JsonConvert.SerializeObject(_config, Formatting.Indented);
                File.WriteAllText(_configPath, json, Encoding.UTF8);
            }
            catch { }
        }

        private void InitializeProvider()
        {
            switch (_config.Provider.ToLowerInvariant())
            {
                case "google":
                    _provider = new GoogleTranslateProvider();
                    break;
                case "deepl":
                    _provider = new DeepLProvider(_config.ApiKey);
                    break;
                case "openai":
                    _provider = new OpenAIProvider(_config.ApiKey, _config.OpenAIModel);
                    break;
                case "disabled":
                case "none":
                    _provider = null;
                    break;
                default:
                    _provider = new GoogleTranslateProvider();
                    break;
            }
        }

        private void LoadCache()
        {
            var cacheFile = Path.Combine(_cachePath, "translations.json");
            if (File.Exists(cacheFile))
            {
                try
                {
                    var json = File.ReadAllText(cacheFile, Encoding.UTF8);
                    var entries = JsonConvert.DeserializeObject<Dictionary<string, string>>(json);
                    if (entries != null)
                    {
                        foreach (var kvp in entries)
                            _cache[kvp.Key] = kvp.Value;
                    }
                }
                catch (Exception e)
                {
                    MetricsManager.LogError("RuLocalization: ошибка загрузки кэша", e);
                }
            }

            foreach (var file in Directory.GetFiles(_cachePath, "manual_*.json"))
            {
                try
                {
                    var json = File.ReadAllText(file, Encoding.UTF8);
                    var entries = JsonConvert.DeserializeObject<Dictionary<string, string>>(json);
                    if (entries != null)
                    {
                        foreach (var kvp in entries)
                            _cache[kvp.Key] = kvp.Value;
                    }
                }
                catch { }
            }
        }

        private void SaveCache()
        {
            try
            {
                var cacheFile = Path.Combine(_cachePath, "translations.json");
                var snapshot = new Dictionary<string, string>(_cache);
                var json = JsonConvert.SerializeObject(snapshot, Formatting.Indented);
                File.WriteAllText(cacheFile, json, Encoding.UTF8);
            }
            catch (Exception e)
            {
                MetricsManager.LogError("RuLocalization: ошибка сохранения кэша", e);
            }
        }

        internal void LogUntranslatedIfEnabled(string sample)
        {
            if (!_config.LogUntranslated || string.IsNullOrEmpty(sample))
                return;
            MetricsManager.LogInfo("RuLocalization [untranslated]: " + sample.Substring(0, Math.Min(120, sample.Length)));
        }
    }

    internal class PendingTranslation
    {
        public string Key;
        public string Original;
    }

    public class TranslationConfig
    {
        [JsonProperty("provider")]
        public string Provider = "google";

        [JsonProperty("api_key")]
        public string ApiKey = "";

        [JsonProperty("openai_model")]
        public string OpenAIModel = "gpt-4o-mini";

        [JsonProperty("batch_size")]
        public int BatchSize = 5;

        [JsonProperty("batch_delay_ms")]
        public int BatchDelayMs = 200;

        [JsonProperty("translate_messages")]
        public bool TranslateMessages = true;

        [JsonProperty("translate_names")]
        public bool TranslateNames = true;

        [JsonProperty("translate_popups")]
        public bool TranslatePopups = true;

        [JsonProperty("translate_descriptions")]
        public bool TranslateDescriptions = true;

        [JsonProperty("log_untranslated")]
        public bool LogUntranslated = false;

        /// <summary>
        /// If true, Popup lines use synchronous MT when provider is set (first frame localized; more API calls).
        /// </summary>
        [JsonProperty("popup_use_immediate_translate")]
        public bool PopupUseImmediateTranslate = true;

        /// <summary>Minimum delay between outbound API calls (worker + immediate).</summary>
        [JsonProperty("api_min_delay_ms")]
        public int ApiMinDelayMs = 0;

        /// <summary>
        /// If true, message log lines call Google (or other provider) synchronously when uncached.
        /// Heavier on API than background queue but text becomes Russian on first show.
        /// </summary>
        [JsonProperty("message_use_immediate_translate")]
        public bool MessageUseImmediateTranslate = true;
    }
}
