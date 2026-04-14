using System;
using System.Collections.Concurrent;
using UnityEngine;

namespace RuLocalization.Patches
{
    public class UiMainThreadDispatcher : MonoBehaviour
    {
        private static readonly ConcurrentQueue<Action> _queue = new ConcurrentQueue<Action>();
        private static bool _installed;

        public static void Install()
        {
            if (_installed)
                return;
            _installed = true;

            var go = new GameObject("RuLocalization_MainThreadDispatcher");
            DontDestroyOnLoad(go);
            go.AddComponent<UiMainThreadDispatcher>();
        }

        public static void Enqueue(Action action)
        {
            if (action == null)
                return;
            _queue.Enqueue(action);
        }

        private void Update()
        {
            int budget = 16;
            while (budget-- > 0 && _queue.TryDequeue(out var action))
            {
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    MetricsManager.LogError("RuLocalization: main-thread dispatch error", e);
                }
            }
        }
    }
}
