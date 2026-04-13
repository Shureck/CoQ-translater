using HarmonyLib;
using XRL.World;

namespace RuLocalization.Patches
{
    /// <summary>
    /// Patches the display name pipeline. We use a Postfix on the property getters
    /// since GetDisplayName has many overloads with complex signatures.
    /// The properties like DisplayNameOnly, ShortDisplayName etc. are simpler targets.
    /// </summary>
    [HarmonyPatch]
    public static class DisplayNamePatch
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(GameObject), "get_DisplayNameOnly")]
        public static void DisplayNameOnlyPostfix(ref string __result, GameObject __instance)
        {
            if (string.IsNullOrEmpty(__result) || !TranslationEngine.Instance.Config.TranslateNames)
                return;
            __result = TranslationEngine.Instance.TranslateName(__result);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(GameObject), "get_ShortDisplayName")]
        public static void ShortDisplayNamePostfix(ref string __result, GameObject __instance)
        {
            if (string.IsNullOrEmpty(__result) || !TranslationEngine.Instance.Config.TranslateNames)
                return;
            __result = TranslationEngine.Instance.TranslateName(__result);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(GameObject), "get_BaseDisplayName")]
        public static void BaseDisplayNamePostfix(ref string __result, GameObject __instance)
        {
            if (string.IsNullOrEmpty(__result) || !TranslationEngine.Instance.Config.TranslateNames)
                return;
            __result = TranslationEngine.Instance.TranslateName(__result);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(GameObject), "get_DisplayNameOnlyStripped")]
        public static void DisplayNameOnlyStrippedPostfix(ref string __result, GameObject __instance)
        {
            if (string.IsNullOrEmpty(__result) || !TranslationEngine.Instance.Config.TranslateNames)
                return;
            __result = TranslationEngine.Instance.TranslateName(__result);
        }
    }
}
