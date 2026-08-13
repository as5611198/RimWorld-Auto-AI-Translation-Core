using RimWorld;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;
using static AutoTranslator_Core.DeleteTranslationWindow;
// 這個檔案負責主畫面分頁的按鈕、進度條與日誌區塊。
// EN: This file draws the main tab controls, progress bars, and log panels.

namespace AutoTranslator_Core
{
    // 這個類別負責 自動翻譯器模組 的主要流程與狀態。
    // EN: This class manages the main workflow and state for AutoTranslatorMod.
    public partial class AutoTranslatorMod : Mod
    {


        // 這個方法負責繪製 主畫面分頁 介面。
        // EN: This method draws main tab.
        private void DrawMainTab(Listing_Standard l, Rect viewRect)
        {

            Rect topBarRect = l.GetRect(28f);
            float btnWidth = (topBarRect.width - 30f) / 4f;
            float gap = 10f;
            if (Widgets.ButtonText(new Rect(topBarRect.x, topBarRect.y, btnWidth, topBarRect.height), "📜 " + "ATC_UpdateLog_Btn".Translate()))
            {
                Find.WindowStack.Add(new UpdateLogWindow());
            }
            if (Widgets.ButtonText(new Rect(topBarRect.x + (btnWidth + gap) * 1, topBarRect.y, btnWidth, topBarRect.height), "🗑️ " + "ATC_DeleteModTrans_Btn".Translate()))
            {
                Find.WindowStack.Add(new DeleteTranslationWindow());
            }
            if (Widgets.ButtonText(new Rect(topBarRect.x + (btnWidth + gap) * 2, topBarRect.y, btnWidth, topBarRect.height), "📖 " + "ATC_Tutorial_Btn".Translate()))
            {
                Find.WindowStack.Add(new TutorialWindow());
            }
            GUI.color = new Color(1f, 0.7f, 0.3f);
            if (Widgets.ButtonText(new Rect(topBarRect.x + (btnWidth + gap) * 3, topBarRect.y, btnWidth, topBarRect.height), "ATC_ExportTrans_Btn".Translate()))
            {
                ExportFlowController.StartExportFlow();
            }
            GUI.color = Color.white;
            l.Gap(6f);


            var updatedMods = ModUpdateDetector.GetUpdatedOrNewModsCached();
            bool isCheckingUpdates = ModUpdateDetector.IsRefreshingUpdatedList && !ModUpdateDetector.HasUpdatedListCache;

            Rect actionRow = l.GetRect(32f);
            const float actionGap = 6f;
            float actionWidth = (actionRow.width - actionGap * 2f) / 3f;
            Rect singleModRect = new Rect(actionRow.x, actionRow.y, actionWidth, actionRow.height);
            Rect startRect = new Rect(singleModRect.xMax + actionGap, actionRow.y, actionWidth, actionRow.height);
            Rect reloadRect = new Rect(startRect.xMax + actionGap, actionRow.y, actionWidth, actionRow.height);
            TranslationRequestActivitySnapshot requestActivity = AutoTranslatorAPI.GetTranslationRequestActivity();
            int localUiQueueCount = UIInterceptor.GetQueueCount();
            int displayedQueuedCount = requestActivity.Queued + requestActivity.Dispatching + localUiQueueCount;
            bool hasTranslationWork = AutoTranslatorSettings.IsRunning ||
                                      requestActivity.TotalOutstanding > 0 ||
                                      UIInterceptor.HasOutstandingTranslationWork;

            if (hasTranslationWork) GUI.color = Color.grey;

            string multiBtnText = updatedMods.Count > 0
                           ? "ATC_SmartUpdateBtn".Translate(updatedMods.Count).ToString()
                           : "ATC_TranslateMultiMod".Translate().ToString();

            if (Widgets.ButtonText(singleModRect, multiBtnText))
            {
                if (!hasTranslationWork) Find.WindowStack.Add(new ModSelectWindow(updatedMods));
            }
            AddDisabledReasonTooltip(
                singleModRect,
                hasTranslationWork,
                AutoTranslatorSettings.IsStopping
                    ? "ATC_Disabled_Stopping".Translate().ToString()
                    : "ATC_Disabled_TaskRunning".Translate().ToString());

            GUI.color = hasTranslationWork ? Color.grey : new Color(0.6f, 0.9f, 0.6f);
            if (Widgets.ButtonText(startRect, "🚀 " + "ATC_StartFullScan".Translate()) && !hasTranslationWork)
            {
                if (!HasValidConfig())
                {
                    Messages.Message(
                        "ATC_EmptyConfigWarning".Translate().ToString(),
                        MessageTypeDefOf.RejectInput,
                        false);
                }
                else
                {
                    AutoTranslatorSettings.ClearLog();
                    AutoTranslatorSettings.ResetPipelineCancellation();
                    AutoTranslatorScanner.StartFullScan();
                }
            }
            AddDisabledReasonTooltip(
                startRect,
                hasTranslationWork,
                AutoTranslatorSettings.IsStopping
                    ? "ATC_Disabled_Stopping".Translate().ToString()
                    : "ATC_Disabled_TaskRunning".Translate().ToString());

            GUI.color = hasTranslationWork ? Color.grey : new Color(0.4f, 1f, 0.8f);
            if (Widgets.ButtonText(reloadRect, "🔄 " + "ATC_Button_HotReload".Translate()) && !hasTranslationWork)
                UIInterceptor.RequestHotReload();
            AddDisabledReasonTooltip(
                reloadRect,
                hasTranslationWork,
                AutoTranslatorSettings.IsStopping
                    ? "ATC_Disabled_Stopping".Translate().ToString()
                    : "ATC_Disabled_TaskRunning".Translate().ToString());
            GUI.color = Color.white;
            l.Gap(6f);

            if (isCheckingUpdates)
            {
                Text.Font = GameFont.Tiny;
                GUI.color = Color.gray;
                l.Label("ATC_CheckingModStatus".Translate());
                GUI.color = Color.white;
                Text.Font = GameFont.Small;
                l.Gap(3f);
            }

            string displayTask = string.IsNullOrEmpty(Settings.CurrentTaskName) ? "ATC_Idle".Translate().ToString() : Settings.CurrentTaskName;
            string sessionText = "ATC_Stats_Session".Translate(Settings.SessionCharCount);
            string totalText = "ATC_Stats_Total".Translate(Settings.TotalCharCount);
            string statusText = "ATC_CurrentTask".Translate() + $": {displayTask}    {sessionText}    {totalText}";
            if (Settings.EnableUIInterceptor)
            {
                statusText += "    " + "ATC_Stats_UIQueue".Translate(UIInterceptor.GetQueueCount()) +
                              " / " + "ATC_Stats_UICache".Translate(UIInterceptor.Cache.Count);
            }
            Rect combinedStatusRect = l.GetRect(24f);
            Text.WordWrap = false;
            Widgets.Label(combinedStatusRect, statusText);
            Text.WordWrap = true;
            if (GUI.skin.label.CalcSize(new GUIContent(statusText)).x > combinedStatusRect.width)
                TooltipHandler.TipRegion(combinedStatusRect, statusText);

            Rect progressRow = l.GetRect(24f);
            const float stopButtonWidth = 150f;
            Rect barRect = new Rect(
                progressRow.x,
                progressRow.y + 1f,
                progressRow.width - stopButtonWidth - 8f,
                22f);
            Rect stopRect = new Rect(barRect.xMax + 8f, progressRow.y, stopButtonWidth, 24f);
            float displayedProgress = Settings.CurrentProgress;
            string displayedProgressText = $"{(displayedProgress * 100):F0}%";
            if (!string.IsNullOrWhiteSpace(AutoTranslatorSettings.AgentBatchProgressText))
            {
                displayedProgress = Mathf.Clamp01(AutoTranslatorSettings.AgentBatchProgress);
                displayedProgressText = AutoTranslatorSettings.AgentBatchProgressText;
            }
            else if (!string.IsNullOrWhiteSpace(Settings.SubTaskName))
            {
                displayedProgress = Mathf.Clamp01(Settings.SubProgress);
                displayedProgressText = $"{Settings.SubTaskName} ({(displayedProgress * 100):F0}%)";
            }
            Widgets.FillableBar(barRect, displayedProgress);
            Widgets.DrawBox(barRect, 1);
            DrawOutlinedProgressLabel(barRect.ContractedBy(3f), displayedProgressText, GameFont.Small);

            bool canStop = hasTranslationWork && !AutoTranslatorSettings.IsStopping;
            GUI.color = canStop ? new Color(1f, 0.35f, 0.35f) : Color.grey;
            string stopText = AutoTranslatorSettings.IsStopping
                ? "ATC_Stopping".Translate().ToString()
                : "ATC_EmergencyStop".Translate().ToString();
            if (Widgets.ButtonText(stopRect, stopText) && canStop)
            {
                AutoTranslatorSettings.RequestPipelineCancellation();
                AutoTranslatorSettings.AddLog("⚠️ " + "ATC_CancelRequested".Translate());
            }
            TooltipHandler.TipRegion(
                stopRect,
                canStop
                    ? "ATC_StopAllTaskTooltip".Translate().ToString()
                    : AutoTranslatorSettings.IsStopping
                        ? "ATC_Disabled_Stopping".Translate().ToString()
                        : "ATC_Disabled_NoActiveTask".Translate().ToString());
            GUI.color = Color.white;

            l.Gap(6f);

            int unresolvedCount = TranslationUnresolvedManager.Count +
                                  Window_UnresolvedTranslations.CountDllUnresolvedEntries();
            int filteredCount = GetFilteredModsCountCached();
            int forcedCount = GetForceIncludedModsCountCached();
            bool showUnresolved = !AutoTranslatorSettings.IsRunning && unresolvedCount > 0;
            bool showFiltered = !AutoTranslatorSettings.IsRunning &&
                                (filteredCount > 0 || forcedCount > 0 || IsValidModsCacheRefreshing);
            if (showUnresolved || showFiltered)
            {
                Rect summaryRect = l.GetRect(30f);
                const float summaryGap = 8f;
                float unresolvedWidth = showUnresolved && showFiltered
                    ? summaryRect.width * 0.62f
                    : summaryRect.width;

                if (showUnresolved)
                {
                    GUI.color = new Color(1f, 0.75f, 0.35f);
                    Rect unresolvedRect = new Rect(
                        summaryRect.x,
                        summaryRect.y,
                        unresolvedWidth,
                        summaryRect.height);
                    if (Widgets.ButtonText(unresolvedRect, "ATC_Unresolved_Title".Translate(unresolvedCount)))
                    {
                        Find.WindowStack.Add(new Window_UnresolvedTranslations());
                    }
                }

                if (showFiltered)
                {
                    GUI.color = new Color(0.72f, 0.72f, 0.72f);
                    float filteredX = showUnresolved
                        ? summaryRect.x + unresolvedWidth + summaryGap
                        : summaryRect.x;
                    Rect filteredRect = new Rect(
                        filteredX,
                        summaryRect.y,
                        summaryRect.xMax - filteredX,
                        summaryRect.height);
                    string filteredText = "ATC_FilteredModsButton".Translate(filteredCount, forcedCount);
                    if (Widgets.ButtonText(filteredRect, filteredText))
                    {
                        Find.WindowStack.Add(new Window_FilteredMods());
                    }
                }

                GUI.color = Color.white;
                l.Gap(3f);
            }

            Rect headerRect = l.GetRect(24f);
            float leftWidth = headerRect.width * 0.6f;
            float rightWidth = headerRect.width * 0.4f - 10f;
            Widgets.Label(new Rect(headerRect.x, headerRect.y, leftWidth, headerRect.height), "ATC_LogPanelTitle".Translate());
            Widgets.Label(new Rect(headerRect.x + leftWidth + 10f, headerRect.y, rightWidth, headerRect.height), "ATC_ErrorLogTitle".Translate());

            float availableLogHeight = viewRect.height - l.CurHeight - 12f;
            Rect logArea = l.GetRect(Mathf.Max(260f, availableLogHeight));
            Rect leftRect = new Rect(logArea.x, logArea.y, leftWidth, logArea.height);
            Rect rightRect = new Rect(logArea.x + leftWidth + 10f, logArea.y, rightWidth, logArea.height);

            Widgets.DrawBoxSolid(leftRect, new Color(0.05f, 0.05f, 0.05f, 1f));
            Widgets.DrawBox(leftRect, 1);
            DrawLogView(leftRect, AutoTranslatorSettings.RuntimeLogs, ref AutoTranslatorSettings.logScrollPos, false);

            Widgets.DrawBoxSolid(rightRect, new Color(0.1f, 0.0f, 0.0f, 1f));
            Widgets.DrawBox(rightRect, 1);
            DrawLogView(rightRect, AutoTranslatorSettings.ErrorLogs, ref AutoTranslatorSettings.errorScrollPos, true);


            Rect eggRect = new Rect(viewRect.width - 150f, l.CurHeight + 5f, 140f, 20f);
            GUI.color = new Color(1f, 1f, 1f, 0.15f);
            Text.Font = GameFont.Tiny;
            Widgets.Label(eggRect, "ATC_Rickroll".Translate());
            if (Widgets.ButtonInvisible(eggRect))
            {
                string activeLanguageFolder = LanguageDatabase.activeLanguage != null ? LanguageDatabase.activeLanguage.folderName : string.Empty;
                if (Settings.TargetLang == TargetLanguage.Simplified || activeLanguageFolder == "ChineseSimplified") Application.OpenURL("https://www.bilibili.com/video/BV1UT42167xb/?share_source=copy_web&vd_source=c35f0d8bdae316c56309ea1d46f1172e");
                else Application.OpenURL("https://www.youtube.com/watch?v=dQw4w9WgXcQ");
            }
            Text.Font = GameFont.Small;
            GUI.color = Color.white;
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

        private static void AddDisabledReasonTooltip(Rect rect, bool disabled, string reason)
        {
            if (!disabled || string.IsNullOrWhiteSpace(reason)) return;
            TooltipHandler.TipRegion(rect, "ATC_DisabledReason".Translate(reason));
        }


// 這個方法負責判斷 HasValid設定 條件是否成立。
// EN: This method checks has valid config.
private bool HasValidConfig()
        {
            return AutoTranslatorAPI.HasAnyReadyConfig();
        }


// 這個方法負責繪製 LogView 介面。
// EN: This method draws log view.
private void DrawLogView(Rect rect, List<string> logs, ref Vector2 scrollPos, bool isErrorBox)
        {
            const int runtimeDisplayLimit = 180;
            const int errorDisplayLimit = 80;
            int displayLimit = isErrorBox ? errorDisplayLimit : runtimeDisplayLimit;
            float calcWidth = Mathf.Max(1f, rect.width - 20f);
            float cacheWidth = Mathf.Round(calcWidth);
            LogViewCache cache = isErrorBox ? _errorLogViewCache : _runtimeLogViewCache;
            List<string> snapshot = null;

            lock (AutoTranslatorSettings.logLock)
            {
                int start = System.Math.Max(0, logs.Count - displayLimit);
                string firstLine = logs.Count > 0 ? logs[start] : "";
                string lastLine = logs.Count > 0 ? logs[logs.Count - 1] : "";
                bool needsRebuild =
                    cache.SourceCount != logs.Count ||
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
                    float h = Text.CalcHeight(log, calcWidth);
                    cache.DisplayLogs.Add(log);
                    cache.Heights.Add(h);
                    cache.TotalHeight += h;
                }
            }

            List<string> displayLogs = cache.DisplayLogs;
            List<float> heights = cache.Heights;
            float totalHeight = cache.TotalHeight;
            float contentHeight = Mathf.Max(totalHeight, rect.height);
            Rect viewRect = new Rect(0, 0, rect.width - 20f, contentHeight);

            Widgets.BeginScrollView(rect, ref scrollPos, viewRect);
            float currentY = 0;

            for (int i = 0; i < displayLogs.Count; i++)
            {
                string log = displayLogs[i];
                float h = heights[i];
                Rect lineRect = new Rect(5f, currentY, viewRect.width, h);
                currentY += h;

                if (lineRect.yMax < scrollPos.y || lineRect.y > scrollPos.y + rect.height)
                {
                    continue;
                }

                if (isErrorBox || log.Contains("❌") || log.Contains("⚠️") || log.Contains("🛑")) GUI.color = new Color(1f, 0.4f, 0.4f);
                else if (log.Contains("✅") || log.Contains("✨") || log.Contains("🎉")) GUI.color = new Color(0.4f, 1f, 0.4f);
                else if (log.Contains("⚙️") || log.Contains("🔌") || log.Contains("🔄") || log.Contains("⏭️")) GUI.color = new Color(1f, 0.8f, 0.4f);
                else if (log.Contains("📦") || log.Contains("🌐") || log.Contains("🚀") || log.Contains("🔍") || log.Contains("🧹")) GUI.color = new Color(0.4f, 0.8f, 1f);
                else GUI.color = new Color(0.8f, 0.8f, 0.8f);

                Widgets.Label(lineRect, log);
            }

            float viewHeight = totalHeight;
            float maxScroll = Mathf.Max(0f, viewHeight - rect.height);

            if (scrollPos.y > maxScroll)
            {
                scrollPos.y = maxScroll;
            }

            if (!isErrorBox && (maxScroll - scrollPos.y <= 100f))
            {
                scrollPos.y = maxScroll;
            }

            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            Widgets.EndScrollView();
        }

    }
}
