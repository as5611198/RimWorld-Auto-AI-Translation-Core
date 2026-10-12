using System;
using System.Threading.Tasks;
using Verse;

namespace AutoTranslator_Core.Workflow
{
    public static class WorkflowBackendRuntime
    {
        private static readonly object Gate = new object();
        private static WorkflowBackend _backend;
        private static bool _startupQueued;

        public static WorkflowBackend GetOrCreate()
        {
            lock (Gate)
            {
                if (_backend != null) return _backend;
                if (string.IsNullOrWhiteSpace(AutoTranslatorMod.CoreModRoot))
                    throw new InvalidOperationException("Core mod root is unavailable.");
                AutoTranslatorScanner.EnsureWorkflowPackStorageReady();
                _backend = WorkflowBackend.CreateDefault(
                    AutoTranslatorMod.CoreModRoot,
                    AutoTranslatorScanner.GetLocalPackPath());
                return _backend;
            }
        }

        public static void QueueStartupTranslationLoading()
        {
            lock (Gate)
            {
                if (_startupQueued) return;
                _startupQueued = true;
            }
            _ = RunStartupTranslationLoadingAsync();
        }

        private static async Task RunStartupTranslationLoadingAsync()
        {
            bool completed = false;
            try
            {
                if (AutoTranslatorMod.Settings == null) return;
                while (!completed)
                {
                    while (WorkflowTaskCoordinator.Instance.IsBusy ||
                           AutoTranslatorSettings.LegacyPipelineIsRunning)
                        await Task.Delay(500);
                    if (!WorkflowTaskCoordinator.Instance.TryBegin(
                            WorkflowTaskKind.StartupSynchronization,
                            "Startup translation injection",
                            out WorkflowTaskLease lease))
                    {
                        await Task.Delay(500);
                        continue;
                    }
                    using (lease)
                    {
                        try
                        {
                            AutoTranslatorSettings.ResetPipelineCancellation();
                            WorkflowTaskCoordinator.Instance.ReportStage(
                                string.Empty,
                                "读取本地缓存译文并注入游戏",
                                "⏳ 启动译文加载：读取本地缓存译文并注入游戏",
                                "workflow.startup-injection begin; database-candidates=skipped");
                            WorkflowTaskCoordinator.Instance.ReportProgress(
                                0, 1, string.Empty, "读取本地缓存译文并注入游戏",
                                subCompletedUnits: 0, subTotalUnits: 1,
                                writeRuntimeLog: false);
                            bool reloadSucceeded = await AutoTranslatorScanner.RequestMemoryDropAsync();
                            if (!reloadSucceeded)
                                throw new InvalidOperationException("Translation memory reload failed.");
                            lease.CancellationToken.ThrowIfCancellationRequested();
                            WorkflowTaskCoordinator.Instance.ReportProgress(
                                1, 1, string.Empty, "本地缓存译文注入完成",
                                subCompletedUnits: 1, subTotalUnits: 1,
                                writeRuntimeLog: false);
                            WorkflowTaskCoordinator.Instance.MarkTerminal(
                                lease.RunId, WorkflowRunState.Completed);
                            AutoTranslatorSettings.AddLog("✓ 启动译文加载完成");
                            completed = true;
                        }
                        catch (OperationCanceledException ex)
                        {
                            WorkflowTaskCoordinator.Instance.MarkTerminal(
                                lease.RunId, WorkflowRunState.Cancelled, ex.Message);
                            throw;
                        }
                        catch (Exception ex)
                        {
                            WorkflowTaskCoordinator.Instance.MarkTerminal(
                                lease.RunId, WorkflowRunState.Failed, ex.Message);
                            throw;
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Log.Warning("[AutoTranslationCore] Startup translation loading was cancelled.");
            }
            catch (Exception ex)
            {
                Log.Error("[AutoTranslationCore] Startup translation loading failed: " + ex);
            }
            finally
            {
                if (!completed)
                {
                    lock (Gate) _startupQueued = false;
                }
            }
        }
    }
}
