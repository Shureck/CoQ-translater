using HarmonyLib;

namespace RuLocalization.Patches
{
    [HarmonyPatch(typeof(XRL.Messages.MessageQueue), "Add")]
    public static class MessageQueuePatch
    {
        [HarmonyPrefix]
        public static void Prefix(ref string __0)
        {
            if (string.IsNullOrEmpty(__0)) return;
            __0 = TranslationEngine.Instance.Translate(__0, TranslationKind.Message);
        }
    }
}
