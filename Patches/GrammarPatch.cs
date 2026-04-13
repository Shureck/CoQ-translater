using HarmonyLib;
using XRL.Language;

namespace RuLocalization.Patches
{
    [HarmonyPatch]
    public static class GrammarPatch
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(Grammar), "Pluralize")]
        public static void PluralizePostfix(ref string __result, string word)
        {
            if (string.IsNullOrEmpty(__result) || !TranslationEngine.Instance.Config.TranslateNames)
                return;

            var entry = TranslationEngine.Instance.GetNameEntry(word);
            if (entry != null && !string.IsNullOrEmpty(entry.PluralNominative))
            {
                __result = entry.PluralNominative;
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Grammar), "Cardinal")]
        [HarmonyPatch(new[] { typeof(int) })]
        public static void CardinalPostfix(ref string __result, int __0)
        {
            __result = RussianGrammar.Cardinal(__0);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Grammar), "Cardinal")]
        [HarmonyPatch(new[] { typeof(long) })]
        public static void CardinalLongPostfix(ref string __result, long __0)
        {
            if (__0 > int.MaxValue || __0 < int.MinValue)
            {
                __result = __0.ToString();
                return;
            }
            __result = RussianGrammar.Cardinal((int)__0);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Grammar), "Ordinal")]
        [HarmonyPatch(new[] { typeof(int) })]
        public static void OrdinalPostfix(ref string __result, int __0)
        {
            __result = RussianGrammar.Ordinal(__0);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Grammar), "Ordinal")]
        [HarmonyPatch(new[] { typeof(long) })]
        public static void OrdinalLongPostfix(ref string __result, long __0)
        {
            if (__0 > int.MaxValue || __0 < int.MinValue)
            {
                __result = __0.ToString();
                return;
            }
            __result = RussianGrammar.Ordinal((int)__0);
        }
    }
}
