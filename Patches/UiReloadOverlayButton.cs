using System;
using UnityEngine;

namespace RuLocalization.Patches
{
    public class UiReloadOverlayButton : MonoBehaviour
    {
        private static bool _installed;
        private static UiReloadOverlayButton _instance;

        private Rect _buttonRect = new Rect(12f, 10f, 200f, 30f);
        private GUIStyle _buttonStyle;
        private float _nextAllowedClickTime;

        public static void Install()
        {
            if (_installed)
                return;
            _installed = true;

            try
            {
                var go = new GameObject("RuLocalization_UIReloadButton");
                DontDestroyOnLoad(go);
                _instance = go.AddComponent<UiReloadOverlayButton>();
                MetricsManager.LogInfo("RuLocalization: UI reload overlay installed");
            }
            catch (Exception e)
            {
                MetricsManager.LogError("RuLocalization: failed to install UI reload overlay", e);
            }
        }

        private void Update()
        {
            try
            {
                if (Input.GetKeyDown(KeyCode.F10))
                    TriggerReload();
            }
            catch
            {
            }
        }

        private void OnGUI()
        {
            if (_instance == null)
                return;

            EnsureStyle();
            GUI.depth = -10000;

            // Keep it at top-right regardless of current game screen.
            var x = Mathf.Max(10f, Screen.width - _buttonRect.width - 10f);
            _buttonRect.x = x;
            _buttonRect.y = 10f;

            if (GUI.Button(_buttonRect, "RU Reload [F10]", _buttonStyle))
                TriggerReload();
        }

        private void TriggerReload()
        {
            var now = Time.realtimeSinceStartup;
            if (now < _nextAllowedClickTime)
                return;

            _nextAllowedClickTime = now + 0.75f;
            TranslationEngine.Instance.ReloadUiTranslations();
        }

        private void EnsureStyle()
        {
            if (_buttonStyle != null)
                return;

            _buttonStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 14,
                alignment = TextAnchor.MiddleCenter,
                fontStyle = FontStyle.Bold
            };
        }
    }
}
