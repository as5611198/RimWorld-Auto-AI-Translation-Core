using RimWorld;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;

namespace AutoTranslator_Core
{
    internal sealed class Window_UnresolvedTranslations : Window
    {
        private const float ModuleRowHeight = 46f;
        private const float GroupRowHeight = 32f;
        private const float EntryRowHeight = 100f;
        private const string AllGroupsKey = "all";

        private readonly HashSet<string> _selectedEntryIds =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private List<TranslationUnresolvedEntry> _entries = new List<TranslationUnresolvedEntry>();
        private string _selectedPackageId = string.Empty;
        private string _selectedGroupKey = AllGroupsKey;
        private string _searchText = string.Empty;
        private Vector2 _moduleScroll = Vector2.zero;
        private Vector2 _groupScroll = Vector2.zero;
        private Vector2 _entryScroll = Vector2.zero;

        public override Vector2 InitialSize => new Vector2(1000f, 700f);

        public Window_UnresolvedTranslations()
        {
            doCloseButton = false;
            doCloseX = true;
            closeOnAccept = false;
            closeOnCancel = true;
            forcePause = true;
            absorbInputAroundWindow = true;
        }

        public override void PreOpen()
        {
            base.PreOpen();
            RefreshSnapshot();
        }

        public override void DoWindowContents(Rect inRect)
        {
            bool previousBypass = Patch_GUI_Label_GUIContent.BypassInterceptor;
            Patch_GUI_Label_GUIContent.BypassInterceptor = true;
            try
            {
                DrawContents(inRect);
            }
            finally
            {
                Text.Font = GameFont.Small;
                Text.Anchor = TextAnchor.UpperLeft;
                Text.WordWrap = true;
                GUI.color = Color.white;
                Patch_GUI_Label_GUIContent.BypassInterceptor = previousBypass;
            }
        }

        private void DrawContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, 0f, inRect.width - 370f, 34f), "ATC_Unresolved_Title".Translate(_entries.Count));
            Text.Font = GameFont.Small;
            GUI.color = Color.gray;
            Widgets.Label(new Rect(0f, 36f, inRect.width, 24f), "ATC_Unresolved_Summary".Translate());
            GUI.color = Color.white;

            Rect searchRect = new Rect(inRect.width - 350f, 0f, 350f, 30f);
            _searchText = Widgets.TextField(searchRect, _searchText ?? string.Empty);
            if (string.IsNullOrEmpty(_searchText))
            {
                GUI.color = Color.gray;
                Widgets.Label(new Rect(searchRect.x + 6f, searchRect.y + 3f, searchRect.width - 12f, searchRect.height),
                    "ATC_Unresolved_Search".Translate());
                GUI.color = Color.white;
            }

            if (_entries.Count == 0)
            {
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(new Rect(0f, 85f, inRect.width, inRect.height - 155f), "ATC_Unresolved_Empty".Translate());
                Text.Anchor = TextAnchor.UpperLeft;
                if (Widgets.ButtonText(new Rect(inRect.width - 150f, inRect.height - 44f, 150f, 40f), "CloseButton".Translate()))
                    Close();
                return;
            }

            float contentY = 70f;
            float bottomY = inRect.height - 52f;
            float contentHeight = bottomY - contentY - 8f;
            const float gap = 8f;
            const float moduleWidth = 210f;
            const float groupWidth = 235f;
            Rect moduleRect = new Rect(0f, contentY, moduleWidth, contentHeight);
            Rect groupRect = new Rect(moduleRect.xMax + gap, contentY, groupWidth, contentHeight);
            Rect entryRect = new Rect(groupRect.xMax + gap, contentY, inRect.width - groupRect.xMax - gap, contentHeight);

            DrawModules(moduleRect);
            DrawGroups(groupRect);
            DrawEntries(entryRect);
            DrawActions(new Rect(0f, bottomY, inRect.width, 44f));
        }

        private void DrawModules(Rect rect)
        {
            DrawPanel(rect);
            DrawPanelHeader(new Rect(rect.x + 8f, rect.y + 5f, rect.width - 16f, 26f), "ATC_Unresolved_Mods".Translate());
            Rect outRect = new Rect(rect.x + 5f, rect.y + 34f, rect.width - 10f, rect.height - 39f);
            List<IGrouping<string, TranslationUnresolvedEntry>> groups = _entries
                .GroupBy(entry => entry.PackageId ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => GetModName(group.First()), StringComparer.OrdinalIgnoreCase)
                .ToList();
            Rect viewRect = new Rect(0f, 0f, outRect.width - 16f, Math.Max(outRect.height, groups.Count * ModuleRowHeight));
            Widgets.BeginScrollView(outRect, ref _moduleScroll, viewRect);
            for (int i = 0; i < groups.Count; i++)
            {
                IGrouping<string, TranslationUnresolvedEntry> group = groups[i];
                Rect rowRect = new Rect(0f, i * ModuleRowHeight, viewRect.width, ModuleRowHeight - 2f);
                bool selected = string.Equals(_selectedPackageId, group.Key, StringComparison.OrdinalIgnoreCase);
                if (selected) Widgets.DrawHighlightSelected(rowRect);
                else if (Mouse.IsOver(rowRect)) Widgets.DrawHighlight(rowRect);

                Text.Font = GameFont.Small;
                Widgets.Label(new Rect(rowRect.x + 6f, rowRect.y + 3f, rowRect.width - 12f, 22f), GetModName(group.First()));
                Text.Font = GameFont.Tiny;
                GUI.color = Color.gray;
                Widgets.Label(new Rect(rowRect.x + 6f, rowRect.y + 25f, rowRect.width - 12f, 17f),
                    GetTargetLanguage(group.First()) + "  (" + group.Count() + ")");
                GUI.color = Color.white;
                Text.Font = GameFont.Small;
                TooltipHandler.TipRegion(rowRect, group.Key ?? string.Empty);
                if (Widgets.ButtonInvisible(rowRect))
                {
                    _selectedPackageId = group.Key;
                    _selectedGroupKey = AllGroupsKey;
                    _groupScroll = Vector2.zero;
                    _entryScroll = Vector2.zero;
                }
            }
            Widgets.EndScrollView();
        }

        private void DrawGroups(Rect rect)
        {
            DrawPanel(rect);
            DrawPanelHeader(new Rect(rect.x + 8f, rect.y + 5f, rect.width - 16f, 26f), "ATC_Unresolved_Groups".Translate());
            Rect outRect = new Rect(rect.x + 5f, rect.y + 34f, rect.width - 10f, rect.height - 39f);
            List<GroupRow> rows = BuildGroupRows();
            Rect viewRect = new Rect(0f, 0f, outRect.width - 16f, Math.Max(outRect.height, rows.Count * GroupRowHeight));
            Widgets.BeginScrollView(outRect, ref _groupScroll, viewRect);
            for (int i = 0; i < rows.Count; i++)
            {
                GroupRow row = rows[i];
                Rect rowRect = new Rect(0f, i * GroupRowHeight, viewRect.width, GroupRowHeight - 1f);
                bool selected = string.Equals(_selectedGroupKey, row.Key, StringComparison.OrdinalIgnoreCase);
                if (selected) Widgets.DrawHighlightSelected(rowRect);
                else if (Mouse.IsOver(rowRect)) Widgets.DrawHighlight(rowRect);

                Text.Font = row.Depth == 0 ? GameFont.Small : GameFont.Tiny;
                Rect labelRect = new Rect(rowRect.x + 6f + row.Depth * 14f, rowRect.y + 5f,
                    rowRect.width - 12f - row.Depth * 14f, rowRect.height - 6f);
                Widgets.Label(labelRect, row.Label + "  (" + row.Count + ")");
                TooltipHandler.TipRegion(rowRect, row.Tooltip ?? row.Label);
                if (Widgets.ButtonInvisible(rowRect))
                {
                    _selectedGroupKey = row.Key;
                    _entryScroll = Vector2.zero;
                }
            }
            Text.Font = GameFont.Small;
            Widgets.EndScrollView();
        }

        private void DrawEntries(Rect rect)
        {
            DrawPanel(rect);
            List<TranslationUnresolvedEntry> visible = GetVisibleEntries();
            string header = "ATC_Unresolved_Entries".Translate(visible.Count);
            DrawPanelHeader(new Rect(rect.x + 8f, rect.y + 5f, rect.width - 16f, 26f), header);
            Rect outRect = new Rect(rect.x + 5f, rect.y + 34f, rect.width - 10f, rect.height - 39f);
            Rect viewRect = new Rect(0f, 0f, outRect.width - 16f, Math.Max(outRect.height, visible.Count * EntryRowHeight));
            Widgets.BeginScrollView(outRect, ref _entryScroll, viewRect);
            for (int i = 0; i < visible.Count; i++)
            {
                DrawEntryRow(new Rect(0f, i * EntryRowHeight, viewRect.width, EntryRowHeight - 3f), visible[i]);
            }
            Widgets.EndScrollView();
        }

        private void DrawEntryRow(Rect rowRect, TranslationUnresolvedEntry entry)
        {
            if (Mouse.IsOver(rowRect)) Widgets.DrawHighlight(rowRect);
            Widgets.DrawBox(rowRect, 1);

            bool fileLevelFailure = TranslationUnresolvedManager.IsFileLevelFailure(entry);
            if (!fileLevelFailure)
            {
                bool selected = _selectedEntryIds.Contains(entry.Id);
                Widgets.Checkbox(new Vector2(rowRect.x + 7f, rowRect.y + 7f), ref selected, 24f);
                if (selected) _selectedEntryIds.Add(entry.Id);
                else _selectedEntryIds.Remove(entry.Id);
            }
            else
            {
                _selectedEntryIds.Remove(entry.Id);
            }

            const float manualWidth = 88f;
            Rect manualRect = new Rect(rowRect.xMax - manualWidth - 7f, rowRect.y + 7f, manualWidth, 27f);
            if (!fileLevelFailure && Widgets.ButtonText(manualRect, "ATC_Unresolved_Manual".Translate()))
            {
                OpenInWorkbench(entry);
                return;
            }

            float textX = rowRect.x + 38f;
            float textWidth = manualRect.x - textX - 7f;
            Text.Font = GameFont.Small;
            Text.WordWrap = false;
            Widgets.Label(new Rect(textX, rowRect.y + 5f, textWidth, 24f), entry.Key ?? string.Empty);
            Text.WordWrap = true;
            Text.Font = GameFont.Tiny;
            GUI.color = new Color(1f, 0.58f, 0.48f);
            Widgets.Label(new Rect(textX, rowRect.y + 30f, textWidth, 19f),
                GetReasonLabel(entry) + "  |  " + "ATC_Unresolved_Attempts".Translate(entry.Attempts));
            GUI.color = Color.white;
            Widgets.Label(new Rect(textX, rowRect.y + 51f, rowRect.width - textX + rowRect.x - 8f, 40f),
                entry.SourceText ?? string.Empty);

            TooltipHandler.TipRegion(rowRect, BuildEntryTooltip(entry));
        }

        private void DrawActions(Rect rect)
        {
            List<TranslationUnresolvedEntry> visible = GetVisibleEntries();
            List<TranslationUnresolvedEntry> selected = GetSelectedEntries();
            float x = rect.x;
            if (Widgets.ButtonText(new Rect(x, rect.y + 2f, 120f, 40f), "ATC_Unresolved_SelectVisible".Translate()))
            {
                foreach (TranslationUnresolvedEntry entry in visible.Where(
                    candidate => !TranslationUnresolvedManager.IsFileLevelFailure(candidate)))
                {
                    _selectedEntryIds.Add(entry.Id);
                }
            }
            x += 128f;
            if (Widgets.ButtonText(new Rect(x, rect.y + 2f, 120f, 40f), "ATC_Unresolved_ClearSelection".Translate()))
            {
                _selectedEntryIds.Clear();
            }

            Text.Anchor = TextAnchor.MiddleLeft;
            GUI.color = Color.gray;
            Widgets.Label(new Rect(x + 130f, rect.y + 2f, 170f, 40f), "ATC_Unresolved_Selected".Translate(selected.Count));
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;

            const float actionWidth = 180f;
            Rect ignoreRect = new Rect(rect.xMax - actionWidth * 2f - 8f, rect.y + 2f, actionWidth, 40f);
            Rect retryRect = new Rect(rect.xMax - actionWidth, rect.y + 2f, actionWidth, 40f);
            bool hasSelection = selected.Count > 0;
            bool canKeepOriginal = hasSelection && !AutoTranslatorSettings.IsRunning;
            GUI.color = canKeepOriginal ? Color.white : Color.grey;
            if (Widgets.ButtonText(ignoreRect, "ATC_Unresolved_KeepOriginal".Translate()))
            {
                if (!hasSelection)
                    Messages.Message("ATC_Unresolved_NoSelection".Translate(), MessageTypeDefOf.RejectInput, false);
                else if (AutoTranslatorSettings.IsRunning)
                    Messages.Message("ATC_Unresolved_Running".Translate(), MessageTypeDefOf.RejectInput, false);
                else
                    ConfirmIgnore(selected);
            }

            bool canRetry = hasSelection && !AutoTranslatorSettings.IsRunning && AutoTranslatorAPI.HasAnyReadyConfig();
            GUI.color = canRetry ? new Color(0.6f, 0.9f, 0.6f) : Color.grey;
            if (Widgets.ButtonText(retryRect, "ATC_Unresolved_RetryAI".Translate()))
            {
                if (!hasSelection)
                    Messages.Message("ATC_Unresolved_NoSelection".Translate(), MessageTypeDefOf.RejectInput, false);
                else if (AutoTranslatorSettings.IsRunning)
                    Messages.Message("ATC_Unresolved_Running".Translate(), MessageTypeDefOf.RejectInput, false);
                else if (!AutoTranslatorAPI.HasAnyReadyConfig())
                    Messages.Message("ATC_EmptyConfigWarning".Translate(), MessageTypeDefOf.RejectInput, false);
                else
                {
                    AutoTranslatorScanner.StartUnresolvedRetry(selected);
                    Close();
                }
            }
            GUI.color = Color.white;
        }

        private void ConfirmIgnore(List<TranslationUnresolvedEntry> selected)
        {
            List<TranslationUnresolvedEntry> entries = selected.Select(CloneEntry).ToList();
            List<string> ids = selected.Select(entry => entry.Id).ToList();
            Find.WindowStack.Add(new Dialog_MessageBox(
                "ATC_Unresolved_ConfirmIgnore".Translate(ids.Count),
                "ATC_Unresolved_ConfirmButton".Translate(),
                () =>
                {
                    if (AutoTranslatorSettings.IsRunning)
                    {
                        Messages.Message("ATC_Unresolved_Running".Translate(), MessageTypeDefOf.RejectInput, false);
                        return;
                    }
                    AutoTranslatorScanner.KeepOriginalForUnresolved(entries);
                    foreach (string id in ids) _selectedEntryIds.Remove(id);
                    RefreshSnapshot();
                },
                "ATC_Btn_Cancel".Translate(),
                null,
                "ATC_Unresolved_TitleShort".Translate()));
        }

        private static TranslationUnresolvedEntry CloneEntry(TranslationUnresolvedEntry source)
        {
            return new TranslationUnresolvedEntry
            {
                Id = source.Id,
                TargetLanguage = source.TargetLanguage,
                PackageId = source.PackageId,
                ModName = source.ModName,
                Bucket = source.Bucket,
                DefType = source.DefType,
                Key = source.Key,
                SourceText = source.SourceText,
                SourceFile = source.SourceFile,
                TargetFile = source.TargetFile,
                Reason = source.Reason,
                Detail = source.Detail,
                Attempts = source.Attempts,
                SourceHash = source.SourceHash,
                State = source.State
            };
        }

        private void OpenInWorkbench(TranslationUnresolvedEntry entry)
        {
            string currentTarget = AutoTranslatorMod.Settings.TargetLang.ToString();
            if (!string.IsNullOrWhiteSpace(entry.TargetLanguage) &&
                !string.Equals(entry.TargetLanguage, currentTarget, StringComparison.OrdinalIgnoreCase))
            {
                Messages.Message(
                    "ATC_Unresolved_WrongLanguage".Translate(entry.TargetLanguage),
                    MessageTypeDefOf.RejectInput,
                    false);
                return;
            }
            if (!AutoTranslatorScanner.IsUnresolvedEntryCurrent(entry))
            {
                Messages.Message(
                    "ATC_Unresolved_SourceChanged".Translate(entry.Key),
                    MessageTypeDefOf.RejectInput,
                    false);
                return;
            }

            ModMetaData mod = ModLister.AllInstalledMods.FirstOrDefault(candidate =>
                candidate != null && string.Equals(candidate.PackageId, entry.PackageId, StringComparison.OrdinalIgnoreCase));
            if (mod == null)
            {
                Messages.Message("ATC_Unresolved_ModNotFound".Translate(entry.ModName), MessageTypeDefOf.RejectInput, false);
                return;
            }

            string category = string.Equals(entry.Bucket, "Keyed", StringComparison.OrdinalIgnoreCase)
                ? "Keyed"
                : string.IsNullOrWhiteSpace(entry.DefType) ? "General" : entry.DefType;
            if (TranslationWorkbenchTab.OpenAndFocus(mod, category, entry.Key, entry.Key)) Close();
        }

        private List<GroupRow> BuildGroupRows()
        {
            List<TranslationUnresolvedEntry> modEntries = GetSelectedModEntries();
            List<GroupRow> rows = new List<GroupRow>
            {
                new GroupRow
                {
                    Key = AllGroupsKey,
                    Label = "ATC_Unresolved_All".Translate().ToString(),
                    Tooltip = "ATC_Unresolved_All".Translate().ToString(),
                    Count = modEntries.Count,
                    Depth = 0
                }
            };

            foreach (IGrouping<string, TranslationUnresolvedEntry> bucketGroup in modEntries
                .GroupBy(entry => entry.Bucket ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
            {
                string bucketKey = "bucket|" + bucketGroup.Key;
                rows.Add(new GroupRow
                {
                    Key = bucketKey,
                    Label = string.IsNullOrWhiteSpace(bucketGroup.Key) ? "Unknown" : bucketGroup.Key,
                    Tooltip = bucketGroup.Key,
                    Count = bucketGroup.Count(),
                    Depth = 0,
                    Bucket = bucketGroup.Key
                });

                if (string.Equals(bucketGroup.Key, "Keyed", StringComparison.OrdinalIgnoreCase))
                {
                    AddSourceRows(rows, bucketGroup, bucketGroup.Key, string.Empty, 1);
                    continue;
                }

                foreach (IGrouping<string, TranslationUnresolvedEntry> defGroup in bucketGroup
                    .GroupBy(entry => entry.DefType ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
                {
                    string defType = string.IsNullOrWhiteSpace(defGroup.Key) ? "General" : defGroup.Key;
                    rows.Add(new GroupRow
                    {
                        Key = bucketKey + "|def|" + defType,
                        Label = defType,
                        Tooltip = defType,
                        Count = defGroup.Count(),
                        Depth = 1,
                        Bucket = bucketGroup.Key,
                        DefType = defGroup.Key
                    });
                    AddSourceRows(rows, defGroup, bucketGroup.Key, defGroup.Key, 2);
                }
            }
            return rows;
        }

        private static void AddSourceRows(
            List<GroupRow> rows,
            IEnumerable<TranslationUnresolvedEntry> entries,
            string bucket,
            string defType,
            int depth)
        {
            foreach (IGrouping<string, TranslationUnresolvedEntry> sourceGroup in entries
                .GroupBy(entry => entry.SourceFile ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
            {
                string sourceFile = sourceGroup.Key ?? string.Empty;
                string label = string.IsNullOrWhiteSpace(sourceFile)
                    ? "ATC_Unresolved_UnknownSource".Translate().ToString()
                    : ShortenSourceFile(sourceFile);
                rows.Add(new GroupRow
                {
                    Key = "source|" + bucket + "|" + defType + "|" + sourceFile,
                    Label = label,
                    Tooltip = string.IsNullOrWhiteSpace(sourceFile) ? label : sourceFile,
                    Count = sourceGroup.Count(),
                    Depth = depth,
                    Bucket = bucket,
                    DefType = defType,
                    SourceFile = sourceFile
                });
            }
        }

        private List<TranslationUnresolvedEntry> GetVisibleEntries()
        {
            IEnumerable<TranslationUnresolvedEntry> query = GetSelectedModEntries();
            GroupRow selectedGroup = BuildGroupRows().FirstOrDefault(row =>
                string.Equals(row.Key, _selectedGroupKey, StringComparison.OrdinalIgnoreCase));
            if (selectedGroup != null && !string.Equals(selectedGroup.Key, AllGroupsKey, StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(selectedGroup.Bucket))
                    query = query.Where(entry => string.Equals(entry.Bucket, selectedGroup.Bucket, StringComparison.OrdinalIgnoreCase));
                if (selectedGroup.DefType != null)
                    query = query.Where(entry => string.Equals(entry.DefType ?? string.Empty, selectedGroup.DefType, StringComparison.OrdinalIgnoreCase));
                if (selectedGroup.SourceFile != null)
                    query = query.Where(entry => string.Equals(entry.SourceFile ?? string.Empty, selectedGroup.SourceFile, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrWhiteSpace(_searchText))
            {
                string search = _searchText.Trim();
                query = query.Where(entry => Contains(entry.Key, search) ||
                    Contains(entry.SourceText, search) ||
                    Contains(entry.Reason, search) ||
                    Contains(entry.Detail, search) ||
                    Contains(entry.SourceFile, search));
            }

            return query
                .OrderBy(entry => entry.Key ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private List<TranslationUnresolvedEntry> GetSelectedModEntries()
        {
            return _entries
                .Where(entry => string.Equals(entry.PackageId, _selectedPackageId, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        private List<TranslationUnresolvedEntry> GetSelectedEntries()
        {
            return _entries
                .Where(entry => _selectedEntryIds.Contains(entry.Id) &&
                                !TranslationUnresolvedManager.IsFileLevelFailure(entry))
                .ToList();
        }

        private void RefreshSnapshot()
        {
            _entries = TranslationUnresolvedManager.Snapshot()
                .Where(entry => entry != null && string.Equals(
                    entry.State,
                    TranslationUnresolvedStates.Pending,
                    StringComparison.OrdinalIgnoreCase))
                .ToList();

            HashSet<string> currentIds = new HashSet<string>(_entries.Select(entry => entry.Id), StringComparer.OrdinalIgnoreCase);
            _selectedEntryIds.RemoveWhere(id => !currentIds.Contains(id));
            if (_entries.Count == 0)
            {
                _selectedPackageId = string.Empty;
                _selectedGroupKey = AllGroupsKey;
                return;
            }

            if (!_entries.Any(entry => string.Equals(entry.PackageId, _selectedPackageId, StringComparison.OrdinalIgnoreCase)))
            {
                _selectedPackageId = _entries
                    .OrderBy(GetModName, StringComparer.OrdinalIgnoreCase)
                    .First()
                    .PackageId;
                _selectedGroupKey = AllGroupsKey;
                _moduleScroll = Vector2.zero;
                _groupScroll = Vector2.zero;
                _entryScroll = Vector2.zero;
            }

            if (!BuildGroupRows().Any(row => string.Equals(
                    row.Key,
                    _selectedGroupKey,
                    StringComparison.OrdinalIgnoreCase)))
            {
                _selectedGroupKey = AllGroupsKey;
                _groupScroll = Vector2.zero;
                _entryScroll = Vector2.zero;
            }
        }

        private static string GetModName(TranslationUnresolvedEntry entry)
        {
            if (entry == null) return "ATC_Unresolved_UnknownMod".Translate().ToString();
            return string.IsNullOrWhiteSpace(entry.ModName)
                ? entry.PackageId ?? "ATC_Unresolved_UnknownMod".Translate().ToString()
                : entry.ModName;
        }

        private static string GetTargetLanguage(TranslationUnresolvedEntry entry)
        {
            return entry != null && !string.IsNullOrWhiteSpace(entry.TargetLanguage)
                ? entry.TargetLanguage
                : AutoTranslatorMod.Settings.TargetLang.ToString();
        }

        private static string GetReasonLabel(TranslationUnresolvedEntry entry)
        {
            string reason = entry != null ? entry.Reason ?? string.Empty : string.Empty;
            string key;
            switch (reason)
            {
                case TranslationUnresolvedReasons.ApiFailure: key = "ATC_Unresolved_ReasonApi"; break;
                case TranslationUnresolvedReasons.MalformedResponse: key = "ATC_Unresolved_ReasonMalformed"; break;
                case TranslationUnresolvedReasons.EmptyResponse: key = "ATC_Unresolved_ReasonEmpty"; break;
                case TranslationUnresolvedReasons.EnglishResidual: key = "ATC_Unresolved_ReasonEnglish"; break;
                case TranslationUnresolvedReasons.WrongChineseVariant: key = "ATC_Unresolved_ReasonVariant"; break;
                case TranslationUnresolvedReasons.WrongTargetLanguage: key = "ATC_Unresolved_ReasonWrongLanguage"; break;
                case TranslationUnresolvedReasons.ProtectedTokenMismatch: key = "ATC_Unresolved_ReasonToken"; break;
                case TranslationUnresolvedReasons.FormatArgumentMismatch: key = "ATC_Unresolved_ReasonFormat"; break;
                case TranslationUnresolvedReasons.TitleTagMismatch: key = "ATC_Unresolved_ReasonTitleTag"; break;
                case TranslationUnresolvedReasons.SaveFailure: key = "ATC_Unresolved_ReasonSave"; break;
                case TranslationUnresolvedReasons.SourceFailure: key = "ATC_Unresolved_ReasonSource"; break;
                case TranslationUnresolvedReasons.PolicyReview: key = "ATC_Unresolved_ReasonPolicyReview"; break;
                case TranslationUnresolvedReasons.PolicyAgentFailure: key = "ATC_Unresolved_ReasonPolicyAgentFailure"; break;
                default: key = "ATC_Unresolved_ReasonUnknown"; break;
            }
            return key.Translate().ToString();
        }

        private static string BuildEntryTooltip(TranslationUnresolvedEntry entry)
        {
            List<string> lines = new List<string>
            {
                entry.Key ?? string.Empty,
                entry.SourceText ?? string.Empty,
                string.Empty,
                GetReasonLabel(entry)
            };
            if (!string.IsNullOrWhiteSpace(entry.Detail)) lines.Add(entry.Detail);
            if (!string.IsNullOrWhiteSpace(entry.SourceFile)) lines.Add(entry.SourceFile);
            return string.Join("\n", lines);
        }

        private static string ShortenSourceFile(string sourceFile)
        {
            string normalized = (sourceFile ?? string.Empty).Replace('\\', '/').Trim('/');
            string[] parts = normalized.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length <= 2) return normalized;
            return parts[parts.Length - 2] + "/" + parts[parts.Length - 1];
        }

        private static bool Contains(string value, string search)
        {
            return !string.IsNullOrEmpty(value) && value.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void DrawPanel(Rect rect)
        {
            Widgets.DrawMenuSection(rect);
        }

        private static void DrawPanelHeader(Rect rect, string label)
        {
            Text.Font = GameFont.Small;
            Widgets.Label(rect, label ?? string.Empty);
        }

        private sealed class GroupRow
        {
            public string Key;
            public string Label;
            public string Tooltip;
            public int Count;
            public int Depth;
            public string Bucket;
            public string DefType;
            public string SourceFile;
        }
    }
}
