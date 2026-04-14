using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using HarmonyLib;

namespace RuLocalization.Patches
{
    public static class RenderPipelineRegistration
    {
        private static int _registered;

        public static void Register(Harmony harmony)
        {
            if (harmony == null || Interlocked.Exchange(ref _registered, 1) == 1)
                return;

            int patched = 0;
            int failed = 0;
            var firstPrefix = new HarmonyMethod(typeof(RenderPipelinePrefix), nameof(RenderPipelinePrefix.FirstStringPrefix));
            var secondPrefix = new HarmonyMethod(typeof(RenderPipelinePrefix), nameof(RenderPipelinePrefix.SecondStringPrefix));
            var thirdPrefix = new HarmonyMethod(typeof(RenderPipelinePrefix), nameof(RenderPipelinePrefix.ThirdStringPrefix));

            foreach (var method in FindCandidateMethods())
            {
                try
                {
                    var index = GetFirstStringParamIndex(method);
                    if (index == 0)
                        harmony.Patch(method, prefix: firstPrefix);
                    else if (index == 1)
                        harmony.Patch(method, prefix: secondPrefix);
                    else if (index == 2)
                        harmony.Patch(method, prefix: thirdPrefix);
                    else
                        continue;
                    patched++;
                }
                catch (Exception e)
                {
                    failed++;
                    MetricsManager.LogError($"RuLocalization: RenderPatch fail {method.DeclaringType?.FullName}.{method.Name}", e);
                }
            }

            MetricsManager.LogInfo($"RuLocalization: render pipeline patches applied: {patched}, failed: {failed}");
        }

        private static IEnumerable<MethodInfo> FindCandidateMethods()
        {
            var nameHints = new[] { "Write", "Draw", "Render", "Text", "Print", "Blit", "Buffer", "Display" };
            var matches = new List<MethodInfo>();

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try
                {
                    types = asm.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types.Where(t => t != null).ToArray();
                }
                catch
                {
                    continue;
                }

                foreach (var type in types)
                {
                    if (type == null || string.IsNullOrEmpty(type.Namespace))
                        continue;
                    if (type.IsInterface)
                        continue;
                    if (!type.Namespace.StartsWith("XRL.UI", StringComparison.Ordinal) &&
                        !type.Namespace.StartsWith("ConsoleLib.Console", StringComparison.Ordinal))
                        continue;

                    MethodInfo[] methods;
                    try
                    {
                        methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
                    }
                    catch
                    {
                        continue;
                    }

                    foreach (var method in methods)
                    {
                        if (method == null || method.IsAbstract || method.IsGenericMethod || method.IsSpecialName)
                            continue;
                        if (method.DeclaringType != type)
                            continue;
                        if (method.DeclaringType != null && method.DeclaringType.IsInterface)
                            continue;
                        if (method.GetMethodBody() == null)
                            continue;
                        if (!nameHints.Any(h => method.Name.IndexOf(h, StringComparison.OrdinalIgnoreCase) >= 0))
                            continue;

                        var ps = method.GetParameters();
                        if (ps.Length == 0 || !ps.Any(p => p.ParameterType == typeof(string)))
                            continue;

                        if (method.DeclaringType == typeof(XRL.Messages.MessageQueue))
                            continue;

                        matches.Add(method);
                    }
                }
            }

            return matches.Distinct();
        }

        private static int GetFirstStringParamIndex(MethodInfo method)
        {
            if (method == null)
                return -1;
            var ps = method.GetParameters();
            for (int i = 0; i < ps.Length && i < 3; i++)
            {
                if (ps[i].ParameterType == typeof(string))
                    return i;
            }
            return -1;
        }
    }

    public static class RenderPipelinePrefix
    {
        [ThreadStatic]
        private static int _depth;

        public static void FirstStringPrefix(MethodBase __originalMethod, object __instance, ref string __0)
        {
            ApplyByIndex(__originalMethod, __instance, 0, ref __0);
        }

        public static void SecondStringPrefix(MethodBase __originalMethod, object __instance, ref string __1)
        {
            ApplyByIndex(__originalMethod, __instance, 1, ref __1);
        }

        public static void ThirdStringPrefix(MethodBase __originalMethod, object __instance, ref string __2)
        {
            ApplyByIndex(__originalMethod, __instance, 2, ref __2);
        }

        private static void ApplyByIndex(MethodBase originalMethod, object instance, int index, ref string value)
        {
            if (string.IsNullOrEmpty(value) || _depth > 0)
                return;

            _depth++;
            try
            {
                var source = originalMethod?.DeclaringType?.FullName + "." + originalMethod?.Name + $"(arg{index})";
                TranslationEngine.Instance.TrackUiTextBinding(source, instance, value);
                var translated = TranslationEngine.Instance.TranslateScreenText(value, source);
                if (!string.Equals(translated, value, StringComparison.Ordinal))
                    value = translated;

                UiStatusNotifier.TryPost();
            }
            catch (Exception e)
            {
                MetricsManager.LogError("RuLocalization: render prefix exception", e);
            }
            finally
            {
                _depth--;
            }
        }
    }

    internal static class UiStatusNotifier
    {
        private static long _lastPostTick;
        private static MethodInfo _messageAddMethod;
        private static int _messageResolveAttempted;

        public static void TryPost()
        {
            var cfg = TranslationEngine.Instance.Config;
            if (!cfg.UiStatusIndicatorEnabled)
                return;

            var now = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
            var interval = Math.Max(1000, cfg.UiStatusIntervalMs);
            if (now - Interlocked.Read(ref _lastPostTick) < interval)
                return;

            var message = $"[RuLocalization] pending={TranslationEngine.Instance.PendingCount} cache={TranslationEngine.Instance.CacheCount} api={TranslationEngine.Instance.ApiCalls}";
            if (!TryAddMessage(message))
                return;

            Interlocked.Exchange(ref _lastPostTick, now);
        }

        private static bool TryAddMessage(string text)
        {
            if (_messageAddMethod == null && Interlocked.Exchange(ref _messageResolveAttempted, 1) == 0)
                _messageAddMethod = ResolveMessageQueueAddMethod();

            if (_messageAddMethod == null)
                return false;

            try
            {
                var ps = _messageAddMethod.GetParameters();
                if (ps.Length == 1)
                {
                    _messageAddMethod.Invoke(null, new object[] { text });
                    return true;
                }

                var args = new object[ps.Length];
                args[0] = text;
                for (int i = 1; i < ps.Length; i++)
                    args[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue : GetDefault(ps[i].ParameterType);
                _messageAddMethod.Invoke(null, args);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static MethodInfo ResolveMessageQueueAddMethod()
        {
            try
            {
                return typeof(XRL.Messages.MessageQueue)
                    .GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .Where(m => m.Name == "Add")
                    .Where(m =>
                    {
                        var ps = m.GetParameters();
                        return ps.Length > 0 && ps[0].ParameterType == typeof(string);
                    })
                    .OrderBy(m => m.GetParameters().Length)
                    .FirstOrDefault();
            }
            catch
            {
                return null;
            }
        }

        private static object GetDefault(Type type)
        {
            if (type == null)
                return null;
            return type.IsValueType ? Activator.CreateInstance(type) : null;
        }
    }
}
