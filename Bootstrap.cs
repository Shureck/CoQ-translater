using System;
using RuLocalization.Patches;
using XRL;

namespace RuLocalization
{
    [HasModSensitiveStaticCache]
    public static class Bootstrap
    {
        [ModSensitiveCacheInit]
        public static void Initialize()
        {
            try
            {
                var mod = ModManager.GetMod(typeof(Bootstrap).Assembly);
                if (mod == null)
                {
                    MetricsManager.LogError("RuLocalization: не удалось найти мод");
                    return;
                }

                MetricsManager.LogInfo("RuLocalization: запуск инициализации...");
                TranslationEngine.Instance.Initialize(mod.Path);
                MetricsManager.LogInfo($"RuLocalization: движок готов. Кэш: {TranslationEngine.Instance.CacheCount} записей");

                try
                {
                    if (mod.Harmony != null)
                    {
                        ManualPopupRegistration.Register(mod.Harmony);
                        RenderPipelineRegistration.Register(mod.Harmony);
                    }
                    MetricsManager.LogInfo("RuLocalization: Popup и render-патчи применены");
                }
                catch (Exception e)
                {
                    MetricsManager.LogError("RuLocalization: ошибка Popup-патчей (не критично, продолжаем)", e);
                }

                MetricsManager.LogInfo("RuLocalization: инициализация завершена");
            }
            catch (Exception e)
            {
                MetricsManager.LogError("RuLocalization: критическая ошибка инициализации", e);
            }
        }
    }
}
