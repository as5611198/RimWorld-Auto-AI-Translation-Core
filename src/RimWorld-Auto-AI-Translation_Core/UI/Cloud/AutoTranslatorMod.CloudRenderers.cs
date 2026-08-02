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

            Rect topBarRect1 = l.GetRect(30f);
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
                Widgets.Label(topBarRect1, "ATC_Cloud_Fetching".Translate());
                GUI.color = Color.white;
            }
            else
            {
                if (Widgets.ButtonText(new Rect(topBarRect1.x, topBarRect1.y, 140f, topBarRect1.height), "ATC_Cloud_Refresh".Translate()))
                {
                    StartCloudRegistryFetch();
                }

                GUI.color = new Color(0.45f, 1f, 0.65f);
                Rect bestAvailableRect = new Rect(topBarRect1.x + 150f, topBarRect1.y, 170f, topBarRect1.height);
                if (Widgets.ButtonText(bestAvailableRect, "ATC_Cloud_Btn_BatchBest".Translate()))
                    ExecuteBatchDownload("Best");
                if (Mouse.IsOver(bestAvailableRect)) TooltipHandler.TipRegion(bestAvailableRect, "ATC_Cloud_Btn_BatchBestTip".Translate());

                GUI.color = new Color(1f, 0.8f, 0.2f);
                if (Widgets.ButtonText(new Rect(topBarRect1.x + 330f, topBarRect1.y, 170f, topBarRect1.height), "ATC_Cloud_Btn_BatchOfficial".Translate()))
                    ExecuteBatchDownload("Official_Group");
                GUI.color = Color.white;

                Rect downloadBarRect2 = l.GetRect(30f);
                GUI.color = new Color(0.7f, 1f, 0.75f);
                if (Widgets.ButtonText(new Rect(downloadBarRect2.x, downloadBarRect2.y, 170f, downloadBarRect2.height), "ATC_Cloud_Btn_BatchManual".Translate()))
                    ExecuteBatchDownload("Manual");

                GUI.color = new Color(0.4f, 0.8f, 1f);
                if (Widgets.ButtonText(new Rect(downloadBarRect2.x + 180f, downloadBarRect2.y, 170f, downloadBarRect2.height), "ATC_Cloud_Btn_BatchAI".Translate()))
                    ExecuteBatchDownload("AI_Auto");
                GUI.color = Color.white;
            }

            l.Gap(5f);


            Rect topBarRect2 = l.GetRect(30f);

            GUI.color = new Color(1f, 0.9f, 0.6f);
            if (Widgets.ButtonText(new Rect(topBarRect2.x, topBarRect2.y, 140f, topBarRect2.height), "ATC_Cloud_Btn_OpenWorkspace".Translate()))
            {
                string packPath = AutoTranslatorScanner.GetLocalPackPath();
                string workspaceRoot = System.IO.Path.Combine(packPath, "Upload_Workspace");
                System.IO.Directory.CreateDirectory(workspaceRoot);
                UnityEngine.Application.OpenURL("file://" + workspaceRoot);
            }

            GUI.color = new Color(1f, 0.6f, 0.2f);
            if (Widgets.ButtonText(new Rect(topBarRect2.x + 150f, topBarRect2.y, 140f, topBarRect2.height), "ATC_Cloud_Btn_BatchUpload".Translate()))
            {
                ExecuteBatchUpload(CloudBatchUploadSource.Workspace);
            }

            GUI.color = new Color(1f, 0.72f, 0.25f);
            if (Widgets.ButtonText(new Rect(topBarRect2.x + 300f, topBarRect2.y, 170f, topBarRect2.height), "ATC_Cloud_Btn_BatchUploadPack".Translate()))
            {
                ExecuteBatchUpload(CloudBatchUploadSource.LocalPack);
            }
            GUI.color = Color.white;

            l.Gap(5f);


            Rect userRow = l.GetRect(24f);
            Widgets.Label(new Rect(userRow.x, userRow.y + 2f, 100f, 24f), "ATC_Cloud_Nickname".Translate());
            Settings.CloudNickname = Widgets.TextField(new Rect(userRow.x + 100f, userRow.y, 150f, 24f), Settings.CloudNickname);

            Widgets.Label(new Rect(userRow.x + 280f, userRow.y + 2f, 100f, 24f), "ATC_Cloud_AdminKey".Translate());
            Settings.CloudAdminToken = GUI.PasswordField(new Rect(userRow.x + 380f, userRow.y, 150f, 24f), Settings.CloudAdminToken, '*');
            bool hasPrivilegeCode = !string.IsNullOrWhiteSpace(Settings.CloudAdminToken);


            l.Gap(5f);
            Rect typeRow = l.GetRect(30f);
            string currentUploadType = NormalizeCloudUploadType(Settings.CloudUploadType, hasPrivilegeCode);
            if (Settings.CloudUploadType != currentUploadType)
            {
                Settings.CloudUploadType = currentUploadType;
                WriteSettings();
            }
            Widgets.Label(new Rect(typeRow.x, typeRow.y + 5f, 120f, 24f), "ATC_Cloud_Type_Select".Translate());

            if (DrawUploadTypeOption(new Rect(typeRow.x + 130f, typeRow.y, 180f, 30f), "ATC_Cloud_Type_AI".Translate().ToString(), Settings.CloudUploadType == "AI_Auto"))
            {
                Settings.CloudUploadType = "AI_Auto";
                WriteSettings();
            }
            if (DrawUploadTypeOption(new Rect(typeRow.x + 320f, typeRow.y, 180f, 30f), "ATC_Cloud_Type_Manual".Translate().ToString(), Settings.CloudUploadType == "Manual"))
            {
                Settings.CloudUploadType = "Manual";
                WriteSettings();
            }
            if (hasPrivilegeCode && DrawUploadTypeOption(new Rect(typeRow.x + 510f, typeRow.y, 180f, 30f), "ATC_Type_Official".Translate().ToString(), Settings.CloudUploadType == "Official_Group"))
            {
                Settings.CloudUploadType = "Official_Group";
                WriteSettings();
            }
            l.Gap(5f);
            Rect batchLogLabelRow = l.GetRect(22f);
            Widgets.Label(batchLogLabelRow, "ATC_Cloud_BatchUploadLogLabel".Translate());

            Rect batchLogRect = l.GetRect(54f);
            Settings.CloudBatchUploadLog = Widgets.TextArea(batchLogRect, Settings.CloudBatchUploadLog ?? "");
            if (string.IsNullOrEmpty(Settings.CloudBatchUploadLog))
            {
                GUI.color = Color.gray;
                Widgets.Label(new Rect(batchLogRect.x + 5f, batchLogRect.y + 2f, batchLogRect.width - 10f, batchLogRect.height), "ATC_Cloud_BatchUploadLogHint".Translate());
                GUI.color = Color.white;
            }

            l.Gap(5f);
            Rect cloudLangRow = l.GetRect(30f);
            Widgets.Label(new Rect(cloudLangRow.x, cloudLangRow.y + 5f, 120f, 24f), "ATC_Cloud_SelectLang".Translate());

            if (Widgets.ButtonText(new Rect(cloudLangRow.x + 130f, cloudLangRow.y, 200f, 30f), "🌐 " + GetLangLabel(Settings.CloudTargetLang)))
            {
                List<FloatMenuOption> options = new List<FloatMenuOption>();
                foreach (TargetLanguage lang in Enum.GetValues(typeof(TargetLanguage)))
                {
                    options.Add(new FloatMenuOption(GetLangLabel(lang), () => {
                        Settings.CloudTargetLang = lang;
                    }));
                }
                Find.WindowStack.Add(new FloatMenu(options));
            }
            l.Gap(10f);
            Widgets.DrawLineHorizontal(0, l.CurHeight, viewRect.width);
            l.Gap(10f);
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
                Widgets.DrawBoxSolid(rect, new Color(0.25f, 0.35f, 0.25f, 0.32f));
            }
            else if (Mouse.IsOver(rect))
            {
                Widgets.DrawHighlight(rect);
            }

            GUI.color = selected ? new Color(0.75f, 1f, 0.75f) : new Color(0.75f, 0.75f, 0.75f);
            Widgets.DrawBox(rect, 1);
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


            if (_cachedCloudLookup == null || _lastCloudRegistryCount != AutoTranslatorSettings.CloudRegistry.Count || _lastCloudLangFolder != targetLangFolder)
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
                        .OrderByDescending(c => c.TranslationType == "Official_Group" || c.IsVerified)
                        .ThenByDescending(c => c.TranslationDate).ToList();
                }

                _lastCloudRegistryCount = AutoTranslatorSettings.CloudRegistry.Count;
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
                    allVersions = foundList;
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
                else if (cloudRecord.TranslationType == "Official_Group" || cloudRecord.IsVerified)
                {
                    statusText = "ATC_Cloud_Status_Official".Translate();
                    statusColor = new Color(1f, 0.8f, 0.2f);
                    canDownload = true;
                }
                else if (cloudRecord.TranslationType == "Manual")
                {
                    statusText = "ATC_Cloud_Status_Manual".Translate();
                    statusColor = new Color(0.4f, 1f, 0.4f);
                    canDownload = true;
                }

                if (downloadBlacklisted)
                {
                    statusText = "ATC_Blacklist_DownloadBlockedStatus".Translate().ToString();
                    statusColor = new Color(0.8f, 0.55f, 0.55f);
                    canDownload = false;
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


                Text.Font = GameFont.Small;
                float btnWidth = 85f;
                float cursorX = rowRect.xMax - 5f;

                cursorX -= btnWidth;
                Rect correctionsBtn = new Rect(cursorX, rowRect.y + 5f, btnWidth - 5f, 30f);
                GUI.color = isOfficialGamePackage ? new Color(0.7f, 0.9f, 1f) : new Color(0.55f, 0.85f, 1f);
                if (Widgets.ButtonText(correctionsBtn, "ATC_Corrections_Button".Translate()))
                {
                    Find.WindowStack.Add(new Window_AppliedCorrections(mod, targetLangFolder));
                }
                if (Mouse.IsOver(correctionsBtn))
                {
                    TooltipHandler.TipRegion(correctionsBtn, "ATC_Corrections_ButtonTip".Translate());
                }
                GUI.color = Color.white;
                cursorX -= 5f;


                if (!isOfficialGamePackage && !string.IsNullOrEmpty(Settings.CloudAdminToken) && cloudRecord != null)
                {
                    cursorX -= btnWidth;
                    Rect deleteCloudBtn = new Rect(cursorX, rowRect.y + 5f, btnWidth - 5f, 30f);
                    if (AutoTranslatorSettings.CloudUploadTarget == mod.PackageId + "_del")
                    {
                        GUI.color = Color.red;
                        Text.Anchor = TextAnchor.MiddleCenter;
                        Widgets.Label(deleteCloudBtn, "ATC_Cloud_Deleting".Translate());
                        Text.Anchor = TextAnchor.UpperLeft;
                    }
                    else
                    {
                        GUI.color = new Color(1f, 0.3f, 0.3f);
                        if (Widgets.ButtonText(deleteCloudBtn, "ATC_Cloud_Btn_DeleteCloud".Translate()))
                        {
                            AutoTranslatorSettings.CloudUploadTarget = mod.PackageId + "_del";

                            string pid = mod.PackageId; string lang = targetLangFolder; string token = Settings.CloudAdminToken; string recId = cloudRecord.RecordId;
                            System.Threading.Tasks.Task.Run(async () => {
                                bool success = await AutoTranslatorCloudClient.DeleteCloudRecordAsync(pid, lang, recId, token); ATC_Dispatcher.RunOnMainThread(() => {
                                    AutoTranslatorSettings.CloudUploadTarget = "";
                                    if (success) { Messages.Message("ATC_Msg_DeleteCloudSuccess".Translate(mod.Name), MessageTypeDefOf.PositiveEvent, false); AutoTranslatorSettings.HasFetchedCloudThisSession = false; }
                                    else Messages.Message("ATC_Msg_DeleteCloudFailed".Translate(mod.Name), MessageTypeDefOf.RejectInput, false);
                                });
                            });
                        }
                    }
                    GUI.color = Color.white;
                    cursorX -= 5f;
                }


                if (!isOfficialGamePackage)
                {
                    cursorX -= btnWidth;
                    Rect deleteLocalBtn = new Rect(cursorX, rowRect.y + 5f, btnWidth - 5f, 30f);
                    GUI.color = new Color(1f, 0.6f, 0.6f);
                    if (Widgets.ButtonText(deleteLocalBtn, "ATC_Cloud_Btn_DeleteLocal".Translate()))
                    {
                        AutoTranslatorScanner.LocalTranslationDeleteResult result =
                            AutoTranslatorScanner.DeleteLocalTranslationFiles(new List<ModMetaData> { mod });

                        if (result.HasErrors)
                        {
                            string error = string.IsNullOrEmpty(result.FirstError) ? "Unknown error" : result.FirstError;
                            AutoTranslatorSettings.AddErrorLog("ATC_Message_DeleteTransError".Translate(error));
                            Messages.Message("ATC_Message_DeleteTransError".Translate(error), MessageTypeDefOf.RejectInput, false);
                        }
                        else
                        {
                            Messages.Message("ATC_Msg_DeleteLocalSuccess".Translate(mod.Name), MessageTypeDefOf.NeutralEvent, false);
                        }
                    }
                    GUI.color = Color.white;
                    cursorX -= 5f;
                }


                if (!isOfficialGamePackage)
                {
                    cursorX -= btnWidth;
                    Rect uploadBtn = new Rect(cursorX, rowRect.y + 5f, btnWidth - 5f, 30f);
                    if (AutoTranslatorSettings.CloudUploadTarget == mod.PackageId)
                    {
                        GUI.color = Color.yellow;
                        Text.Anchor = TextAnchor.MiddleCenter;
                        Widgets.Label(uploadBtn, "ATC_Cloud_Btn_Uploading".Translate());
                        Text.Anchor = TextAnchor.UpperLeft;
                        GUI.color = Color.white;
                    }
                    else
                    {
                        GUI.color = new Color(1f, 0.8f, 0.4f);
                        if (Widgets.ButtonText(uploadBtn, "ATC_Cloud_Btn_Upload".Translate()))
                        {
                            AutoTranslatorSettings.CloudUploadTarget = mod.PackageId;
                            string packPath = AutoTranslatorScanner.GetLocalPackPath();
                            string uNickname = Settings.CloudNickname; string uToken = Settings.CloudAdminToken;
                            string workspaceDir = System.IO.Path.Combine(packPath, "Upload_Workspace", mod.PackageId, targetLangFolder);
                            string liveLangDir = System.IO.Path.Combine(packPath, "Languages", targetLangFolder);
                            bool useWorkspace = System.IO.Directory.Exists(workspaceDir) && AutoTranslatorScanner.GetXmlFilesForTranslationCache(workspaceDir, System.IO.SearchOption.AllDirectories).Count > 0;
                            string finalSourceDir = useWorkspace ? workspaceDir : liveLangDir;

                            string pId = mod.PackageId;
                            string tFolder = targetLangFolder;
                            string mName = mod.Name;
                            string fSource = finalSourceDir;
                            Find.WindowStack.Add(new Window_UploadPreview(mod, tFolder, fSource, mName));


                            AutoTranslatorSettings.CloudUploadTarget = "";
                        }
                        GUI.color = Color.white;
                    }
                    cursorX -= 5f;
                }


                if (!isOfficialGamePackage)
                {
                    cursorX -= 40f;
                    Rect openFolderBtn = new Rect(cursorX, rowRect.y + 5f, 35f, 30f);
                    GUI.color = new Color(1f, 0.9f, 0.6f);

                    if (Widgets.ButtonText(openFolderBtn, "ATC_Cloud_Btn_Dir".Translate()))
                    {
                        string packPath = AutoTranslatorScanner.GetLocalPackPath();
                        string workspaceDir = System.IO.Path.Combine(packPath, "Upload_Workspace", mod.PackageId, targetLangFolder);
                        System.IO.Directory.CreateDirectory(workspaceDir);
                        UnityEngine.Application.OpenURL("file://" + workspaceDir);
                    }

                    if (Mouse.IsOver(openFolderBtn)) TooltipHandler.TipRegion(openFolderBtn, "ATC_Cloud_Btn_DirTooltip".Translate());
                    GUI.color = Color.white;
                    cursorX -= 5f;
                }


                if (!isOfficialGamePackage && canDownload)
                {
                    float dlWidth = 85f;
                    cursorX -= dlWidth;
                    Rect downloadBtn = new Rect(cursorX, rowRect.y + 5f, dlWidth - 5f, 30f);
                    GUI.color = new Color(0.6f, 1f, 0.6f);
                    if (Widgets.ButtonText(downloadBtn, "ATC_Cloud_Btn_Download".Translate()))
                    {
                        Messages.Message("ATC_Msg_DownloadStart".Translate(mod.Name), MessageTypeDefOf.NeutralEvent, false);

                        CloudModRecord targetRecord = cloudRecord;
                        System.Threading.Tasks.Task.Run(async () => {
                            bool success = await AutoTranslatorCloudClient.DownloadAndInjectAsync(mod.PackageId, targetLangFolder, targetRecord);
                            ATC_Dispatcher.RunOnMainThread(() => {
                                if (success) Messages.Message("ATC_Msg_DownloadSuccess".Translate(mod.Name), MessageTypeDefOf.PositiveEvent, false);
                                else Messages.Message("ATC_Msg_DownloadFailed".Translate(mod.Name), MessageTypeDefOf.RejectInput, false);
                            });
                        });
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

                    if (Widgets.ButtonText(verDropRect, verLabel))
                    {
                        List<FloatMenuOption> verOptions = new List<FloatMenuOption>();
                        foreach (var v in allVersions)
                        {
                            string mTag = v.IsSmartMerged ? "ATC_Cloud_SmartMerged".Translate().ToString() : "";
                            string vLocType = GetCloudTranslationTypeLabel(v);

                            string optLabel = $"[{v.LastUpdated:yyyy-MM-dd}] ({vLocType}) - {v.Author}{mTag}";
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


                        string tipStr = "ATC_Cloud_UploadDate".Translate(cloudRecord.LastUpdated.ToString("yyyy-MM-dd HH:mm")) + "\n" +
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
                    if (Widgets.ButtonText(deleteCloudBtn, "ATC_Cloud_Btn_DeleteCloud".Translate()))
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
            if (type == "Manual") return "ATC_Type_Manual".Translate();
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
