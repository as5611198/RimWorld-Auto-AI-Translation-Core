using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;
// 這個檔案負責啟動時的掛鉤注入。
// EN: This file installs startup hooks for main-thread pumping.

namespace AutoTranslator_Core
{
    [StaticConstructorOnStartup]
    // 這個類別負責 自動翻譯器啟動掛鉤 的主要流程與狀態。
    // EN: This class manages the main workflow and state for AutoTranslator_StartupHook.
    public static class AutoTranslator_StartupHook
    {
        // 這個欄位保存 installed 的執行狀態或快取資料。
        // EN: This field stores installed runtime state or cached data.
        private static bool _installed;
        private static bool _playDataHookInstalled;
        private static int _defsLoadedMemoryDropQueued;

        static AutoTranslator_StartupHook()
        {
            InstallPlayDataLoaderHook();
            AutoTranslator_LongEventCompat.ExecuteWhenFinished(EnsureInstalled);
        }

        public static void EnsureInstalled()
        {
            if (UnityData.IsInMainThread)
            {
                InstallPump();
                return;
            }

            ATC_Dispatcher.RunOnMainThread(InstallPump);
        }

        // 這個方法負責處理 InstallPump 相關流程。
        // EN: This method handles install pump.
        private static void InstallPump()
        {
            if (_installed)
            {
                return;
            }

            _installed = true;

            try
            {
                GameObject hook = new GameObject("ATC_MainThreadPump");
                UnityEngine.Object.DontDestroyOnLoad(hook);
                hook.AddComponent<AutoTranslator_PumpBehaviour>();
                hook.AddComponent<UIInterceptorLifecycle>();
                AutoTranslatorScanner.ApplyStartupKeyedHotfixes();
                TargetedHardcodedUi.HardcodedUiTargetedPatchManager.Initialize();
                AutoTranslatorScanner.RequestKeyedMemoryDrop();
            }
            catch (Exception ex)
            {
                Log.Warning("[AutoTranslationCore] Startup pump install failed: " + ex);
            }
        }

        private static void InstallPlayDataLoaderHook()
        {
            if (_playDataHookInstalled) return;

            try
            {
                MethodInfo target = AccessTools.Method(typeof(PlayDataLoader), "DoPlayLoad");
                MethodInfo postfix = AccessTools.Method(typeof(AutoTranslator_StartupHook), nameof(AfterPlayDataLoaded));
                if (target == null || postfix == null)
                {
                    Log.Warning("[AutoTranslationCore] PlayDataLoader.DoPlayLoad hook was not installed: method not found.");
                    return;
                }

                var harmony = new Harmony("MingYang.AutoTranslation.Startup");
                harmony.Patch(target, postfix: new HarmonyMethod(postfix));
                _playDataHookInstalled = true;
            }
            catch (Exception ex)
            {
                Log.Warning("[AutoTranslationCore] PlayDataLoader.DoPlayLoad hook install failed: " + ex);
            }
        }

        private static void AfterPlayDataLoaded()
        {
            if (System.Threading.Interlocked.Exchange(ref _defsLoadedMemoryDropQueued, 1) == 1)
            {
                return;
            }

            ATC_Dispatcher.RunOnMainThread(QueueDefsLoadedMemoryDrop);
        }

        public static void PollForLoadedPlayData()
        {
            if (System.Threading.Interlocked.CompareExchange(ref _defsLoadedMemoryDropQueued, 0, 0) == 1)
            {
                return;
            }

            bool loaded;
            try
            {
                loaded = PlayDataLoader.Loaded;
            }
            catch
            {
                return;
            }

            if (!loaded)
            {
                return;
            }

            if (System.Threading.Interlocked.Exchange(ref _defsLoadedMemoryDropQueued, 1) == 1)
            {
                return;
            }

            Log.Message("[AutoTranslationCore] PlayDataLoader.Loaded fallback detected; queuing startup memory drop.");
            QueueDefsLoadedMemoryDrop();
        }

        private static void QueueDefsLoadedMemoryDrop()
        {
            try
            {
                AutoTranslatorMod.EnsureNetworkDispatchReady();
                EnsureInstalled();
                AutoTranslatorScanner.QueueStartupFullMemoryDrop(1000);
            }
            catch (Exception ex)
            {
                System.Threading.Interlocked.Exchange(ref _defsLoadedMemoryDropQueued, 0);
                Log.Warning("[AutoTranslationCore] Defs-loaded memory drop queue failed: " + ex);
            }
        }
    }

    // 這個類別負責 自動翻譯器LongEventCompat 的主要流程與狀態。
    // EN: This class manages the main workflow and state for AutoTranslator_LongEventCompat.
    internal static class AutoTranslator_LongEventCompat
    {
        // 這個方法負責執行 WhenFinished 動作。
        // EN: This method executes when finished.
        public static void ExecuteWhenFinished(Action action)
        {
            if (action == null) return;
            if (TryInvokeLongEventMethod("ExecuteWhenFinished", action))
            {
                return;
            }

            QueueLongEvent(action, action);
        }

        // 這個方法負責排入 LongEvent 佇列。
        // EN: This method queues long event.
        public static void QueueLongEvent(Action action, Action fallback = null)
        {
            if (action == null) return;
            if (TryInvokeLongEventMethod("QueueLongEvent", action))
            {
                return;
            }

            try
            {
                (fallback ?? action)();
            }
            catch (Exception ex)
            {
                Log.Warning("[AutoTranslationCore] Long event fallback failed: " + ex);
            }
        }

        // 這個方法負責嘗試執行 InvokeLongEventMethod 並回報是否成功。
        // EN: This method tries to invoke long event method and reports whether it succeeded.
        private static bool TryInvokeLongEventMethod(string methodName, Action action)
        {
            try
            {
                MethodInfo[] methods = typeof(LongEventHandler).GetMethods(BindingFlags.Public | BindingFlags.Static);
                for (int i = 0; i < methods.Length; i++)
                {
                    MethodInfo method = methods[i];
                    if (method.Name != methodName) continue;

                    ParameterInfo[] parameters = method.GetParameters();
                    object[] args;
                    if (!TryBuildArgs(parameters, action, out args)) continue;

                    method.Invoke(null, args);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log.Warning("[AutoTranslationCore] Long event compatibility call failed: " + ex);
            }

            return false;
        }

        // 這個方法負責嘗試執行 BuildArgs 並回報是否成功。
        // EN: This method tries to build args and reports whether it succeeded.
        private static bool TryBuildArgs(ParameterInfo[] parameters, Action action, out object[] args)
        {
            args = null;
            if (parameters == null || parameters.Length == 0) return false;
            if (!typeof(Action).IsAssignableFrom(parameters[0].ParameterType)) return false;

            args = new object[parameters.Length];
            args[0] = action;

            for (int i = 1; i < parameters.Length; i++)
            {
                Type parameterType = parameters[i].ParameterType;
                if (parameterType == typeof(string))
                {
                    args[i] = null;
                }
                else if (parameterType == typeof(bool))
                {
                    args[i] = false;
                }
                else if (parameterType == typeof(Action<Exception>))
                {
                    args[i] = null;
                }
                else if (parameterType == typeof(Action))
                {
                    args[i] = null;
                }
                else if (parameters[i].HasDefaultValue)
                {
                    args[i] = parameters[i].DefaultValue;
                }
                else
                {
                    return false;
                }
            }

            return true;
        }
    }
}
