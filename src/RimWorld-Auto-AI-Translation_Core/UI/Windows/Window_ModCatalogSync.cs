using System;
using System.Threading;
using System.Threading.Tasks;
using AutoTranslator_Core.Workflow;
using AutoTranslator_Core.Workflow.Analysis;
using UnityEngine;
using Verse;

namespace AutoTranslator_Core
{
    public sealed class Window_ModCatalogSync : Window
    {
        private static bool _openedThisSession;
        private readonly object _gate = new object();
        private ModCatalogSummary _summary;
        private string _error = string.Empty;
        private string _current = "等待后台任务结束…";
        private int _completed;
        private int _total;
        private DateTime _started;
        private bool _running;
        private bool _stopped;
        private CancellationTokenSource _cancellation;

        public static void OpenOnce()
        {
            if (_openedThisSession) return;
            _openedThisSession = true;
            Find.WindowStack.Add(new Window_ModCatalogSync());
        }

        public Window_ModCatalogSync()
        {
            doCloseX = false;
            doCloseButton = false;
            absorbInputAroundWindow = true;
            closeOnClickedOutside = false;
            closeOnAccept = false;
            closeOnCancel = false;
        }

        public override void PostOpen()
        {
            base.PostOpen();
            // Register the window before starting work so it can display progress.
            _ = SynchronizeAsync();
        }

        public override Vector2 InitialSize => new Vector2(610f, 330f);

        private async Task SynchronizeAsync()
        {
            var cancellation = new CancellationTokenSource();
            lock (_gate)
            {
                _running = true; _stopped = false; _summary = null;
                _error = string.Empty; _completed = 0; _total = 0;
                _current = "等待后台任务结束…";
                _cancellation = cancellation;
            }
            try
            {
                while (WorkflowTaskCoordinator.Instance.IsBusy || AutoTranslatorSettings.LegacyPipelineIsRunning)
                    await Task.Delay(250, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                lock (_gate) { _started = DateTime.UtcNow; _current = "准备数据库并读取 Mod 列表…"; }
                ModCatalogSummary result = await WorkflowBackendRuntime.GetOrCreate().SynchronizeModCatalogAsync(
                    (completed, total, name) => { lock (_gate) { _completed = completed; _total = total; _current = name; } },
                    cancellation.Token);
                lock (_gate) { _summary = result; }
            }
            catch (OperationCanceledException) { lock (_gate) { _stopped = true; } }
            catch (Exception ex) { lock (_gate) { _error = ex.Message; } }
            finally
            {
                lock (_gate) { _running = false; _cancellation = null; }
                cancellation.Dispose();
            }
        }

        public override void DoWindowContents(Rect rect)
        {
            CancellationTokenSource stopRequested = null;
            lock (_gate)
            {
                Text.Font = GameFont.Medium;
                Widgets.Label(new Rect(0f, 0f, rect.width, 34f), "同步 Mod 列表");
                Text.Font = GameFont.Small;
                if (_running)
                {
                    string dots = new string('.', (int)(Time.realtimeSinceStartup * 2f) % 4);
                    Widgets.Label(new Rect(0f, 48f, rect.width, 44f), _current + dots);
                    Rect bar = new Rect(0f, 104f, rect.width, 24f);
                    Widgets.DrawBoxSolid(bar, new Color(0.15f, 0.17f, 0.19f));
                    if (_total > 0)
                        Widgets.DrawBoxSolid(new Rect(bar.x, bar.y, bar.width * _completed / _total, bar.height),
                            new Color(0.25f, 0.65f, 0.8f));
                    else
                    {
                        float pulse = (Mathf.Sin(Time.realtimeSinceStartup * 2f) + 1f) * 0.4f;
                        Widgets.DrawBoxSolid(new Rect(bar.x + bar.width * pulse, bar.y, bar.width * 0.2f, bar.height),
                            new Color(0.25f, 0.65f, 0.8f));
                    }
                    string detail = _total > 0 ? $"已处理 {_completed} / {_total}" : "正在准备";
                    if (_completed >= 5 && _completed < _total)
                    {
                        double seconds = (DateTime.UtcNow - _started).TotalSeconds / _completed * (_total - _completed);
                        detail += $"　预计剩余 {Math.Ceiling(seconds)} 秒";
                    }
                    Widgets.Label(new Rect(0f, 140f, rect.width, 34f), detail);
                    if (_cancellation != null && !_cancellation.IsCancellationRequested &&
                        Widgets.ButtonText(new Rect(rect.width - 120f, rect.height - 38f, 120f, 32f), "停止"))
                        stopRequested = _cancellation;
                    if (_cancellation != null && _cancellation.IsCancellationRequested)
                        Widgets.Label(new Rect(0f, rect.height - 38f, rect.width, 32f), "正在停止，等待当前操作结束…");
                }
                else if (_summary != null)
                {
                    string title = _summary.IsInitial ? "首次建立 Mod 记录" :
                        (_summary.Unchanged ? "Mod 列表未变" : "Mod 列表已更新");
                    Widgets.Label(new Rect(0f, 48f, rect.width, 160f), title +
                        $"\n\n已安装 {_summary.Installed}，已启用 {_summary.Enabled}" +
                        $"\n新启用 {_summary.NewlyEnabled}，禁用 {_summary.Disabled}" +
                        $"\n新增安装 {_summary.Added}，移除安装 {_summary.Removed}");
                }
                else Widgets.Label(new Rect(0f, 48f, rect.width, 150f), _stopped
                    ? "同步已停止，未完成的 Mod 列表更新不会提交。可以重试或关闭窗口。"
                    : "同步未完成，请重试。\n" + _error);
                if (!_running)
                {
                    if (_summary == null && Widgets.ButtonText(new Rect(0f, rect.height - 38f, 120f, 32f), "重试"))
                        _ = SynchronizeAsync();
                    if (Widgets.ButtonText(new Rect(rect.width - 120f, rect.height - 38f, 120f, 32f), "关闭")) Close();
                }
            }
            // Cancel only this window's wait/operation, never whichever other task
            // happened to be active while the window was waiting for its turn.
            if (stopRequested != null)
            {
                try { stopRequested.Cancel(); }
                catch (ObjectDisposedException) { }
            }
        }
    }
}
