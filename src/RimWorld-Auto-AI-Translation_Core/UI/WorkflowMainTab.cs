using AutoTranslator_Core.Workflow;
using AutoTranslator_Core.Workflow.AI;
using AutoTranslator_Core.Workflow.Analysis;
using AutoTranslator_Core.Workflow.DryRun;
using RimWorld;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using Verse;

namespace AutoTranslator_Core
{
    public partial class AutoTranslatorMod : Mod
    {
        private sealed class WorkflowUiLoadResult
        {
            public WorkflowWorkbenchSnapshot Snapshot;
            public WorkflowConfiguration Configuration;
            public int ModSnapshotVersion;
            public long WorkbenchDataRevision;
        }

        private enum WorkflowInspectorMode
        {
            Collapsed,
            TaskDetail,
            RuntimeLog,
            WarningLog,
            ErrorLog
        }

        private enum WorkflowModSortField
        {
            ModName,
            LatestDryRun
        }

        private enum WorkflowModFilter
        {
            All,
            NeedsTranslation,
            Undetermined,
            Untranslated,
            Failed
        }

        private static Task<WorkflowUiLoadResult> _workflowUiLoadTask;
        private static WorkflowWorkbenchSnapshot _workflowUiSnapshot;
        private static WorkflowConfiguration _workflowUiConfiguration;
        private static string _workflowUiLoadError = string.Empty;
        private static string _workflowModSearch = string.Empty;
        private static WorkflowModFilter _workflowModFilter;
        private static readonly HashSet<string> _workflowSelectedModIdentities =
            new HashSet<string>(StringComparer.Ordinal);
        private static bool _workflowSelectionGestureActive;
        private static readonly HashSet<string> _workflowSelectionGestureVisited =
            new HashSet<string>(StringComparer.Ordinal);
        private const float WorkflowSelectionAutoScrollStartDistance = 4f;
        private const float WorkflowSelectionAutoScrollFullSpeedDistance = 90f;
        private const float WorkflowSelectionAutoScrollMinimumSpeed = 35f;
        private const float WorkflowSelectionAutoScrollMaximumSpeed = 420f;
        private const float WorkflowSelectionAutoScrollHorizontalTolerance = 10f;
        private static List<WorkflowModMetadataSnapshot> _workflowModMetadataCache =
            new List<WorkflowModMetadataSnapshot>();
        private static readonly Dictionary<string, ModMetaData> _workflowModsByIdentity =
            new Dictionary<string, ModMetaData>(StringComparer.Ordinal);
        private static string _workflowModMetadataSignature = string.Empty;
        private static long _workflowNextModMetadataCheckUtcTicks;
        private static int _workflowModMetadataVersion;
        private static Vector2 _workflowModScroll = Vector2.zero;
        private static WorkflowInspectorMode _workflowInspectorMode;
        private static WorkflowModSortField _workflowModSortField = WorkflowModSortField.ModName;
        private static bool _workflowModSortAscending = true;
        private static bool _workflowForceAnalysis;
        private static bool _workflowOperationCompletionPendingRefresh;
        private static bool _workflowUiPreviouslyBusy;
        private static Guid _workflowLastCollapsedCompletionRunId;
        private static int _workflowUiLoadedModSnapshotVersion = -1;
        private static long _workflowUiLoadedDataRevision = -1;
        private static bool _expiredDataPreviewLoading;
        private static int _workflowLastDrawFrame = -1;

        private void DrawMainTab(Listing_Standard listing, Rect viewRect)
        {
            int currentFrame = Time.frameCount;
            if (_workflowLastDrawFrame < 0 || currentFrame > _workflowLastDrawFrame + 1)
                _workflowOperationCompletionPendingRefresh = true;
            _workflowLastDrawFrame = currentFrame;
            if (_workflowSelectionGestureActive && !Input.GetMouseButton(0))
                EndWorkflowSelectionGesture();
            UpdateWorkflowUiReadModel();
            float height = Mathf.Max(540f, viewRect.height - listing.CurHeight - 4f);
            Rect full = listing.GetRect(height);
            float y = full.y;

            Rect toolbar = new Rect(full.x, y, full.width, 30f);
            DrawWorkflowToolbar(toolbar);
            y += 34f;

            Rect selectionSummary = new Rect(full.x, y, full.width, 24f);
            DrawWorkflowSelectionSummary(selectionSummary);
            y += 28f;

            WorkflowTaskSnapshot task = WorkflowTaskCoordinator.Instance.Current;
            bool anyTask = task.IsBusy || WorkflowTaskCoordinator.Instance.IsBusy ||
                           AutoTranslatorSettings.LegacyPipelineIsRunning;
            float inspectorHeight = anyTask
                ? (_workflowInspectorMode == WorkflowInspectorMode.Collapsed ? 116f : 178f)
                : (_workflowInspectorMode == WorkflowInspectorMode.Collapsed ? 25f : 132f);
            const float optionsHeight = 27f;
            const float actionsHeight = 31f;
            const float lowerGaps = 12f;
            float listHeight = Mathf.Max(
                245f,
                full.yMax - y - inspectorHeight - optionsHeight - actionsHeight - lowerGaps);

            DrawWorkflowModList(new Rect(full.x, y, full.width, listHeight));
            y += listHeight + 5f;

            if (inspectorHeight > 0f)
            {
                DrawWorkflowInspector(
                    new Rect(full.x, y, full.width, inspectorHeight), task, anyTask);
                y += inspectorHeight + 3f;
            }

            DrawWorkflowOptions(new Rect(full.x, y, full.width, optionsHeight), anyTask);
            y += optionsHeight + 2f;
            DrawWorkflowActions(new Rect(full.x, y, full.width, actionsHeight), anyTask);
        }

        private void DrawWorkflowToolbar(Rect rect)
        {
            float gap = rect.width < 850f ? 3f : 7f;
            float available = rect.width - gap * 5f;
            float searchWidth = available * 0.31f;
            float scopeWidth = available * 0.19f;
            float filterWidth = available * 0.21f;
            float actionWidth = (available - searchWidth - scopeWidth - filterWidth) / 3f;
            Rect searchGroupRect = new Rect(rect.x, rect.y, searchWidth, rect.height);
            Rect searchIconRect = new Rect(searchGroupRect.x, searchGroupRect.y, 27f, searchGroupRect.height);
            Rect searchRect = new Rect(searchIconRect.xMax + 3f, searchGroupRect.y,
                searchGroupRect.width - searchIconRect.width - 3f, searchGroupRect.height);
            Rect scopeRect = new Rect(searchRect.xMax + gap, rect.y, scopeWidth, rect.height);
            Rect filterRect = new Rect(scopeRect.xMax + gap, rect.y, filterWidth, rect.height);
            Rect refreshRect = new Rect(filterRect.xMax + gap, rect.y, actionWidth, rect.height);
            Rect hotReloadRect = new Rect(refreshRect.xMax + gap, rect.y, actionWidth, rect.height);
            Rect manageRect = new Rect(hotReloadRect.xMax + gap, rect.y,
                rect.xMax - hotReloadRect.xMax - gap, rect.height);

            Text.Font = GameFont.Medium;
            Text.Anchor = TextAnchor.MiddleCenter;
            GUI.color = WorkflowUiStyle.MutedText;
            Widgets.Label(searchIconRect, "⌕");
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;
            Text.Font = GameFont.Small;
            string nextSearch = Widgets.TextField(searchRect, _workflowModSearch ?? string.Empty);
            if (!string.Equals(nextSearch, _workflowModSearch, StringComparison.Ordinal))
            {
                _workflowModSearch = nextSearch;
                _workflowModScroll = Vector2.zero;
            }
            TooltipHandler.TipRegion(searchGroupRect, WfText("搜索 Mod 名称或 Package ID", "Search mod name or package ID"));

            Text.Anchor = TextAnchor.MiddleCenter;
            GUI.color = WorkflowUiStyle.MutedText;
            Widgets.Label(scopeRect, GetWorkflowScopeLabel());
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;
            if (WorkflowUiStyle.Button(filterRect, GetWorkflowFilterLabel()))
                OpenWorkflowFilterMenu();
            bool busy = WorkflowTaskCoordinator.Instance.IsBusy || AutoTranslatorSettings.LegacyPipelineIsRunning;
            if (WorkflowUiStyle.Button(refreshRect, WfText("刷新状态", "Refresh"),
                    WorkflowButtonStyle.Quiet, !busy))
                StartWorkflowOperation(
                    backend => backend.RunManualTranslationStateRefreshAsync(),
                    WfText("刷新翻译状态", "Refresh translation state"));
            if (WorkflowUiStyle.Button(hotReloadRect, WfText("热重载", "Hot reload"),
                    WorkflowButtonStyle.Warning, !busy))
                StartWorkflowOperation(
                    backend => backend.RunRuntimeTranslationReloadAsync(),
                    WfText("热重载 XML／DLL 译文", "Hot reload XML/DLL translations"));
            TooltipHandler.TipRegion(hotReloadRect, WfText(
                "先根据数据库中的已保存结果重建缺失或不一致的 XML／DLL 输出文件，再重新加载到游戏。\n不需要勾选 Mod，不会重新分析，也不会调用 AI。",
                "Rebuild missing or inconsistent XML/DLL output files from saved database results, then reload them into the game.\nNo Mod selection, analysis, or AI call is required."));
            if (WorkflowUiStyle.Button(manageRect, WfText("管理", "Manage")))
                OpenWorkflowManagementMenu();
        }

        private void DrawWorkflowSelectionSummary(Rect rect)
        {
            Widgets.DrawBoxSolid(rect, WorkflowUiStyle.RaisedPanel);
            Widgets.DrawBoxSolid(new Rect(rect.x, rect.y, 3f, rect.height), WorkflowUiStyle.GoodText);
            List<WorkflowModSummary> selected = (_workflowUiSnapshot?.Mods ?? new List<WorkflowModSummary>())
                .Where(mod => !mod.IsProtectedSystemPackage &&
                              _workflowSelectedModIdentities.Contains(mod.ModIdentity)).ToList();
            List<WorkflowModSummary> filtered = GetFilteredWorkflowMods()
                .Where(mod => !mod.IsProtectedSystemPackage).ToList();
            HashSet<string> filteredIdentities = new HashSet<string>(
                filtered.Select(mod => mod.ModIdentity), StringComparer.Ordinal);
            int selectedOutsideFilter = selected.Count(mod => !filteredIdentities.Contains(mod.ModIdentity));
            bool busy = WorkflowTaskCoordinator.Instance.IsBusy || AutoTranslatorSettings.LegacyPipelineIsRunning;
            Text.Font = GameFont.Tiny;
            float gap = rect.width < 850f ? 3f : 6f;
            float buttonWidth = rect.width < 850f ? 88f : 126f;
            Rect clearRect = new Rect(rect.xMax - buttonWidth - 5f, rect.y + 2f, buttonWidth, 20f);
            Rect invertRect = new Rect(clearRect.x - gap - buttonWidth, rect.y + 2f, buttonWidth, 20f);
            Rect selectRect = new Rect(invertRect.x - gap - buttonWidth, rect.y + 2f, buttonWidth, 20f);
            string countText = WfText(
                $"已选择 {selected.Count} 个 Mod" +
                (selectedOutsideFilter > 0 ? $"（筛选外还有 {selectedOutsideFilter} 个）" : string.Empty),
                $"Selected {selected.Count} mods" +
                (selectedOutsideFilter > 0 ? $" ({selectedOutsideFilter} outside filter)" : string.Empty));
            GUI.color = selected.Count > 0 ? WorkflowUiStyle.GoodText : WorkflowUiStyle.MutedText;
            Widgets.Label(new Rect(rect.x + 9f, rect.y + 3f,
                Mathf.Max(20f, selectRect.x - rect.x - 15f), rect.height - 4f), countText);
            GUI.color = Color.white;
            if (WorkflowUiStyle.Button(selectRect,
                    WfText("全选筛选结果", "Select filtered"), WorkflowButtonStyle.Quiet, !busy, GameFont.Tiny))
                foreach (WorkflowModSummary mod in filtered)
                    _workflowSelectedModIdentities.Add(mod.ModIdentity);
            if (WorkflowUiStyle.Button(invertRect,
                    WfText("反选筛选结果", "Invert filtered"), WorkflowButtonStyle.Quiet, !busy, GameFont.Tiny))
                foreach (WorkflowModSummary mod in filtered)
                    if (!_workflowSelectedModIdentities.Remove(mod.ModIdentity))
                        _workflowSelectedModIdentities.Add(mod.ModIdentity);
            if (WorkflowUiStyle.Button(clearRect, WfText("清空选择", "Clear"),
                    WorkflowButtonStyle.Quiet, selected.Count > 0 && !busy, GameFont.Tiny))
            {
                _workflowSelectedModIdentities.Clear();
                EndWorkflowSelectionGesture();
            }
            Text.Font = GameFont.Small;
        }

        private void DrawWorkflowModList(Rect rect)
        {
            if (Event.current.rawType == EventType.MouseUp && Event.current.button == 0)
            {
                _workflowSelectionGestureActive = false;
                _workflowSelectionGestureVisited.Clear();
            }
            Widgets.DrawBoxSolid(rect, WorkflowUiStyle.Panel);
            WorkflowUiStyle.DrawBorder(rect, new Color(0.28f, 0.31f, 0.33f));
            const float headerHeight = 23f;
            const float rowHeight = 39f;
            Rect header = new Rect(rect.x, rect.y, rect.width, headerHeight);
            Widgets.DrawBoxSolid(header, WorkflowUiStyle.Header);
            float[] widths = GetWorkflowColumnWidths(rect.width - 18f);
            float x = header.x + 4f;
            Text.Font = GameFont.Tiny;
            DrawWorkflowSortHeader(new Rect(x, header.y, widths[0], header.height),
                WfText("Mod", "Mod"), WorkflowModSortField.ModName);
            x += widths[0];
            Widgets.Label(new Rect(x, header.y + 3f, widths[1], header.height - 3f),
                WfText("本地分析结果", "Local analysis"));
            x += widths[1];
            Widgets.Label(new Rect(x, header.y + 3f, widths[2], header.height - 3f),
                WfText("当前有效分类", "Effective classification"));
            x += widths[2];
            Widgets.Label(new Rect(x, header.y + 3f, widths[3], header.height - 3f),
                WfText("翻译状态", "Translation"));
            x += widths[3];
            DrawWorkflowSortHeader(new Rect(x, header.y, widths[4], header.height),
                WfText("最近试跑", "Latest dry run"), WorkflowModSortField.LatestDryRun);
            x += widths[4];
            Widgets.Label(new Rect(x, header.y + 3f, widths[5], header.height - 3f),
                WfText("详情", "Detail"));
            Text.Font = GameFont.Small;

            List<WorkflowModSummary> mods = GetFilteredWorkflowMods();
            Rect outRect = new Rect(rect.x + 2f, header.yMax, rect.width - 4f, rect.height - headerHeight - 2f);
            float viewHeight = Mathf.Max(outRect.height, mods.Count * rowHeight);
            Rect view = new Rect(0f, 0f, outRect.width - 16f, viewHeight);
            float maximumScroll = Mathf.Max(0f, viewHeight - outRect.height);
            _workflowModScroll.y = Mathf.Clamp(_workflowModScroll.y, 0f, maximumScroll);
            bool busy = WorkflowTaskCoordinator.Instance.IsBusy ||
                        AutoTranslatorSettings.LegacyPipelineIsRunning;
            if (busy || !Application.isFocused) EndWorkflowSelectionGesture();
            UpdateWorkflowSelectionAutoScroll(
                outRect, mods, rowHeight, maximumScroll, busy);
            Widgets.BeginScrollView(outRect, ref _workflowModScroll, view);
            int visibleRowCount = Mathf.Max(1, Mathf.CeilToInt(outRect.height / rowHeight));
            int queryBatchSize = Mathf.Max(visibleRowCount, visibleRowCount * 3);
            int prefetchRows = Mathf.Max(0, (queryBatchSize - visibleRowCount) / 2);
            int firstVisible = Mathf.Max(0, Mathf.FloorToInt(_workflowModScroll.y / rowHeight));
            int firstRow = Mathf.Max(0, firstVisible - prefetchRows);
            int lastRowExclusive = Mathf.Min(mods.Count, firstRow + queryBatchSize);
            for (int i = firstRow; i < lastRowExclusive; i++)
            {
                Rect row = new Rect(0f, i * rowHeight, view.width, rowHeight);
                if (row.yMax < _workflowModScroll.y ||
                    row.y > _workflowModScroll.y + outRect.height) continue;
                DrawWorkflowModRow(row, mods[i], widths, i);
            }
            if (mods.Count == 0)
            {
                string empty = !string.IsNullOrWhiteSpace(_workflowUiLoadError)
                    ? WfText("读取工作流数据失败：", "Failed to read workflow data: ") + _workflowUiLoadError
                    : _workflowUiLoadTask != null
                        ? WfText("正在读取工作流数据库……", "Loading workflow database...")
                        : WfText("没有符合当前筛选条件的 Mod。", "No mods match the current filter.");
                Widgets.Label(new Rect(8f, 10f, view.width - 16f, 30f), empty);
            }
            Widgets.EndScrollView();
        }

        private static void DrawWorkflowSortHeader(
            Rect rect, string label, WorkflowModSortField field)
        {
            bool selected = _workflowModSortField == field;
            string arrow = !selected ? "⇅" : _workflowModSortAscending ? "↑" : "↓";
            const float iconWidth = 22f;
            const float labelIconGap = 4f;
            float availableLabelWidth = Mathf.Max(1f, rect.width - iconWidth - labelIconGap - 6f);
            float labelWidth = Mathf.Min(Text.CalcSize(label).x + 1f, availableLabelWidth);
            Rect labelRect = new Rect(rect.x + 3f, rect.y + 3f,
                labelWidth, rect.height - 3f);
            Rect iconRect = new Rect(labelRect.xMax + labelIconGap, rect.y + 1f,
                iconWidth, rect.height - 2f);
            Widgets.Label(labelRect, label);
            Text.Anchor = TextAnchor.MiddleCenter;
            GUI.color = selected ? WorkflowUiStyle.GoodText : WorkflowUiStyle.MutedText;
            Widgets.Label(iconRect, arrow);
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;
            TooltipHandler.TipRegion(iconRect, selected
                ? (_workflowModSortAscending
                    ? WfText("当前正序；点击切换为逆序", "Ascending; click for descending")
                    : WfText("当前逆序；点击切换为正序", "Descending; click for ascending"))
                : WfText("点击使用此列排序", "Sort by this column"));
            if (!Widgets.ButtonInvisible(iconRect)) return;
            if (selected)
            {
                _workflowModSortAscending = !_workflowModSortAscending;
            }
            else
            {
                _workflowModSortField = field;
                _workflowModSortAscending = field == WorkflowModSortField.ModName;
            }
            _workflowModScroll = Vector2.zero;
        }

        private void DrawWorkflowModRow(Rect row, WorkflowModSummary mod, float[] widths, int index)
        {
            if ((index & 1) == 1) Widgets.DrawBoxSolid(row, new Color(1f, 1f, 1f, 0.025f));
            bool selected = _workflowSelectedModIdentities.Contains(mod.ModIdentity);
            if (selected) Widgets.DrawBoxSolid(row, WorkflowUiStyle.Selection);
            else Widgets.DrawHighlightIfMouseover(row);
            Widgets.DrawLineHorizontal(row.x, row.yMax - 1f, row.width);
            float x = row.x + 4f;

            Rect modCell = new Rect(x, row.y, widths[0], row.height);
            bool busy = WorkflowTaskCoordinator.Instance.IsBusy || AutoTranslatorSettings.LegacyPipelineIsRunning;
            Rect selectionHitRect = new Rect(modCell.x, modCell.y + 4f, 25f, 30f);
            HandleWorkflowSelectionGesture(selectionHitRect, mod, busy);
            DrawWorkflowSelectionMark(selectionHitRect, selected, mod.IsProtectedSystemPackage, busy);
            Text.Font = GameFont.Tiny;
            Widgets.Label(new Rect(modCell.x + 27f, modCell.y + 3f, modCell.width - 29f, 18f), mod.DisplayName);
            GUI.color = Color.grey;
            Widgets.Label(new Rect(modCell.x + 27f, modCell.y + 19f, modCell.width - 29f, 17f), mod.PackageId);
            GUI.color = Color.white;
            x += widths[0];

            if (mod.IsProtectedSystemPackage)
            {
                Rect protectedState = new Rect(x, row.y + 5f,
                    widths[1] + widths[2] + widths[3] + widths[4] - 5f, row.height - 10f);
                GUI.color = WorkflowUiStyle.MutedText;
                Widgets.Label(protectedState,
                    WfText("系统／官方内容，不作为翻译目标", "System/official content; not a translation target"));
                GUI.color = Color.white;
                Text.Font = GameFont.Small;
                return;
            }

            Rect analysisCell = new Rect(x, row.y + 1f, widths[1] - 3f, row.height - 2f);
            DrawWorkflowAnalysisLine(new Rect(analysisCell.x, analysisCell.y, analysisCell.width, 18f),
                "XML", mod.XmlAnalysis);
            DrawWorkflowAnalysisLine(new Rect(analysisCell.x, analysisCell.y + 18f, analysisCell.width, 18f),
                "DLL", mod.DllAnalysis);
            if (Widgets.ButtonInvisible(analysisCell))
                Find.WindowStack.Add(new Window_WorkflowLocalAnalysisSummary(mod));
            TooltipHandler.TipRegion(analysisCell,
                WfText("点击查看 XML／DLL 原始分类统计及失败原因", "View raw XML/DLL classification counts and errors"));
            GUI.color = Color.white;
            x += widths[1];
            DrawWorkflowEffectiveStateLines(new Rect(x, row.y + 1f, widths[2] - 3f, row.height - 2f), mod);
            x += widths[2];
            DrawWorkflowTranslationStateLines(new Rect(x, row.y + 1f, widths[3] - 3f, row.height - 2f), mod);
            x += widths[3];
            DryRunReport perMod = null;
            _workflowUiSnapshot?.LatestDryRun?.ModReports?.TryGetValue(mod.ModIdentity, out perMod);
            Rect dryRunCell = new Rect(x, row.y + 5f, widths[4] - 3f, 27f);
            string dryRunText = perMod == null
                ? WfText("未包含", "Not included")
                : GetDryRunProtectedBudget(perMod).ToString("N0") + " Token";
            if (WorkflowUiStyle.Button(dryRunCell, dryRunText, WorkflowButtonStyle.Link,
                    perMod != null, GameFont.Tiny) && perMod != null)
                OpenPerModDryRunReport(mod, perMod);
            x += widths[4];
            if (WorkflowUiStyle.Button(
                    new Rect(x + 2f, row.y + 6f, Mathf.Max(44f, widths[5] - 5f), 27f),
                    WfText("查看", "View"), WorkflowButtonStyle.Link, true, GameFont.Tiny))
            {
                OpenWorkflowEditorForMod(mod.ModIdentity);
            }
            Text.Font = GameFont.Small;
        }

        private static void HandleWorkflowSelectionGesture(
            Rect hitRect,
            WorkflowModSummary mod,
            bool busy)
        {
            Event current = Event.current;
            if (mod == null || mod.IsProtectedSystemPackage || busy) return;
            bool inside = hitRect.Contains(current.mousePosition);
            if (current.type == EventType.MouseDown && current.button == 0 && inside)
            {
                _workflowSelectionGestureActive = true;
                _workflowSelectionGestureVisited.Clear();
                ToggleWorkflowModSelectionOnce(mod.ModIdentity);
                current.Use();
                return;
            }
            if (current.type == EventType.MouseDrag && current.button == 0 &&
                _workflowSelectionGestureActive && inside)
            {
                ToggleWorkflowModSelectionOnce(mod.ModIdentity);
                current.Use();
            }
        }

        private static void ToggleWorkflowModSelectionOnce(string modIdentity)
        {
            if (string.IsNullOrWhiteSpace(modIdentity) ||
                !_workflowSelectionGestureVisited.Add(modIdentity)) return;
            if (!_workflowSelectedModIdentities.Remove(modIdentity))
                _workflowSelectedModIdentities.Add(modIdentity);
        }

        private static void UpdateWorkflowSelectionAutoScroll(
            Rect listRect,
            IList<WorkflowModSummary> mods,
            float rowHeight,
            float maximumScroll,
            bool busy)
        {
            if (!_workflowSelectionGestureActive || busy ||
                Event.current.type != EventType.Repaint || !Input.GetMouseButton(0)) return;
            Vector2 mouse = Event.current.mousePosition;
            Rect selectionColumn = new Rect(
                listRect.x - WorkflowSelectionAutoScrollHorizontalTolerance,
                listRect.y,
                35f + WorkflowSelectionAutoScrollHorizontalTolerance * 2f,
                listRect.height);
            if (mouse.x < selectionColumn.x || mouse.x > selectionColumn.xMax) return;
            float overflow = mouse.y < listRect.y
                ? mouse.y - listRect.y
                : mouse.y > listRect.yMax ? mouse.y - listRect.yMax : 0f;
            float distance = Mathf.Abs(overflow);
            if (distance <= WorkflowSelectionAutoScrollStartDistance) return;
            float normalized = Mathf.Clamp01(
                (distance - WorkflowSelectionAutoScrollStartDistance) /
                Mathf.Max(1f, WorkflowSelectionAutoScrollFullSpeedDistance -
                               WorkflowSelectionAutoScrollStartDistance));
            float eased = normalized * normalized;
            float speed = Mathf.Lerp(
                WorkflowSelectionAutoScrollMinimumSpeed,
                WorkflowSelectionAutoScrollMaximumSpeed,
                eased);
            float direction = overflow < 0f ? -1f : 1f;
            float deltaTime = Mathf.Clamp(Time.unscaledDeltaTime, 1f / 240f, 0.05f);
            float previous = _workflowModScroll.y;
            _workflowModScroll.y = Mathf.Clamp(
                previous + direction * speed * deltaTime, 0f, maximumScroll);
            if (Mathf.Approximately(previous, _workflowModScroll.y)) return;
            int rowIndex = direction < 0f
                ? Mathf.FloorToInt(_workflowModScroll.y / rowHeight)
                : Mathf.FloorToInt(
                    (_workflowModScroll.y + listRect.height - 1f) / rowHeight);
            if (rowIndex < 0 || rowIndex >= (mods?.Count ?? 0)) return;
            WorkflowModSummary mod = mods[rowIndex];
            if (mod == null || mod.IsProtectedSystemPackage) return;
            ToggleWorkflowModSelectionOnce(mod.ModIdentity);
        }

        private static void EndWorkflowSelectionGesture()
        {
            _workflowSelectionGestureActive = false;
            _workflowSelectionGestureVisited.Clear();
        }

        private static void DrawWorkflowSelectionMark(
            Rect rect,
            bool selected,
            bool protectedSystemPackage,
            bool busy)
        {
            Text.Font = GameFont.Medium;
            Text.Anchor = TextAnchor.MiddleCenter;
            if (protectedSystemPackage)
            {
                GUI.color = new Color(0.48f, 0.50f, 0.52f);
                Widgets.Label(rect, "×");
            }
            else if (selected)
            {
                GUI.color = busy ? Color.grey : WorkflowUiStyle.GoodText;
                Widgets.Label(rect, "✓");
            }
            else
            {
                GUI.color = busy ? Color.grey : new Color(0.86f, 0.34f, 0.31f);
                Widgets.Label(rect, "×");
            }
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;
            Text.Font = GameFont.Small;
        }

        private static void DrawWorkflowEffectiveStateLines(Rect rect, WorkflowModSummary mod)
        {
            DrawWorkflowEffectiveStateLine(new Rect(rect.x, rect.y, rect.width, 18f), "XML", mod.XmlState);
            DrawWorkflowEffectiveStateLine(new Rect(rect.x, rect.y + 18f, rect.width, 18f), "DLL", mod.DllState);
        }

        private static void DrawWorkflowEffectiveStateLine(
            Rect rect, string prefix, WorkflowSourceStateSummary state)
        {
            state = state ?? new WorkflowSourceStateSummary();
            DrawWorkflowSegments(rect, new[]
            {
                Tuple.Create(prefix + WfText("：", ": "), Color.white),
                Tuple.Create(WfText("需 ", "need ") + state.NeedsTranslation,
                    state.NeedsTranslation > 0 ? WorkflowUiStyle.WarningText : WorkflowUiStyle.MutedText),
                Tuple.Create(WfText("｜待 ", " | pending ") + state.Undetermined,
                    state.Undetermined > 0 ? new Color(0.52f, 0.75f, 0.92f) : WorkflowUiStyle.MutedText),
                Tuple.Create(WfText("｜无需 ", " | no ") + state.NoTranslationNeeded,
                    WorkflowUiStyle.MutedText)
            });
        }

        private static void DrawWorkflowTranslationStateLines(Rect rect, WorkflowModSummary mod)
        {
            DrawWorkflowTranslationStateLine(new Rect(rect.x, rect.y, rect.width, 18f), "XML", mod.XmlState);
            DrawWorkflowTranslationStateLine(new Rect(rect.x, rect.y + 18f, rect.width, 18f), "DLL", mod.DllState);
        }

        private static void DrawWorkflowTranslationStateLine(
            Rect rect, string prefix, WorkflowSourceStateSummary state)
        {
            state = state ?? new WorkflowSourceStateSummary();
            DrawWorkflowSegments(rect, new[]
            {
                Tuple.Create(prefix + WfText("：", ": "), Color.white),
                Tuple.Create(WfText("译 ", "done ") + state.Translated,
                    state.Translated > 0 ? WorkflowUiStyle.GoodText : WorkflowUiStyle.MutedText),
                Tuple.Create(WfText("｜未 ", " | new ") + state.Untranslated,
                    state.Untranslated > 0 ? WorkflowUiStyle.WarningText : WorkflowUiStyle.MutedText),
                Tuple.Create(WfText("｜失败 ", " | failed ") + state.Failed,
                    state.Failed > 0 ? new Color(1f, 0.4f, 0.4f) : WorkflowUiStyle.MutedText)
            });
        }

        private static void DrawWorkflowAnalysisLine(
            Rect rect, string prefix, WorkflowAnalysisSourceSummary summary)
        {
            summary = summary ?? new WorkflowAnalysisSourceSummary();
            string text;
            if (summary.Freshness == AnalysisResultFreshness.NeverAnalyzed)
            {
                GUI.color = WorkflowUiStyle.MutedText;
                text = prefix + WfText("：未分析", ": not analyzed");
            }
            else if (summary.Freshness == AnalysisResultFreshness.Expired)
            {
                GUI.color = WorkflowUiStyle.WarningText;
                text = prefix + WfText("：已过期", ": expired");
                TooltipHandler.TipRegion(rect, BuildExpiredAnalysisTooltip(summary));
            }
            else if (summary.Freshness == AnalysisResultFreshness.Failed)
            {
                GUI.color = new Color(1f, 0.4f, 0.4f);
                text = prefix + WfText("：任务失败", ": failed");
                if (!string.IsNullOrWhiteSpace(summary.ErrorText))
                    TooltipHandler.TipRegion(rect, summary.ErrorText);
            }
            else
            {
                DrawWorkflowSegments(rect, new[]
                {
                    Tuple.Create("● ", WorkflowUiStyle.GoodText),
                    Tuple.Create(prefix + WfText("：", ": "), Color.white),
                    Tuple.Create(WfText("需 ", "need ") + summary.NeedsTranslation,
                        summary.NeedsTranslation > 0 ? WorkflowUiStyle.WarningText : WorkflowUiStyle.MutedText),
                    Tuple.Create(WfText("｜待 ", " | pending ") + summary.Undetermined,
                        summary.Undetermined > 0 ? new Color(0.52f, 0.75f, 0.92f) : WorkflowUiStyle.MutedText),
                    Tuple.Create(WfText("｜无需 ", " | no ") + summary.NoTranslationNeeded,
                        WorkflowUiStyle.MutedText)
                });
                return;
            }
            Widgets.Label(rect, text);
            GUI.color = Color.white;
        }

        private static void DrawWorkflowSegments(Rect rect, IEnumerable<Tuple<string, Color>> segments)
        {
            float x = rect.x;
            Color previous = GUI.color;
            foreach (Tuple<string, Color> segment in segments ?? Enumerable.Empty<Tuple<string, Color>>())
            {
                if (segment == null || string.IsNullOrEmpty(segment.Item1) || x >= rect.xMax) continue;
                float width = Text.CalcSize(segment.Item1).x + 1f;
                GUI.color = segment.Item2;
                Widgets.Label(new Rect(x, rect.y, Mathf.Min(width, rect.xMax - x), rect.height), segment.Item1);
                x += width;
            }
            GUI.color = previous;
        }

        private static string BuildExpiredAnalysisTooltip(WorkflowAnalysisSourceSummary summary)
        {
            List<string> reasons = new List<string>();
            if (!string.Equals(summary.AnalyzerVersion, summary.CurrentAnalyzerVersion, StringComparison.Ordinal))
                reasons.Add(WfText("分析器版本：", "Analyzer version: ") +
                            (summary.AnalyzerVersion ?? string.Empty) + " → " +
                            (summary.CurrentAnalyzerVersion ?? string.Empty));
            if (!string.Equals(
                    summary.ModVersionFingerprint,
                    summary.CurrentModVersionFingerprint,
                    StringComparison.Ordinal))
                reasons.Add(WfText("Mod 内容指纹已变化", "Mod content fingerprint changed"));
            return reasons.Count > 0
                ? string.Join("\n", reasons)
                : WfText("历史分析结果已不匹配当前输入。", "Historical analysis no longer matches current inputs.");
        }

        private void DrawWorkflowInspector(Rect rect, WorkflowTaskSnapshot task, bool anyTask)
        {
            Widgets.DrawBoxSolid(rect, WorkflowUiStyle.RaisedPanel);
            WorkflowUiStyle.DrawBorder(rect, new Color(0.28f, 0.31f, 0.33f));
            if (!anyTask && _workflowInspectorMode == WorkflowInspectorMode.Collapsed)
            {
                Text.Font = GameFont.Tiny;
                Widgets.Label(new Rect(rect.x + 7f, rect.y + 3f, rect.width - 110f, 19f),
                    WfText("当前无任务", "No active task"));
                if (WorkflowUiStyle.Button(new Rect(rect.xMax - 101f, rect.y + 2f, 95f, 21f),
                        WfText("任务／日志 ▸", "Task / logs ▸"), WorkflowButtonStyle.Link, true, GameFont.Tiny))
                    _workflowInspectorMode = WorkflowInspectorMode.TaskDetail;
                Text.Font = GameFont.Small;
                return;
            }
            if (!anyTask)
            {
                DrawWorkflowInspectorOutput(rect.ContractedBy(6f), task, true);
                return;
            }

            bool expanded = _workflowInspectorMode != WorkflowInspectorMode.Collapsed;
            float leftWidth = expanded ? rect.width * 0.4f : rect.width;
            Rect left = new Rect(rect.x + 6f, rect.y + 5f, leftWidth - 12f, rect.height - 10f);
            Rect right = new Rect(rect.x + leftWidth, rect.y, rect.width - leftWidth, rect.height);
            if (task.IsBusy && task.Kind == WorkflowTaskKind.HistoricalDataMigration)
            {
                Widgets.Label(left, WfText("迁移历史数据（不预扫总数）", "Historical migration (no pre-count)") +
                    "\n" + task.CurrentMod + "\n" + task.Detail);
                if (expanded) DrawWorkflowInspectorOutput(right.ContractedBy(6f), task, false);
                return;
            }
            string name = task.IsBusy ? task.DisplayName : Settings.CurrentTaskName;
            float progress = task.IsBusy && task.TotalUnits > 0
                ? task.Progress
                : Mathf.Clamp01(Settings.CurrentProgress);
            string current = task.IsBusy ? task.CurrentMod : Settings.SubTaskName;
            string detail = task.IsBusy ? task.Detail : AutoTranslatorSettings.AgentBatchProgressText;
            string progressText;
            if (task.IsBusy && task.TotalStages > 1)
            {
                progressText = WfText("总进度 ", "Overall ") + $"{progress * 100f:F0}%" +
                    WfText("　·　阶段 ", "  ·  Stage ") +
                    Math.Max(1, task.CurrentStageIndex) + "/" + task.TotalStages +
                    (string.IsNullOrWhiteSpace(task.CurrentStageName)
                        ? string.Empty
                        : "：" + task.CurrentStageName);
                if (task.TotalItems > 0)
                    progressText += task.IsConcurrentStage
                        ? WfText("　·　本阶段已处理 Mod ", "  ·  Processed mods ") +
                          task.CurrentItemIndex + " / " + task.TotalItems
                        : WfText("　·　本阶段 Mod ", "  ·  Stage mod ") +
                          Math.Max(1, task.CurrentItemIndex) + " / " + task.TotalItems;
            }
            else
            {
                progressText = WfText("进度 ", "Progress ") + $"{progress * 100f:F0}%";
                if (task.IsBusy && task.TotalItems > 0)
                    progressText += task.IsConcurrentStage
                        ? WfText("　·　已处理 Mod ", "  ·  Processed mods ") +
                          task.CurrentItemIndex + " / " + task.TotalItems
                        : WfText("　·　Mod ", "  ·  Mod ") +
                          Math.Max(1, task.CurrentItemIndex) + " / " + task.TotalItems;
            }

            Text.Font = GameFont.Tiny;
            Widgets.Label(new Rect(left.x, left.y, left.width - 82f, 18f),
                WfText("后台任务：", "Background task: ") + name +
                (string.IsNullOrWhiteSpace(current) ? string.Empty : "　·　" + current));
            Rect expandButton = new Rect(left.xMax - 75f, left.y - 2f, 75f, 21f);
            if (WorkflowUiStyle.Button(expandButton,
                    expanded ? WfText("收起", "Collapse") : WfText("展开", "Expand"),
                    WorkflowButtonStyle.Link, true, GameFont.Tiny))
                _workflowInspectorMode = expanded
                    ? WorkflowInspectorMode.Collapsed
                    : WorkflowInspectorMode.TaskDetail;
            Rect bar = new Rect(left.x, left.y + 21f, left.width, 24f);
            Widgets.FillableBar(bar, progress);
            Widgets.DrawBox(bar, 1);
            DrawOutlinedProgressLabel(bar.ContractedBy(2f), progressText, GameFont.Tiny);
            Rect subBar = new Rect(left.x, left.y + 50f, left.width, 20f);
            bool hasSubProgress = task.IsBusy &&
                                  (task.SubTotalUnits > 0 || task.SubProgressRatio >= 0d);
            float subProgress = hasSubProgress ? task.SubProgress : 0f;
            if (hasSubProgress)
            {
                Widgets.FillableBar(subBar, subProgress);
                Widgets.DrawBox(subBar, 1);
                string subProgressText = WfText(
                    task.IsConcurrentStage ? "本阶段综合进度：" : "当前 Mod 进度：",
                    task.IsConcurrentStage ? "Stage aggregate progress: " : "Current mod progress: ");
                if (task.SubTotalUnits > 0)
                    subProgressText += task.SubCompletedUnits.ToString("N0") + " / " +
                                       task.SubTotalUnits.ToString("N0") + "  ·  ";
                subProgressText += (subProgress * 100f).ToString("F0") + "%";
                DrawOutlinedProgressLabel(
                    subBar.ContractedBy(1f), subProgressText, GameFont.Tiny);
            }
            else
            {
                GUI.color = Color.grey;
                Widgets.Label(subBar, detail ?? string.Empty);
                GUI.color = Color.white;
            }
            if (hasSubProgress)
            {
                GUI.color = WorkflowUiStyle.MutedText;
                Widgets.Label(new Rect(left.x, left.y + 73f, left.width, 18f), detail ?? string.Empty);
                GUI.color = Color.white;
            }
            GUI.color = Color.grey;
            string elapsed = task.IsBusy && task.StartedUtc != default(DateTime)
                ? WfText("已用时 ", "Elapsed ") +
                  (DateTime.UtcNow - task.StartedUtc).ToString(@"hh\:mm\:ss") +
                  (task.LastUpdatedUtc == default(DateTime)
                      ? string.Empty
                      : WfText("　·　最近更新 ", "  ·  Last update ") +
                        FormatWorkflowAge(DateTime.UtcNow - task.LastUpdatedUtc))
                : string.Empty;
            Widgets.Label(new Rect(left.x, left.y + 93f, left.width, 16f), elapsed);
            GUI.color = Color.white;
            Text.Font = GameFont.Small;

            if (expanded)
            {
                Widgets.DrawLineVertical(right.x, right.y, right.height);
                DrawWorkflowInspectorOutput(right.ContractedBy(6f), task, true);
            }
        }

        private void DrawWorkflowInspectorOutput(Rect rect, WorkflowTaskSnapshot task, bool showCollapse)
        {
            Rect header = new Rect(rect.x, rect.y, rect.width, 22f);
            Text.Font = GameFont.Tiny;
            float tabWidth = 72f;
            DrawInspectorTab(new Rect(header.x, header.y, tabWidth, 21f),
                WfText("任务详情", "Task"), WorkflowInspectorMode.TaskDetail);
            DrawInspectorTab(new Rect(header.x + tabWidth + 3f, header.y, tabWidth, 21f),
                WfText("运行日志", "Runtime"), WorkflowInspectorMode.RuntimeLog);
            DrawInspectorTab(new Rect(header.x + (tabWidth + 3f) * 2f, header.y, tabWidth, 21f),
                WfText("警告日志", "Warnings"), WorkflowInspectorMode.WarningLog);
            DrawInspectorTab(new Rect(header.x + (tabWidth + 3f) * 3f, header.y, tabWidth, 21f),
                WfText("错误日志", "Errors"), WorkflowInspectorMode.ErrorLog);
            float collapseX = header.xMax - 62f;
            if (_workflowInspectorMode == WorkflowInspectorMode.RuntimeLog ||
                _workflowInspectorMode == WorkflowInspectorMode.WarningLog ||
                _workflowInspectorMode == WorkflowInspectorMode.ErrorLog)
            {
                Rect openFile = new Rect(collapseX - 96f, header.y, 92f, 21f);
                if (WorkflowUiStyle.Button(openFile, WfText("打开日志文件", "Open log file"),
                        WorkflowButtonStyle.Link, true, GameFont.Tiny)) OpenWorkflowLogFile();
                Rect copyButton = new Rect(openFile.x - 55f, header.y, 51f, 21f);
                DrawWorkflowCopyButton(
                    copyButton, task);
                bool hasCurrentLog;
                lock (AutoTranslatorSettings.logLock)
                    hasCurrentLog = GetCurrentWorkflowLogList().Count > 0;
                Rect clearButton = new Rect(copyButton.x - 55f, header.y, 51f, 21f);
                if (WorkflowUiStyle.Button(clearButton, WfText("清空", "Clear"),
                        WorkflowButtonStyle.Link, hasCurrentLog, GameFont.Tiny))
                    AutoTranslatorSettings.ClearCurrentDisplayedLog(
                        _workflowInspectorMode == WorkflowInspectorMode.ErrorLog,
                        _workflowInspectorMode == WorkflowInspectorMode.WarningLog);
                TooltipHandler.TipRegion(clearButton,
                    WfText("仅清空当前界面显示，不删除日志文件。",
                        "Clear only the current on-screen list; the log file is not deleted."));
            }
            else
                DrawWorkflowCopyButton(new Rect(collapseX - 55f, header.y, 51f, 21f), task);
            if (showCollapse && WorkflowUiStyle.Button(
                    new Rect(collapseX, header.y, 62f, 21f), WfText("收起", "Collapse"),
                    WorkflowButtonStyle.Link, true, GameFont.Tiny))
                _workflowInspectorMode = WorkflowInspectorMode.Collapsed;
            Rect body = new Rect(rect.x, header.yMax + 2f, rect.width, rect.yMax - header.yMax - 2f);
            if (_workflowInspectorMode == WorkflowInspectorMode.RuntimeLog)
            {
                DrawLogView(body, AutoTranslatorSettings.RuntimeLogs,
                    ref AutoTranslatorSettings.logScrollPos, _runtimeLogViewCache, false, false);
            }
            else if (_workflowInspectorMode == WorkflowInspectorMode.WarningLog)
            {
                DrawLogView(body, AutoTranslatorSettings.WarningLogs,
                    ref AutoTranslatorSettings.warningScrollPos, _warningLogViewCache, false, true);
            }
            else if (_workflowInspectorMode == WorkflowInspectorMode.ErrorLog)
            {
                DrawLogView(body, AutoTranslatorSettings.ErrorLogs,
                    ref AutoTranslatorSettings.errorScrollPos, _errorLogViewCache, true, false);
            }
            else
            {
                DrawWorkflowTaskDetail(body, task);
            }
            Text.Font = GameFont.Small;
        }

        private static void DrawInspectorTab(Rect rect, string label, WorkflowInspectorMode mode)
        {
            if (WorkflowUiStyle.Button(rect, label,
                    _workflowInspectorMode == mode ? WorkflowButtonStyle.ActiveTab : WorkflowButtonStyle.Quiet,
                    true, GameFont.Tiny)) _workflowInspectorMode = mode;
        }

        private static void DrawWorkflowTaskDetail(Rect body, WorkflowTaskSnapshot task)
        {
            if (task == null || !task.IsBusy)
            {
                Widgets.Label(body, BuildWorkflowTaskDetailText(task));
                return;
            }

            string detailText = BuildWorkflowTaskDetailText(task);
            int detailLineStart = -1;
            for (int line = 0, searchFrom = 0; line < 4; line++)
            {
                int separator = detailText.IndexOf('\n', searchFrom);
                if (separator < 0) break;
                detailLineStart = separator;
                searchFrom = separator + 1;
            }
            string summary = detailLineStart >= 0 ? detailText.Substring(0, detailLineStart) : detailText;
            string latest = detailLineStart >= 0 ? detailText.Substring(detailLineStart + 1) : string.Empty;
            float summaryHeight = Text.LineHeight * 4f;
            Widgets.Label(new Rect(body.x, body.y, body.width, summaryHeight), summary);
            // Keep the volatile latest-update field at the bottom and reserve three
            // lines so wrapping or a temporarily empty detail never moves the summary.
            Widgets.Label(new Rect(body.x, body.y + summaryHeight, body.width,
                Math.Min(Text.LineHeight * 3f, Math.Max(0f, body.height - summaryHeight))), latest);
        }

        private static string BuildWorkflowTaskDetailText(WorkflowTaskSnapshot task)
        {
            if (task == null || !task.IsBusy)
            {
                WorkflowTaskSnapshot last = WorkflowTaskCoordinator.Instance.LastCompleted;
                if (last == null || last.RunId == Guid.Empty)
                    return WfText("当前无任务，尚无最近任务记录。", "No active or recent task.");
                bool usesPerRunResult = last.Kind == WorkflowTaskKind.AiReview ||
                                        last.Kind == WorkflowTaskKind.AiTranslation ||
                                        last.Kind == WorkflowTaskKind.OneClickTranslation;
                string perRunDetail = last.State == WorkflowRunState.Failed &&
                                      !string.IsNullOrWhiteSpace(last.ErrorText)
                    ? last.ErrorText
                    : last.Detail;
                bool isGlobalReload = last.Kind == WorkflowTaskKind.RuntimeTranslationReload;
                string resultLineZh = isGlobalReload
                    ? "范围：全部已保存译文　最终步骤：" + perRunDetail
                    : usesPerRunResult
                    ? "本次结果：" + perRunDetail
                    : $"范围：{last.TotalItems} 个 Mod　最终步骤：{last.Detail}";
                string resultLineEn = isGlobalReload
                    ? "Scope: all saved translations  Final step: " + perRunDetail
                    : usesPerRunResult
                    ? "This run: " + perRunDetail
                    : $"Scope: {last.TotalItems} mods  Final step: {last.Detail}";
                bool appendSeparateError = !usesPerRunResult &&
                                           !string.IsNullOrWhiteSpace(last.ErrorText);
                return WfText(
                    $"当前无任务\n最近任务：{last.DisplayName}\n终态：{last.State}　完成：{last.CompletedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}\n{resultLineZh}" +
                    (appendSeparateError ? "\n错误：" + last.ErrorText : string.Empty),
                    $"No active task\nLast: {last.DisplayName}\nState: {last.State}  Completed: {last.CompletedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}\n{resultLineEn}" +
                    (appendSeparateError ? "\nError: " + last.ErrorText : string.Empty));
            }
            string state = task.IsCancellationRequested
                ? WfText("正在停止", "Stopping")
                : WfText("运行中", "Running");
            if (task.Kind == WorkflowTaskKind.HistoricalDataMigration)
                return WfText("状态：", "State: ") + state + "\n" + task.CurrentMod + "\n" + task.Detail;
            string item = task.TotalItems > 0
                ? (task.IsConcurrentStage
                    ? task.CurrentItemIndex
                    : Math.Max(1, task.CurrentItemIndex)) + "/" + task.TotalItems
                : WfText("未提供", "Unavailable");
            string subProgress = task.SubTotalUnits > 0 || task.SubProgressRatio >= 0d
                ? (task.SubProgress * 100f).ToString("F0") + "%"
                : WfText("未提供", "Unavailable");
            if (task.Kind == WorkflowTaskKind.RuntimeTranslationReload)
            {
                string entryProgress = task.TotalUnits > 0
                    ? task.CompletedUnits + "/" + task.TotalUnits
                    : WfText("正在准备", "Preparing");
                return WfText(
                    $"状态：{state}\n任务范围：全部已保存译文　当前：{entryProgress}\n当前阶段：重建输出并加载运行时译文\n总体进度：{task.Progress * 100f:F0}%　当前步骤进度：{subProgress}\n当前步骤：{task.Detail}",
                    $"State: {state}\nScope: all saved translations  Current: {entryProgress}\nStage: rebuild outputs and reload runtime translations\nOverall: {task.Progress * 100f:F0}%  Current step: {subProgress}\nCurrent step: {task.Detail}");
            }
            if (task.IsConcurrentStage)
            {
                return WfText(
                    $"状态：{state}\n任务范围：{task.TotalItems} 个 Mod　已完成：{item}\n并发状态：{task.CurrentMod}\n总体进度：{task.Progress * 100f:F0}%　本阶段综合进度：{subProgress}\n最近更新：{task.Detail}",
                    $"State: {state}\nScope: {task.TotalItems} mods  Completed: {item}\nConcurrency: {task.CurrentMod}\nOverall: {task.Progress * 100f:F0}%  Stage aggregate: {subProgress}\nLatest update: {task.Detail}");
            }
            return WfText(
                $"状态：{state}\n任务范围：{task.TotalItems} 个 Mod　当前：{item}\n当前 Mod：{task.CurrentMod}\n总体进度：{task.Progress * 100f:F0}%　当前步骤进度：{subProgress}\n当前步骤：{task.Detail}",
                $"State: {state}\nScope: {task.TotalItems} mods  Current: {item}\nCurrent mod: {task.CurrentMod}\nOverall: {task.Progress * 100f:F0}%  Current step: {subProgress}\nCurrent step: {task.Detail}");
        }

        private static string GetWorkflowInspectorCopyText(WorkflowTaskSnapshot task)
        {
            if (_workflowInspectorMode == WorkflowInspectorMode.TaskDetail)
                return BuildWorkflowTaskDetailText(task);
            lock (AutoTranslatorSettings.logLock)
            {
                IEnumerable<string> lines = GetCurrentWorkflowLogList();
                return string.Join("\n", lines.ToList());
            }
        }

        private static void DrawWorkflowCopyButton(Rect rect, WorkflowTaskSnapshot task)
        {
            bool hasContent;
            if (_workflowInspectorMode == WorkflowInspectorMode.TaskDetail)
                hasContent = !string.IsNullOrWhiteSpace(BuildWorkflowTaskDetailText(task));
            else
            {
                lock (AutoTranslatorSettings.logLock)
                    hasContent = GetCurrentWorkflowLogList().Count > 0;
            }
            if (!WorkflowUiStyle.Button(
                    rect, WfText("复制", "Copy"), WorkflowButtonStyle.Link,
                    hasContent, GameFont.Tiny)) return;
            string text = GetWorkflowInspectorCopyText(task);
            GUIUtility.systemCopyBuffer = text;
            Messages.Message(
                WfText("已复制到剪贴板", "Copied to clipboard"),
                MessageTypeDefOf.NeutralEvent,
                false);
        }

        private static string FormatWorkflowAge(TimeSpan age)
        {
            if (age.TotalSeconds < 2d) return WfText("刚刚", "just now");
            if (age.TotalMinutes < 1d) return Math.Max(2, (int)age.TotalSeconds) + WfText(" 秒前", "s ago");
            return Math.Max(1, (int)age.TotalMinutes) + WfText(" 分钟前", "m ago");
        }

        private void DrawWorkflowOptions(Rect rect, bool busy)
        {
            const float gap = 6f;
            float width = (rect.width - gap * 6f) / 7f;
            Text.Font = GameFont.Tiny;
            bool force = _workflowForceAnalysis;
            string forceLabel = WfText("强制分析", "Force");
            float forceWidth = Mathf.Min(width * 0.58f, Text.CalcSize(forceLabel).x + 30f);
            Rect forceRect = new Rect(rect.x, rect.y + 3f, forceWidth, rect.height - 3f);
            Widgets.CheckboxLabeled(forceRect, forceLabel, ref force);
            if (!busy) _workflowForceAnalysis = force;

            bool dllEnabled = _workflowUiConfiguration?.EnableDllAnalysis ?? false;
            Rect dllRect = new Rect(forceRect.xMax + 4f, rect.y,
                rect.x + width - forceRect.xMax - 4f, rect.height - 2f);
            if (WorkflowUiStyle.Button(dllRect,
                    dllEnabled
                        ? WfText("✓ DLL 分析", "✓ DLL analysis")
                        : WfText("○ DLL 分析", "○ DLL analysis"),
                    dllEnabled ? WorkflowButtonStyle.Primary : WorkflowButtonStyle.Quiet,
                    !busy, GameFont.Tiny))
                SaveWorkflowConfiguration(config => config.EnableDllAnalysis = !dllEnabled);
            TooltipHandler.TipRegion(dllRect, dllEnabled
                ? WfText("DLL 分析已开启；点击关闭。", "DLL analysis is enabled; click to disable.")
                : WfText("DLL 分析未开启；点击开启。", "DLL analysis is disabled; click to enable."));

            DryRunReport report = _workflowUiSnapshot?.LatestDryRun;
            Rect dryRunRect = new Rect(rect.x + width + gap, rect.y, width, rect.height - 2f);
            if (report != null && WorkflowUiStyle.Button(
                    dryRunRect,
                    WfText("查看上次：", "View last: ") + report.ProtectedBudgetTokens.ToString("N0") + " Token",
                    WorkflowButtonStyle.Quiet, true, GameFont.Tiny))
                Find.WindowStack.Add(new Window_WorkflowDryRunReport(report, _workflowUiSnapshot));
            if (report != null)
                TooltipHandler.TipRegion(dryRunRect,
                    WfText("点击查看上次试跑详情。", "View the latest dry-run details."));

            string scope = GetAiReviewScopeLabel(_workflowUiConfiguration?.AiReviewScope);
            Rect scopeRect = new Rect(rect.x + (width + gap) * 2f, rect.y, width, rect.height - 2f);
            if (WorkflowUiStyle.Button(scopeRect,
                    WfText("复核范围：", "Scope: ") + scope + "  ▾",
                    WorkflowButtonStyle.Dropdown, !busy, GameFont.Tiny))
                OpenAiReviewScopeMenu();
            Text.Font = GameFont.Small;
        }

        private void DrawWorkflowActions(Rect rect, bool busy)
        {
            const float gap = 6f;
            float width = (rect.width - gap * 6f) / 7f;
            List<ModMetaData> selectedMods = GetSelectedWorkflowMods();
            List<string> selectedModIdentities = selectedMods
                .Select(ModAnalysisTargetFactory.CreateModIdentity)
                .Where(identity => !string.IsNullOrWhiteSpace(identity))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            bool canRun = !busy && selectedMods.Count > 0;
            string[] labels =
            {
                WfText("分析", "Analyze"),
                WfText($"试跑（{selectedMods.Count}）", $"Dry run ({selectedMods.Count})"),
                WfText("AI 复核", "AI review"),
                WfText("AI 翻译", "AI translate"),
                WfText("一键翻译", "One-click"),
                WfText("清除结果…", "Clear results..."),
                WfText("停止", "Stop")
            };
            string[] tooltips =
            {
                WfText("补齐所选 Mod 的本地 XML／DLL 分析结果。", "Complete local XML/DLL analysis for selected mods."),
                WfText("只估算所选 Mod 的 Token，不调用模型。", "Estimate tokens without calling the model."),
                WfText("使用 AI 复核所选 Mod 的条目分类。", "Use AI to review classifications for selected mods."),
                WfText("翻译当前需要翻译且尚无有效译文的条目；首次缺少译文状态同步时会自动补一次。", "Translate needed entries without a valid translation; the initial translation-state sync runs automatically when required."),
                WfText("依次执行分析、AI 复核和 AI 翻译；首次缺少译文状态同步时会在翻译前自动补一次。", "Run analysis, AI review, and AI translation in sequence; the initial translation-state sync runs automatically before translation when required."),
                WfText("清除所选 Mod 的分类或翻译结果。", "Clear classification or translation results for selected mods."),
                WfText("请求停止当前后台任务。", "Request cancellation of the current background task.")
            };
            Text.Font = GameFont.Tiny;
            for (int i = 0; i < 7; i++)
            {
                Rect button = new Rect(rect.x + i * (width + gap), rect.y, width, rect.height);
                bool enabled = i == 6 ? busy : canRun;
                WorkflowButtonStyle style = i == 4
                    ? WorkflowButtonStyle.Primary
                    : i == 6 ? WorkflowButtonStyle.Stop : WorkflowButtonStyle.Quiet;
                bool clicked = WorkflowUiStyle.Button(button, labels[i], style, enabled, GameFont.Tiny);
                TooltipHandler.TipRegion(button, tooltips[i]);
                if (!clicked || !enabled) continue;
                switch (i)
                {
                    case 0:
                        StartWorkflowOperation(
                            backend => backend.RunAnalysisButtonAsync(selectedMods, _workflowForceAnalysis),
                            WfText("本地分析", "Local analysis"));
                        break;
                    case 1: StartWorkflowDryRun(selectedModIdentities); break;
                    case 2:
                        StartWorkflowOperation(
                            backend => backend.RunAiReviewAsync(selectedMods),
                            WfText("AI 复核", "AI review"));
                        break;
                    case 3:
                        StartWorkflowOperation(
                            backend => backend.RunAiTranslationAsync(selectedMods),
                            WfText("AI 翻译", "AI translation"));
                        break;
                    case 4:
                        OpenOneClickTranslationConfirmation(
                            selectedMods,
                            _workflowForceAnalysis,
                            _workflowUiConfiguration?.EnableDllAnalysis ?? false,
                            _workflowUiConfiguration?.AiReviewScope);
                        break;
                    case 5: OpenWorkflowResultCleanup(selectedModIdentities); break;
                    case 6: RequestWorkflowStop(); break;
                }
            }
            Text.Font = GameFont.Small;
        }

        private static void OpenOneClickTranslationConfirmation(
            IEnumerable<ModMetaData> selectedMods,
            bool forceAnalysis,
            bool dllAnalysisEnabled,
            AiReviewScope reviewScope)
        {
            List<ModMetaData> frozenSelection = (selectedMods ?? Enumerable.Empty<ModMetaData>())
                .Where(mod => mod != null)
                .ToList();
            if (frozenSelection.Count == 0) return;

            AiReviewScope frozenReviewScope = new AiReviewScope
            {
                IncludeNeedsTranslation = reviewScope?.IncludeNeedsTranslation ?? false,
                IncludeUndetermined = reviewScope?.IncludeUndetermined ?? true,
                IncludeNoTranslationNeeded = reviewScope?.IncludeNoTranslationNeeded ?? false
            };
            string reviewScopeLabel = GetAiReviewScopeLabel(frozenReviewScope);
            string analysisTarget = dllAnalysisEnabled ? "XML／DLL" : "XML";
            string analysisTargetEnglish = dllAnalysisEnabled ? "XML/DLL" : "XML";
            string analysisDescription = forceAnalysis
                ? "强制重新生成所选 Mod 的" + analysisTarget + "本地分析结果。"
                : "复用并跳过已有有效的" + analysisTarget + "分析结果，只补齐缺失或已失效的结果。";
            string analysisDescriptionEnglish = forceAnalysis
                ? "force regeneration of local " + analysisTargetEnglish + " analysis results for the selected mods."
                : "reuse and skip valid existing " + analysisTargetEnglish +
                  " analysis results, processing only missing or stale results.";
            WorkflowExecutionOptions frozenOptions = new WorkflowExecutionOptions
            {
                TemporaryAiReviewScope = frozenReviewScope
            };
            string forceWarning = forceAnalysis
                ? "⚠ 当前已开启【强制分析】。已有有效分析结果也会重新生成，处理时间可能明显增加。\n\n"
                : string.Empty;
            string forceWarningEnglish = forceAnalysis
                ? "WARNING: Force analysis is enabled. Valid existing analysis results will also be regenerated, " +
                  "which may significantly increase processing time.\n\n"
                : string.Empty;

            string message = WfText(
                forceWarning +
                "一键翻译将对当前选择的 " + frozenSelection.Count + " 个 Mod 自动依次执行：\n\n" +
                "1. 分析：" + analysisDescription + "\n" +
                "2. AI 复核：按照当前复核范围（" + reviewScopeLabel + "）判断条目是否需要翻译；已有 AI 复核结果的条目会跳过。\n" +
                "3. AI 翻译：翻译需要翻译且尚无有效译文的条目。\n\n" +
                "若当前语言尚未完成首次译文状态同步，进入 AI 翻译前会自动同步一次；已有同步记录时不会重复执行。\n\n" +
                "处理时间取决于 Mod 数量、条目数量和网络状况，可能需要较长时间。" +
                "AI 复核和 AI 翻译会使用已启用的模型接口，并可能产生 Token 用量。\n\n" +
                "任务开始后可以点击“停止”请求终止；已经完成并保存的结果会保留。\n\n" +
                "是否开始一键翻译？",
                forceWarningEnglish +
                "One-click translation will run these steps in sequence for the " +
                frozenSelection.Count + " selected mods:\n\n" +
                "1. Analysis: " + analysisDescriptionEnglish + "\n" +
                "2. AI review: decide whether entries in the current review scope (" +
                reviewScopeLabel + ") need translation; entries with saved AI review results are skipped.\n" +
                "3. AI translation: translate needed entries that have no valid translation.\n\n" +
                "If the current language has never completed translation-state synchronization, it will run automatically once before AI translation; an existing synchronization record is reused.\n\n" +
                "Processing time depends on the number of mods and entries and on network conditions, " +
                "and may take a while. AI review and AI translation use enabled model providers and may consume tokens.\n\n" +
                "After the task starts, you can request cancellation with Stop; completed and saved results will be kept.\n\n" +
                "Start one-click translation?");

            Find.WindowStack.Add(new Window_AtcDialog(
                message,
                WfText("开始", "Start"),
                () =>
                {
                    if (WorkflowTaskCoordinator.Instance.IsBusy ||
                        AutoTranslatorSettings.LegacyPipelineIsRunning)
                    {
                        Messages.Message(WfText(
                                "当前已有后台任务，请等待任务结束后再试。",
                                "A background task is already running. Try again after it finishes."),
                            MessageTypeDefOf.NeutralEvent, false);
                        return;
                    }

                    StartWorkflowOperation(
                        backend => backend.RunOneClickTranslationAsync(
                            frozenSelection, forceAnalysis, frozenOptions),
                        WfText("一键翻译", "One-click translation"));
                },
                WfText("取消", "Cancel"),
                null,
                forceAnalysis
                    ? WfText("确认一键翻译（强制分析已开启）",
                        "Confirm one-click translation (force analysis enabled)")
                    : WfText("确认一键翻译", "Confirm one-click translation"),
                false,
                true,
                new Vector2(760f, 440f)));
        }

        private static void OpenWorkflowResultCleanup(ICollection<string> selectedModIdentities)
        {
            List<string> frozenSelection = (selectedModIdentities ?? Array.Empty<string>())
                .Where(identity => !string.IsNullOrWhiteSpace(identity))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (frozenSelection.Count == 0) return;
            Find.WindowStack.Add(new Window_WorkflowResultCleanup(
                frozenSelection,
                options => StartWorkflowOperation(
                    async backend =>
                    {
                        WorkflowResultCleanupSummary result = await backend.ClearModResultsAsync(
                            frozenSelection, options);
                        ATC_Dispatcher.RunOnMainThread(() => Messages.Message(
                            WfText(
                                "清除完成：AI 复核 " + result.AiReviewCandidateCount.ToString("N0") +
                                " 条，手动分类 " + result.ManualClassificationCandidateCount.ToString("N0") +
                                " 条，AI 翻译结果 " + result.LocalAiTranslationCount.ToString("N0") + " 条。",
                                "Cleanup complete: AI review " + result.AiReviewCandidateCount.ToString("N0") +
                                ", manual classifications " + result.ManualClassificationCandidateCount.ToString("N0") +
                                ", AI translation results " + result.LocalAiTranslationCount.ToString("N0") + "."),
                            MessageTypeDefOf.PositiveEvent,
                            false));
                    },
                    WfText("清除所选 Mod 的结果", "Clear selected mod results"))));
        }

        private static float[] GetWorkflowColumnWidths(float width)
        {
            return new[]
            {
                width * 0.25f, width * 0.22f, width * 0.18f,
                width * 0.17f, width * 0.12f, width * 0.06f
            };
        }

        private static long GetDryRunProtectedBudget(DryRunReport report)
        {
            if (report == null) return 0L;
            long review = report.AiReview?.TotalTokens ?? 0L;
            long translation = report.AiTranslation?.TotalTokens ?? 0L;
            return (long)Math.Ceiling((review + translation) * DryRunReport.BudgetProtectionRatio);
        }

        private static List<WorkflowModSummary> GetFilteredWorkflowMods()
        {
            IEnumerable<WorkflowModSummary> query = _workflowUiSnapshot?.Mods ??
                                                     Enumerable.Empty<WorkflowModSummary>();
            query = query.Where(mod => mod.IsActive);
            if (!string.IsNullOrWhiteSpace(_workflowModSearch))
            {
                string search = _workflowModSearch.Trim();
                query = query.Where(mod =>
                    (mod.DisplayName ?? string.Empty).IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (mod.PackageId ?? string.Empty).IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0);
            }
            List<WorkflowModSummary> protectedMods = _workflowModFilter == WorkflowModFilter.All
                ? query.Where(mod => mod.IsProtectedSystemPackage)
                    .OrderBy(mod => mod.ProtectedSystemPackageOrder)
                    .ToList()
                : new List<WorkflowModSummary>();
            query = query.Where(mod => !mod.IsProtectedSystemPackage);
            switch (_workflowModFilter)
            {
                case WorkflowModFilter.NeedsTranslation: query = query.Where(mod => mod.NeedsTranslation > 0); break;
                case WorkflowModFilter.Undetermined: query = query.Where(mod => mod.Undetermined > 0); break;
                case WorkflowModFilter.Untranslated: query = query.Where(mod => mod.Untranslated > 0); break;
                case WorkflowModFilter.Failed:
                    query = query.Where(mod => mod.Failed > 0 ||
                        mod.XmlAnalysis?.Freshness == AnalysisResultFreshness.Failed ||
                        mod.DllAnalysis?.Freshness == AnalysisResultFreshness.Failed);
                    break;
            }
            if (_workflowModSortField == WorkflowModSortField.LatestDryRun)
            {
                query = _workflowModSortAscending
                    ? query.OrderBy(mod => GetPerModDryRun(mod) == null ? 1 : 0)
                        .ThenBy(mod => GetDryRunProtectedBudget(GetPerModDryRun(mod)))
                        .ThenBy(mod => mod.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                    : query.OrderBy(mod => GetPerModDryRun(mod) == null ? 1 : 0)
                        .ThenByDescending(mod => GetDryRunProtectedBudget(GetPerModDryRun(mod)))
                        .ThenBy(mod => mod.DisplayName, StringComparer.CurrentCultureIgnoreCase);
            }
            else
            {
                query = _workflowModSortAscending
                    ? query.OrderBy(mod => mod.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                    : query.OrderByDescending(mod => mod.DisplayName, StringComparer.CurrentCultureIgnoreCase);
            }
            List<WorkflowModSummary> ordinary = query.ToList();
            ordinary.AddRange(protectedMods);
            return ordinary;
        }

        private static DryRunReport GetPerModDryRun(WorkflowModSummary mod)
        {
            if (mod == null) return null;
            Dictionary<string, DryRunReport> reports = _workflowUiSnapshot?.LatestDryRun?.ModReports;
            if (reports == null) return null;
            return reports.TryGetValue(mod.ModIdentity, out DryRunReport report) ? report : null;
        }

        private static void OpenWorkflowEditorForMod(string modIdentity)
        {
            if (!_workflowModsByIdentity.TryGetValue(modIdentity ?? string.Empty, out ModMetaData mod)) return;
            _workflowEditorCandidateSearch = string.Empty;
            _workflowEditorClassificationFilter = WorkflowEditorClassificationFilter.All;
            _workflowEditorTranslationFilter = WorkflowEditorTranslationFilter.All;
            SelectWorkflowEditorMod(mod, modIdentity);
            AutoTranslatorSettings.ActiveTab = AutoTranslatorSettings.EditorTabIndex;
        }

        private static List<string> GetCurrentWorkflowLogList()
        {
            if (_workflowInspectorMode == WorkflowInspectorMode.ErrorLog)
                return AutoTranslatorSettings.ErrorLogs;
            if (_workflowInspectorMode == WorkflowInspectorMode.WarningLog)
                return AutoTranslatorSettings.WarningLogs;
            return AutoTranslatorSettings.RuntimeLogs;
        }

        private static void OpenPerModDryRunReport(WorkflowModSummary mod, DryRunReport perMod)
        {
            if (mod == null || perMod == null) return;
            Find.WindowStack.Add(new Window_WorkflowModDryRunReport(mod.DisplayName, perMod));
        }

        private static List<ModMetaData> GetSelectedWorkflowMods()
        {
            return _workflowSelectedModIdentities
                .Select(identity => _workflowModsByIdentity.TryGetValue(identity, out ModMetaData mod) ? mod : null)
                .Where(mod => mod != null && mod.Active && WorkflowStepSelection.IsTranslationTarget(mod))
                .ToList();
        }

        private static string GetWorkflowScopeLabel()
        {
            return WfText("范围：已加载 Mod / DLC", "Scope: loaded mods / DLC");
        }

        private static string GetWorkflowFilterLabel()
        {
            switch (_workflowModFilter)
            {
                case WorkflowModFilter.NeedsTranslation: return WfText("分析状态：需要翻译", "Analysis status: needs translation");
                case WorkflowModFilter.Undetermined: return WfText("分析状态：待判定", "Analysis status: undetermined");
                case WorkflowModFilter.Untranslated: return WfText("分析状态：未翻译", "Analysis status: untranslated");
                case WorkflowModFilter.Failed: return WfText("分析状态：失败", "Analysis status: failed");
                default: return WfText("分析状态：全部", "Analysis status: all");
            }
        }

        private static void OpenWorkflowFilterMenu()
        {
            List<FloatMenuOption> options = new List<FloatMenuOption>();
            AddWorkflowFilterOption(options, WorkflowModFilter.All, WfText("全部", "All"));
            AddWorkflowFilterOption(options, WorkflowModFilter.NeedsTranslation,
                WfText("需要翻译", "Needs translation"));
            AddWorkflowFilterOption(options, WorkflowModFilter.Undetermined,
                WfText("待判定", "Undetermined"));
            AddWorkflowFilterOption(options, WorkflowModFilter.Untranslated,
                WfText("未翻译", "Untranslated"));
            AddWorkflowFilterOption(options, WorkflowModFilter.Failed, WfText("失败", "Failed"));
            Find.WindowStack.Add(new FloatMenu(options));
        }

        private static void AddWorkflowFilterOption(
            ICollection<FloatMenuOption> options, WorkflowModFilter filter, string label)
        {
            string prefix = _workflowModFilter == filter ? "✓ " : "   ";
            options.Add(new FloatMenuOption(prefix + label, () =>
            {
                _workflowModFilter = filter;
                _workflowModScroll = Vector2.zero;
            }));
        }

        private static string GetAiReviewScopeLabel(AiReviewScope scope)
        {
            scope = scope ?? new AiReviewScope();
            List<string> values = new List<string>();
            if (scope.IncludeNeedsTranslation) values.Add(WfText("需要翻译", "Needs"));
            if (scope.IncludeUndetermined) values.Add(WfText("待判定", "Undetermined"));
            if (scope.IncludeNoTranslationNeeded) values.Add(WfText("无需翻译", "No translation"));
            return values.Count == 0 ? WfText("未设置", "None") : string.Join("、", values);
        }

        private static void UpdateWorkflowUiReadModel()
        {
            RefreshWorkflowModMetadataCacheIfNeeded();
            long currentDataRevision = WorkflowTaskCoordinator.Instance.WorkbenchDataRevision;
            bool busy = WorkflowTaskCoordinator.Instance.IsBusy ||
                        AutoTranslatorSettings.LegacyPipelineIsRunning;
            if (_workflowUiPreviouslyBusy && !busy)
            {
                _workflowOperationCompletionPendingRefresh = true;
                _workflowInspectorMode = WorkflowInspectorMode.Collapsed;
            }
            WorkflowTaskSnapshot lastCompleted = WorkflowTaskCoordinator.Instance.LastCompleted;
            if (!busy && lastCompleted != null && lastCompleted.RunId != Guid.Empty &&
                lastCompleted.RunId != _workflowLastCollapsedCompletionRunId)
            {
                _workflowLastCollapsedCompletionRunId = lastCompleted.RunId;
                _workflowInspectorMode = WorkflowInspectorMode.Collapsed;
            }
            if (_workflowUiLoadedModSnapshotVersion != _workflowModMetadataVersion)
                _workflowOperationCompletionPendingRefresh = true;
            if (_workflowUiLoadedDataRevision != currentDataRevision)
                _workflowOperationCompletionPendingRefresh = true;
            _workflowUiPreviouslyBusy = busy;
            if (_workflowUiLoadTask != null && _workflowUiLoadTask.IsCompleted)
            {
                try
                {
                    Stopwatch applyTimer = Stopwatch.StartNew();
                    WorkflowUiLoadResult result = _workflowUiLoadTask.GetAwaiter().GetResult();
                    _workflowUiSnapshot = result.Snapshot;
                    _workflowUiConfiguration = result.Configuration;
                    _workflowUiLoadedModSnapshotVersion = result.ModSnapshotVersion;
                    _workflowUiLoadedDataRevision = result.WorkbenchDataRevision;
                    _workflowUiLoadError = string.Empty;
                    applyTimer.Stop();
                    AutoTranslatorSettings.AddDebugLog(
                        "workflow.workbench.phase ui-apply elapsed_ms=" +
                        applyTimer.ElapsedMilliseconds + " mods=" +
                        (_workflowUiSnapshot?.Mods?.Count ?? 0));
                }
                catch (Exception ex)
                {
                    _workflowUiLoadError = ex.GetBaseException().Message;
                    _workflowUiSnapshot = new WorkflowWorkbenchSnapshot();
                    _workflowUiLoadedModSnapshotVersion = _workflowModMetadataVersion;
                    _workflowUiLoadedDataRevision = currentDataRevision;
                    AutoTranslatorSettings.AddErrorLog(
                        WfText("读取工作流数据失败：", "Failed to read workflow data: ") +
                        ex.GetBaseException());
                }
                _workflowUiLoadTask = null;
                _workflowOperationCompletionPendingRefresh = false;
            }
            if (_workflowUiLoadTask != null) return;
            if (!_workflowOperationCompletionPendingRefresh && _workflowUiSnapshot != null) return;
            List<WorkflowModMetadataSnapshot> mods = _workflowModMetadataCache
                .Select(CloneWorkflowModMetadataSnapshot).ToList();
            int modSnapshotVersion = _workflowModMetadataVersion;
            WorkflowBackend backend;
            try
            {
                backend = WorkflowBackendRuntime.GetOrCreate();
            }
            catch (Exception ex)
            {
                _workflowUiLoadError = ex.GetBaseException().Message;
                _workflowUiSnapshot = new WorkflowWorkbenchSnapshot();
                _workflowUiLoadedModSnapshotVersion = modSnapshotVersion;
                _workflowUiLoadedDataRevision = currentDataRevision;
                _workflowOperationCompletionPendingRefresh = false;
                return;
            }
            _workflowUiLoadTask = Task.Run(() =>
            {
                return new WorkflowUiLoadResult
                {
                    Snapshot = backend.GetWorkbenchSnapshot(mods),
                    Configuration = backend.GetConfiguration(),
                    ModSnapshotVersion = modSnapshotVersion,
                    WorkbenchDataRevision = currentDataRevision
                };
            });
        }

        internal static void RefreshWorkflowModMetadataCacheIfNeeded(bool force = false)
        {
            long nowTicks = DateTime.UtcNow.Ticks;
            if (!force && _workflowModMetadataCache.Count > 0 &&
                nowTicks < _workflowNextModMetadataCheckUtcTicks) return;
            _workflowNextModMetadataCheckUtcTicks = nowTicks + TimeSpan.TicksPerSecond;
            Stopwatch timer = Stopwatch.StartNew();
            List<Tuple<WorkflowModMetadataSnapshot, ModMetaData>> captured =
                new List<Tuple<WorkflowModMetadataSnapshot, ModMetaData>>();
            foreach (ModMetaData mod in Verse.ModLister.AllInstalledMods)
            {
                if (mod?.RootDir == null || !mod.Active) continue;
                try
                {
                    string identity = ModAnalysisTargetFactory.CreateModIdentity(mod);
                    int protectedOrder = AutoTranslatorScanner.GetNonTranslatableSystemPackageOrder(
                        mod.PackageId);
                    captured.Add(Tuple.Create(new WorkflowModMetadataSnapshot
                    {
                        ModIdentity = identity,
                        PackageId = mod.PackageId ?? string.Empty,
                        DisplayName = mod.Name ?? mod.PackageId ?? string.Empty,
                        RootPath = mod.RootDir?.FullName ?? string.Empty,
                        IsActive = mod.Active,
                        IsProtectedSystemPackage = protectedOrder >= 0,
                        ProtectedSystemPackageOrder = protectedOrder
                    }, mod));
                }
                catch (Exception ex)
                {
                    AutoTranslatorSettings.AddDebugLog(
                        "workflow.workbench snapshot-skip error=" + ex.Message);
                }
            }
            List<IGrouping<string, Tuple<WorkflowModMetadataSnapshot, ModMetaData>>> groups = captured
                .GroupBy(item => item.Item1.ModIdentity, StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .ToList();
            List<WorkflowModMetadataSnapshot> snapshots = new List<WorkflowModMetadataSnapshot>();
            Dictionary<string, ModMetaData> bindings =
                new Dictionary<string, ModMetaData>(StringComparer.Ordinal);
            StringBuilder signature = new StringBuilder();
            foreach (IGrouping<string, Tuple<WorkflowModMetadataSnapshot, ModMetaData>> group in groups)
            {
                List<Tuple<WorkflowModMetadataSnapshot, ModMetaData>> installations = group
                    .OrderByDescending(item => item.Item1.IsActive)
                    .ThenBy(item => item.Item1.RootPath, WorkflowPath.Comparer)
                    .ToList();
                Tuple<WorkflowModMetadataSnapshot, ModMetaData> selected = installations[0];
                WorkflowModMetadataSnapshot snapshot = CloneWorkflowModMetadataSnapshot(selected.Item1);
                snapshot.IsActive = installations.Any(item => item.Item1.IsActive);
                snapshot.InstallationCount = installations.Count;
                snapshots.Add(snapshot);
                bindings[group.Key] = selected.Item2;
                signature.Append(snapshot.ModIdentity).Append('|')
                    .Append(snapshot.PackageId).Append('|')
                    .Append(snapshot.DisplayName).Append('|')
                    .Append(snapshot.RootPath).Append('|')
                    .Append(snapshot.IsActive ? '1' : '0').Append('|')
                    .Append(snapshot.InstallationCount).Append('\n');
            }
            string nextSignature = signature.ToString();
            bool changed = !string.Equals(
                nextSignature, _workflowModMetadataSignature, StringComparison.Ordinal);
            if (changed)
            {
                _workflowModMetadataSignature = nextSignature;
                _workflowModMetadataCache = snapshots;
                _workflowModMetadataVersion++;
            }
            _workflowModsByIdentity.Clear();
            foreach (KeyValuePair<string, ModMetaData> binding in bindings)
                _workflowModsByIdentity[binding.Key] = binding.Value;
            _workflowSelectedModIdentities.RemoveWhere(
                identity => !_workflowModsByIdentity.ContainsKey(identity) ||
                            _workflowModMetadataCache.Any(mod =>
                                mod.IsProtectedSystemPackage && mod.ModIdentity == identity));
            timer.Stop();
            if (changed || force)
            {
                AutoTranslatorSettings.AddDebugLog(
                    "workflow.workbench.phase lightweight-mod-snapshot elapsed_ms=" +
                    timer.ElapsedMilliseconds + " mods=" + snapshots.Count +
                    " installations=" + captured.Count);
            }
        }

        private static WorkflowModMetadataSnapshot CloneWorkflowModMetadataSnapshot(
            WorkflowModMetadataSnapshot source)
        {
            return new WorkflowModMetadataSnapshot
            {
                ModIdentity = source?.ModIdentity ?? string.Empty,
                PackageId = source?.PackageId ?? string.Empty,
                DisplayName = source?.DisplayName ?? string.Empty,
                RootPath = source?.RootPath ?? string.Empty,
                IsActive = source?.IsActive ?? false,
                InstallationCount = source?.InstallationCount ?? 1,
                IsProtectedSystemPackage = source?.IsProtectedSystemPackage ?? false,
                ProtectedSystemPackageOrder = source?.ProtectedSystemPackageOrder ?? -1
            };
        }

        private static async void StartWorkflowOperation(
            Func<WorkflowBackend, Task> operation,
            string displayName)
        {
            try
            {
                AutoTranslatorSettings.AddLog("⚙️ " + displayName);
                Stopwatch submitTimer = Stopwatch.StartNew();
                WorkflowBackend backend = WorkflowBackendRuntime.GetOrCreate();
                Task workflowTask = operation(backend);
                submitTimer.Stop();
                AutoTranslatorSettings.AddLog(
                    "⏱ " + displayName + WfText("：主线程提交耗时 ", ": UI submission took ") +
                    submitTimer.ElapsedMilliseconds + " ms");
                await workflowTask;
                ATC_Dispatcher.RunOnMainThread(() =>
                {
                    AutoTranslatorSettings.AddLog("✅ " + displayName);
                    _workflowOperationCompletionPendingRefresh = true;
                });
            }
            catch (OperationCanceledException)
            {
                ATC_Dispatcher.RunOnMainThread(() =>
                    AutoTranslatorSettings.AddLog("🛑 " + displayName + " " + WfText("已停止", "stopped")));
            }
            catch (Exception ex)
            {
                ATC_Dispatcher.RunOnMainThread(() =>
                    AutoTranslatorSettings.AddErrorLog(displayName + ": " + ex.Message));
            }
        }

        private static async void StartWorkflowDryRun(List<string> selectedModIdentities)
        {
            try
            {
                AutoTranslatorSettings.AddLog("⚙️ " + WfText("开始试跑", "Dry run started"));
                Stopwatch submitTimer = Stopwatch.StartNew();
                WorkflowBackend backend = WorkflowBackendRuntime.GetOrCreate();
                Task<DryRunReport> dryRunTask = backend.RunDryRunAsync(selectedModIdentities);
                submitTimer.Stop();
                AutoTranslatorSettings.AddLog(
                    "⏱ " + WfText("试跑：主线程提交耗时 ", "Dry run: UI submission took ") +
                    submitTimer.ElapsedMilliseconds + " ms；Mod=" +
                    (selectedModIdentities?.Count ?? 0));
                DryRunReport report = await dryRunTask;
                ATC_Dispatcher.RunOnMainThread(() =>
                {
                    AutoTranslatorSettings.AddLog("✅ " + WfText("试跑完成", "Dry run completed"));
                    _workflowOperationCompletionPendingRefresh = true;
                    Find.WindowStack.Add(new Window_WorkflowDryRunReport(report, _workflowUiSnapshot));
                });
            }
            catch (OperationCanceledException)
            {
                ATC_Dispatcher.RunOnMainThread(() =>
                    AutoTranslatorSettings.AddLog("🛑 " + WfText("试跑已停止", "Dry run stopped")));
            }
            catch (Exception ex)
            {
                ATC_Dispatcher.RunOnMainThread(() =>
                    AutoTranslatorSettings.AddErrorLog(WfText("试跑失败：", "Dry run failed: ") +
                                                       ex.GetBaseException().Message));
            }
        }

        private static void RequestWorkflowStop()
        {
            bool requested = WorkflowTaskCoordinator.Instance.RequestCancellation();
            if (AutoTranslatorSettings.LegacyPipelineIsRunning)
            {
                AutoTranslatorSettings.RequestPipelineCancellation();
                requested = true;
            }
            if (requested)
                AutoTranslatorSettings.AddWarningLog(
                    WfText("已请求停止当前后台任务", "Background task cancellation requested"));
        }

        private static void SaveWorkflowConfiguration(Action<WorkflowConfiguration> change)
        {
            WorkflowConfiguration current = _workflowUiConfiguration ?? new WorkflowConfiguration();
            WorkflowConfiguration next = new WorkflowConfiguration
            {
                EnableDllAnalysis = current.EnableDllAnalysis,
                DryRunUndeterminedTranslationRatio = current.DryRunUndeterminedTranslationRatio,
                AiReviewScope = new AiReviewScope
                {
                    IncludeNeedsTranslation = current.AiReviewScope?.IncludeNeedsTranslation ?? false,
                    IncludeUndetermined = current.AiReviewScope?.IncludeUndetermined ?? true,
                    IncludeNoTranslationNeeded = current.AiReviewScope?.IncludeNoTranslationNeeded ?? false
                }
            };
            change(next);
            StartWorkflowOperation(
                backend => Task.Run(() => backend.SaveConfiguration(next)),
                WfText("保存工作流设置", "Save workflow settings"));
        }

        private static void OpenAiReviewScopeMenu()
        {
            AiReviewScope scope = _workflowUiConfiguration?.AiReviewScope ?? new AiReviewScope();
            Find.WindowStack.Add(new FloatMenu(new List<FloatMenuOption>
            {
                new FloatMenuOption(
                    (scope.IncludeNeedsTranslation ? "✓ " : "□ ") + WfText("需要翻译", "Needs translation"),
                    () => SaveWorkflowConfiguration(config =>
                        config.AiReviewScope.IncludeNeedsTranslation = !scope.IncludeNeedsTranslation)),
                new FloatMenuOption(
                    "✓ " + WfText("待判定（必选）", "Undetermined (required)"),
                    null),
                new FloatMenuOption(
                    (scope.IncludeNoTranslationNeeded ? "✓ " : "□ ") + WfText("无需翻译", "No translation needed"),
                    () => SaveWorkflowConfiguration(config =>
                        config.AiReviewScope.IncludeNoTranslationNeeded = !scope.IncludeNoTranslationNeeded))
            }));
        }

        private static void OpenWorkflowManagementMenu()
        {
            bool busy = WorkflowTaskCoordinator.Instance.IsBusy ||
                        AutoTranslatorSettings.LegacyPipelineIsRunning;
            Find.WindowStack.Add(new FloatMenu(new List<FloatMenuOption>
            {
                new FloatMenuOption("ATC_UpdateLog_Btn".Translate(), () => Find.WindowStack.Add(new UpdateLogWindow())),
                new FloatMenuOption("ATC_Tutorial_Btn".Translate(), () => Find.WindowStack.Add(new TutorialWindow())),
                new FloatMenuOption("ATC_ExportTrans_Btn".Translate(), busy
                    ? (Action)null
                    : (Action)(() => ExportFlowController.StartExportFlow())),
                new FloatMenuOption(WfText("查看任务日志", "View task log"),
                    () => _workflowInspectorMode = WorkflowInspectorMode.RuntimeLog),
                new FloatMenuOption(WfText("迁移历史数据", "Migrate historical data"),
                    busy ? (Action)null : BeginHistoricalDataMigration),
                new FloatMenuOption(WfText("清理过期数据…", "Clean expired data..."),
                    busy || _expiredDataPreviewLoading
                        ? (Action)null
                        : BeginExpiredDataCleanupConfirmation)
            }));
        }

        private static void BeginHistoricalDataMigration()
        {
            Find.WindowStack.Add(new Window_AtcDialog(
                WfText("将当前语种、已启用 Mod 的旧 XML 译文迁入新版数据库。\n" +
                    "全部记为 AI 来源，只补缺，不覆盖已有译文；缺少新版条目时先进行本地 XML 分析。\n" +
                    "旧 XML 没有保存历史原文，本次按条目位置匹配。旧 UI 缓存不导入 DLL 条目。\n" +
                    "不调用 AI，不删除旧文件，不预扫总数。是否开始？",
                    "Import historical XML translations for enabled mods and the current language.\n" +
                    "Imported text counts as AI; existing translations are preserved. Local XML analysis creates missing entries.\n" +
                    "Historical originals are unavailable; matching uses entry locations. Legacy UI cache is excluded.\n" +
                    "No AI calls, deletion, or total pre-count. Continue?"),
                WfText("开始迁移", "Start migration"), StartHistoricalDataMigration,
                WfText("取消", "Cancel"), null, WfText("迁移历史数据", "Migrate historical data"), true));
        }

        private static async void StartHistoricalDataMigration()
        {
            try
            {
                var mods = ModLister.AllInstalledMods.Where(mod => mod != null && mod.Active).ToList();
                var result = await WorkflowBackendRuntime.GetOrCreate().RunHistoricalDataMigrationAsync(mods);
                ATC_Dispatcher.RunOnMainThread(() =>
                {
                    _workflowOperationCompletionPendingRefresh = true;
                    Find.WindowStack.Add(new Window_AtcDialog(result.ToString(),
                        WfText("关闭", "Close"), null, null, null,
                        WfText("历史数据迁移报告", "Historical migration report")));
                });
            }
            catch (OperationCanceledException)
            {
                ATC_Dispatcher.RunOnMainThread(() =>
                {
                    _workflowOperationCompletionPendingRefresh = true;
                    AutoTranslatorSettings.AddLog(WfText("历史数据迁移已停止，已入库译文保留。", "Migration stopped; imported text is retained."));
                });
            }
            catch (Exception ex)
            {
                ATC_Dispatcher.RunOnMainThread(() =>
                {
                    _workflowOperationCompletionPendingRefresh = true;
                    AutoTranslatorSettings.AddErrorLog(WfText("历史数据迁移失败：", "Migration failed: ") + ex.GetBaseException().Message);
                });
            }
        }

        private static async void BeginExpiredDataCleanupConfirmation()
        {
            if (_expiredDataPreviewLoading) return;
            _expiredDataPreviewLoading = true;
            AutoTranslatorSettings.AddLog("⏳ " + WfText(
                "正在统计可清理的过期数据", "Counting expired data that can be cleaned"));
            try
            {
                ExpiredWorkflowDataSummary summary = await Task.Run(
                    () => WorkflowBackendRuntime.GetOrCreate().GetExpiredDataSummary());
                ATC_Dispatcher.RunOnMainThread(() =>
                {
                    _expiredDataPreviewLoading = false;
                    if (!summary.HasExpiredData)
                    {
                        Messages.Message(WfText(
                                "当前没有可清理的过期数据。",
                                "There is no expired data to clean."),
                            MessageTypeDefOf.NeutralEvent, false);
                        return;
                    }

                    string message = WfText(
                        "即将永久删除以下数据库记录（包括因 XML/DLL 分析器版本变化而失效的结果）：\n\n" +
                        "过期候选：" + summary.CandidateCount.ToString("N0") + " 条\n" +
                        "关联译文状态：" + summary.TranslationResultCount.ToString("N0") + " 条\n" +
                        "关联文件索引：" + summary.TranslationFileEntryCount.ToString("N0") + " 条\n" +
                        "关联文件操作记录：" + summary.PendingFileOperationCount.ToString("N0") + " 条\n\n" +
                        "不会删除当前有效分析结果、磁盘上的译文文件或模型配置。" +
                        "清理后将压缩数据库，过程可能需要几分钟。\n\n" +
                        "删除过期数据后无法恢复和找回。是否继续？",
                        "The following database records will be permanently deleted, including results invalidated by XML/DLL analyzer version changes:\n\n" +
                        "Expired candidates: " + summary.CandidateCount.ToString("N0") + "\n" +
                        "Related translation states: " + summary.TranslationResultCount.ToString("N0") + "\n" +
                        "Related file index entries: " + summary.TranslationFileEntryCount.ToString("N0") + "\n" +
                        "Related file operation records: " + summary.PendingFileOperationCount.ToString("N0") + "\n\n" +
                        "Current analysis results, translation files on disk, and model settings will not be deleted. " +
                        "The database will be compacted afterward and this may take several minutes.\n\n" +
                        "Deleted expired data cannot be recovered. Continue?");
                    Find.WindowStack.Add(new Window_AtcDialog(
                        message,
                        WfText("确认永久删除", "Permanently delete"),
                        StartExpiredDataCleanup,
                        WfText("取消", "Cancel"),
                        null,
                        WfText("清理过期数据", "Clean expired data"),
                        true));
                });
            }
            catch (Exception ex)
            {
                ATC_Dispatcher.RunOnMainThread(() =>
                {
                    _expiredDataPreviewLoading = false;
                    AutoTranslatorSettings.AddErrorLog(WfText(
                        "统计过期数据失败：", "Failed to count expired data: ") +
                        ex.GetBaseException().Message);
                });
            }
        }

        private static void StartExpiredDataCleanup()
        {
            StartWorkflowOperation(
                async backend =>
                {
                    ExpiredWorkflowDataSummary result = await backend.RunExpiredDataCleanupAsync();
                    ATC_Dispatcher.RunOnMainThread(() => Messages.Message(
                        WfText(
                            "已清理 " + result.CandidateCount.ToString("N0") + " 条过期候选。" +
                            (result.DatabaseCompacted ? "数据库空间已回收。" : "数据库压缩失败，详情请查看错误日志。"),
                            "Cleaned " + result.CandidateCount.ToString("N0") + " expired candidates. " +
                            (result.DatabaseCompacted ? "Database space was reclaimed." :
                                "Database compaction failed; see the error log.")),
                        result.DatabaseCompacted ? MessageTypeDefOf.PositiveEvent : MessageTypeDefOf.CautionInput,
                        false));
                },
                WfText("清理过期数据", "Clean expired data"));
        }

        private static void OpenWorkflowLogFile()
        {
            string path = System.IO.Path.Combine(
                AutoTranslatorScanner.GetLocalPackPath(), "AutoTranslation_Log.txt");
            if (!System.IO.File.Exists(path))
            {
                Messages.Message(
                    WfText("日志文件尚未生成。", "The log file has not been created yet."),
                    MessageTypeDefOf.NeutralEvent, false);
                return;
            }
            Application.OpenURL("file:///" + path.Replace('\\', '/'));
        }

        private static void DrawOutlinedProgressLabel(Rect rect, string text, GameFont font)
        {
            TextAnchor oldAnchor = Text.Anchor;
            GameFont oldFont = Text.Font;
            Color oldColor = GUI.color;
            Text.Anchor = TextAnchor.MiddleCenter;
            Text.Font = font;
            GUI.color = new Color(0f, 0f, 0f, 0.95f);
            Widgets.Label(new Rect(rect.x - 1f, rect.y, rect.width, rect.height), text);
            Widgets.Label(new Rect(rect.x + 1f, rect.y, rect.width, rect.height), text);
            Widgets.Label(new Rect(rect.x, rect.y - 1f, rect.width, rect.height), text);
            Widgets.Label(new Rect(rect.x, rect.y + 1f, rect.width, rect.height), text);
            GUI.color = Color.white;
            Widgets.Label(rect, text);
            GUI.color = oldColor;
            Text.Font = oldFont;
            Text.Anchor = oldAnchor;
        }

        private void DrawLogView(
            Rect rect,
            List<string> logs,
            ref Vector2 scrollPos,
            LogViewCache cache,
            bool isErrorBox,
            bool isWarningBox)
        {
            const int runtimeDisplayLimit = 180;
            const int errorDisplayLimit = 80;
            int displayLimit = isErrorBox ? errorDisplayLimit : runtimeDisplayLimit;
            float calcWidth = Mathf.Max(1f, rect.width - 20f);
            float cacheWidth = Mathf.Round(calcWidth);
            List<string> snapshot = null;
            bool sourceCountChanged;

            lock (AutoTranslatorSettings.logLock)
            {
                int start = Math.Max(0, logs.Count - displayLimit);
                string firstLine = logs.Count > 0 ? logs[start] : "";
                string lastLine = logs.Count > 0 ? logs[logs.Count - 1] : "";
                sourceCountChanged = cache.SourceCount != logs.Count;
                bool needsRebuild =
                    sourceCountChanged ||
                    !Mathf.Approximately(cache.Width, cacheWidth) ||
                    !string.Equals(cache.FirstLine, firstLine, StringComparison.Ordinal) ||
                    !string.Equals(cache.LastLine, lastLine, StringComparison.Ordinal);

                if (needsRebuild)
                {
                    snapshot = new List<string>(logs.Count - start);
                    for (int i = start; i < logs.Count; i++)
                    {
                        snapshot.Add(logs[i]);
                    }

                    cache.SourceCount = logs.Count;
                    cache.FirstLine = firstLine;
                    cache.LastLine = lastLine;
                    cache.Width = cacheWidth;
                }
            }

            Text.Font = GameFont.Tiny;
            if (snapshot != null)
            {
                cache.DisplayLogs.Clear();
                cache.Heights.Clear();
                cache.TotalHeight = 0f;
                foreach (string log in snapshot)
                {
                    float height = Text.CalcHeight(log, calcWidth);
                    cache.DisplayLogs.Add(log);
                    cache.Heights.Add(height);
                    cache.TotalHeight += height;
                }
            }

            List<string> displayLogs = cache.DisplayLogs;
            List<float> heights = cache.Heights;
            float totalHeight = cache.TotalHeight;
            float contentHeight = Mathf.Max(totalHeight, rect.height);
            Rect viewRect = new Rect(0, 0, rect.width - 20f, contentHeight);

            float scrollBeforeInput = scrollPos.y;
            Widgets.BeginScrollView(rect, ref scrollPos, viewRect);
            float currentY = 0f;

            for (int i = 0; i < displayLogs.Count; i++)
            {
                string log = displayLogs[i];
                float height = heights[i];
                Rect lineRect = new Rect(5f, currentY, viewRect.width, height);
                currentY += height;

                if (lineRect.yMax < scrollPos.y || lineRect.y > scrollPos.y + rect.height)
                {
                    continue;
                }

                if (isErrorBox || log.Contains("❌") || log.Contains("🛑"))
                    GUI.color = new Color(1f, 0.4f, 0.4f);
                else if (isWarningBox || log.Contains("⚠️"))
                    GUI.color = new Color(1f, 0.8f, 0.4f);
                else if (log.Contains("✅") || log.Contains("✨") || log.Contains("🎉"))
                    GUI.color = new Color(0.4f, 1f, 0.4f);
                else if (log.Contains("⚙️") || log.Contains("🔌") || log.Contains("🔄") || log.Contains("⏭️"))
                    GUI.color = new Color(1f, 0.8f, 0.4f);
                else if (log.Contains("📦") || log.Contains("🌐") || log.Contains("🚀") ||
                         log.Contains("🔍") || log.Contains("🧹"))
                    GUI.color = new Color(0.4f, 0.8f, 1f);
                else
                    GUI.color = new Color(0.8f, 0.8f, 0.8f);

                Widgets.Label(lineRect, log);
            }

            float maxScroll = Mathf.Max(0f, totalHeight - rect.height);

            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            Widgets.EndScrollView();

            if (scrollPos.y < scrollBeforeInput - 0.5f)
                cache.FollowTail = false;
            else if (maxScroll - scrollPos.y <= 1f)
                cache.FollowTail = true;
            if (sourceCountChanged && cache.FollowTail)
                scrollPos.y = maxScroll;
            scrollPos.y = Mathf.Clamp(scrollPos.y, 0f, maxScroll);
        }

        internal static string WfText(string chinese, string english)
        {
            string folder = LanguageDatabase.activeLanguage?.folderName ?? string.Empty;
            return folder.StartsWith("Chinese", StringComparison.OrdinalIgnoreCase) ? chinese : english;
        }
    }
}
