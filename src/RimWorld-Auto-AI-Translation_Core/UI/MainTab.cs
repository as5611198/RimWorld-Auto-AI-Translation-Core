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

            Rect topBarRect = l.GetRect(30f);
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
            l.Gap(15f);


            var updatedMods = ModUpdateDetector.GetUpdatedOrNewModsCached();
            bool isCheckingUpdates = ModUpdateDetector.IsRefreshingUpdatedList && !ModUpdateDetector.HasUpdatedListCache;

            Rect actionRow = l.GetRect(40f);
            Rect singleModRect = new Rect(actionRow.x, actionRow.y, actionRow.width * 0.3f, actionRow.height);
            Rect skipRect = new Rect(actionRow.x + actionRow.width * 0.32f, actionRow.y, actionRow.width * 0.33f, actionRow.height);
            Rect stopRect = new Rect(actionRow.x + actionRow.width * 0.67f, actionRow.y, actionRow.width * 0.33f, actionRow.height);
            Rect startRect = new Rect(actionRow.x + actionRow.width * 0.32f, actionRow.y, actionRow.width * 0.68f, actionRow.height);

            if (AutoTranslatorSettings.IsRunning) GUI.color = Color.grey;

            string multiBtnText = updatedMods.Count > 0
                           ? "ATC_SmartUpdateBtn".Translate(updatedMods.Count).ToString()
                           : "ATC_TranslateMultiMod".Translate().ToString();

            if (Widgets.ButtonText(singleModRect, multiBtnText))
            {
                if (!AutoTranslatorSettings.IsRunning) Find.WindowStack.Add(new ModSelectWindow(updatedMods));
            }

            if (AutoTranslatorSettings.IsRunning)
            {
                GUI.color = new Color(1f, 0.8f, 0.4f);
                if (Widgets.ButtonText(skipRect, "ATC_SkipCurrentMod".Translate()))
                {
                    AutoTranslatorSettings.IsSkipCurrentRequested = true;
                    AutoTranslatorSettings.AddLog("⏭️ " + "ATC_Log_SkipRequested".Translate());
                }
                GUI.color = new Color(1f, 0.4f, 0.4f);
                if (Widgets.ButtonText(stopRect, "🛑 " + "ATC_EmergencyStop".Translate()))
                {
                    AutoTranslatorSettings.RequestPipelineCancellation();
                    AutoTranslatorSettings.AddLog("⚠️ " + "ATC_CancelRequested".Translate());
                }
                GUI.color = Color.white;
            }
            else
            {
                GUI.color = new Color(0.6f, 0.9f, 0.6f);
                if (Widgets.ButtonText(startRect, "🚀 " + "ATC_StartFullScan".Translate()))
                {
                    if (!HasValidConfig()) Messages.Message("ATC_EmptyConfigWarning".Translate().ToString(), MessageTypeDefOf.RejectInput, false);
                    else
                    {
                        AutoTranslatorSettings.ClearLog();
                        AutoTranslatorSettings.ResetPipelineCancellation();
                        AutoTranslatorScanner.StartFullScan();
                    }
                }
                GUI.color = Color.white;
            }
            l.Gap(15f);

            if (isCheckingUpdates)
            {
                Text.Font = GameFont.Tiny;
                GUI.color = Color.gray;
                l.Label("ATC_CheckingModStatus".Translate());
                GUI.color = Color.white;
                Text.Font = GameFont.Small;
                l.Gap(5f);
            }

            Rect reloadRow = l.GetRect(35f);
            GUI.color = new Color(0.4f, 1f, 0.8f);
            if (Widgets.ButtonText(reloadRow, "🔄 " + "ATC_Button_HotReload".Translate()))
            {
                UIInterceptor.RequestHotReload();
            }
            GUI.color = Color.white;
            l.Gap(15f);


            string displayTask = string.IsNullOrEmpty(Settings.CurrentTaskName) ? "ATC_Idle".Translate().ToString() : Settings.CurrentTaskName;
            l.Label("ATC_CurrentTask".Translate() + $": {displayTask}");
            Rect barRect = l.GetRect(25f);
            Widgets.FillableBar(barRect, Settings.CurrentProgress);
            TextAnchor oldAnchor = Text.Anchor;
            Text.Anchor = TextAnchor.MiddleCenter;
            Widgets.Label(barRect, $"{(Settings.CurrentProgress * 100):F0}%");
            Text.Anchor = oldAnchor;

            Rect subBarRect = l.GetRect(15f);
            string displaySubTask = string.IsNullOrEmpty(Settings.SubTaskName) ? "" : Settings.SubTaskName;
            Widgets.FillableBar(subBarRect, Settings.SubProgress);
            Text.Font = GameFont.Tiny;
            Widgets.Label(new Rect(subBarRect.x, subBarRect.y, subBarRect.width, subBarRect.height), $" {displaySubTask} ({(Settings.SubProgress * 100):F0}%)");
            Text.Font = GameFont.Small;
            l.Gap(15f);


            Rect statsRect = l.GetRect(50f);
            Rect statsLeft = new Rect(statsRect.x, statsRect.y, statsRect.width * 0.48f, statsRect.height);
            Rect statsRight = new Rect(statsRect.xMax - statsRect.width * 0.48f, statsRect.y, statsRect.width * 0.48f, statsRect.height);
            string sessionText = "ATC_Stats_Session".Translate(Settings.SessionCharCount);
            string totalText = "ATC_Stats_Total".Translate(Settings.TotalCharCount);
            Widgets.Label(statsLeft, $"📊 {sessionText}\n📈 {totalText}");

            if (Settings.EnableUIInterceptor)
            {
                string uiQueue = UIInterceptor.GetQueueCount().ToString();
                string uiCache = UIInterceptor.Cache.Count.ToString();
                Widgets.Label(statsRight, $"🛡️ " + "ATC_Stats_UIQueue".Translate(uiQueue) + $"\n📦 " + "ATC_Stats_UICache".Translate(uiCache));
            }
            l.Gap(15f);

            int unresolvedCount = TranslationUnresolvedManager.Count;
            if (!AutoTranslatorSettings.IsRunning && unresolvedCount > 0)
            {
                GUI.color = new Color(1f, 0.75f, 0.35f);
                if (Widgets.ButtonText(l.GetRect(36f), "ATC_Unresolved_Title".Translate(unresolvedCount)))
                {
                    Find.WindowStack.Add(new Window_UnresolvedTranslations());
                }
                GUI.color = Color.white;
                l.Gap(10f);
            }


            if (!AutoTranslatorSettings.IsRunning)
            {
                int filteredCount = GetFilteredModsCountCached();
                int forcedCount = GetForceIncludedModsCountCached();
                if (filteredCount > 0 || forcedCount > 0 || IsValidModsCacheRefreshing)
                {
                    GUI.color = new Color(0.72f, 0.72f, 0.72f);
                    string text = "ATC_FilteredModsButton".Translate(filteredCount, forcedCount);
                    if (Widgets.ButtonText(l.GetRect(32f), text))
                    {
                        Find.WindowStack.Add(new Window_FilteredMods());
                    }
                    l.Gap(10f);
                }
                GUI.color = Color.white;
            }

            Rect headerRect = l.GetRect(24f);
            float leftWidth = headerRect.width * 0.6f;
            float rightWidth = headerRect.width * 0.4f - 10f;
            Widgets.Label(new Rect(headerRect.x, headerRect.y, leftWidth, headerRect.height), "ATC_LogPanelTitle".Translate());
            Widgets.Label(new Rect(headerRect.x + leftWidth + 10f, headerRect.y, rightWidth, headerRect.height), "ATC_ErrorLogTitle".Translate());

            Rect logArea = l.GetRect(350f);
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
