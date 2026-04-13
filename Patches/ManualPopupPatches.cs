using System;
using System.Reflection;
using HarmonyLib;
using XRL.UI;

namespace RuLocalization.Patches
{
    public static class ManualPopupRegistration
    {
        public static void Register(HarmonyLib.Harmony harmony)
        {
            if (harmony == null)
                return;

            var t = typeof(Popup);
            var prefix = typeof(PopupPrefixes);

            PatchByFirstStringParam(harmony, t, "ShowFail", prefix, nameof(PopupPrefixes.GenericFirstStringPrefix));
            PatchByFirstStringParam(harmony, t, "ShowBlockSpace", prefix, nameof(PopupPrefixes.GenericFirstStringPrefix));
            PatchByFirstStringParam(harmony, t, "ShowBlockPrompt", prefix, nameof(PopupPrefixes.GenericFirstStringPrefix));
            PatchByFirstStringParam(harmony, t, "ShowBlockWithCopy", prefix, nameof(PopupPrefixes.GenericFirstStringPrefix));
            PatchByFirstStringParam(harmony, t, "ShowYesNo", prefix, nameof(PopupPrefixes.GenericFirstStringPrefix));
            PatchByFirstStringParam(harmony, t, "ShowYesNoCancel", prefix, nameof(PopupPrefixes.GenericFirstStringPrefix));
            PatchByFirstStringParam(harmony, t, "AskString", prefix, nameof(PopupPrefixes.GenericFirstStringPrefix));
            PatchByFirstStringParam(harmony, t, "ShowConversation", prefix, nameof(PopupPrefixes.GenericFirstStringPrefix));

            PatchAllOverloads(harmony, t, "ShowAsync", prefix, nameof(PopupPrefixes.GenericFirstStringPrefix));
        }

        private static void PatchByFirstStringParam(HarmonyLib.Harmony harmony, Type t, string name, Type prefix, string handler)
        {
            try
            {
                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name != name)
                        continue;
                    var ps = m.GetParameters();
                    if (ps.Length == 0 || ps[0].ParameterType != typeof(string))
                        continue;
                    harmony.Patch(m, prefix: new HarmonyMethod(prefix, handler));
                    return;
                }
            }
            catch (Exception e)
            {
                MetricsManager.LogError($"RuLocalization: не удалось пропатчить Popup.{name}: {e.Message}");
            }
        }

        private static void PatchAllOverloads(HarmonyLib.Harmony harmony, Type t, string name, Type prefix, string handler)
        {
            try
            {
                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name != name)
                        continue;
                    var ps = m.GetParameters();
                    if (ps.Length == 0 || ps[0].ParameterType != typeof(string))
                        continue;
                    harmony.Patch(m, prefix: new HarmonyMethod(prefix, handler));
                }
            }
            catch (Exception e)
            {
                MetricsManager.LogError($"RuLocalization: не удалось пропатчить Popup.{name}: {e.Message}");
            }
        }
    }

    public static class PopupPrefixes
    {
        public static void GenericFirstStringPrefix(ref string __0)
        {
            if (!string.IsNullOrEmpty(__0))
                __0 = TranslationEngine.Instance.Translate(__0, TranslationKind.Popup);
        }
    }
}
