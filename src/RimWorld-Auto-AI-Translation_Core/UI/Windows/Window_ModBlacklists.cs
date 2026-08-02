using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace AutoTranslator_Core
{
    public sealed class Window_ModBlacklists : Window
    {
        private const float RowHeight = 48f;
        private string _searchText = "";
        private string _cachedSearchText = null;
        private List<ModMetaData> _cachedMods = new List<ModMetaData>();
        private Vector2 _scrollPosition = Vector2.zero;

        public override Vector2 InitialSize => new Vector2(820f, 760f);

        public Window_ModBlacklists()
        {
            doCloseX = true;
            doCloseButton = false;
            forcePause = true;
            absorbInputAroundWindow = true;
        }

        public override void PostClose()
        {
            base.PostClose();
            LoadedModManager.GetMod<AutoTranslatorMod>()?.WriteSettings();
        }

        public override void DoWindowContents(Rect inRect)
        {
            bool previousBypass = Patch_GUI_Label_GUIContent.BypassInterceptor;
            Patch_GUI_Label_GUIContent.BypassInterceptor = true;
            try
            {
                Text.Font = GameFont.Medium;
                Widgets.Label(new Rect(0f, 0f, inRect.width, 34f), "ATC_Blacklist_Title".Translate());
                Text.Font = GameFont.Small;

                Rect searchRect = new Rect(0f, 42f, inRect.width, 30f);
                string nextSearch = Widgets.TextField(searchRect, _searchText ?? "");
                if (!string.Equals(nextSearch, _searchText, StringComparison.Ordinal))
                {
                    _searchText = nextSearch;
                    _scrollPosition = Vector2.zero;
                }
                if (string.IsNullOrEmpty(_searchText))
                {
                    GUI.color = Color.gray;
                    Widgets.Label(new Rect(searchRect.x + 6f, searchRect.y + 2f, searchRect.width - 12f, searchRect.height), "ATC_MultiSelect_Search".Translate());
                    GUI.color = Color.white;
                }

                float translationColumnX = inRect.width - 300f;
                float downloadColumnX = inRect.width - 145f;
                Rect headerRect = new Rect(0f, 80f, inRect.width, 30f);
                Widgets.DrawLineHorizontal(headerRect.x, headerRect.yMax, headerRect.width);
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(new Rect(translationColumnX, headerRect.y, 140f, headerRect.height), "ATC_Blacklist_TranslationColumn".Translate());
                Widgets.Label(new Rect(downloadColumnX, headerRect.y, 140f, headerRect.height), "ATC_Blacklist_DownloadColumn".Translate());
                Text.Anchor = TextAnchor.UpperLeft;

                List<ModMetaData> mods = GetDisplayMods();
                Rect outRect = new Rect(0f, 116f, inRect.width, inRect.height - 166f);
                Rect viewRect = new Rect(0f, 0f, outRect.width - 20f, mods.Count * RowHeight);
                Widgets.BeginScrollView(outRect, ref _scrollPosition, viewRect);
                int firstVisible = Mathf.Max(0, Mathf.FloorToInt(_scrollPosition.y / RowHeight) - 2);
                int lastVisible = Mathf.Min(mods.Count - 1, Mathf.CeilToInt((_scrollPosition.y + outRect.height) / RowHeight) + 2);
                for (int i = firstVisible; i <= lastVisible; i++)
                {
                    DrawModRow(mods[i], new Rect(0f, i * RowHeight, viewRect.width, RowHeight), translationColumnX, downloadColumnX);
                }
                Widgets.EndScrollView();

                Rect clearRect = new Rect(0f, inRect.height - 40f, 180f, 35f);
                if (Widgets.ButtonText(clearRect, "ATC_Blacklist_ClearAll".Translate()))
                {
                    AutoTranslatorMod.Settings.ClearPackageBlacklists();
                    LoadedModManager.GetMod<AutoTranslatorMod>()?.WriteSettings();
                }

                Rect closeRect = new Rect(inRect.width - 140f, inRect.height - 40f, 140f, 35f);
                if (Widgets.ButtonText(closeRect, "ATC_ContactAuthor_Close".Translate())) Close();
            }
            finally
            {
                Text.Anchor = TextAnchor.UpperLeft;
                GUI.color = Color.white;
                Patch_GUI_Label_GUIContent.BypassInterceptor = previousBypass;
            }
        }

        private List<ModMetaData> GetDisplayMods()
        {
            string search = (_searchText ?? "").Trim();
            if (_cachedSearchText == search && _cachedMods != null) return _cachedMods;

            IEnumerable<ModMetaData> mods = ModLister.AllInstalledMods
                .Where(mod => mod != null &&
                              !string.IsNullOrWhiteSpace(mod.PackageId) &&
                              !string.Equals(mod.PackageId, "auto.aitranslation.core", StringComparison.OrdinalIgnoreCase));
            if (search.Length > 0)
            {
                mods = mods.Where(mod =>
                    (mod.Name ?? "").IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    mod.PackageId.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0);
            }

            _cachedMods = mods
                .OrderByDescending(mod => mod.Active)
                .ThenBy(mod => mod.Name ?? "", StringComparer.OrdinalIgnoreCase)
                .ToList();
            _cachedSearchText = search;
            return _cachedMods;
        }

        private static void DrawModRow(ModMetaData mod, Rect rowRect, float translationColumnX, float downloadColumnX)
        {
            Widgets.DrawHighlightIfMouseover(rowRect);
            if (!mod.Active) GUI.color = new Color(0.68f, 0.68f, 0.68f);

            Rect nameRect = new Rect(rowRect.x + 4f, rowRect.y + 3f, translationColumnX - 14f, rowRect.height - 6f);
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(nameRect, (mod.Name ?? mod.PackageId) + "\n<size=10><color=#888888>" + mod.PackageId + "</color></size>");
            Text.Anchor = TextAnchor.UpperLeft;
            GUI.color = Color.white;

            bool translationBlocked = AutoTranslatorMod.Settings.IsTranslationBlacklisted(mod.PackageId);
            Rect translationRect = new Rect(translationColumnX, rowRect.y, 140f, rowRect.height);
            DrawToggleCell(translationRect, translationBlocked, value => AutoTranslatorMod.Settings.SetTranslationBlacklisted(mod.PackageId, value));

            bool downloadBlocked = AutoTranslatorMod.Settings.IsCloudDownloadBlacklisted(mod.PackageId);
            Rect downloadRect = new Rect(downloadColumnX, rowRect.y, 140f, rowRect.height);
            DrawToggleCell(downloadRect, downloadBlocked, value => AutoTranslatorMod.Settings.SetCloudDownloadBlacklisted(mod.PackageId, value));

            Widgets.DrawLineHorizontal(rowRect.x, rowRect.yMax - 1f, rowRect.width);
        }

        private static void DrawToggleCell(Rect rect, bool value, Action<bool> setter)
        {
            Vector2 checkboxPosition = new Vector2(rect.center.x - 12f, rect.center.y - 12f);
            Widgets.CheckboxDraw(checkboxPosition.x, checkboxPosition.y, value, false, 24f, null, null);
            if (Widgets.ButtonInvisible(rect))
            {
                setter(!value);
                LoadedModManager.GetMod<AutoTranslatorMod>()?.WriteSettings();
            }
        }
    }
}
