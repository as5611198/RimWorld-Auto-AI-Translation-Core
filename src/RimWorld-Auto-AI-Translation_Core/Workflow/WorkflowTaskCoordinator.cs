using System;
using System.Threading;
using AutoTranslator_Core.TargetedHardcodedUi;

namespace AutoTranslator_Core.Workflow
{
    public sealed class WorkflowTaskSnapshot
    {
        public bool IsBusy { get; internal set; }
        public bool IsCancellationRequested { get; internal set; }
        public Guid RunId { get; internal set; }
        public WorkflowTaskKind Kind { get; internal set; }
        public string DisplayName { get; internal set; } = string.Empty;
        public DateTime StartedUtc { get; internal set; }
        public long CompletedUnits { get; internal set; }
        public long TotalUnits { get; internal set; }
        public int CurrentItemIndex { get; internal set; }
        public int TotalItems { get; internal set; }
        public int ActiveItemCount { get; internal set; }
        public bool IsConcurrentStage { get; internal set; }
        public int CurrentStageIndex { get; internal set; }
        public int TotalStages { get; internal set; }
        public string CurrentStageName { get; internal set; } = string.Empty;
        public string CurrentMod { get; internal set; } = string.Empty;
        public string Detail { get; internal set; } = string.Empty;
        public long SubCompletedUnits { get; internal set; }
        public long SubTotalUnits { get; internal set; }
        public double SubProgressRatio { get; internal set; } = -1d;
        public DateTime LastUpdatedUtc { get; internal set; }
        public WorkflowRunState State { get; internal set; }
        public string ErrorText { get; internal set; } = string.Empty;
        public DateTime CompletedUtc { get; internal set; }

        public float Progress => TotalUnits <= 0
            ? 0f
            : Math.Max(0f, Math.Min(1f, (float)CompletedUnits / TotalUnits));

        public float SubProgress => SubTotalUnits <= 0
            ? SubProgressRatio < 0d
                ? 0f
                : Math.Max(0f, Math.Min(1f, (float)SubProgressRatio))
            : Math.Max(0f, Math.Min(1f, (float)SubCompletedUnits / SubTotalUnits));
    }

    public sealed class WorkflowTaskLease : IDisposable
    {
        private readonly WorkflowTaskCoordinator _owner;
        private int _disposed;

        internal WorkflowTaskLease(WorkflowTaskCoordinator owner, Guid runId, CancellationToken token)
        {
            _owner = owner;
            RunId = runId;
            CancellationToken = token;
        }

        public Guid RunId { get; }
        public CancellationToken CancellationToken { get; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) _owner.Complete(RunId);
        }
    }

    public sealed class WorkflowTaskCoordinator
    {
        private readonly object _sync = new object();
        private WorkflowTaskSnapshot _active;
        private WorkflowTaskSnapshot _lastCompleted;
        private CancellationTokenSource _cancellation;
        private string _lastRuntimeProgressKey = string.Empty;
        private long _lastProgressSequence;
        private long _workbenchDataRevision;

        public static WorkflowTaskCoordinator Instance { get; } = new WorkflowTaskCoordinator();

        private WorkflowTaskCoordinator() { }

        public bool IsBusy
        {
            get
            {
                lock (_sync)
                    return _active != null || AutoTranslatorScanner.IsMemoryDropBusy ||
                           HardcodedUiTargetedPatchManager.IsLoading;
            }
        }

        public long WorkbenchDataRevision => Interlocked.Read(ref _workbenchDataRevision);

        public void NotifyWorkbenchDataChanged()
        {
            Interlocked.Increment(ref _workbenchDataRevision);
        }

        public WorkflowTaskSnapshot Current
        {
            get
            {
                lock (_sync)
                {
                    if (_active == null) return new WorkflowTaskSnapshot();
                    return new WorkflowTaskSnapshot
                    {
                        IsBusy = true,
                        IsCancellationRequested = _cancellation != null && _cancellation.IsCancellationRequested,
                        RunId = _active.RunId,
                        Kind = _active.Kind,
                        DisplayName = _active.DisplayName,
                        StartedUtc = _active.StartedUtc,
                        CompletedUnits = _active.CompletedUnits,
                        TotalUnits = _active.TotalUnits,
                        CurrentItemIndex = _active.CurrentItemIndex,
                        TotalItems = _active.TotalItems,
                        ActiveItemCount = _active.ActiveItemCount,
                        IsConcurrentStage = _active.IsConcurrentStage,
                        CurrentStageIndex = _active.CurrentStageIndex,
                        TotalStages = _active.TotalStages,
                        CurrentStageName = _active.CurrentStageName,
                        CurrentMod = _active.CurrentMod,
                        Detail = _active.Detail,
                        SubCompletedUnits = _active.SubCompletedUnits,
                        SubTotalUnits = _active.SubTotalUnits,
                        SubProgressRatio = _active.SubProgressRatio,
                        LastUpdatedUtc = _active.LastUpdatedUtc
                    };
                }
            }
        }

        public WorkflowTaskSnapshot LastCompleted
        {
            get { lock (_sync) return Clone(_lastCompleted, false); }
        }

        public void MarkTerminal(Guid runId, WorkflowRunState state, string errorText = "")
        {
            lock (_sync)
            {
                if (_active == null || _active.RunId != runId) return;
                _active.State = state;
                _active.ErrorText = errorText ?? string.Empty;
                _active.CompletedUtc = DateTime.UtcNow;
                _active.LastUpdatedUtc = _active.CompletedUtc;
            }
        }

        public bool TryBegin(WorkflowTaskKind kind, string displayName, out WorkflowTaskLease lease)
        {
            lock (_sync)
            {
                bool memoryDropBusy = AutoTranslatorScanner.IsMemoryDropBusy;
                bool dllReloadBusy = HardcodedUiTargetedPatchManager.IsLoading;
                if (_active != null || AutoTranslatorSettings.LegacyPipelineIsRunning ||
                    memoryDropBusy || dllReloadBusy)
                {
                    AutoTranslatorSettings.AddDebugLog(
                        "workflow.begin rejected kind=" + kind + " reason=" +
                        (memoryDropBusy ? "memory_drop_busy" :
                         dllReloadBusy ? "dll_reload_busy" : "busy"));
                    lease = null;
                    return false;
                }

                Guid runId = Guid.NewGuid();
                _cancellation = new CancellationTokenSource();
                _active = new WorkflowTaskSnapshot
                {
                    IsBusy = true,
                    RunId = runId,
                    Kind = kind,
                    DisplayName = GetLocalizedTaskName(kind),
                    StartedUtc = DateTime.UtcNow,
                    LastUpdatedUtc = DateTime.UtcNow
                };
                _lastRuntimeProgressKey = string.Empty;
                _lastProgressSequence = 0L;
                lease = new WorkflowTaskLease(this, runId, _cancellation.Token);
                AutoTranslatorSettings.AddDebugLog(
                    "workflow.begin run=" + runId.ToString("N") + " kind=" + kind +
                    " name=" + (displayName ?? string.Empty));
                return true;
            }
        }

        public bool RequestCancellation()
        {
            lock (_sync)
            {
                if (_cancellation == null || _cancellation.IsCancellationRequested) return false;
                _cancellation.Cancel();
                AutoTranslatorSettings.AddDebugLog(
                    "workflow.cancel requested run=" + (_active?.RunId.ToString("N") ?? string.Empty));
                return true;
            }
        }

        public void ReportProgress(
            long completedUnits,
            long totalUnits,
            string currentMod,
            string detail,
            int currentItemIndex = 0,
            int totalItems = 0,
            long subCompletedUnits = 0,
            long subTotalUnits = 0,
            bool writeRuntimeLog = true,
            int currentStageIndex = 0,
            int totalStages = 0,
            string currentStageName = "",
            double subProgressRatio = -1d,
            int activeItemCount = 0,
            bool isConcurrentStage = false,
            long progressSequence = 0L)
        {
            string runtimeLine = null;
            lock (_sync)
            {
                if (_active == null) return;
                if (progressSequence > 0L)
                {
                    if (progressSequence <= _lastProgressSequence) return;
                    _lastProgressSequence = progressSequence;
                }
                _active.CompletedUnits = Math.Max(0, completedUnits);
                _active.TotalUnits = Math.Max(0, totalUnits);
                _active.CurrentItemIndex = Math.Max(0, currentItemIndex);
                _active.TotalItems = Math.Max(0, totalItems);
                _active.ActiveItemCount = Math.Max(0, activeItemCount);
                _active.IsConcurrentStage = isConcurrentStage;
                _active.CurrentStageIndex = Math.Max(0, currentStageIndex);
                _active.TotalStages = Math.Max(0, totalStages);
                _active.CurrentStageName = currentStageName ?? string.Empty;
                _active.CurrentMod = currentMod ?? string.Empty;
                _active.Detail = detail ?? string.Empty;
                _active.SubCompletedUnits = Math.Max(0, subCompletedUnits);
                _active.SubTotalUnits = Math.Max(0, subTotalUnits);
                _active.SubProgressRatio = subProgressRatio < 0d
                    ? -1d
                    : Math.Max(0d, Math.Min(1d, subProgressRatio));
                _active.LastUpdatedUtc = DateTime.UtcNow;
                string phase = GetProgressPhase(detail);
                string key = (currentMod ?? string.Empty) + "\n" + phase;
                if (writeRuntimeLog && !string.IsNullOrWhiteSpace(currentMod) &&
                    !string.Equals(key, _lastRuntimeProgressKey, StringComparison.Ordinal))
                {
                    _lastRuntimeProgressKey = key;
                    runtimeLine = "▶ " + _active.DisplayName +
                                  " · " + currentMod +
                                  (string.IsNullOrWhiteSpace(phase) ? string.Empty : " · " + phase);
                }
            }
            if (!string.IsNullOrWhiteSpace(runtimeLine))
                AutoTranslatorSettings.AddLog(runtimeLine);
        }

        public void ReportStage(
            string currentMod,
            string detail,
            string runtimeMessage,
            string debugMessage)
        {
            lock (_sync)
            {
                if (_active == null) return;
                _active.CurrentMod = currentMod ?? string.Empty;
                _active.Detail = detail ?? string.Empty;
                _active.LastUpdatedUtc = DateTime.UtcNow;
            }
            if (!string.IsNullOrWhiteSpace(runtimeMessage))
                AutoTranslatorSettings.AddLog(runtimeMessage);
            if (!string.IsNullOrWhiteSpace(debugMessage))
                AutoTranslatorSettings.AddDebugLog(debugMessage);
        }

        internal void Complete(Guid runId)
        {
            CancellationTokenSource toDispose = null;
            lock (_sync)
            {
                if (_active == null || _active.RunId != runId) return;
                if (_active.CompletedUtc == default(DateTime))
                {
                    _active.State = WorkflowRunState.Completed;
                    _active.CompletedUtc = DateTime.UtcNow;
                }
                _lastCompleted = Clone(_active, false);
                _active = null;
                _lastRuntimeProgressKey = string.Empty;
                _lastProgressSequence = 0L;
                toDispose = _cancellation;
                _cancellation = null;
            }
            if (toDispose != null) toDispose.Dispose();
            AutoTranslatorSettings.AddDebugLog("workflow.complete run=" + runId.ToString("N"));
        }

        private static WorkflowTaskSnapshot Clone(WorkflowTaskSnapshot source, bool busy)
        {
            if (source == null) return new WorkflowTaskSnapshot();
            return new WorkflowTaskSnapshot
            {
                IsBusy = busy, IsCancellationRequested = source.IsCancellationRequested,
                RunId = source.RunId, Kind = source.Kind, DisplayName = source.DisplayName,
                StartedUtc = source.StartedUtc, CompletedUnits = source.CompletedUnits,
                TotalUnits = source.TotalUnits, CurrentItemIndex = source.CurrentItemIndex,
                TotalItems = source.TotalItems, ActiveItemCount = source.ActiveItemCount,
                IsConcurrentStage = source.IsConcurrentStage,
                CurrentStageIndex = source.CurrentStageIndex,
                TotalStages = source.TotalStages,
                CurrentStageName = source.CurrentStageName,
                CurrentMod = source.CurrentMod,
                Detail = source.Detail, SubCompletedUnits = source.SubCompletedUnits,
                SubTotalUnits = source.SubTotalUnits, SubProgressRatio = source.SubProgressRatio,
                LastUpdatedUtc = source.LastUpdatedUtc,
                State = source.State, ErrorText = source.ErrorText, CompletedUtc = source.CompletedUtc
            };
        }

        private static string GetProgressPhase(string detail)
        {
            string value = detail ?? string.Empty;
            int separator = value.IndexOf('·');
            return (separator >= 0 ? value.Substring(0, separator) : value).Trim();
        }

        private static string GetLocalizedTaskName(WorkflowTaskKind kind)
        {
            switch (kind)
            {
                case WorkflowTaskKind.StartupSynchronization:
                    return AutoTranslatorMod.WfText("启动译文加载", "Startup translation loading");
                case WorkflowTaskKind.ManualSynchronization:
                    return AutoTranslatorMod.WfText("译文状态同步", "Translation status synchronization");
                case WorkflowTaskKind.AnalysisBatch:
                case WorkflowTaskKind.XmlAnalysis:
                case WorkflowTaskKind.DllAnalysis:
                    return AutoTranslatorMod.WfText("本地分析", "Local analysis");
                case WorkflowTaskKind.DryRun:
                    return AutoTranslatorMod.WfText("试跑", "Dry run");
                case WorkflowTaskKind.AiReview:
                    return AutoTranslatorMod.WfText("AI复核", "AI review");
                case WorkflowTaskKind.AiTranslation:
                    return AutoTranslatorMod.WfText("AI翻译", "AI translation");
                case WorkflowTaskKind.OneClickTranslation:
                    return AutoTranslatorMod.WfText("一键翻译", "One-click translation");
                case WorkflowTaskKind.CloudTranslation:
                    return AutoTranslatorMod.WfText("云端译文下载与同步", "Cloud translation download and synchronization");
                case WorkflowTaskKind.ManualTranslationEdit:
                    return AutoTranslatorMod.WfText("手动译文编辑", "Manual translation edit");
                case WorkflowTaskKind.ClassificationEdit:
                    return AutoTranslatorMod.WfText("分类编辑", "Classification edit");
                case WorkflowTaskKind.ConfigurationEdit:
                    return AutoTranslatorMod.WfText("配置更新", "Configuration update");
                case WorkflowTaskKind.ExpiredDataCleanup:
                    return AutoTranslatorMod.WfText("清理过期数据", "Expired data cleanup");
                case WorkflowTaskKind.ResultCleanup:
                    return AutoTranslatorMod.WfText("清除所选 Mod 的结果", "Clear selected mod results");
                case WorkflowTaskKind.RuntimeTranslationReload:
                    return AutoTranslatorMod.WfText("译文热重载", "Translation hot reload");
                case WorkflowTaskKind.HistoricalDataMigration:
                    return AutoTranslatorMod.WfText("迁移历史数据", "Migrate historical data");
                default:
                    return AutoTranslatorMod.WfText("后台任务", "Background task");
            }
        }
    }
}
