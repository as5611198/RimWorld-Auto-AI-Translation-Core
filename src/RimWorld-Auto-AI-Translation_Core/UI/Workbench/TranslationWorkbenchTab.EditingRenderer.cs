using RimWorld;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using Verse;
using static AutoTranslator_Core.DeleteTranslationWindow;
// 這個檔案負責 翻譯工作台分頁編輯繪製 相關邏輯，支援 Auto Translation Core 的執行流程。
// EN: This file contains translation workbench tab editing renderer support code.

namespace AutoTranslator_Core
{
        // 這個類別負責 翻譯工作台分頁 的主要流程與狀態。
        // EN: This class manages the main workflow and state for TranslationWorkbenchTab.
        public static partial class TranslationWorkbenchTab
        {
            // 這個方法負責繪製 編輯Mode 介面。
            // EN: This method draws editing mode.
            private static void DrawEditingMode(UnityEngine.Rect leftOutRect, UnityEngine.Rect rightOutRect)
            {
                    UnityEngine.Rect backBtnRect = new UnityEngine.Rect(leftOutRect.x + 5f, leftOutRect.y + 5f, leftOutRect.width - 10f, 35f);
                    UnityEngine.GUI.color = new UnityEngine.Color(1f, 0.7f, 0.7f);
                    string backLabel = _activeWorkbenchFocus != null && _activeWorkbenchFocus.FromGlobalSearch
                        ? "ATC_Workbench_ReturnToSearchResults".Translate().ToString()
                        : "ATC_Workbench_BackToList".Translate().ToString();
                    if (Verse.Widgets.ButtonText(backBtnRect, "🔙 " + backLabel))
                    {
                        if (_isSavingModifications) return;
                        ReturnToWorkbenchModList();
                        return;
                    }
                    UnityEngine.GUI.color = UnityEngine.Color.white;

                    Verse.Widgets.DrawLineHorizontal(leftOutRect.x, leftOutRect.y + 45f, leftOutRect.width);

                    if (_isLoading)
                    {
                        Verse.Text.Anchor = UnityEngine.TextAnchor.MiddleCenter;
                        Verse.Widgets.Label(new UnityEngine.Rect(leftOutRect.x, leftOutRect.y + 50f, leftOutRect.width, 100f), "🔄 " + "ATC_UploadPreview_Loading".Translate());
                        Verse.Text.Anchor = UnityEngine.TextAnchor.UpperLeft;
                    }
                    else
                    {
                        UnityEngine.Rect catOutRect = new UnityEngine.Rect(leftOutRect.x, leftOutRect.y + 50f, leftOutRect.width, leftOutRect.height - 50f);
                        UnityEngine.Rect catViewRect = new UnityEngine.Rect(0, 0, catOutRect.width - 20f, (_categorizedData.Count + 1) * WorkbenchCategoryRowHeight);
                        Verse.Widgets.BeginScrollView(catOutRect, ref _catListScroll, catViewRect);
                        try
                        {
                            float curY = 0f;
                            UnityEngine.Rect allRowRect = new UnityEngine.Rect(5f, curY, catViewRect.width - 5f, 30f);
                            if (IsAllWorkbenchCategoriesSelected()) Verse.Widgets.DrawHighlightSelected(allRowRect);
                            else Verse.Widgets.DrawHighlightIfMouseover(allRowRect);
                            if (Verse.Widgets.ButtonInvisible(allRowRect))
                            {
                                _selectedCategory = AllWorkbenchCategoriesView;
                                _itemScroll = UnityEngine.Vector2.zero;
                                InvalidateVisibleItemCache();
                            }
                            int totalEntries = _categorizedData.Values.Where(items => items != null).Sum(items => items.Count);
                            string allCategoriesLabel = "ATC_Workbench_AllCategories".Translate(totalEntries).ToString();
                            Verse.Text.WordWrap = false;
                            Verse.Widgets.Label(allRowRect, allCategoriesLabel);
                            Verse.Text.WordWrap = true;
                            if (UnityEngine.GUI.skin.label.CalcSize(new UnityEngine.GUIContent(allCategoriesLabel)).x > allRowRect.width)
                            {
                                TooltipHandler.TipRegion(allRowRect, allCategoriesLabel);
                            }
                            curY += WorkbenchCategoryRowHeight;

                            foreach (var category in _categorizedData.Keys)
                            {
                                UnityEngine.Rect rowRect = new UnityEngine.Rect(5f, curY, catViewRect.width - 5f, 30f);
                                if (_selectedCategory == category) Verse.Widgets.DrawHighlightSelected(rowRect);
                                else Verse.Widgets.DrawHighlightIfMouseover(rowRect);

                        if (Verse.Widgets.ButtonInvisible(rowRect))
                        {
                            _selectedCategory = category;
                            _itemScroll = UnityEngine.Vector2.zero;
                            InvalidateVisibleItemCache();
                        }

                                string categoryLabel = $"{category} ({_categorizedData[category].Count})";
                                Verse.Text.WordWrap = false;
                                Verse.Widgets.Label(rowRect, categoryLabel);
                                Verse.Text.WordWrap = true;
                                if (UnityEngine.GUI.skin.label.CalcSize(new UnityEngine.GUIContent(categoryLabel)).x > rowRect.width)
                                {
                                    TooltipHandler.TipRegion(rowRect, categoryLabel);
                                }
                                curY += WorkbenchCategoryRowHeight;
                            }
                        }
                        finally
                        {
                            Verse.Text.WordWrap = true;
                            Verse.Widgets.EndScrollView();
                        }
                    }

                    UnityEngine.Rect headerRect = new UnityEngine.Rect(rightOutRect.x + 10f, rightOutRect.y + 5f, rightOutRect.width - 20f, 30f);
                    Verse.Text.Font = Verse.GameFont.Medium;
                    Verse.Widgets.Label(headerRect, _editingMod.Name);
                    Verse.Text.Font = Verse.GameFont.Small;

                    bool compactEditingToolbar = rightOutRect.width < 430f;
                    float itemSearchWidth = compactEditingToolbar
                        ? Mathf.Max(60f, rightOutRect.width - 160f)
                        : rightOutRect.width - 275f;
                    UnityEngine.Rect itemSearchRect = new UnityEngine.Rect(rightOutRect.x + 10f, rightOutRect.y + 40f, itemSearchWidth, 30f);
                    string newSearchText = Verse.Widgets.TextField(itemSearchRect, _itemSearchText);
                    if (!string.Equals(newSearchText, _itemSearchText, StringComparison.Ordinal))
                    {
                        _itemSearchText = newSearchText;
                        _itemScroll = UnityEngine.Vector2.zero;
                        InvalidateVisibleItemCache();
                    }
                    if (string.IsNullOrEmpty(_itemSearchText))
                    {
                        UnityEngine.GUI.color = UnityEngine.Color.gray;
                        Verse.Widgets.Label(new UnityEngine.Rect(itemSearchRect.x + 5f, itemSearchRect.y + 2f, itemSearchRect.width, itemSearchRect.height), "🔍 " + "ATC_Workbench_SearchHint".Translate());
                        UnityEngine.GUI.color = UnityEngine.Color.white;
                    }

                    UnityEngine.Rect batchReplaceBtnRect = compactEditingToolbar
                        ? new UnityEngine.Rect(rightOutRect.x + 10f, rightOutRect.y + 73f, 110f, 22f)
                        : new UnityEngine.Rect(rightOutRect.xMax - 255f, rightOutRect.y + 40f, 110f, 30f);
                    GUI.color = _isSavingModifications ? UnityEngine.Color.gray : new UnityEngine.Color(0.7f, 0.85f, 1f);
                    if (Verse.Widgets.ButtonText(batchReplaceBtnRect, "ATC_Workbench_BatchReplaceBtn".Translate()))
                    {
                        if (!_isSavingModifications)
                        {
                            Find.WindowStack.Add(new Window_WorkbenchBatchReplace());
                        }
                    }
                    if (Mouse.IsOver(batchReplaceBtnRect))
                    {
                        TooltipHandler.TipRegion(batchReplaceBtnRect, "ATC_Workbench_BatchReplaceTip".Translate());
                    }

                    UnityEngine.Rect saveBtnRect = new UnityEngine.Rect(rightOutRect.xMax - 140f, rightOutRect.y + 40f, 130f, 30f);
                    bool hasUnsavedChanges = HasUnsavedWorkbenchChanges();
                    UnityEngine.GUI.color = _isSavingModifications
                        ? UnityEngine.Color.gray
                        : (hasUnsavedChanges ? new UnityEngine.Color(0.4f, 1f, 0.4f) : new UnityEngine.Color(0.45f, 0.45f, 0.45f));
                    string saveLabel = _isSavingModifications
                        ? "ATC_Workbench_Saving".Translate().ToString()
                        : (hasUnsavedChanges ? "ATC_Workbench_SaveBtn".Translate().ToString() : "ATC_Workbench_SaveSynced".Translate().ToString());
                    if (Verse.Widgets.ButtonText(saveBtnRect, "💾 " + saveLabel))
                    {
                        if (_isSavingModifications || !hasUnsavedChanges) return;
                        SaveModifications();
                    }
                    UnityEngine.GUI.color = UnityEngine.Color.white;

                    UnityEngine.Rect statusLineRect = compactEditingToolbar
                        ? new UnityEngine.Rect(rightOutRect.x + 125f, rightOutRect.y + 73f, rightOutRect.width - 135f, 20f)
                        : new UnityEngine.Rect(rightOutRect.x + 10f, rightOutRect.y + 73f, rightOutRect.width - 20f, 20f);
                    DrawWorkbenchStatusLine(statusLineRect, hasUnsavedChanges);

                    float manualButtonGap = 6f;
                    float manualButtonWidth = (rightOutRect.width - 20f - manualButtonGap * 2f) / 3f;
                    float manualButtonY = rightOutRect.y + 97f;
                    Rect exportOriginalRect = new Rect(rightOutRect.x + 10f, manualButtonY, manualButtonWidth, 28f);
                    Rect importEditedRect = new Rect(exportOriginalRect.xMax + manualButtonGap, manualButtonY, manualButtonWidth, 28f);
                    Rect openWorkspaceRect = new Rect(importEditedRect.xMax + manualButtonGap, manualButtonY, manualButtonWidth, 28f);

                    bool canUseManualXml = !_isLoading && !_isSavingModifications && !_isManualXmlBusy &&
                                           !string.IsNullOrEmpty(_selectedCategory);
                    GUI.color = canUseManualXml ? new Color(0.62f, 0.84f, 1f) : Color.gray;
                    if (Widgets.ButtonText(exportOriginalRect, "ATC_Workbench_ExportOriginalXml".Translate()) && canUseManualXml)
                    {
                        ExportCurrentWorkbenchOriginalXml();
                    }
                    TooltipHandler.TipRegion(exportOriginalRect, "ATC_Workbench_ExportOriginalXmlTip".Translate());

                    GUI.color = canUseManualXml ? new Color(0.62f, 0.92f, 0.72f) : Color.gray;
                    if (Widgets.ButtonText(importEditedRect, "ATC_Workbench_ImportEditedXml".Translate()) && canUseManualXml)
                    {
                        ImportCurrentWorkbenchManualXml();
                    }
                    TooltipHandler.TipRegion(importEditedRect, "ATC_Workbench_ImportEditedXmlTip".Translate());

                    GUI.color = !_isManualXmlBusy ? Color.white : Color.gray;
                    if (Widgets.ButtonText(openWorkspaceRect, "ATC_Workbench_OpenWorkspace".Translate()) && !_isManualXmlBusy)
                    {
                        OpenCurrentWorkbenchWorkspace();
                    }
                    TooltipHandler.TipRegion(openWorkspaceRect, "ATC_Workbench_OpenWorkspaceTip".Translate());
                    GUI.color = Color.white;

                    Verse.Widgets.DrawLineHorizontal(rightOutRect.x, rightOutRect.y + 131f, rightOutRect.width);

                    if (!_isLoading && !string.IsNullOrEmpty(_selectedCategory) &&
                        (IsAllWorkbenchCategoriesSelected() || _categorizedData.ContainsKey(_selectedCategory)))
                    {
                        var items = GetVisibleItemsForCurrentCategory(GetCurrentWorkbenchSourceItems());

                        float rowHeight = WorkbenchItemRowHeight;
                        UnityEngine.Rect itemsOutRect = new UnityEngine.Rect(rightOutRect.x, rightOutRect.y + 136f, rightOutRect.width, rightOutRect.height - 136f);
                        UnityEngine.Rect itemsViewRect = new UnityEngine.Rect(0, 0, itemsOutRect.width - 20f, items.Count * rowHeight);
                        int firstVisible = Mathf.Max(0, Mathf.FloorToInt(_itemScroll.y / rowHeight) - 2);
                        int lastVisible = Mathf.Min(items.Count - 1, Mathf.CeilToInt((_itemScroll.y + itemsOutRect.height) / rowHeight) + 2);
                        Verse.Widgets.BeginScrollView(itemsOutRect, ref _itemScroll, itemsViewRect);

                        try
                        {
                            float halfWidth = (itemsViewRect.width - 10f) / 2f;

                            for (int i = firstVisible; i <= lastVisible; i++)
                            {
                                var item = items[i];
                                string itemCategory = GetWorkbenchItemCategory(item);
                                float editY = i * rowHeight;
                                UnityEngine.Rect itemRect = new UnityEngine.Rect(5f, editY, itemsViewRect.width - 10f, rowHeight - 5f);
                                bool isFocusedItem = IsFocusedWorkbenchItem(item);
                                if (isFocusedItem)
                                {
                                    Verse.Widgets.DrawBoxSolid(itemRect, new UnityEngine.Color(0.22f, 0.25f, 0.12f, 0.35f));
                                }
                                Verse.Widgets.DrawHighlightIfMouseover(itemRect);

                                Verse.Text.Font = Verse.GameFont.Tiny;
                                UnityEngine.GUI.color = UnityEngine.Color.gray;
                                string rowTitle = IsAllWorkbenchCategoriesSelected()
                                    ? $"[{itemCategory}] {item.Key}"
                                    : item.Key;
                                bool retainedEditedItem = IsRetainedEditedWorkbenchItem(item);
                                string mismatchSearchText = isFocusedItem && _activeWorkbenchFocus != null
                                    ? _activeWorkbenchFocus.SearchText
                                    : _itemSearchText;
                                bool noLongerMatchesSearch = (isFocusedItem || retainedEditedItem) && !DoesWorkbenchItemMatchSearch(item, mismatchSearchText);
                                Verse.Text.WordWrap = false;
                                float rowTitleWidth = Mathf.Max(0f, itemRect.width - 392f);
                                Verse.Widgets.Label(new UnityEngine.Rect(itemRect.x, itemRect.y, rowTitleWidth, 15f), rowTitle);
                                if (noLongerMatchesSearch)
                                {
                                    UnityEngine.Rect mismatchRect = new UnityEngine.Rect(itemRect.x, itemRect.y + 12f, rowTitleWidth, 14f);
                                    UnityEngine.GUI.color = new UnityEngine.Color(1f, 0.82f, 0.35f);
                                    Verse.Widgets.Label(mismatchRect, "ATC_Workbench_NoLongerMatchesSearch".Translate().ToString());
                                    TooltipHandler.TipRegion(mismatchRect, "ATC_Workbench_NoLongerMatchesSearch".Translate());
                                }
                                Verse.Text.WordWrap = true;

                                UnityEngine.Rect correctionBtnRect = new UnityEngine.Rect(itemRect.xMax - 112f, itemRect.y, 108f, 22f);
                                UnityEngine.GUI.color = new UnityEngine.Color(0.55f, 0.85f, 1f);
                                if (Verse.Widgets.ButtonText(correctionBtnRect, "ATC_Correction_RowBtn".Translate()))
                                {
                                    Find.WindowStack.Add(new Window_CorrectionSubmit(
                                        _editingMod,
                                        itemCategory,
                                        item.Key,
                                        item.OriginalText,
                                        item.OriginalTranslatedText ?? "",
                                        item.TranslatedText ?? ""));
                                }
                                UnityEngine.GUI.color = UnityEngine.Color.gray;
                                if (Mouse.IsOver(correctionBtnRect))
                                {
                                    TooltipHandler.TipRegion(correctionBtnRect, "ATC_Correction_RowBtnTip".Translate());
                                }

                                UnityEngine.Rect revertBtnRect = new UnityEngine.Rect(correctionBtnRect.x - 86f, itemRect.y, 78f, 22f);
                                UnityEngine.Rect translateOriginalBtnRect = new UnityEngine.Rect(revertBtnRect.x - 94f, itemRect.y, 86f, 22f);
                                UnityEngine.Rect copyOriginalBtnRect = new UnityEngine.Rect(translateOriginalBtnRect.x - 88f, itemRect.y, 80f, 22f);
                                bool hasOriginalText = !string.IsNullOrEmpty(item.OriginalText);
                                UnityEngine.GUI.color = hasOriginalText ? new UnityEngine.Color(0.65f, 0.9f, 1f) : UnityEngine.Color.gray;
                                if (Verse.Widgets.ButtonText(copyOriginalBtnRect, "ATC_Workbench_CopyOriginal".Translate()))
                                {
                                    if (hasOriginalText)
                                    {
                                        GUIUtility.systemCopyBuffer = item.OriginalText;
                                        SetWorkbenchStatus("ATC_Workbench_OriginalCopied".Translate().ToString());
                                    }
                                }
                                if (Mouse.IsOver(copyOriginalBtnRect))
                                {
                                    TooltipHandler.TipRegion(copyOriginalBtnRect, "ATC_Workbench_CopyOriginalTip".Translate());
                                }

                                bool canTranslateOriginal = hasOriginalText && !item.IsTranslatingOriginal && !_isSavingModifications;
                                UnityEngine.GUI.color = canTranslateOriginal ? new UnityEngine.Color(0.55f, 0.9f, 0.75f) : UnityEngine.Color.gray;
                                string translateOriginalLabel = item.IsTranslatingOriginal
                                    ? "ATC_Workbench_TranslateOriginalBusy".Translate().ToString()
                                    : "ATC_Workbench_TranslateOriginal".Translate().ToString();
                                if (Verse.Widgets.ButtonText(translateOriginalBtnRect, translateOriginalLabel))
                                {
                                    if (canTranslateOriginal)
                                    {
                                        TranslateWorkbenchOriginalText(item);
                                    }
                                }
                                if (Mouse.IsOver(translateOriginalBtnRect))
                                {
                                    TooltipHandler.TipRegion(translateOriginalBtnRect, "ATC_Workbench_TranslateOriginalTip".Translate());
                                }

                                bool canRevert = !string.Equals(item.TranslatedText ?? "", item.OriginalTranslatedText ?? "", StringComparison.Ordinal);
                                UnityEngine.GUI.color = canRevert && !_isSavingModifications ? new UnityEngine.Color(1f, 0.85f, 0.45f) : UnityEngine.Color.gray;
                                if (Verse.Widgets.ButtonText(revertBtnRect, "ATC_Workbench_RevertItem".Translate()))
                                {
                                    if (canRevert && !_isSavingModifications)
                                    {
                                        item.TranslatedText = item.OriginalTranslatedText ?? "";
                                        RefreshWorkbenchItemModifiedState(item);
                                        _retainedEditedCategory = itemCategory;
                                        _retainedEditedKey = item.Key ?? "";
                                        _categorizedDataVersion++;
                                        InvalidateVisibleItemCache();
                                        SetWorkbenchStatus("ATC_Workbench_ItemReverted".Translate().ToString());
                                    }
                                }

                                Verse.Text.Font = Verse.GameFont.Small;
                                UnityEngine.GUI.color = new UnityEngine.Color(0.8f, 0.8f, 0.8f);
                                UnityEngine.Rect originalRect = new UnityEngine.Rect(itemRect.x, itemRect.y + 24f, halfWidth - 5f, itemRect.height - 24f);


                                try { Verse.Widgets.Label(originalRect, item.OriginalText ?? ""); }
                                catch { Verse.Widgets.Label(originalRect, "[Error: Invalid Rich Text]"); }

                                UnityEngine.GUI.color = UnityEngine.Color.white;
                                UnityEngine.Rect transRect = new UnityEngine.Rect(itemRect.x + halfWidth + 5f, itemRect.y + 24f, halfWidth - 5f, itemRect.height - 24f);


                                string newText = item.TranslatedText ?? "";
                                if (_isSavingModifications)
                                {
                                    Verse.Widgets.Label(transRect, newText);
                                }
                                else
                                {
                                    try { newText = Verse.Widgets.TextArea(transRect, newText); }
                                    catch { newText = Verse.Widgets.TextField(transRect, newText); }
                                }

                                if (newText != item.TranslatedText)
                                {
                                    item.TranslatedText = newText;
                                    RefreshWorkbenchItemModifiedState(item);
                                    _retainedEditedCategory = itemCategory;
                                    _retainedEditedKey = item.Key ?? "";
                                    _categorizedDataVersion++;
                                    InvalidateVisibleItemCache();
                                }
                            }
                        }
                        finally
                        {
                            Verse.Widgets.EndScrollView();
                        }
                    }
            }

            private static void ReturnToWorkbenchModList()
            {
                _editingMod = null;
                _categorizedData.Clear();
                _selectedCategory = "";
                _activeWorkbenchFocus = null;
                _pendingWorkbenchFocus = null;
                _retainedEditedCategory = "";
                _retainedEditedKey = "";
                _itemSearchText = "";
                _itemScroll = UnityEngine.Vector2.zero;
                _catListScroll = UnityEngine.Vector2.zero;
                _workbenchStatusText = "";
                InvalidateVisibleItemCache();
                InitTranslatedModsCache();
            }

            private static bool HasUnsavedWorkbenchChanges()
            {
                foreach (var categoryPair in _categorizedData)
                {
                    if (categoryPair.Value == null) continue;
                    foreach (WorkbenchItem item in categoryPair.Value)
                    {
                        if (item == null) continue;
                        if (HasWorkbenchItemUnsavedChanges(item)) return true;
                    }
                }

                return false;
            }

            private static bool IsFocusedWorkbenchItem(WorkbenchItem item)
            {
                return item != null &&
                    _activeWorkbenchFocus != null &&
                    IsWorkbenchCategoryVisible(_activeWorkbenchFocus.Category) &&
                    string.Equals(GetWorkbenchItemCategory(item), _activeWorkbenchFocus.Category ?? "", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(item.Key ?? "", _activeWorkbenchFocus.Key ?? "", StringComparison.OrdinalIgnoreCase);
            }

            private static bool IsRetainedEditedWorkbenchItem(WorkbenchItem item)
            {
                return item != null &&
                    IsWorkbenchCategoryVisible(_retainedEditedCategory) &&
                    string.Equals(GetWorkbenchItemCategory(item), _retainedEditedCategory ?? "", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(item.Key ?? "", _retainedEditedKey ?? "", StringComparison.OrdinalIgnoreCase);
            }

            private static bool DoesWorkbenchItemMatchSearch(WorkbenchItem item, string searchText)
            {
                if (item == null) return false;
                if (string.IsNullOrWhiteSpace(searchText)) return true;

                string searchLower = searchText.ToLowerInvariant();
                return (item.OriginalText != null && item.OriginalText.ToLowerInvariant().Contains(searchLower)) ||
                    (item.TranslatedText != null && item.TranslatedText.ToLowerInvariant().Contains(searchLower)) ||
                    (item.Key != null && item.Key.ToLowerInvariant().Contains(searchLower));
            }

            private static void DrawWorkbenchStatusLine(UnityEngine.Rect rect, bool hasUnsavedChanges)
            {
                bool showTemporaryStatus = !string.IsNullOrWhiteSpace(_workbenchStatusText) && Time.realtimeSinceStartup <= _workbenchStatusUntilTime;
                string status = showTemporaryStatus ? _workbenchStatusText : "";
                UnityEngine.Color color = UnityEngine.Color.gray;

                if (_isSavingModifications)
                {
                    status = "ATC_Workbench_Saving".Translate().ToString();
                }
                else if (string.IsNullOrWhiteSpace(status) && hasUnsavedChanges)
                {
                    status = "ATC_Workbench_UnsavedChanges".Translate().ToString();
                }
                else if (string.IsNullOrWhiteSpace(status) && _activeWorkbenchFocus != null && _activeWorkbenchFocus.FromGlobalSearch)
                {
                    status = "ATC_Workbench_FocusedSearchResult".Translate(_activeWorkbenchFocus.SearchText ?? "").ToString();
                }
                else if (string.IsNullOrWhiteSpace(status))
                {
                    status = "ATC_Workbench_SaveSynced".Translate().ToString();
                }

                if (_isSavingModifications)
                {
                    color = UnityEngine.Color.yellow;
                }
                else if (hasUnsavedChanges)
                {
                    color = new UnityEngine.Color(1f, 0.82f, 0.35f);
                }

                UnityEngine.GUI.color = color;
                Verse.Text.Font = Verse.GameFont.Tiny;
                Verse.Text.WordWrap = false;
                Verse.Widgets.Label(rect, status);
                Verse.Text.WordWrap = true;
                Verse.Text.Font = Verse.GameFont.Small;
                UnityEngine.GUI.color = UnityEngine.Color.white;
            }

            private static void SetWorkbenchStatus(string statusText)
            {
                _workbenchStatusText = statusText ?? "";
                _workbenchStatusUntilTime = Time.realtimeSinceStartup + 4f;
            }

            private static void TranslateWorkbenchOriginalText(WorkbenchItem item)
            {
                if (item == null || item.IsTranslatingOriginal || _isSavingModifications) return;

                string sourceText = (item.OriginalText ?? "").Trim();
                if (string.IsNullOrWhiteSpace(sourceText)) return;

                if (AutoTranslatorSettings.IsRunning)
                {
                    Verse.Messages.Message("ATC_Workbench_TranslateOriginalBusyGlobal".Translate(), MessageTypeDefOf.RejectInput, false);
                    return;
                }

                if (!AutoTranslatorAPI.HasAnyReadyConfig())
                {
                    Verse.Messages.Message("ATC_EmptyConfigWarning".Translate(), MessageTypeDefOf.RejectInput, false);
                    return;
                }

                item.IsTranslatingOriginal = true;
                SetWorkbenchStatus("ATC_Workbench_TranslateOriginalStarted".Translate().ToString());
                TargetLanguage targetLanguage = AutoTranslatorMod.Settings.TargetLang;
                AutoTranslatorSettings.ResetPipelineCancellation();

                Task.Run(async () =>
                {
                    string translated = null;
                    string error = null;

                    try
                    {
                        List<string> result = await AutoTranslatorAPI.TranslateBatchAsync(
                            new List<string> { sourceText },
                            suppressFinalParseError: true);
                        if (result != null && result.Count == 1)
                        {
                            translated = (result[0] ?? "").Trim();
                        }
                        else
                        {
                            error = "empty result";
                        }
                    }
                    catch (Exception ex)
                    {
                        error = ex.Message;
                    }

                    ATC_Dispatcher.RunOnMainThread(() =>
                    {
                        item.IsTranslatingOriginal = false;

                        if (AutoTranslatorMod.Settings.TargetLang != targetLanguage)
                        {
                            SetWorkbenchStatus("ATC_Workbench_TranslateOriginalFailed".Translate().ToString());
                            return;
                        }

                        if (string.IsNullOrWhiteSpace(translated))
                        {
                            if (!string.IsNullOrWhiteSpace(error))
                            {
                                Verse.Log.Warning($"[AutoTranslationCore] Single workbench entry translation failed: {error}");
                            }

                            Verse.Messages.Message("ATC_Workbench_TranslateOriginalFailed".Translate(), MessageTypeDefOf.RejectInput, false);
                            SetWorkbenchStatus("ATC_Workbench_TranslateOriginalFailed".Translate().ToString());
                            return;
                        }

                        item.TranslatedText = translated;
                        RefreshWorkbenchItemModifiedState(item);
                        _retainedEditedCategory = GetWorkbenchItemCategory(item);
                        _retainedEditedKey = item.Key ?? "";
                        _categorizedDataVersion++;
                        InvalidateVisibleItemCache();
                        SetWorkbenchStatus("ATC_Workbench_TranslateOriginalDone".Translate().ToString());
                    });
                });
            }
        }
}
