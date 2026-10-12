using RimWorld;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using Verse;
using static AutoTranslator_Core.DeleteTranslationWindow;
// 這個檔案負責雲端翻譯服務的 自動翻譯器模組雲端繪製，處理 registry、上傳、下載或刪除流程。
// EN: This file contains auto translator mod cloud renderers support code.

namespace AutoTranslator_Core
{
    // 這個類別負責 自動翻譯器模組 的主要流程與狀態。
    // EN: This class manages the main workflow and state for AutoTranslatorMod.
    public partial class AutoTranslatorMod : Mod
    {
        // 這個方法負責繪製 雲端ToolbarAnd設定 介面。
        // EN: This method draws cloud toolbar and settings.
        private void DrawCloudToolbarAndSettings(Listing_Standard l, Rect viewRect)
        {
            Rect headerRow = l.GetRect(30f);
            Widgets.Label(new Rect(headerRow.x, headerRow.y + 5f, 90f, 24f), WfText("目标语言", "Language"));
            if (WorkflowUiStyle.Button(new Rect(headerRow.x + 90f, headerRow.y, 200f, 30f),
                    "🌐 " + GetLangLabel(Settings.CloudTargetLang), WorkflowButtonStyle.Quiet))
            {
                List<FloatMenuOption> options = new List<FloatMenuOption>();
                foreach (TargetLanguage lang in Enum.GetValues(typeof(TargetLanguage)))
                {
                    TargetLanguage selectedLanguage = lang;
                    options.Add(new FloatMenuOption(GetLangLabel(lang), () =>
                    {
                        Settings.CloudTargetLang = selectedLanguage;
                        _cachedCloudDisplayMods = null;
                    }));
                }
                Find.WindowStack.Add(new FloatMenu(options));
            }

            if (AutoTranslatorSettings.IsFetchingCloud && AutoTranslatorSettings.CloudFetchStartedUtcTicks > 0)
            {
                double elapsedSeconds = (DateTime.UtcNow.Ticks - AutoTranslatorSettings.CloudFetchStartedUtcTicks) / (double)TimeSpan.TicksPerSecond;
                if (elapsedSeconds > 180.0)
                {
                    AutoTranslatorSettings.CloudFetchGeneration++;
                    AutoTranslatorSettings.IsFetchingCloud = false;
                    AutoTranslatorSettings.CloudFetchStartedUtcTicks = 0;
                    AutoTranslatorSettings.CloudConnectionFailed = true;
                    AutoTranslatorSettings.HasFetchedCloudThisSession = false;
                    Verse.Log.Warning("[ATC Cloud] Registry fetch UI watchdog released a stuck cloud fetch state.");
                }
            }

            if (AutoTranslatorSettings.IsFetchingCloud)
            {
                GUI.color = Color.yellow;
                Widgets.Label(new Rect(headerRow.x + 310f, headerRow.y + 5f, headerRow.width - 310f, 24f),
                    "ATC_Cloud_Fetching".Translate());
                GUI.color = Color.white;
            }
            else
            {
                if (WorkflowUiStyle.Button(new Rect(headerRow.xMax - 150f, headerRow.y, 150f, headerRow.height),
                        "ATC_Cloud_Refresh".Translate(), WorkflowButtonStyle.Quiet))
                {
                    StartCloudRegistryFetch();
                }
            }

            l.Gap(6f);
            Rect scopeRow = l.GetRect(32f);
            Widgets.Label(new Rect(scopeRow.x, scopeRow.y + 6f, 90f, 24f), WfText("下载范围", "Scope"));
            GUI.color = WorkflowUiStyle.MutedText;
            Widgets.Label(new Rect(scopeRow.x + 90f, scopeRow.y + 6f, 250f, 24f),
                WfText("已加载 Mod / DLC", "Loaded mods / DLC"));
            GUI.color = Color.white;

            l.Gap(6f);
            DrawCloudListTypeFilters(l);

            l.Gap(6f);
            Rect downloadRow = l.GetRect(34f);
            const float downloadButtonGap = 10f;
            float downloadButtonWidth = Mathf.Clamp(
                (downloadRow.width - downloadButtonGap * 2f) / 3f, 180f, 250f);
            Rect bestAvailableRect = new Rect(
                downloadRow.x, downloadRow.y, downloadButtonWidth, 32f);
            Rect downloadOptionsRect = new Rect(
                bestAvailableRect.xMax + downloadButtonGap, downloadRow.y,
                downloadButtonWidth, 32f);
            Rect exclusionsRect = new Rect(
                downloadOptionsRect.xMax + downloadButtonGap, downloadRow.y,
                downloadButtonWidth, 32f);
            if (!AutoTranslatorSettings.IsFetchingCloud)
            {
                GUI.color = new Color(0.45f, 1f, 0.65f);
                if (WorkflowUiStyle.Button(bestAvailableRect, WfText("一键下载最佳可用译文", "Download best available"),
                        WorkflowButtonStyle.Primary))
                {
                    ExecuteBatchDownload("Best");
                }
                GUI.color = Color.white;
                if (Mouse.IsOver(bestAvailableRect))
                {
                    TooltipHandler.TipRegion(bestAvailableRect,
                        WfText("按当前范围批量下载。云端选择顺序：汉化组精翻 > 人工精翻 > AI 译文；同类取最新版本。本地手动翻译保持最高优先级。",
                            "Batch download for the current scope. Cloud priority: translation-group curated > human-curated > AI; newest wins within the same type. Local manual translations remain highest priority."));
                }

                string optionsLabel = AutoTranslatorSettings.CloudDownloadOptionsExpanded
                    ? WfText("收起其他下载方式", "Hide other download methods")
                    : WfText("其他下载方式", "Other download methods");
                if (WorkflowUiStyle.Button(downloadOptionsRect, optionsLabel, WorkflowButtonStyle.Quiet))
                {
                    AutoTranslatorSettings.CloudDownloadOptionsExpanded =
                        !AutoTranslatorSettings.CloudDownloadOptionsExpanded;
                }
            }
            if (WorkflowUiStyle.Button(exclusionsRect,
                    WfText("管理下载排除列表", "Download exclusions"), WorkflowButtonStyle.Quiet))
            {
                Find.WindowStack.Add(new Window_ModBlacklists(cloudDownloadOnly: true));
            }

            if (AutoTranslatorSettings.CloudDownloadOptionsExpanded && !AutoTranslatorSettings.IsFetchingCloud)
            {
                Rect downloadOptionsRow = l.GetRect(32f);
                if (WorkflowUiStyle.Button(new Rect(
                            downloadOptionsRow.x, downloadOptionsRow.y, downloadButtonWidth, 30f),
                        WfText("仅下载汉化组精翻", "Translation-group only"), WorkflowButtonStyle.Quiet))
                    ExecuteBatchDownload("Official_Group");
                if (WorkflowUiStyle.Button(new Rect(
                            downloadOptionsRow.x + downloadButtonWidth + downloadButtonGap,
                            downloadOptionsRow.y, downloadButtonWidth, 30f),
                        WfText("仅下载人工精翻", "Human-curated only"), WorkflowButtonStyle.Quiet))
                    ExecuteBatchDownload("Manual");
                if (WorkflowUiStyle.Button(new Rect(
                            downloadOptionsRow.x + (downloadButtonWidth + downloadButtonGap) * 2f,
                            downloadOptionsRow.y, downloadButtonWidth, 30f),
                        WfText("仅下载 AI 译文", "AI only"), WorkflowButtonStyle.Quiet))
                    ExecuteBatchDownload("AI_Auto");
            }

            l.Gap(5f);
            Rect orderRow = l.GetRect(30f);
            Rect orderButtonRect = new Rect(
                orderRow.x, orderRow.y, downloadButtonWidth, orderRow.height);
            if (WorkflowUiStyle.Button(orderButtonRect,
                    "ATC_Cloud_OrderButton".Translate(), WorkflowButtonStyle.Quiet))
            {
                Find.WindowStack.Add(new Window_CloudTranslationOrder());
            }
            TooltipHandler.TipRegion(orderButtonRect, "ATC_Cloud_OrderButtonTip".Translate());

            float parallelLabelX = orderRow.x + downloadButtonWidth + downloadButtonGap;
            Widgets.Label(new Rect(parallelLabelX, orderRow.y + 4f, 190f, 24f),
                "ATC_Cloud_ParallelDownloads".Translate(Settings.CloudBatchDownloadConcurrency));
            int parallelDownloads = Mathf.RoundToInt(Widgets.HorizontalSlider(
                new Rect(parallelLabelX + 195f, orderRow.y + 7f,
                    Mathf.Max(100f, orderRow.xMax - parallelLabelX - 195f), 20f),
                Settings.CloudBatchDownloadConcurrency,
                1f,
                4f,
                false,
                null,
                "1",
                "4",
                1f));
            if (parallelDownloads != Settings.CloudBatchDownloadConcurrency)
            {
                Settings.CloudBatchDownloadConcurrency = parallelDownloads;
                WriteSettings();
            }

            l.Gap(8f);
            Rect contributionHeader = l.GetRect(30f);
            string contributionLabel = AutoTranslatorSettings.CloudContributionExpanded
                ? WfText("贡献译文　▲", "Contribute translations  ▲")
                : WfText("贡献译文　▼", "Contribute translations  ▼");
            if (WorkflowUiStyle.Button(contributionHeader, contributionLabel, WorkflowButtonStyle.Quiet))
            {
                AutoTranslatorSettings.CloudContributionExpanded =
                    !AutoTranslatorSettings.CloudContributionExpanded;
            }

            if (AutoTranslatorSettings.CloudContributionExpanded)
            {
                l.Gap(4f);
                Rect userRow = l.GetRect(26f);
                Widgets.Label(new Rect(userRow.x, userRow.y + 3f, 90f, 24f), "ATC_Cloud_Nickname".Translate());
                Settings.CloudNickname = Widgets.TextField(new Rect(userRow.x + 90f, userRow.y, 180f, 24f), Settings.CloudNickname);
                if (WorkflowUiStyle.Button(new Rect(userRow.x + 290f, userRow.y, 160f, 26f),
                        AutoTranslatorSettings.CloudShowMineOnly
                            ? WfText("显示全部 Mod", "Show all mods")
                            : WfText("我的上传记录", "My uploads"), WorkflowButtonStyle.Quiet))
                {
                    AutoTranslatorSettings.CloudShowMineOnly = !AutoTranslatorSettings.CloudShowMineOnly;
                    _cachedCloudDisplayMods = null;
                }

                Rect uploadButtons = l.GetRect(32f);
                if (WorkflowUiStyle.Button(new Rect(uploadButtons.x, uploadButtons.y, 170f, 30f),
                        "ATC_Cloud_Btn_OpenWorkspace".Translate(), WorkflowButtonStyle.Quiet))
                {
                    string packPath = AutoTranslatorScanner.GetLocalPackPath();
                    string workspaceRoot = System.IO.Path.Combine(packPath, "Upload_Workspace");
                    System.IO.Directory.CreateDirectory(workspaceRoot);
                    UnityEngine.Application.OpenURL("file://" + workspaceRoot);
                }
                if (WorkflowUiStyle.Button(new Rect(uploadButtons.x + 180f, uploadButtons.y, 190f, 30f),
                        WfText("上传贡献工作区", "Upload workspace"), WorkflowButtonStyle.Primary))
                    ExecuteBatchUpload(CloudBatchUploadSource.Workspace);
                if (WorkflowUiStyle.Button(new Rect(uploadButtons.x + 380f, uploadButtons.y, 190f, 30f),
                        WfText("上传当前本地译文", "Upload local pack"), WorkflowButtonStyle.Primary))
                    ExecuteBatchUpload(CloudBatchUploadSource.LocalPack);

                Rect typeRow = l.GetRect(30f);
                bool hasPrivilegeCode = !string.IsNullOrWhiteSpace(Settings.CloudAdminToken);
                string currentUploadType = NormalizeCloudUploadType(Settings.CloudUploadType, hasPrivilegeCode);
                if (Settings.CloudUploadType != currentUploadType)
                {
                    Settings.CloudUploadType = currentUploadType;
                    WriteSettings();
                }
                Widgets.Label(new Rect(typeRow.x, typeRow.y + 5f, 100f, 24f), WfText("上传标记", "Label"));
                if (DrawUploadTypeOption(new Rect(typeRow.x + 100f, typeRow.y, 170f, 30f),
                        "ATC_Cloud_Type_AI".Translate().ToString(), Settings.CloudUploadType == "AI_Auto"))
                {
                    Settings.CloudUploadType = "AI_Auto";
                    WriteSettings();
                }
                if (DrawUploadTypeOption(new Rect(typeRow.x + 280f, typeRow.y, 170f, 30f),
                        WfText("人工精翻", "Human-curated"), Settings.CloudUploadType == "Manual"))
                {
                    Settings.CloudUploadType = "Manual";
                    WriteSettings();
                }
                Widgets.Label(l.GetRect(42f), WfText(
                    "按本次上传内容逐个 Mod 检查：AI 来源超过 50% 不能标记人工精翻；来源不明和外部导入不计为 AI。标记不会改变译文，也不会调用 AI。",
                    "Each mod is checked against this upload: over 50% AI cannot be labeled Human-curated. Unknown and imported sources are not AI. Labels do not change text or call AI."));

                string adminLabel = AutoTranslatorSettings.CloudAdminExpanded
                    ? WfText("管理员模式　▲", "Administrator  ▲")
                    : WfText("管理员模式　▼", "Administrator  ▼");
                if (WorkflowUiStyle.Button(l.GetRect(28f), adminLabel, WorkflowButtonStyle.Link))
                    AutoTranslatorSettings.CloudAdminExpanded = !AutoTranslatorSettings.CloudAdminExpanded;
                if (AutoTranslatorSettings.CloudAdminExpanded)
                {
                    Rect adminRow = l.GetRect(30f);
                    Widgets.Label(new Rect(adminRow.x, adminRow.y + 4f, 100f, 24f), "ATC_Cloud_AdminKey".Translate());
                    Settings.CloudAdminToken = GUI.PasswordField(
                        new Rect(adminRow.x + 100f, adminRow.y, 220f, 24f), Settings.CloudAdminToken, '*');
                    hasPrivilegeCode = !string.IsNullOrWhiteSpace(Settings.CloudAdminToken);
                    if (hasPrivilegeCode && DrawUploadTypeOption(new Rect(adminRow.x + 340f, adminRow.y, 190f, 30f),
                            "ATC_Type_Official".Translate().ToString(), Settings.CloudUploadType == "Official_Group"))
                    {
                        Settings.CloudUploadType = "Official_Group";
                        WriteSettings();
                    }
                }

                Widgets.Label(l.GetRect(22f), "ATC_Cloud_BatchUploadLogLabel".Translate());
                Rect batchLogRect = l.GetRect(54f);
                Settings.CloudBatchUploadLog = Widgets.TextArea(batchLogRect, Settings.CloudBatchUploadLog ?? "");
                if (string.IsNullOrEmpty(Settings.CloudBatchUploadLog))
                {
                    GUI.color = Color.gray;
                    Widgets.Label(new Rect(batchLogRect.x + 5f, batchLogRect.y + 2f,
                        batchLogRect.width - 10f, batchLogRect.height), "ATC_Cloud_BatchUploadLogHint".Translate());
                    GUI.color = Color.white;
                }
            }

            l.Gap(10f);
            Widgets.DrawLineHorizontal(0, l.CurHeight, viewRect.width);
            l.Gap(10f);
        }

        private const int CloudListTypeTranslationGroup = 1;
        private const int CloudListTypeHumanCurated = 2;
        private const int CloudListTypeAi = 4;
        private const int CloudListTypeAll = CloudListTypeTranslationGroup | CloudListTypeHumanCurated | CloudListTypeAi;

        private static void DrawCloudListTypeFilters(Listing_Standard l)
        {
            Rect row = l.GetRect(32f);
            Widgets.Label(new Rect(row.x, row.y + 6f, 110f, 24f),
                WfText("线上译文类型", "Online type"));

            int mask = NormalizeCloudListTypeMask(AutoTranslatorSettings.CloudListTranslationTypeMask);
            if (DrawCloudListTypeFilterOption(new Rect(row.x + 110f, row.y, 150f, 30f),
                    WfText("全部线上译文", "All online"), mask == CloudListTypeAll))
            {
                SetCloudListTypeMask(CloudListTypeAll);
            }
            if (DrawCloudListTypeFilterOption(new Rect(row.x + 270f, row.y, 160f, 30f),
                    WfText("汉化组精翻", "Translation group"),
                    mask != CloudListTypeAll && (mask & CloudListTypeTranslationGroup) != 0))
            {
                ToggleCloudListTypeMask(CloudListTypeTranslationGroup);
            }
            if (DrawCloudListTypeFilterOption(new Rect(row.x + 440f, row.y, 150f, 30f),
                    WfText("人工精翻", "Human-curated"),
                    mask != CloudListTypeAll && (mask & CloudListTypeHumanCurated) != 0))
            {
                ToggleCloudListTypeMask(CloudListTypeHumanCurated);
            }
            if (DrawCloudListTypeFilterOption(new Rect(row.x + 600f, row.y, 130f, 30f),
                    WfText("AI 译文", "AI"),
                    mask != CloudListTypeAll && (mask & CloudListTypeAi) != 0))
            {
                ToggleCloudListTypeMask(CloudListTypeAi);
            }
        }

        private static bool DrawCloudListTypeFilterOption(Rect rect, string label, bool selected)
        {
            return WorkflowUiStyle.Button(
                rect,
                (selected ? "✓ " : string.Empty) + label,
                selected ? WorkflowButtonStyle.Primary : WorkflowButtonStyle.Quiet);
        }

        private static void ToggleCloudListTypeMask(int bit)
        {
            int current = NormalizeCloudListTypeMask(AutoTranslatorSettings.CloudListTranslationTypeMask);
            int next = current == CloudListTypeAll ? bit : current ^ bit;
            SetCloudListTypeMask(next == 0 ? CloudListTypeAll : next);
        }

        private static void SetCloudListTypeMask(int mask)
        {
            AutoTranslatorSettings.CloudListTranslationTypeMask = NormalizeCloudListTypeMask(mask);
            _cachedCloudDisplayMods = null;
            _cachedOwnCloudRecords = null;
            AutoTranslatorSettings.SelectedCloudVersion.Clear();
            AutoTranslatorSettings.mainScrollPos = Vector2.zero;
        }

        private static int NormalizeCloudListTypeMask(int mask)
        {
            int normalized = mask & CloudListTypeAll;
            return normalized == 0 ? CloudListTypeAll : normalized;
        }

        private static bool CloudRecordMatchesListTypeFilter(CloudModRecord record)
        {
            if (record == null) return false;
            int recordMask;
            if (string.Equals(record.TranslationType, "Official_Group", StringComparison.OrdinalIgnoreCase))
                recordMask = CloudListTypeTranslationGroup;
            else if (string.Equals(record.TranslationType, "Manual", StringComparison.OrdinalIgnoreCase))
                recordMask = CloudListTypeHumanCurated;
            else if (string.Equals(record.TranslationType, "AI_Auto", StringComparison.OrdinalIgnoreCase))
                recordMask = CloudListTypeAi;
            else
                return false;

            return (NormalizeCloudListTypeMask(AutoTranslatorSettings.CloudListTranslationTypeMask) & recordMask) != 0;
        }

        private static string GetCloudListTypeFilterLabel()
        {
            int mask = NormalizeCloudListTypeMask(AutoTranslatorSettings.CloudListTranslationTypeMask);
            if (mask == CloudListTypeAll) return WfText("全部线上译文", "All online translations");

            List<string> labels = new List<string>();
            if ((mask & CloudListTypeTranslationGroup) != 0)
                labels.Add(WfText("汉化组精翻", "Translation group"));
            if ((mask & CloudListTypeHumanCurated) != 0)
                labels.Add(WfText("人工精翻", "Human-curated"));
            if ((mask & CloudListTypeAi) != 0)
                labels.Add(WfText("AI 译文", "AI"));
            return WfText("线上译文：", "Online: ") + string.Join("、", labels);
        }

        // 這個方法負責繪製 上傳TypeOption 介面。
        // EN: This method draws upload type option.
        private static bool DrawUploadTypeOption(Rect rect, string label, bool selected)
        {
            Color oldColor = GUI.color;
            TextAnchor oldAnchor = Text.Anchor;
            bool oldWordWrap = Text.WordWrap;

            if (selected)
            {
                Widgets.DrawBoxSolid(rect, WorkflowUiStyle.Selection);
            }
            else if (Mouse.IsOver(rect))
            {
                Widgets.DrawHighlight(rect);
            }

            GUI.color = selected ? new Color(0.75f, 1f, 0.75f) : new Color(0.75f, 0.75f, 0.75f);
            WorkflowUiStyle.DrawBorder(rect, selected
                ? WorkflowUiStyle.GoodText
                : new Color(0.28f, 0.31f, 0.33f));
            GUI.color = Color.white;

            const float radioSize = 24f;
            float radioY = rect.y + ((rect.height - radioSize) / 2f);
            bool clicked = Widgets.RadioButton(new Vector2(rect.x + 6f, radioY), selected, false) || Widgets.ButtonInvisible(rect);

            Rect labelRect = new Rect(rect.x + 36f, rect.y, rect.width - 40f, rect.height);
            Text.Anchor = TextAnchor.MiddleLeft;
            Text.WordWrap = false;
            Widgets.Label(labelRect, label);
            Text.WordWrap = oldWordWrap;
            Text.Anchor = oldAnchor;
            GUI.color = oldColor;

            return clicked && !selected;
        }

        // 這個方法負責建立 雲端Lookup 所需資料。
        // EN: This method builds cloud lookup.
        private Dictionary<string, List<CloudModRecord>> BuildCloudLookup(string targetLangFolder)
        {


            if (_cachedCloudLookup == null ||
                _lastCloudRegistryCount != AutoTranslatorSettings.CloudRegistry.Count ||
                _lastCloudRegistryGeneration != AutoTranslatorSettings.CloudFetchGeneration ||
                _lastCloudLangFolder != targetLangFolder)
            {
                _cachedCloudLookup = new Dictionary<string, List<CloudModRecord>>(StringComparer.OrdinalIgnoreCase);
                foreach (var record in AutoTranslatorSettings.CloudRegistry)
                {
                    if (record.Language == targetLangFolder)
                    {
                        if (!_cachedCloudLookup.ContainsKey(record.PackageId))
                        {
                            _cachedCloudLookup[record.PackageId] = new List<CloudModRecord>();
                        }
                        _cachedCloudLookup[record.PackageId].Add(record);
                    }
                }


                foreach (var key in _cachedCloudLookup.Keys.ToList())
                {
                    _cachedCloudLookup[key] = _cachedCloudLookup[key]
                        .OrderByDescending(GetBatchRecordPriority)
                        .ThenByDescending(GetBatchRecordUpdatedAt)
                        .ToList();
                }

                _lastCloudRegistryCount = AutoTranslatorSettings.CloudRegistry.Count;
                _lastCloudRegistryGeneration = AutoTranslatorSettings.CloudFetchGeneration;
                _lastCloudLangFolder = targetLangFolder;
            }
            var cloudLookup = _cachedCloudLookup;
            return _cachedCloudLookup;
        }

        // 這個方法負責繪製 雲端模組Row 介面。
        // EN: This method draws cloud mod row.
        private void DrawCloudModRow(ModMetaData mod, Rect rowRect, Dictionary<string, List<CloudModRecord>> cloudLookup, string targetLangFolder)
        {
                Widgets.DrawHighlightIfMouseover(rowRect);
                bool isOfficialGamePackage = AutoTranslatorScanner.IsOfficialBaseGameOrDlcPackage(mod.PackageId);
                bool downloadBlacklisted = AutoTranslatorMod.Settings.IsCloudDownloadBlacklisted(mod.PackageId);


                List<CloudModRecord> allVersions;
                if (cloudLookup.TryGetValue(mod.PackageId, out var foundList))
                {
                    allVersions = foundList.Where(CloudRecordMatchesListTypeFilter).ToList();
                }
                else
                {
                    allVersions = EmptyCloudRecords;
                }

                AutoTranslatorSettings.SelectedCloudVersion.TryGetValue(mod.PackageId, out CloudModRecord cloudRecord);

                if (cloudRecord == null || !allVersions.Any(v => v.RecordId == cloudRecord.RecordId))
                {
                    cloudRecord = allVersions.FirstOrDefault();
                    if (cloudRecord != null)
                    {
                        AutoTranslatorSettings.SelectedCloudVersion[mod.PackageId] = cloudRecord;
                    }
                    else
                    {
                        AutoTranslatorSettings.SelectedCloudVersion.Remove(mod.PackageId);
                    }
                }

                string statusText = "";
                Color statusColor = Color.white;
                bool canDownload = false;

                if (cloudRecord == null)
                {
                    int singleCount = isOfficialGamePackage
                        ? GetCachedSingleCorrectionCount(mod.PackageId, targetLangFolder)
                        : 0;
                    if (singleCount > 0)
                    {
                        statusText = "ATC_Corrections_StatusCount".Translate(singleCount).ToString();
                        statusColor = new Color(0.75f, 0.95f, 1f);
                    }
                    else if (singleCount == 0)
                    {
                        statusText = isOfficialGamePackage ? "ATC_Corrections_StatusOfficial".Translate().ToString() : "ATC_Cloud_Status_NoCloud".Translate().ToString();
                        statusColor = isOfficialGamePackage ? new Color(0.75f, 0.75f, 0.75f) : Color.gray;
                    }
                    else
                    {
                        statusText = "ATC_Corrections_StatusChecking".Translate().ToString();
                        statusColor = new Color(0.75f, 0.75f, 0.75f);
                    }
                }
                else if (IsTranslationGroupCloudRecord(cloudRecord))
                {
                    statusText = "ATC_Cloud_Status_Official".Translate();
                    statusColor = new Color(1f, 0.8f, 0.2f);
                    canDownload = true;
                }
                else if (string.Equals(cloudRecord.TranslationType, "Manual", StringComparison.OrdinalIgnoreCase))
                {
                    statusText = "ATC_Cloud_Status_Manual".Translate();
                    statusColor = new Color(0.4f, 1f, 0.4f);
                    canDownload = true;
                }
                else if (IsLegacyUntaggedCloudAiRecord(cloudRecord))
                {
                    statusText = "ATC_Cloud_Status_LegacyAI".Translate();
                    statusColor = new Color(0.8f, 0.75f, 0.55f);
                    canDownload = true;
                }
                else
                {
                    statusText = "ATC_Cloud_Status_Latest".Translate();
                    statusColor = new Color(0.4f, 0.8f, 1f);
                    canDownload = true;
                }

                if (cloudRecord != null && cloudRecord.IsVerified)
                    statusText += WfText(" · 已审核", " · Verified");

                if (downloadBlacklisted)
                {
                    statusText = "ATC_Blacklist_DownloadBlockedStatus".Translate().ToString();
                    statusColor = new Color(0.8f, 0.55f, 0.55f);
                    canDownload = false;
                }


                Text.Font = GameFont.Small;
                float cursorX = rowRect.xMax - 5f;

                const float detailsWidth = 90f;
                cursorX -= detailsWidth;
                Rect detailsBtn = new Rect(cursorX, rowRect.y + 5f, detailsWidth - 5f, 30f);
                if (WorkflowUiStyle.Button(detailsBtn, WfText("详情", "Details"),
                        WorkflowButtonStyle.Quiet, true, GameFont.Tiny))
                {
                    OpenCloudRowDetails(mod, targetLangFolder, cloudRecord, isOfficialGamePackage);
                }
                cursorX -= 5f;

                if (!isOfficialGamePackage && canDownload)
                {
                    float dlWidth = 85f;
                    cursorX -= dlWidth;
                    Rect downloadBtn = new Rect(cursorX, rowRect.y + 5f, dlWidth - 5f, 30f);
                    GUI.color = new Color(0.6f, 1f, 0.6f);
                    if (WorkflowUiStyle.Button(downloadBtn, "ATC_Cloud_Btn_Download".Translate(),
                            WorkflowButtonStyle.Primary, true, GameFont.Tiny))
                    {
                        CloudModRecord targetRecord = cloudRecord;
                        string targetLanguageLabel = GetLangLabel(Settings.CloudTargetLang);
                        string message = WfText(
                            "即将从公共云端下载译文，并替换该 Mod 在 ATC 生成包中的现有本地译文文件。\n\n" +
                            "Mod：" + mod.Name + "\n" +
                            "云端来源：" + GetCloudTranslationTypeLabel(targetRecord) + "\n" +
                            "云端版本：v" + targetRecord.LatestVersion + "\n" +
                            "目标语言：" + targetLanguageLabel + "（" + targetLangFolder + "）\n\n" +
                            "通过译文编辑器保存、已记录到 V4 数据库的“手动翻译”会在下载后重新恢复。" +
                            "直接修改 XML 但尚未点击“刷新状态”的内容可能被覆盖。\n\n" +
                            "建议先返回翻译工作台刷新状态。是否仍要开始下载？",
                            "Cloud translations will replace the existing local translation files for this mod in the ATC generated pack.\n\n" +
                            "Mod: " + mod.Name + "\n" +
                            "Cloud source: " + GetCloudTranslationTypeLabel(targetRecord) + "\n" +
                            "Cloud version: v" + targetRecord.LatestVersion + "\n" +
                            "Target language: " + targetLanguageLabel + " (" + targetLangFolder + ")\n\n" +
                            "Manual translations saved through the translation editor and recorded in the V4 database will be restored after download. " +
                            "Direct XML edits that have not been synchronized with Refresh Status may be overwritten.\n\n" +
                            "Refreshing status in Translation Workbench first is recommended. Start download anyway?");
                        Find.WindowStack.Add(new Window_AtcDialog(
                            message,
                            WfText("确认下载", "Download"),
                            () => StartPreparedBatchDownload(
                                new List<BatchDownloadItem>
                                {
                                    new BatchDownloadItem
                                    {
                                        PackageId = mod.PackageId,
                                        DisplayName = mod.Name,
                                        Record = targetRecord
                                    }
                                },
                                targetLangFolder),
                            WfText("取消", "Cancel"),
                            null,
                            WfText("确认云端下载", "Confirm cloud download")));
                    }
                    GUI.color = Color.white;
                    cursorX -= 5f;
                }


                if (cloudRecord != null)
                {
                    float dropWidth = 140f;
                    cursorX -= dropWidth;
                    Rect verDropRect = new Rect(cursorX, rowRect.y + 5f, dropWidth - 5f, 30f);

                    string mergedTag = cloudRecord.IsSmartMerged ? "ATC_Cloud_SmartMerged".Translate().ToString() : "";


                    string currentLocType = GetCloudTranslationTypeLabel(cloudRecord);

                    string verLabel = $"v{cloudRecord.LatestVersion} ({currentLocType}){mergedTag}";

                    if (WorkflowUiStyle.Button(verDropRect, verLabel,
                            WorkflowButtonStyle.Quiet, true, GameFont.Tiny))
                    {
                        List<FloatMenuOption> verOptions = new List<FloatMenuOption>();
                        foreach (var v in allVersions)
                        {
                            string mTag = v.IsSmartMerged ? "ATC_Cloud_SmartMerged".Translate().ToString() : "";
                            string vLocType = GetCloudTranslationTypeLabel(v);
                            string uploader = string.IsNullOrWhiteSpace(v.Author)
                                ? WfText("未知", "Unknown")
                                : v.Author.Trim();

                            string optLabel = $"v{v.LatestVersion} ({vLocType}){mTag} · " +
                                WfText("上传者：", "Uploader: ") + uploader +
                                $" · {v.LastUpdated:yyyy-MM-dd}";
                            verOptions.Add(new FloatMenuOption(optLabel, () => { AutoTranslatorSettings.SelectedCloudVersion[mod.PackageId] = v; }));
                        }
                        Find.WindowStack.Add(new FloatMenu(verOptions));
                    }

                    if (Mouse.IsOver(verDropRect))
                    {
                        string yesStr = "ATC_Cloud_YesWithCount".Translate(cloudRecord.MergedAiCount);
                        string noStr = "ATC_Cloud_No".Translate();
                        string mergeStatus = cloudRecord.IsSmartMerged ? yesStr : noStr;
                        string logDisplay = string.IsNullOrWhiteSpace(cloudRecord.UpdateLog) ? "ATC_Cloud_NoLog".Translate().ToString() : cloudRecord.UpdateLog;


                        string currentUploader = string.IsNullOrWhiteSpace(cloudRecord.Author)
                            ? WfText("未知", "Unknown")
                            : cloudRecord.Author.Trim();
                        string tipStr = WfText("上传者：", "Uploader: ") + currentUploader + "\n" +
                                        "ATC_Cloud_UploadDate".Translate(cloudRecord.LastUpdated.ToString("yyyy-MM-dd HH:mm")) + "\n" +
                                        "ATC_Cloud_TransType".Translate(currentLocType) + "\n" +
                                        "ATC_Cloud_IsSmartMerged".Translate(mergeStatus) + "\n" +
                                        "📜 " + "ATC_Cloud_LogTitle".Translate() + ": " + logDisplay;
                        if (IsLegacyUntaggedCloudAiRecord(cloudRecord))
                        {
                            tipStr += "\n" + "ATC_Cloud_LegacyAiTooltip".Translate();
                        }
                        TooltipHandler.TipRegion(verDropRect, tipStr);
                    }
                }

                float leftSpace = cursorX - rowRect.x - 10f;
                Rect nameRect = new Rect(rowRect.x + 5f, rowRect.y + 2f, leftSpace, 20f);
                Rect statusRect = new Rect(rowRect.x + 5f, rowRect.y + 22f, leftSpace, 18f);

                Text.Font = GameFont.Small;


                Text.WordWrap = false;
                Widgets.Label(nameRect, mod.Name);
                Text.WordWrap = true;

                if (Mouse.IsOver(nameRect))
                {
                    TooltipHandler.TipRegion(nameRect, mod.Name);
                }

                Text.Font = GameFont.Tiny;
                GUI.color = statusColor;
                Widgets.Label(statusRect, statusText);
                GUI.color = Color.white;
        }

        private void OpenCloudRowDetails(
            ModMetaData mod,
            string targetLangFolder,
            CloudModRecord cloudRecord,
            bool isOfficialGamePackage)
        {
            List<FloatMenuOption> options = new List<FloatMenuOption>
            {
                new FloatMenuOption(WfText("查看纠错记录", "View corrections"), () =>
                    Find.WindowStack.Add(new Window_AppliedCorrections(mod, targetLangFolder)))
            };

            if (!isOfficialGamePackage)
            {
                bool downloadBlocked = Settings.IsCloudDownloadBlacklisted(mod.PackageId);
                options.Add(new FloatMenuOption(
                    downloadBlocked
                        ? WfText("移出云端下载排除列表", "Allow cloud downloads")
                        : WfText("加入云端下载排除列表", "Exclude from cloud downloads"),
                    () =>
                    {
                        Settings.SetCloudDownloadBlacklisted(mod.PackageId, !downloadBlocked);
                        WriteSettings();
                    }));

                options.Add(new FloatMenuOption(WfText("打开贡献工作区", "Open contribution workspace"), () =>
                {
                    string packPath = AutoTranslatorScanner.GetLocalPackPath();
                    string workspaceDir = Path.Combine(packPath, "Upload_Workspace", mod.PackageId, targetLangFolder);
                    Directory.CreateDirectory(workspaceDir);
                    UnityEngine.Application.OpenURL("file://" + workspaceDir);
                }));

                options.Add(new FloatMenuOption(WfText("贡献这个 Mod 的译文", "Contribute this mod"), () =>
                {
                    string packPath = AutoTranslatorScanner.GetLocalPackPath();
                    string workspaceDir = Path.Combine(packPath, "Upload_Workspace", mod.PackageId, targetLangFolder);
                    string liveLangDir = Path.Combine(packPath, "Languages", targetLangFolder);
                    bool useWorkspace = Directory.Exists(workspaceDir) &&
                        AutoTranslatorScanner.GetXmlFilesForTranslationCache(
                            workspaceDir, SearchOption.AllDirectories).Count > 0;
                    string sourceDir = useWorkspace ? workspaceDir : liveLangDir;
                    Find.WindowStack.Add(new Window_UploadPreview(
                        mod, targetLangFolder, sourceDir, mod.Name));
                }));

                if (!string.IsNullOrWhiteSpace(Settings.CloudAdminToken) && cloudRecord != null)
                {
                    options.Add(new FloatMenuOption(WfText("管理员：删除这个云端版本", "Admin: delete cloud version"), () =>
                    {
                        string packageId = mod.PackageId;
                        string displayName = mod.Name;
                        string token = Settings.CloudAdminToken;
                        string recordId = cloudRecord.RecordId;
                        AutoTranslatorSettings.CloudUploadTarget = packageId + "_del";
                        Task.Run(async () =>
                        {
                            bool success = await AutoTranslatorCloudClient.DeleteCloudRecordAsync(
                                packageId, targetLangFolder, recordId, token);
                            ATC_Dispatcher.RunOnMainThread(() =>
                            {
                                AutoTranslatorSettings.CloudUploadTarget = string.Empty;
                                if (success)
                                {
                                    Messages.Message("ATC_Msg_DeleteCloudSuccess".Translate(displayName),
                                        MessageTypeDefOf.PositiveEvent, false);
                                    AutoTranslatorSettings.HasFetchedCloudThisSession = false;
                                }
                                else
                                {
                                    Messages.Message("ATC_Msg_DeleteCloudFailed".Translate(displayName),
                                        MessageTypeDefOf.RejectInput, false);
                                }
                            });
                        });
                    }));
                }
            }

            Find.WindowStack.Add(new FloatMenu(options));
        }

        private void DrawOwnCloudRecordRow(CloudModRecord record, Rect rowRect, Dictionary<string, List<CloudModRecord>> cloudLookup, string targetLangFolder)
        {
            if (record == null) return;

            ModMetaData localMod = null;
            GetCloudLocalModMap().TryGetValue(record.PackageId ?? "", out localMod);

            if (localMod != null)
            {
                AutoTranslatorSettings.SelectedCloudVersion[localMod.PackageId] = record;
                DrawCloudModRow(localMod, rowRect, cloudLookup, targetLangFolder);
                return;
            }

            Widgets.DrawHighlightIfMouseover(rowRect);

            float btnWidth = 85f;
            float cursorX = rowRect.xMax - 5f;
            if (!string.IsNullOrEmpty(Settings.CloudAdminToken))
            {
                cursorX -= btnWidth;
                Rect deleteCloudBtn = new Rect(cursorX, rowRect.y + 5f, btnWidth - 5f, 30f);
                string deleteKey = record.PackageId + "_" + record.RecordId + "_del";
                if (AutoTranslatorSettings.CloudUploadTarget == deleteKey)
                {
                    GUI.color = Color.red;
                    Text.Anchor = TextAnchor.MiddleCenter;
                    Widgets.Label(deleteCloudBtn, "ATC_Cloud_Deleting".Translate());
                    Text.Anchor = TextAnchor.UpperLeft;
                }
                else
                {
                    GUI.color = new Color(1f, 0.3f, 0.3f);
                    if (WorkflowUiStyle.Button(deleteCloudBtn, "ATC_Cloud_Btn_DeleteCloud".Translate(),
                            WorkflowButtonStyle.Stop, true, GameFont.Tiny))
                    {
                        AutoTranslatorSettings.CloudUploadTarget = deleteKey;

                        string pid = record.PackageId;
                        string lang = targetLangFolder;
                        string token = Settings.CloudAdminToken;
                        string recId = record.RecordId;
                        string displayName = string.IsNullOrWhiteSpace(record.ModName) ? record.PackageId : record.ModName;
                        System.Threading.Tasks.Task.Run(async () => {
                            bool success = await AutoTranslatorCloudClient.DeleteCloudRecordAsync(pid, lang, recId, token);
                            ATC_Dispatcher.RunOnMainThread(() => {
                                AutoTranslatorSettings.CloudUploadTarget = "";
                                if (success)
                                {
                                    Messages.Message("ATC_Msg_DeleteCloudSuccess".Translate(displayName), MessageTypeDefOf.PositiveEvent, false);
                                    AutoTranslatorSettings.HasFetchedCloudThisSession = false;
                                }
                                else
                                {
                                    Messages.Message("ATC_Msg_DeleteCloudFailed".Translate(displayName), MessageTypeDefOf.RejectInput, false);
                                }
                            });
                        });
                    }
                }
                GUI.color = Color.white;
                cursorX -= 5f;
            }

            float leftSpace = cursorX - rowRect.x - 10f;
            Rect nameRect = new Rect(rowRect.x + 5f, rowRect.y + 2f, leftSpace, 20f);
            Rect statusRect = new Rect(rowRect.x + 5f, rowRect.y + 22f, leftSpace, 18f);

            string display = string.IsNullOrWhiteSpace(record.ModName) ? record.PackageId : record.ModName;
            string currentLocType = GetCloudTranslationTypeLabel(record);

            Text.Font = GameFont.Small;
            Text.WordWrap = false;
            Widgets.Label(nameRect, display);
            Text.WordWrap = true;
            if (Mouse.IsOver(nameRect))
            {
                TooltipHandler.TipRegion(nameRect, display + "\n" + record.PackageId);
            }

            Text.Font = GameFont.Tiny;
            GUI.color = new Color(0.8f, 0.8f, 0.8f);
            Widgets.Label(statusRect, "ATC_Cloud_Status_NotInstalled".Translate(record.LastUpdated.ToString("yyyy-MM-dd HH:mm"), currentLocType));
            GUI.color = Color.white;
        }

        private static Dictionary<string, ModMetaData> GetCloudLocalModMap()
        {
            List<ModMetaData> validMods = GetValidModsCached() ?? new List<ModMetaData>();
            if (_cachedCloudLocalModMap != null &&
                _cachedCloudLocalModMapCount == validMods.Count &&
                _cachedCloudLocalModMapVersion == ValidModsCacheVersion)
            {
                return _cachedCloudLocalModMap;
            }

            var map = new Dictionary<string, ModMetaData>(StringComparer.OrdinalIgnoreCase);
            foreach (ModMetaData mod in validMods)
            {
                if (mod == null || string.IsNullOrEmpty(mod.PackageId)) continue;
                if (!map.ContainsKey(mod.PackageId)) map.Add(mod.PackageId, mod);
            }

            _cachedCloudLocalModMap = map;
            _cachedCloudLocalModMapCount = validMods.Count;
            _cachedCloudLocalModMapVersion = ValidModsCacheVersion;
            return _cachedCloudLocalModMap;
        }

        private static string GetCloudTranslationTypeLabel(CloudModRecord record)
        {
            if (IsLegacyUntaggedCloudAiRecord(record)) return "ATC_Type_LegacyAI".Translate();
            return GetCloudTranslationTypeLabel(record != null ? record.TranslationType : null);
        }

        private static string GetCloudTranslationTypeLabel(string type)
        {
            if (type == "Official_Group") return "ATC_Type_Official".Translate();
            if (type == "Manual") return WfText("人工精翻", "Human-curated");
            if (type == "AI_Auto") return "ATC_Type_AI".Translate();
            return type ?? "";
        }

        private static int GetCachedSingleCorrectionCount(string packageId, string targetLangFolder)
        {
            if (string.IsNullOrWhiteSpace(packageId) || string.IsNullOrWhiteSpace(targetLangFolder)) return 0;
            string cacheKey = packageId.ToLowerInvariant() + "|" + targetLangFolder;
            if (_singleCorrectionCountCache.TryGetValue(cacheKey, out int cached)) return cached;

            if (_singleCorrectionCountFetchInFlight.Add(cacheKey))
            {
                Task.Run(async () =>
                {
                    int count = 0;
                    try
                    {
                        List<AppliedTranslationCorrection> corrections =
                            await AutoTranslatorCloudClient.FetchAppliedCorrectionsAsync(packageId, targetLangFolder);
                        count = corrections != null ? corrections.Count : 0;
                    }
                    catch { }

                    ATC_Dispatcher.RunOnMainThread(() =>
                    {
                        _singleCorrectionCountCache[cacheKey] = count;
                        _singleCorrectionCountFetchInFlight.Remove(cacheKey);
                    });
                });
            }

            return -1;
        }

        private static bool IsLegacyUntaggedCloudAiRecord(CloudModRecord record)
        {
            if (record == null) return false;
            if (!string.Equals(record.TranslationType, "AI_Auto", StringComparison.OrdinalIgnoreCase)) return false;
            if (record.TranslationSourceSchemaVersion > 0) return false;

            string sourceKind = !string.IsNullOrWhiteSpace(record.TranslationSourceKind)
                ? record.TranslationSourceKind
                : record.SourceKind;
            return string.IsNullOrWhiteSpace(sourceKind);
        }
    }
}
