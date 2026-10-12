using HarmonyLib;
using Newtonsoft.Json;
using RimWorld;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using Verse;
using static AutoTranslator_Core.DeleteTranslationWindow;
// 這個檔案負責上傳預覽視窗的主框架。
// EN: This file owns the main upload preview window frame.

namespace AutoTranslator_Core
{
        // 這個類別負責 視窗上傳Preview 的主要流程與狀態。
        // EN: This class manages the main workflow and state for Window_UploadPreview.
        public partial class Window_UploadPreview : Window
        {
            // 這個欄位保存 mod 的執行狀態或快取資料。
            // EN: This field stores mod runtime state or cached data.
            private ModMetaData _mod;
            // 這個欄位保存 target語言Folder 的執行狀態或快取資料。
            // EN: This field stores target language folder runtime state or cached data.
            private string _targetLangFolder;
            // 這個欄位保存 sourceDir 的執行狀態或快取資料。
            // EN: This field stores source dir runtime state or cached data.
            private string _sourceDir;
            // 這個欄位保存 mod名稱 的執行狀態或快取資料。
            // EN: This field stores mod name runtime state or cached data.
            private string _modName;
            // 這個欄位保存 leftScrollPos 的執行狀態或快取資料。
            // EN: This field stores left scroll pos runtime state or cached data.
            private Vector2 _leftScrollPos = Vector2.zero;
            // 這個欄位保存 rightScrollPos 的執行狀態或快取資料。
            // EN: This field stores right scroll pos runtime state or cached data.
            private Vector2 _rightScrollPos = Vector2.zero;
            // 這個欄位保存 is載入 的執行狀態或快取資料。
            // EN: This field stores is loading runtime state or cached data.
            private bool _isLoading = true;
            // 這個欄位保存 isEditable 的執行狀態或快取資料。
            // EN: This field stores is editable runtime state or cached data.
            private bool _isEditable = false;
            private bool _isSavingChanges = false;
            private string _uploadType;
            private Workflow.UploadClassificationSummary _uploadClassification;
            private string _uploadClassificationError;

            // 這個類別負責 PreviewItem 的主要流程與狀態。
            // EN: This class manages the main workflow and state for PreviewItem.
            private class PreviewItem
            {
                // 這個欄位保存 Key 的執行狀態或快取資料。
                // EN: This field stores key runtime state or cached data.
                public string Key;
                public string SourceFile;
                // 這個欄位保存 OriginalText 的執行狀態或快取資料。
                // EN: This field stores original text runtime state or cached data.
                public string OriginalText;
                // 這個欄位保存 TranslatedText 的執行狀態或快取資料。
                // EN: This field stores translated text runtime state or cached data.
                public string TranslatedText;
                // 這個欄位保存 IsModified 的執行狀態或快取資料。
                // EN: This field stores is modified runtime state or cached data.
                public bool IsModified;
            }

            private Dictionary<string, List<PreviewItem>> _categorizedData = new Dictionary<string, List<PreviewItem>>();
            // 這個欄位保存 selectedCategory 的執行狀態或快取資料。
            // EN: This field stores selected category runtime state or cached data.
            private string _selectedCategory = "";
            // 這個欄位保存 updateLogText 的執行狀態或快取資料。
            // EN: This field stores update log text runtime state or cached data.
            private string _updateLogText = "";

            // 這個屬性提供 InitialSize 的讀寫或計算結果。
            // EN: This method handles vector2.
            public override Vector2 InitialSize => new Vector2(1000f, 780f);

            // 這個方法負責處理 視窗上傳Preview 相關流程。
            // EN: This method handles window upload preview.
            public Window_UploadPreview(ModMetaData mod, string targetLangFolder, string sourceDir, string modName)
            {
                _mod = mod;
                _targetLangFolder = targetLangFolder;
                _sourceDir = sourceDir;
                _modName = modName;
                _uploadType = AutoTranslatorMod.NormalizeCloudUploadType(AutoTranslatorMod.Settings.CloudUploadType,
                    !string.IsNullOrWhiteSpace(AutoTranslatorMod.Settings.CloudAdminToken));
                this.doCloseButton = false;
                this.doCloseX = true;
                this.forcePause = true;
                this.absorbInputAroundWindow = true;

                System.Threading.Tasks.Task.Run(() => LoadPreviewData());
            }
            // 這個方法負責處理 Do視窗Contents 相關流程。
            // EN: This method handles do window contents.
            public override void DoWindowContents(Rect inRect)
            {

            Patch_GUI_Label_GUIContent.BypassInterceptor = true;
            try
            {
                Text.Font = GameFont.Medium;
                Widgets.Label(new Rect(0, 0, inRect.width, 35f), "🔍 " + "ATC_UploadPreview_Title".Translate(_mod.Name));
                Text.Font = GameFont.Small;
                Widgets.DrawLineHorizontal(0, 35f, inRect.width);

                if (_isLoading)
                {
                    Text.Anchor = TextAnchor.MiddleCenter;
                    Widgets.Label(new Rect(0, 0, inRect.width, inRect.height), "🔄 " + "ATC_UploadPreview_Loading".Translate());
                    Text.Anchor = TextAnchor.UpperLeft;
                    return;
                }

                if (_categorizedData.Count == 0)
                {
                    GUI.color = new Color(1f, 0.6f, 0.6f);
                    Text.Anchor = TextAnchor.MiddleCenter;
                    Widgets.Label(new Rect(0, 0, inRect.width, inRect.height), "⚠️ " + "ATC_UploadPreview_NoTranslation".Translate()); Text.Anchor = TextAnchor.UpperLeft;
                    GUI.color = Color.white;


                    if (WorkflowUiStyle.Button(new Rect(inRect.width / 2f - 75f, inRect.height - 60f, 150f, 40f), "ATC_Btn_Cancel".Translate()))
                    {
                        this.Close();
                    }
                    return;
                }

                Widgets.Label(new Rect(0f, 40f, inRect.width, 22f), _uploadClassification != null
                    ? _uploadClassification.Description
                    : AutoTranslatorMod.WfText("无法读取来源统计：", "Source statistics unavailable: ") + _uploadClassificationError);
                if (WorkflowUiStyle.Button(new Rect(0f, 65f, 140f, 28f),
                        AutoTranslatorMod.WfText("AI 翻译", "AI translation"), WorkflowButtonStyle.Dropdown))
                    _uploadType = "AI_Auto";
                bool canLabelManual = _uploadClassification != null && _uploadClassification.CanLabelManual;
                if (WorkflowUiStyle.Button(new Rect(150f, 65f, 140f, 28f),
                        AutoTranslatorMod.WfText("人工精翻", "Human-curated"), WorkflowButtonStyle.Dropdown,
                        canLabelManual)) _uploadType = "Manual";
                TooltipHandler.TipRegion(new Rect(150f, 65f, 140f, 28f),
                    canLabelManual ? AutoTranslatorMod.WfText("选择人工精翻标记", "Choose Human-curated label")
                        : _uploadClassification?.ManualBlockedReason ?? _uploadClassificationError ?? string.Empty);
                if (!string.IsNullOrWhiteSpace(AutoTranslatorMod.Settings.CloudAdminToken) &&
                    WorkflowUiStyle.Button(new Rect(300f, 65f, 140f, 28f),
                        "ATC_Type_Official".Translate(), WorkflowButtonStyle.Dropdown)) _uploadType = "Official_Group";
                string selectedTypeLabel = _uploadType == "Official_Group" ? "ATC_Type_Official".Translate().ToString()
                    : _uploadType == "Manual" ? AutoTranslatorMod.WfText("人工精翻", "Human-curated")
                    : AutoTranslatorMod.WfText("AI 翻译", "AI translation");
                Widgets.Label(new Rect(460f, 68f, inRect.width - 460f, 24f),
                    AutoTranslatorMod.WfText("上传标记：", "Upload label: ") + selectedTypeLabel);
                float topOffset = 105f;
                float leftWidth = 220f;
                float spacing = 15f;
                float rightWidth = inRect.width - leftWidth - spacing;
                float contentHeight = inRect.height - topOffset - 150f;

                Rect leftOutRect = new Rect(0, topOffset, leftWidth, contentHeight);
                Rect rightOutRect = new Rect(leftWidth + spacing, topOffset, rightWidth, contentHeight);


                Widgets.DrawBoxSolid(leftOutRect, new Color(0.1f, 0.1f, 0.1f, 0.5f));
                Rect leftViewRect = new Rect(0, 0, leftOutRect.width - 20f, _categorizedData.Count * 35f);
                Widgets.BeginScrollView(leftOutRect, ref _leftScrollPos, leftViewRect);
                float curY = 0f;
                foreach (var category in _categorizedData.Keys)
                {
                    Rect rowRect = new Rect(5f, curY, leftViewRect.width - 5f, 30f);
                    if (_selectedCategory == category) Widgets.DrawHighlightSelected(rowRect);
                    else Widgets.DrawHighlightIfMouseover(rowRect);

                    if (Widgets.ButtonInvisible(rowRect)) { _selectedCategory = category; _rightScrollPos = Vector2.zero; }
                    Text.Anchor = TextAnchor.MiddleLeft;
                    Widgets.Label(rowRect, $"{category} ({_categorizedData[category].Count})");
                    Text.Anchor = TextAnchor.UpperLeft;
                    curY += 35f;
                }
                Widgets.EndScrollView();


                Widgets.DrawBoxSolid(rightOutRect, new Color(0.05f, 0.05f, 0.05f, 0.5f));
                if (!string.IsNullOrEmpty(_selectedCategory) && _categorizedData.ContainsKey(_selectedCategory))
                {
                    var items = _categorizedData[_selectedCategory];
                    float rowHeight = 70f;
                    Rect rightViewRect = new Rect(0, 0, rightOutRect.width - 20f, items.Count * rowHeight);
                    Widgets.BeginScrollView(rightOutRect, ref _rightScrollPos, rightViewRect);

                    float editY = 0f;
                    foreach (var item in items)
                    {
                        Rect itemRect = new Rect(5f, editY, rightViewRect.width - 10f, rowHeight - 5f);
                        Widgets.DrawHighlightIfMouseover(itemRect);

                        Text.Font = GameFont.Tiny;
                        GUI.color = Color.gray;
                        Widgets.Label(new Rect(itemRect.x, itemRect.y, itemRect.width, 15f), item.Key);

                        Text.Font = GameFont.Small;
                        GUI.color = Color.white;
                        Rect transRect = new Rect(itemRect.x, itemRect.y + 15f, itemRect.width, itemRect.height - 15f);


                        if (_isEditable && !_isSavingChanges)
                        {
                            string newText = Widgets.TextArea(transRect, item.TranslatedText ?? "");
                            if (newText != item.TranslatedText)
                            {
                                item.TranslatedText = newText;
                                item.IsModified = true;
                                RefreshPreviewClassification();
                                canLabelManual = _uploadClassification != null && _uploadClassification.CanLabelManual;
                            }
                        }
                        else
                        {
                            Widgets.Label(transRect, item.TranslatedText);
                        }
                        editY += rowHeight;
                    }
                    GUI.color = Color.white;
                    Widgets.EndScrollView();
                }


                float logAreaY = topOffset + contentHeight + 10f;
                Rect logLabelRect = new Rect(0, logAreaY, inRect.width, 22f);
                Widgets.Label(logLabelRect, "📝 " + "ATC_Upload_LogLabel".Translate());

                Rect logInputRect = new Rect(0, logAreaY + 22f, inRect.width, 55f);
                _updateLogText = Widgets.TextArea(logInputRect, _updateLogText);
                if (string.IsNullOrEmpty(_updateLogText))
                {
                    GUI.color = Color.gray;
                    Widgets.Label(new Rect(logInputRect.x + 5f, logInputRect.y + 2f, logInputRect.width, logInputRect.height), "ATC_Upload_LogHint".Translate());
                    GUI.color = Color.white;
                }


                float btnY = inRect.height - 35f;


                if (WorkflowUiStyle.Button(new Rect(0, btnY, 130f, 35f),
                        "ATC_Btn_Cancel".Translate(), WorkflowButtonStyle.Quiet, !_isSavingChanges))
                {
                    this.Close();
                }


                if (WorkflowUiStyle.Button(
                        new Rect(145f, btnY, 150f, 35f),
                        _isEditable ? "✍️ " + "ATC_Upload_EditingMode".Translate() : "⚙️ " + "ATC_Upload_UnlockEdit".Translate(),
                        _isEditable ? WorkflowButtonStyle.ActiveTab : WorkflowButtonStyle.Quiet,
                        !_isSavingChanges))
                {
                    if (!_isSavingChanges) _isEditable = !_isEditable;
                }


                bool isAdmin = !string.IsNullOrEmpty(AutoTranslatorMod.Settings.CloudAdminToken);
                bool hasValidLog = !string.IsNullOrWhiteSpace(_updateLogText) && _updateLogText.Trim().Length >= 5;
                bool canUpload = (isAdmin || hasValidLog) && !_isSavingChanges &&
                    (_uploadType != "Manual" || canLabelManual);

                if (WorkflowUiStyle.Button(
                        new Rect(inRect.width - 180f, btnY, 180f, 35f),
                        "🚀 " + "ATC_Upload_ConfirmUploadBtn".Translate(),
                        WorkflowButtonStyle.Primary,
                        canUpload))
                {
                    if (_isSavingChanges) return;
                    if (!canUpload)
                    {

                        Verse.Messages.Message("ATC_Msg_UploadLogRequired".Translate(), RimWorld.MessageTypeDefOf.RejectInput, false);
                        return;
                    }


                    SaveChangesThenUpload();
                }
                GUI.color = Color.white;
            }
            finally
            {

                Patch_GUI_Label_GUIContent.BypassInterceptor = false;
            }
        }

        private void RefreshPreviewClassification()
        {
            if (_uploadClassification == null) return;
            _uploadClassification.TotalEntries = 0;
            _uploadClassification.AiEntries = 0;
            foreach (var category in _categorizedData)
                foreach (PreviewItem item in category.Value)
                {
                    if (string.IsNullOrWhiteSpace(item.TranslatedText)) continue;
                    _uploadClassification.TotalEntries++;
                    if (_uploadClassification.IsAiEntry(item.SourceFile, item.Key, item.TranslatedText))
                        _uploadClassification.AiEntries++;
                }
        }
    }

}
