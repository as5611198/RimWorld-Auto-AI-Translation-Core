using RimWorld;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using Verse;

namespace AutoTranslator_Core
{
    internal sealed class Window_FilteredMods : Window
    {
        private const float RowHeight = 94f;
        private string _searchText = "";
        private Vector2 _scrollPosition = Vector2.zero;
        private List<FilteredModInfo> _entries = new List<FilteredModInfo>();
        private List<FilteredModInfo> _displayEntries = new List<FilteredModInfo>();
        private readonly Dictionary<FilteredModInfo, EntryPresentation> _entryPresentations =
            new Dictionary<FilteredModInfo, EntryPresentation>();
        private int _cachedVersion = -1;

        private sealed class EntryPresentation
        {
            public string ReasonLabel = "";
            public string SourceSummary = "";
            public string Tooltip = "";
        }

        public override Vector2 InitialSize => new Vector2(940f, 680f);

        public Window_FilteredMods()
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
            RefreshEntries(true);
        }

        public override void DoWindowContents(Rect inRect)
        {
            bool previousBypass = Patch_GUI_Label_GUIContent.BypassInterceptor;
            Patch_GUI_Label_GUIContent.BypassInterceptor = true;
            try
            {
                RefreshEntries(false);

                int filteredCount = _entries.Count(entry => entry.IsFiltered);
                int forceCount = _entries.Count(entry => entry.Reason == FilteredModReason.ForceIncluded);
                Text.Font = GameFont.Medium;
                Widgets.Label(new Rect(0f, 0f, inRect.width - 260f, 34f),
                    "ATC_Filtered_Title".Translate(filteredCount));
                Text.Font = GameFont.Small;
                GUI.color = Color.gray;
                Widgets.Label(new Rect(0f, 34f, inRect.width, 42f),
                    "ATC_Filtered_Summary".Translate(forceCount));
                GUI.color = Color.white;

                Rect searchRect = new Rect(inRect.width - 250f, 0f, 250f, 30f);
                string nextSearch = Widgets.TextField(searchRect, _searchText ?? "");
                if (!string.Equals(nextSearch, _searchText, StringComparison.Ordinal))
                {
                    _searchText = nextSearch;
                    RebuildDisplayEntries();
                    _scrollPosition = Vector2.zero;
                }
                if (string.IsNullOrEmpty(_searchText))
                {
                    GUI.color = Color.gray;
                    Widgets.Label(new Rect(searchRect.x + 6f, searchRect.y + 3f, searchRect.width - 12f, searchRect.height),
                        "ATC_Filtered_Search".Translate());
                    GUI.color = Color.white;
                }

                Rect outRect = new Rect(0f, 82f, inRect.width, inRect.height - 134f);
                Rect viewRect = new Rect(0f, 0f, outRect.width - 18f,
                    Math.Max(outRect.height, _displayEntries.Count * RowHeight));
                Widgets.BeginScrollView(outRect, ref _scrollPosition, viewRect);

                int firstVisible = Mathf.Max(0, Mathf.FloorToInt(_scrollPosition.y / RowHeight) - 2);
                int lastVisible = Mathf.Min(
                    _displayEntries.Count - 1,
                    Mathf.CeilToInt((_scrollPosition.y + outRect.height) / RowHeight) + 2);
                for (int i = firstVisible; i <= lastVisible; i++)
                {
                    DrawEntryRow(_displayEntries[i], new Rect(0f, i * RowHeight, viewRect.width, RowHeight - 3f));
                }
                Widgets.EndScrollView();

                if (AutoTranslatorMod.IsValidModsCacheRefreshing)
                {
                    Widgets.FillableBar(
                        new Rect(0f, inRect.height - 43f, inRect.width - 160f, 30f),
                        AutoTranslatorMod.ValidModsCacheProgress);
                    Text.Anchor = TextAnchor.MiddleCenter;
                    Widgets.Label(
                        new Rect(0f, inRect.height - 43f, inRect.width - 160f, 30f),
                        "ATC_Filtered_Refreshing".Translate(
                            AutoTranslatorMod.ValidModsCacheProgressCurrent,
                            AutoTranslatorMod.ValidModsCacheProgressTotal));
                    Text.Anchor = TextAnchor.UpperLeft;
                }

                if (Widgets.ButtonText(new Rect(inRect.width - 145f, inRect.height - 45f, 145f, 36f),
                    "CloseButton".Translate()))
                {
                    Close();
                }
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

        private void DrawEntryRow(FilteredModInfo entry, Rect rowRect)
        {
            EntryPresentation presentation = GetEntryPresentation(entry);
            Widgets.DrawHighlightIfMouseover(rowRect);
            Widgets.DrawBox(rowRect, 1);
            TooltipHandler.TipRegion(rowRect, presentation.Tooltip);

            const float folderButtonWidth = 116f;
            const float forceButtonWidth = 126f;
            const float buttonGap = 7f;
            float buttonsX = rowRect.xMax - folderButtonWidth - forceButtonWidth - buttonGap - 8f;
            float textWidth = buttonsX - rowRect.x - 14f;

            Text.Font = GameFont.Small;
            Text.WordWrap = false;
            Widgets.Label(new Rect(rowRect.x + 7f, rowRect.y + 5f, textWidth, 24f),
                entry.Name ?? entry.PackageId);
            Text.Font = GameFont.Tiny;
            GUI.color = Color.gray;
            Widgets.Label(new Rect(rowRect.x + 7f, rowRect.y + 28f, textWidth, 18f), entry.PackageId ?? "");

            GUI.color = entry.IsFiltered
                ? new Color(1f, 0.68f, 0.42f)
                : new Color(0.48f, 0.9f, 0.62f);
            Widgets.Label(new Rect(rowRect.x + 7f, rowRect.y + 48f, textWidth, 19f), presentation.ReasonLabel);

            GUI.color = Color.gray;
            Widgets.Label(new Rect(rowRect.x + 7f, rowRect.y + 68f, textWidth, 18f), presentation.SourceSummary);
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            Text.WordWrap = true;

            Rect folderRect = new Rect(buttonsX, rowRect.y + 29f, folderButtonWidth, 34f);
            if (Widgets.ButtonText(folderRect, "ATC_Filtered_OpenFolder".Translate()))
            {
                OpenModFolder(entry);
            }
            TooltipHandler.TipRegion(folderRect, "ATC_Filtered_OpenFolderTip".Translate());

            Rect forceRect = new Rect(folderRect.xMax + buttonGap, folderRect.y, forceButtonWidth, folderRect.height);
            bool canToggleForce = entry.ForceEnabled || entry.CanForce;
            GUI.color = canToggleForce ? Color.white : Color.gray;
            string forceLabel = entry.ForceEnabled
                ? "ATC_Filtered_CancelForce".Translate()
                : "ATC_Filtered_ForceInclude".Translate();
            if (Widgets.ButtonText(forceRect, forceLabel) && canToggleForce)
            {
                ToggleForce(entry);
            }
            GUI.color = Color.white;
            TooltipHandler.TipRegion(forceRect,
                canToggleForce ? "ATC_Filtered_ForceTip".Translate() : "ATC_Filtered_ForceUnavailableTip".Translate());
        }

        private void ToggleForce(FilteredModInfo entry)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.PackageId)) return;
            if (entry.ForceEnabled)
            {
                SetForce(entry.PackageId, false);
                return;
            }

            Find.WindowStack.Add(new Dialog_MessageBox(
                "ATC_Filtered_ForceWarning".Translate(entry.Name ?? entry.PackageId),
                "ATC_Filtered_ForceConfirm".Translate(),
                () => SetForce(entry.PackageId, true),
                "ATC_Btn_Cancel".Translate(),
                null,
                "ATC_Filtered_ForceTitle".Translate()));
        }

        private void SetForce(string packageId, bool enabled)
        {
            AutoTranslatorMod.Settings.SetForceTranslationEnabled(packageId, enabled);
            LoadedModManager.GetMod<AutoTranslatorMod>()?.WriteSettings();
            _cachedVersion = -1;
            Messages.Message(
                enabled ? "ATC_Filtered_ForceEnabled".Translate() : "ATC_Filtered_ForceDisabled".Translate(),
                enabled ? MessageTypeDefOf.CautionInput : MessageTypeDefOf.NeutralEvent,
                false);
        }

        private static void OpenModFolder(FilteredModInfo entry)
        {
            string root = entry != null ? entry.RootDir : "";
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                Messages.Message("ATC_Filtered_FolderMissing".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }

            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = root,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Log.Warning("[AutoTranslationCore] Cannot open mod folder: " + ex.Message);
                Messages.Message("ATC_Filtered_FolderOpenFailed".Translate(ex.Message), MessageTypeDefOf.RejectInput, false);
            }
        }

        private void RefreshEntries(bool force)
        {
            int version = AutoTranslatorMod.ValidModsCacheVersion;
            if (!force && _cachedVersion == version) return;

            _entries = AutoTranslatorMod.GetFilteredModsCached();
            _cachedVersion = version;
            RebuildEntryPresentations();
            RebuildDisplayEntries();
        }

        private void RebuildEntryPresentations()
        {
            _entryPresentations.Clear();
            foreach (FilteredModInfo entry in _entries ?? new List<FilteredModInfo>())
            {
                _entryPresentations[entry] = BuildEntryPresentation(entry);
            }
        }

        private EntryPresentation GetEntryPresentation(FilteredModInfo entry)
        {
            if (entry != null && _entryPresentations.TryGetValue(entry, out EntryPresentation cached))
                return cached;

            EntryPresentation presentation = BuildEntryPresentation(entry);
            if (entry != null) _entryPresentations[entry] = presentation;
            return presentation;
        }

        private static EntryPresentation BuildEntryPresentation(FilteredModInfo entry)
        {
            if (entry == null) return new EntryPresentation();
            return new EntryPresentation
            {
                ReasonLabel = GetReasonLabel(entry),
                SourceSummary = BuildSourceSummary(entry),
                Tooltip = BuildTooltip(entry)
            };
        }

        private void RebuildDisplayEntries()
        {
            string search = (_searchText ?? "").Trim();
            IEnumerable<FilteredModInfo> query = _entries ?? new List<FilteredModInfo>();
            if (search.Length > 0)
            {
                query = query.Where(entry =>
                    (entry.Name ?? "").IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (entry.PackageId ?? "").IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    GetEntryPresentation(entry).ReasonLabel.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (entry.CandidateSourcePaths ?? new List<string>()).Any(
                        path => path.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0));
            }

            _displayEntries = query
                .OrderByDescending(entry => entry.IsFiltered)
                .ThenBy(entry => entry.Name ?? "", StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string GetReasonLabel(FilteredModInfo entry)
        {
            switch (entry.Reason)
            {
                case FilteredModReason.ToolOrPack: return "ATC_Filtered_ReasonTool".Translate();
                case FilteredModReason.TranslationPatch: return "ATC_Filtered_ReasonTranslationPatch".Translate();
                case FilteredModReason.TranslationBlacklist: return "ATC_Filtered_ReasonBlacklist".Translate();
                case FilteredModReason.MissingRoot: return "ATC_Filtered_ReasonMissingRoot".Translate();
                case FilteredModReason.UnsupportedSourceLayout: return "ATC_Filtered_ReasonUnsupportedLayout".Translate();
                case FilteredModReason.PartialSourcesSkipped: return "ATC_Filtered_ReasonPartialSources".Translate();
                case FilteredModReason.NoTranslationSources: return "ATC_Filtered_ReasonNoSources".Translate();
                case FilteredModReason.ForceIncluded: return "ATC_Filtered_ReasonForceIncluded".Translate();
                case FilteredModReason.ScanFailed: return "ATC_Filtered_ReasonScanFailed".Translate(entry.Error ?? "");
                default: return "ATC_Filtered_ReasonNoSources".Translate();
            }
        }

        private static string BuildSourceSummary(FilteredModInfo entry)
        {
            List<string> paths = entry.CandidateSourcePaths ?? new List<string>();
            if (paths.Count == 0) return "ATC_Filtered_NoCandidatePaths".Translate();
            string summary = string.Join(", ", paths.Take(2).Select(path => GetRelativePath(entry.RootDir, path)));
            if (paths.Count > 2) summary += "  +" + (paths.Count - 2);
            return "ATC_Filtered_CandidatePaths".Translate(summary);
        }

        private static string BuildTooltip(FilteredModInfo entry)
        {
            List<string> lines = new List<string>
            {
                entry.Name ?? "",
                entry.PackageId ?? "",
                GetReasonLabel(entry),
                entry.RootDir ?? ""
            };
            if (!string.IsNullOrWhiteSpace(entry.Error)) lines.Add(entry.Error);
            if (entry.CandidateSourcePaths != null && entry.CandidateSourcePaths.Count > 0)
            {
                lines.Add("");
                lines.Add("ATC_Filtered_CandidateHeader".Translate());
                const int maxTooltipPaths = 10;
                lines.AddRange(entry.CandidateSourcePaths
                    .Take(maxTooltipPaths)
                    .Select(path => GetRelativePath(entry.RootDir, path)));
                int remaining = entry.CandidateSourcePaths.Count - maxTooltipPaths;
                if (remaining > 0) lines.Add("... +" + remaining);
            }
            return string.Join("\n", lines);
        }

        private static string GetRelativePath(string root, string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            if (string.IsNullOrWhiteSpace(root)) return path;
            try
            {
                string normalizedRoot = Path.GetFullPath(root)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string normalizedPath = Path.GetFullPath(path);
                if (normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return normalizedPath.Substring(normalizedRoot.Length + 1);
                }
            }
            catch { }
            return path;
        }
    }
}
