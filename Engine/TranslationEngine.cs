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
        private readonly ConcurrentDictionary<string, byte> _pendingKeys = new ConcurrentDictionary<string, byte>();
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
        private readonly ConcurrentDictionary<string, long> _uiDecisionLogTimes =
            new ConcurrentDictionary<string, long>(StringComparer.Ordinal);

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

        /// <summary>
        /// Async-safe translation path for low-level UI render pipeline.
        /// Never calls provider synchronously on UI thread.
        /// </summary>
        public string TranslateScreenText(string text, string source = null)
        {
            if (!_config.UiRenderTranslation)
                return text;

            if (string.IsNullOrEmpty(text) || text.Length < 2)
                return text;

            var marker = TryApplyRuntimeMarker(text);
            if (marker != text)
            {
                LogUiDecision("marker", source, text);
                return marker;
            }

            if (IsNonTranslatable(text))
            {
                LogUiDecision("non-translatable", source, text);
                return text;
            }

            var uiOrExact = TryUiOrExactPhrase(text);
            if (uiOrExact != null)
            {
                LogUiDecision("glossary-hit", source, text);
                return uiOrExact;
            }

            if (HasUnsafeUiMarkup(text))
            {
                LogUiDecision("unsafe-markup-pass", source, text);
                return text;
            }

            var key = NormalizeKey(text);
            if (_cache.TryGetValue(key, out var cached))
            {
                LogUiDecision("cache-hit", source, text);
                return RestoreFormatting(text, cached);
            }

            if (ShouldUseUiImmediate(text, source) && _provider != null)
            {
                var immediate = TranslateImmediateInternal(text, text, key);
                if (!string.Equals(immediate, text, StringComparison.Ordinal))
                {
                    LogUiDecision("immediate-hit", source, text);
                    return immediate;
                }
            }

            var partial = ApplyPrefixReplacements(text);
            if (ShouldSkipUiQueue(text, source))
            {
                LogUiDecision("skip-queue", source, text);
                return partial != text ? partial : text;
            }

            EnqueueTranslation(key, text);
            LogUiDecision("queued", source, text);
            return partial != text ? partial : text;
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
                var stripped = PrepareForMachineTranslation(forMt);
                if (string.IsNullOrEmpty(stripped))
                    return original;
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
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(original))
                return;
            if (_cache.ContainsKey(key))
                return;
            if (!_pendingKeys.TryAdd(key, 0))
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
                    {
                        batch.Add(item);
                    }
                    else
                    {
                        _pendingKeys.TryRemove(item.Key, out _);
                        Interlocked.Decrement(ref _pendingCount);
                    }
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
                try
                {
                    if (_cache.ContainsKey(item.Key))
                        continue;
                    var stripped = PrepareForMachineTranslation(item.Original);
                    if (string.IsNullOrEmpty(stripped))
                        continue;
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
                finally
                {
                    _pendingKeys.TryRemove(item.Key, out _);
                    Interlocked.Decrement(ref _pendingCount);
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
            return PrepareForMachineTranslation(text);
        }

        private string RestoreFormatting(string original, string translated)
        {
            return ColorTagParser.TransferTags(original, translated);
        }

        private string PrepareForMachineTranslation(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;

            var normalized = ColorTagParser.StripTags(text);
            for (int i = 0; i < 6; i++)
            {
                var next = StripQudWrapperMarkup(normalized);
                if (next == normalized)
                    break;
                normalized = next;
            }

            return normalized.Trim();
        }

        private static string StripQudWrapperMarkup(string text)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf("{{", StringComparison.Ordinal) < 0)
                return text;

            var sb = new StringBuilder(text.Length);
            int i = 0;
            while (i < text.Length)
            {
                if (i + 1 < text.Length && text[i] == '{' && text[i + 1] == '{')
                {
                    int bar = text.IndexOf('|', i + 2);
                    int close = text.IndexOf("}}", i + 2, StringComparison.Ordinal);
                    if (bar > i + 1 && close > bar)
                    {
                        var tag = text.Substring(i + 2, bar - (i + 2)).Trim();
                        if (IsLikelyQudMarkupTag(tag))
                        {
                            sb.Append(text.Substring(bar + 1, close - (bar + 1)));
                            i = close + 2;
                            continue;
                        }
                    }
                }

                sb.Append(text[i]);
                i++;
            }

            return sb.ToString();
        }

        private static bool IsLikelyQudMarkupTag(string tag)
        {
            if (string.IsNullOrEmpty(tag) || tag.Length > 16)
                return false;

            foreach (var ch in tag)
            {
                if (!char.IsLetterOrDigit(ch) && ch != '#' && ch != '_' && ch != '-' && ch != '=')
                    return false;
            }
            return true;
        }

        private string TryApplyRuntimeMarker(string text)
        {
            if (!_config.UiMarkerEnabled || string.IsNullOrEmpty(_config.UiMarkerSourceText))
                return text;

            var stripped = ColorTagParser.StripTags(text).Trim();
            if (!string.Equals(stripped, _config.UiMarkerSourceText, StringComparison.OrdinalIgnoreCase))
                return text;

            var translated = string.IsNullOrEmpty(_config.UiMarkerTranslatedText)
                ? text
                : _config.UiMarkerTranslatedText;
            return ColorTagParser.TransferTags(text, translated);
        }

        private void LogUiDecision(string reason, string source, string sample)
        {
            if (!_config.UiRenderDiagnostics || string.IsNullOrEmpty(sample))
                return;

            var stripped = ColorTagParser.StripTags(sample).Trim();
            if (string.IsNullOrEmpty(stripped))
                return;
            if (stripped.Length > 120)
                stripped = stripped.Substring(0, 120);

            var dedupKey = reason + "|" + stripped;
            var now = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
            if (_uiDecisionLogTimes.TryGetValue(dedupKey, out var prev) && now - prev < _config.UiDiagnosticsMinIntervalMs)
                return;

            _uiDecisionLogTimes[dedupKey] = now;
            if (_uiDecisionLogTimes.Count > 2000)
                _uiDecisionLogTimes.Clear();

            var src = string.IsNullOrEmpty(source) ? "unknown" : source;
            MetricsManager.LogInfo($"RuLocalization UI[{reason}] {src}: {stripped}");
        }

        private bool ShouldSkipUiQueue(string text, string source)
        {
            if (string.IsNullOrEmpty(text))
                return true;

            var stripped = ColorTagParser.StripTags(text).Trim();
            if (string.IsNullOrEmpty(stripped))
                return true;
            var raw = text.Trim();
            if (raw.IndexOf("<color=", StringComparison.OrdinalIgnoreCase) >= 0 ||
                raw.IndexOf("</color>", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            if (raw.IndexOf("{{hotkey|", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            foreach (var ch in stripped)
            {
                if ((ch < 32 && ch != '\t' && ch != '\n' && ch != '\r') ||
                    (ch >= '\ue000' && ch <= '\uf8ff'))
                    return true;
            }
            var isUiTextSkin = !string.IsNullOrEmpty(source) &&
                source.IndexOf("UITextSkin.SetText", StringComparison.OrdinalIgnoreCase) >= 0;
            var maxChars = isUiTextSkin ? _config.UiRenderMaxUiTextChars : _config.UiRenderMaxQueueChars;
            if (stripped.Length > maxChars)
                return true;

            int letters = 0;
            int digits = 0;
            int dotCount = 0;
            int blockCount = 0;
            foreach (var ch in stripped)
            {
                if (char.IsLetter(ch)) letters++;
                else if (char.IsDigit(ch)) digits++;
                if (ch == '.') dotCount++;
                if (ch == '■') blockCount++;
            }
            var alnum = letters + digits;
            var symbolRatio = 1.0 - (double)alnum / Math.Max(1, stripped.Length);
            if (stripped.Length <= 1 && alnum == 0)
                return true;
            if (alnum == 0 && stripped.Length <= 4)
                return true;
            if (stripped.Length >= 24 && letters <= 2 && symbolRatio >= 0.85)
                return true;
            if (dotCount >= 20 && letters <= 3)
                return true;
            if (blockCount >= 2 && letters <= 3)
                return true;
            if (stripped.IndexOf("{{", StringComparison.Ordinal) >= 0 &&
                stripped.IndexOf("}}", StringComparison.Ordinal) < 0)
                return true;
            if (stripped.EndsWith("|", StringComparison.Ordinal) &&
                stripped.IndexOf("{{", StringComparison.Ordinal) >= 0)
                return true;

            if (!string.IsNullOrEmpty(source) &&
                source.IndexOf("Markup.ParseText", StringComparison.OrdinalIgnoreCase) >= 0 &&
                stripped.Length > _config.UiRenderMaxMarkupChars &&
                HasUnsafeUiMarkup(text))
                return true;

            return false;
        }

        private bool HasUnsafeUiMarkup(string text)
        {
            if (string.IsNullOrEmpty(text))
                return false;
            return text.IndexOf("<color=", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("</color>", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("{{hotkey|", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private bool ShouldUseUiImmediate(string text, string source)
        {
            if (!_config.UiRenderUseImmediateSmall || string.IsNullOrEmpty(text))
                return false;
            if (string.IsNullOrEmpty(source))
                return false;

            var stripped = ColorTagParser.StripTags(text).Trim();
            if (string.IsNullOrEmpty(stripped) || stripped.Length > _config.UiRenderImmediateMaxChars)
                return false;

            bool hasLatin = false;
            foreach (var ch in stripped)
            {
                if ((ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z'))
                {
                    hasLatin = true;
                    break;
                }
            }
            if (!hasLatin)
                return false;

            if (stripped.StartsWith("[") && stripped.EndsWith("]"))
                return false;

            return source.IndexOf("UITextSkin.SetText", StringComparison.OrdinalIgnoreCase) >= 0
                || source.IndexOf("ScreenBuffer.Write", StringComparison.OrdinalIgnoreCase) >= 0
                || source.IndexOf("StringFormat.ClipText", StringComparison.OrdinalIgnoreCase) >= 0;
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
                case "local_hf":
                    _provider = new LocalHuggingFaceProvider(_config.LocalHfServiceUrl, _config.LocalHfTimeoutMs);
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

        [JsonProperty("local_hf_service_url")]
        public string LocalHfServiceUrl = "http://127.0.0.1:5005/translate";

        [JsonProperty("local_hf_timeout_ms")]
        public int LocalHfTimeoutMs = 30000;

        [JsonProperty("ui_render_translation")]
        public bool UiRenderTranslation = true;

        [JsonProperty("ui_render_diagnostics")]
        public bool UiRenderDiagnostics = false;

        [JsonProperty("ui_diagnostics_min_interval_ms")]
        public int UiDiagnosticsMinIntervalMs = 10000;

        [JsonProperty("ui_status_indicator_enabled")]
        public bool UiStatusIndicatorEnabled = true;

        [JsonProperty("ui_status_interval_ms")]
        public int UiStatusIntervalMs = 4000;

        [JsonProperty("ui_marker_enabled")]
        public bool UiMarkerEnabled = true;

        [JsonProperty("ui_marker_source_text")]
        public string UiMarkerSourceText = "OPTIONS";

        [JsonProperty("ui_marker_translated_text")]
        public string UiMarkerTranslatedText = "[RU TEST] НАСТРОЙКИ";

        [JsonProperty("ui_render_max_queue_chars")]
        public int UiRenderMaxQueueChars = 140;

        [JsonProperty("ui_render_max_markup_chars")]
        public int UiRenderMaxMarkupChars = 80;

        [JsonProperty("ui_render_max_uitext_chars")]
        public int UiRenderMaxUiTextChars = 2200;

        [JsonProperty("ui_render_use_immediate_small")]
        public bool UiRenderUseImmediateSmall = false;

        [JsonProperty("ui_render_immediate_max_chars")]
        public int UiRenderImmediateMaxChars = 96;
    }
}
