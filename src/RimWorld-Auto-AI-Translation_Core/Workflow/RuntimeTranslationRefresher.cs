using AutoTranslator_Core.TargetedHardcodedUi;
using System;
using System.IO;
using System.Threading.Tasks;
using Verse;

namespace AutoTranslator_Core.Workflow
{
    [Flags]
    internal enum RuntimeTranslationRefreshScope
    {
        None = 0,
        Xml = 1,
        Dll = 2
    }

    internal static class RuntimeTranslationRefresher
    {
        internal static async Task<bool> ForceReloadAllAsync()
        {
            AutoTranslatorSettings settings = AutoTranslatorMod.Settings;
            if (settings == null) return false;

            string languageRoot = Path.Combine(
                AutoTranslatorScanner.GetLocalPackPath(),
                "Languages",
                AutoTranslatorScanner.GetFolderNameByLanguage(settings.TargetLang));
            AutoTranslatorScanner.NotifyTranslationFilesChanged(languageRoot);
            bool xmlRefreshed = await AutoTranslatorScanner.RequestMemoryDropAsync();
            bool dllReloadRequested = await EnableAndRequestDllReloadAsync();
            AutoTranslatorSettings.AddLog(
                "运行时译文热重载：XML=" + (xmlRefreshed ? "成功" : "失败") +
                "；DLL=" + (dllReloadRequested ? "已应用" : "应用失败"));
            return xmlRefreshed && dllReloadRequested;
        }

        internal static Task<bool> EnableAndRequestDllReloadAsync()
        {
            TaskCompletionSource<bool> completion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            ATC_Dispatcher.RunOnMainThread(async () =>
            {
                try
                {
                    AutoTranslatorSettings settings = AutoTranslatorMod.Settings;
                    if (settings == null)
                    {
                        completion.TrySetResult(false);
                        return;
                    }

                    settings.EnableHardcodedUiPrototype = true;
                    LoadedModManager.GetMod<AutoTranslatorMod>()?.WriteSettings();
                    completion.TrySetResult(await HardcodedUiTargetedPatchManager.RequestReloadAsync());
                }
                catch (Exception ex)
                {
                    AutoTranslatorSettings.AddErrorLog(
                        "DLL 补丁清单重载请求失败：" + ex.Message);
                    completion.TrySetResult(false);
                }
            });
            return completion.Task;
        }
    }
}
