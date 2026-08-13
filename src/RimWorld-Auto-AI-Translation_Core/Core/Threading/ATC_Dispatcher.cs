using HarmonyLib;
using Newtonsoft.Json;
using RimWorld;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using Verse;
using static AutoTranslator_Core.DeleteTranslationWindow;
// 這個檔案負責跨執行緒工作排程。
// EN: This file schedules work onto the Unity main thread.

namespace AutoTranslator_Core
{
        // 這個類別負責 ATC派發器 的主要流程與狀態。
        // EN: This class manages the main workflow and state for ATC_Dispatcher.
        public class ATC_Dispatcher : MonoBehaviour
        {

            private static readonly System.Collections.Concurrent.ConcurrentQueue<Action> executionQueue = new System.Collections.Concurrent.ConcurrentQueue<Action>();
            // 這個欄位保存 last派發器WarningFrame 的執行狀態或快取資料。
            // EN: This field stores last dispatcher warning frame runtime state or cached data.
            private static int _lastDispatcherWarningFrame = -1;
            // 這個欄位保存 pumpPosted 的執行狀態或快取資料。
            // EN: This field stores pump posted runtime state or cached data.
            private static int _pumpPosted = 0;
            private static long _lastPumpUtcTicks;
            // 這個欄位保存 instance 的執行狀態或快取資料。
            // EN: This field stores instance runtime state or cached data.
            private static ATC_Dispatcher _instance;
            private static readonly object _ensureLock = new object();
            // 這個欄位保存 next模型取得MaintenanceFrame 的執行狀態或快取資料。
            // EN: This field stores next model fetch maintenance frame runtime state or cached data.
            private int _nextModelFetchMaintenanceFrame = 0;

            // 這個方法負責確保 Alive 已準備完成。
            // EN: This method ensures alive is ready.
            public static void EnsureAlive()
            {
                if (!UnityData.IsInMainThread) return;
                AutoTranslatorMod.MainThreadContext = System.Threading.SynchronizationContext.Current;
                if (_instance != null) return;

                lock (_ensureLock)
                {
                    if (_instance != null) return;

                    GameObject go = new GameObject("ATC_Dispatcher_Engine");
                    UnityEngine.Object.DontDestroyOnLoad(go);
                    _instance = go.AddComponent<ATC_Dispatcher>();
                }
            }


            // 這個方法負責處理 RunOn主畫面執行緒 相關流程。
            // EN: This method handles run on main thread.
            public static void RunOnMainThread(Action action)
            {
                if (action == null) return;
                if (UnityData.IsInMainThread)
                {
                    EnsureAlive();
                    MarkPumpHeartbeat();
                    ExecuteAction(action);
                    ProcessQueuedActions(64);
                    return;
                }

                executionQueue.Enqueue(action);
                RequestPump();
            }

            internal static void PumpPending(int maxActions = 64)
            {
                if (!UnityData.IsInMainThread) return;

                EnsureAlive();
                MarkPumpHeartbeat();
                ProcessQueuedActions(Math.Max(1, maxActions));
            }

            internal static bool HasRecentPump(int maximumAgeMilliseconds)
            {
                long ticks = System.Threading.Interlocked.Read(ref _lastPumpUtcTicks);
                if (ticks <= 0L) return false;

                long ageTicks = DateTime.UtcNow.Ticks - ticks;
                return ageTicks >= 0L &&
                       ageTicks <= TimeSpan.FromMilliseconds(Math.Max(1, maximumAgeMilliseconds)).Ticks;
            }

            private static void MarkPumpHeartbeat()
            {
                System.Threading.Interlocked.Exchange(ref _lastPumpUtcTicks, DateTime.UtcNow.Ticks);
            }

            // 這個方法負責送出 Pump 請求。
            // EN: This method requests pump.
            private static void RequestPump()
            {
                var context = AutoTranslatorMod.MainThreadContext;
                if (context == null) return;
                if (System.Threading.Interlocked.Exchange(ref _pumpPosted, 1) == 1) return;

                try
                {
                    context.Post(_ =>
                    {
                        try
                        {
                            if (!UnityData.IsInMainThread) return;
                            EnsureAlive();
                            ProcessQueuedActions(64);
                        }
                        finally
                        {
                            System.Threading.Interlocked.Exchange(ref _pumpPosted, 0);
                            if (!executionQueue.IsEmpty)
                            {
                                RequestPump();
                            }
                        }
                    }, null);
                }
                catch
                {
                    System.Threading.Interlocked.Exchange(ref _pumpPosted, 0);
                }
            }

            // 這個方法負責執行 Action 動作。
            // EN: This method executes action.
            private static void ExecuteAction(Action action)
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    if (_lastDispatcherWarningFrame != Time.frameCount)
                    {
                        _lastDispatcherWarningFrame = Time.frameCount;
                        Verse.Log.Warning($"[AutoTranslationCore] Main-thread action failed: {ex}");
                    }
                }
            }

            // 這個方法負責處理 QueuedActions 流程。
            // EN: This method processes queued actions.
            private static void ProcessQueuedActions(int maxActions)
            {
                int processed = 0;
                while (processed < maxActions && executionQueue.TryDequeue(out Action action))
                {
                    ExecuteAction(action);
                    processed++;
                }
            }

            // 這個方法負責處理 Awake 相關流程。
            // EN: This method handles awake.
            private void Awake()
            {
                if (_instance != null && _instance != this)
                {
                    UnityEngine.Object.Destroy(gameObject);
                    return;
                }

                _instance = this;
                UnityEngine.Object.DontDestroyOnLoad(gameObject);
            }

            // 這個方法負責處理 OnDestroy 相關流程。
            // EN: This method handles on destroy.
            private void OnDestroy()
            {
                if (_instance == this)
                {
                    _instance = null;
                }
            }


            // 這個方法負責處理 Update 相關流程。
            // EN: This method handles update.
            public void Update()
            {
                AutoTranslatorMod.MainThreadContext = System.Threading.SynchronizationContext.Current;
                PumpPending();
                AutoTranslatorScanner.PumpMainThreadDispatcher();

                if (Time.frameCount >= _nextModelFetchMaintenanceFrame)
                {
                    _nextModelFetchMaintenanceFrame = Time.frameCount + 120;
                    AutoTranslatorAPI.MaintainModelFetchState();
                }
            }
        }
}
