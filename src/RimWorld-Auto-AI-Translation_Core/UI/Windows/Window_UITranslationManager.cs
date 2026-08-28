using RimWorld;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEngine;
using Verse;

namespace AutoTranslator_Core
{
    public sealed class Window_UITranslationManager : Window
    {
        private const float RowHeight = 54f;
        private List<UITranslationManagedEntry> _allEntries = new List<UITranslationManagedEntry>();
        private List<UITranslationManagedEntry> _displayEntries = new List<UITranslationManagedEntry>();
        private Vector2 _scrollPosition = Vector2.zero;
        private string _searchText = string.Empty;
        private string _packageFilter = string.Empty;
        private UITranslationManagedEntry _selected;
        private string _translationDraft = string.Empty;

        public override Vector2 InitialSize => new Vector2(1080f, 760f);

        public Window_UITranslationManager()
        {
            doCloseX = true;
            forcePause = true;
            absorbInputAroundWindow = true;
            RefreshEntries();
        }

        public override void PostClose()
        {
            base.PostClose();
            UIInterceptor.FlushCache();
            LoadedModManager.GetMod<AutoTranslatorMod>()?.WriteSettings();
        }

        public override void DoWindowContents(Rect inRect)
        {
            bool previousBypass = Patch_GUI_Label_GUIContent.BypassInterceptor;
            Patch_GUI_Label_GUIContent.BypassInterceptor = true;
            try
            {
                DrawHeader(inRect);
                float leftWidth = Mathf.Min(510f, inRect.width * 0.49f);
                DrawEntryList(new Rect(0f, 88f, leftWidth, inRect.height - 136f));
                DrawEditor(new Rect(leftWidth + 18f, 88f, inRect.width - leftWidth - 18f, inRect.height - 136f));

                Rect folderRect = new Rect(0f, inRect.height - 40f, 180f, 35f);
                if (Widgets.ButtonText(folderRect, "ATC_UIManager_OpenFolder".Translate())) OpenCacheFolder();

                Rect refreshRect = new Rect(folderRect.xMax + 10f, inRect.height - 40f, 150f, 35f);
                if (Widgets.ButtonText(refreshRect, "ATC_UIManager_Refresh".Translate())) RefreshEntries();

                Rect closeRect = new Rect(inRect.width - 140f, inRect.height - 40f, 140f, 35f);
                if (Widgets.ButtonText(closeRect, "ATC_ContactAuthor_Close".Translate())) Close();
            }
            finally
            {
                GUI.color = Color.white;
                Text.Anchor = TextAnchor.UpperLeft;
                Patch_GUI_Label_GUIContent.BypassInterceptor = previousBypass;
            }
        }

        private void DrawHeader(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, 0f, inRect.width, 34f), "ATC_UIManager_Title".Translate());
            Text.Font = GameFont.Small;

            Rect searchRect = new Rect(0f, 42f, inRect.width * 0.58f, 32f);
            string nextSearch = Widgets.TextField(searchRect, _searchText ?? string.Empty);
            if (!string.Equals(nextSearch, _searchText, StringComparison.Ordinal))
            {
                _searchText = nextSearch;
                RebuildDisplayEntries();
            }
            if (string.IsNullOrEmpty(_searchText))
            {
                GUI.color = Color.gray;
                Widgets.Label(new Rect(searchRect.x + 6f, searchRect.y + 3f, searchRect.width - 12f, searchRect.height), "ATC_UIManager_Search".Translate());
                GUI.color = Color.white;
            }

            Rect packageRect = new Rect(searchRect.xMax + 10f, 42f, inRect.width - searchRect.width - 10f, 32f);
            string packageLabel = string.IsNullOrWhiteSpace(_packageFilter)
                ? "ATC_UIManager_AllMods".Translate().ToString()
                : GetPackageDisplayName(_packageFilter);
            if (Widgets.ButtonText(packageRect, packageLabel)) ShowPackageFilterMenu();
        }

        private void DrawEntryList(Rect outRect)
        {
            Widgets.DrawBox(outRect, 1);
            Rect inner = outRect.ContractedBy(4f);
            Rect viewRect = new Rect(0f, 0f, inner.width - 18f, Math.Max(inner.height, _displayEntries.Count * RowHeight));
            Widgets.BeginScrollView(inner, ref _scrollPosition, viewRect);
            int first = Mathf.Max(0, Mathf.FloorToInt(_scrollPosition.y / RowHeight) - 1);
            int last = Mathf.Min(_displayEntries.Count - 1, Mathf.CeilToInt((_scrollPosition.y + inner.height) / RowHeight) + 1);
            for (int index = first; index <= last; index++)
            {
                UITranslationManagedEntry entry = _displayEntries[index];
                Rect row = new Rect(0f, index * RowHeight, viewRect.width, RowHeight);
                if (_selected != null && string.Equals(_selected.Original, entry.Original, StringComparison.Ordinal))
                    Widgets.DrawHighlightSelected(row);
                else
                    Widgets.DrawHighlightIfMouseover(row);

                string status = entry.IsIgnored
                    ? "ATC_UIManager_StatusIgnored".Translate().ToString()
                    : string.IsNullOrWhiteSpace(entry.Translation)
                        ? "ATC_UIManager_StatusPending".Translate().ToString()
                        : "ATC_UIManager_StatusTranslated".Translate().ToString();
                string packageId = string.IsNullOrWhiteSpace(entry.PackageId)
                    ? "ATC_UIManager_UnknownMod".Translate().ToString()
                    : entry.PackageId;
                Rect labelRect = row.ContractedBy(5f);
                Widgets.Label(labelRect, Compact(entry.Original, 68) + "\n<size=10><color=#888888>" + Compact(packageId, 42) + " | " + status + "</color></size>");
                if (Widgets.ButtonInvisible(row)) SelectEntry(entry);
                Widgets.DrawLineHorizontal(row.x, row.yMax - 1f, row.width);
            }
            Widgets.EndScrollView();
        }

        private void DrawEditor(Rect rect)
        {
            Widgets.DrawBox(rect, 1);
            Rect content = rect.ContractedBy(10f);
            if (_selected == null)
            {
                GUI.color = Color.gray;
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(content, "ATC_UIManager_SelectEntry".Translate());
                Text.Anchor = TextAnchor.UpperLeft;
                GUI.color = Color.white;
                return;
            }

            float y = content.y;
            Text.Font = GameFont.Tiny;
            GUI.color = Color.gray;
            Widgets.Label(new Rect(content.x, y, content.width, 22f), "ATC_UIManager_Original".Translate());
            GUI.color = Color.white;
            y += 22f;
            Rect originalRect = new Rect(content.x, y, content.width, 94f);
            Widgets.DrawBoxSolid(originalRect, new Color(0.08f, 0.08f, 0.08f, 0.45f));
            Widgets.Label(originalRect.ContractedBy(6f), _selected.Original ?? string.Empty);
            y += 104f;

            GUI.color = Color.gray;
            Widgets.Label(new Rect(content.x, y, content.width, 22f), "ATC_UIManager_Translation".Translate());
            GUI.color = Color.white;
            y += 22f;
            _translationDraft = Widgets.TextArea(new Rect(content.x, y, content.width, 132f), _translationDraft ?? string.Empty);
            y += 142f;
            Text.Font = GameFont.Small;

            string packageId = _selected.PackageId;
            Widgets.Label(new Rect(content.x, y, content.width, 28f), "ATC_UIManager_SourceMod".Translate(
                string.IsNullOrWhiteSpace(packageId) ? "ATC_UIManager_UnknownMod".Translate().ToString() : GetPackageDisplayName(packageId)));
            y += 34f;

            bool ignored = _selected.IsIgnored;
            Rect ignoredRect = new Rect(content.x, y, content.width, 30f);
            Widgets.CheckboxLabeled(ignoredRect, "ATC_UIManager_IgnoreEntry".Translate(), ref ignored);
            if (ignored != _selected.IsIgnored)
            {
                UIInterceptor.SetManagedIgnored(_selected.Original, ignored);
                RefreshEntries(_selected.Original);
            }
            y += 38f;

            if (!string.IsNullOrWhiteSpace(packageId))
            {
                bool blocked = AutoTranslatorMod.Settings.IsUiTranslationModBlacklisted(packageId);
                Rect blockedRect = new Rect(content.x, y, content.width, 30f);
                Widgets.CheckboxLabeled(blockedRect, "ATC_UIManager_BlockMod".Translate(), ref blocked);
                if (blocked != AutoTranslatorMod.Settings.IsUiTranslationModBlacklisted(packageId))
                {
                    AutoTranslatorMod.Settings.SetUiTranslationModBlacklisted(packageId, blocked);
                    LoadedModManager.GetMod<AutoTranslatorMod>()?.WriteSettings();
                }
                y += 38f;
            }

            Rect saveRect = new Rect(content.x, content.yMax - 40f, 150f, 35f);
            GUI.color = new Color(0.65f, 1f, 0.7f);
            if (Widgets.ButtonText(saveRect, "ATC_UIManager_Save".Translate())) SaveTranslation();
            GUI.color = Color.white;

            Rect resetRect = new Rect(saveRect.xMax + 10f, content.yMax - 40f, 150f, 35f);
            if (Widgets.ButtonText(resetRect, "ATC_UIManager_Reset".Translate()))
            {
                UIInterceptor.RemoveManagedTranslation(_selected.Original);
                RefreshEntries(_selected.Original);
            }
        }

        private void SaveTranslation()
        {
            if (_selected == null) return;
            if (!UIInterceptor.TrySetManualTranslation(_selected.Original, _translationDraft, out string error))
            {
                Messages.Message(error, MessageTypeDefOf.RejectInput, false);
                return;
            }
            Messages.Message("ATC_UIManager_Saved".Translate(), MessageTypeDefOf.PositiveEvent, false);
            RefreshEntries(_selected.Original);
        }

        private void SelectEntry(UITranslationManagedEntry entry)
        {
            _selected = entry;
            _translationDraft = string.IsNullOrWhiteSpace(entry.Translation) ? entry.Original : entry.Translation;
        }

        private void RefreshEntries(string reselectOriginal = null)
        {
            string selectedOriginal = reselectOriginal ?? _selected?.Original;
            _allEntries = UIInterceptor.GetManagedEntries();
            RebuildDisplayEntries();
            _selected = _allEntries.FirstOrDefault(entry => string.Equals(entry.Original, selectedOriginal, StringComparison.Ordinal));
            if (_selected != null) _translationDraft = string.IsNullOrWhiteSpace(_selected.Translation) ? _selected.Original : _selected.Translation;
        }

        private void RebuildDisplayEntries()
        {
            string search = (_searchText ?? string.Empty).Trim();
            _displayEntries = _allEntries.Where(entry =>
                (string.IsNullOrWhiteSpace(_packageFilter) || string.Equals(entry.PackageId, _packageFilter, StringComparison.OrdinalIgnoreCase)) &&
                (search.Length == 0 ||
                 (entry.Original ?? string.Empty).IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0 ||
                 (entry.Translation ?? string.Empty).IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0 ||
                 (entry.PackageId ?? string.Empty).IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0))
                .ToList();
            _scrollPosition = Vector2.zero;
        }

        private void ShowPackageFilterMenu()
        {
            var options = new List<FloatMenuOption>
            {
                new FloatMenuOption("ATC_UIManager_AllMods".Translate(), () => { _packageFilter = string.Empty; RebuildDisplayEntries(); })
            };
            foreach (string packageId in _allEntries.Select(entry => entry.PackageId).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(GetPackageDisplayName, StringComparer.OrdinalIgnoreCase))
            {
                string captured = packageId;
                options.Add(new FloatMenuOption(GetPackageDisplayName(captured), () => { _packageFilter = captured; RebuildDisplayEntries(); }));
            }
            Find.WindowStack.Add(new FloatMenu(options));
        }

        private static string GetPackageDisplayName(string packageId)
        {
            ModMetaData mod = ModLister.AllInstalledMods.FirstOrDefault(candidate => candidate != null && string.Equals(candidate.PackageId, packageId, StringComparison.OrdinalIgnoreCase));
            return mod == null || string.IsNullOrWhiteSpace(mod.Name) ? packageId : mod.Name + " (" + packageId + ")";
        }

        private static string Compact(string text, int maxLength)
        {
            string value = (text ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Replace('<', '‹').Replace('>', '›').Trim();
            return value.Length <= maxLength ? value : value.Substring(0, maxLength - 3) + "...";
        }

        private static void OpenCacheFolder()
        {
            string path = UIInterceptor.GetManagedCacheDirectory();
            try
            {
                Directory.CreateDirectory(path);
                Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log.Warning("[AutoTranslationCore] Cannot open UI cache folder: " + ex.Message);
                Messages.Message("ATC_UIManager_OpenFolderFailed".Translate(ex.Message), MessageTypeDefOf.RejectInput, false);
            }
        }
    }
}
