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
        private readonly bool _cloudDownloadOnly;

        public override Vector2 InitialSize => new Vector2(820f, 760f);

        public Window_ModBlacklists(bool cloudDownloadOnly = false)
        {
            _cloudDownloadOnly = cloudDownloadOnly;
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
                Rect titleBar = new Rect(0f, 0f, inRect.width, 40f);
                Widgets.DrawBoxSolid(titleBar, WorkflowUiStyle.Header);
                WorkflowUiStyle.DrawBorder(titleBar, new Color(0.31f, 0.35f, 0.38f));
                Widgets.DrawBoxSolid(new Rect(titleBar.x, titleBar.y, 4f, titleBar.height), WorkflowUiStyle.GoodText);
                Text.Font = GameFont.Medium;
                Text.Anchor = TextAnchor.MiddleLeft;
                Widgets.Label(new Rect(14f, 0f, inRect.width - 28f, 40f),
                    _cloudDownloadOnly
                        ? AutoTranslatorMod.WfText("云端下载排除列表", "Cloud download exclusions")
                        : "ATC_Blacklist_Title".Translate().ToString());
                Text.Font = GameFont.Small;
                Text.Anchor = TextAnchor.UpperLeft;

                Rect searchRect = new Rect(0f, 50f, inRect.width, 32f);
                Widgets.DrawBoxSolid(searchRect, WorkflowUiStyle.RaisedPanel);
                WorkflowUiStyle.DrawBorder(searchRect, new Color(0.31f, 0.35f, 0.38f));
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
                Rect headerRect = new Rect(0f, 92f, inRect.width, 32f);
                Widgets.DrawBoxSolid(headerRect, WorkflowUiStyle.RaisedPanel);
                WorkflowUiStyle.DrawBorder(headerRect, new Color(0.31f, 0.35f, 0.38f));
                Text.Anchor = TextAnchor.MiddleCenter;
                if (!_cloudDownloadOnly)
                    Widgets.Label(new Rect(translationColumnX, headerRect.y, 140f, headerRect.height), "ATC_Blacklist_TranslationColumn".Translate());
                Widgets.Label(new Rect(downloadColumnX, headerRect.y, 140f, headerRect.height), "ATC_Blacklist_DownloadColumn".Translate());
                Text.Anchor = TextAnchor.UpperLeft;

                List<ModMetaData> mods = GetDisplayMods();
                Rect outRect = new Rect(0f, 132f, inRect.width, inRect.height - 184f);
                Widgets.DrawBoxSolid(outRect, WorkflowUiStyle.Panel);
                WorkflowUiStyle.DrawBorder(outRect, new Color(0.25f, 0.28f, 0.30f));
                Rect viewRect = new Rect(0f, 0f, outRect.width - 20f, mods.Count * RowHeight);
                Widgets.BeginScrollView(outRect, ref _scrollPosition, viewRect);
                int firstVisible = Mathf.Max(0, Mathf.FloorToInt(_scrollPosition.y / RowHeight) - 2);
                int lastVisible = Mathf.Min(mods.Count - 1, Mathf.CeilToInt((_scrollPosition.y + outRect.height) / RowHeight) + 2);
                for (int i = firstVisible; i <= lastVisible; i++)
                {
                    DrawModRow(mods[i], new Rect(0f, i * RowHeight, viewRect.width, RowHeight),
                        translationColumnX, downloadColumnX, _cloudDownloadOnly);
                }
                Widgets.EndScrollView();

                Rect clearRect = new Rect(0f, inRect.height - 40f, 210f, 35f);
                string clearLabel = _cloudDownloadOnly
                    ? AutoTranslatorMod.WfText("清空下载排除列表", "Clear download exclusions")
                    : "ATC_Blacklist_ClearAll".Translate().ToString();
                if (WorkflowUiStyle.Button(clearRect, clearLabel, WorkflowButtonStyle.Stop))
                {
                    Find.WindowStack.Add(new Window_AtcDialog(
                        _cloudDownloadOnly
                            ? AutoTranslatorMod.WfText(
                                "将允许所有 Mod 再次参与云端下载。是否清空整个下载排除列表？",
                                "All mods will be allowed to participate in cloud downloads again. Clear all download exclusions?")
                            : AutoTranslatorMod.WfText(
                                "将清空全部翻译与下载排除记录。是否继续？",
                                "All translation and download exclusions will be cleared. Continue?"),
                        AutoTranslatorMod.WfText("确认清空", "Clear"),
                        () =>
                        {
                            if (_cloudDownloadOnly) AutoTranslatorMod.Settings.ClearCloudDownloadBlacklist();
                            else AutoTranslatorMod.Settings.ClearPackageBlacklists();
                            LoadedModManager.GetMod<AutoTranslatorMod>()?.WriteSettings();
                        },
                        AutoTranslatorMod.WfText("取消", "Cancel"),
                        null,
                        clearLabel,
                        true));
                }

                Rect closeRect = new Rect(inRect.width - 140f, inRect.height - 40f, 140f, 35f);
                if (WorkflowUiStyle.Button(closeRect, "ATC_ContactAuthor_Close".Translate().ToString(),
                        WorkflowButtonStyle.Quiet)) Close();
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
                .Where(mod => mod != null && mod.Active &&
                              !string.IsNullOrWhiteSpace(mod.PackageId) &&
                              !AutoTranslatorScanner.IsOfficialBaseGameOrDlcPackage(mod.PackageId) &&
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

        private static void DrawModRow(ModMetaData mod, Rect rowRect, float translationColumnX,
            float downloadColumnX, bool cloudDownloadOnly)
        {
            bool translationBlocked = !cloudDownloadOnly &&
                                      AutoTranslatorMod.Settings.IsTranslationBlacklisted(mod.PackageId);
            bool downloadBlocked = AutoTranslatorMod.Settings.IsCloudDownloadBlacklisted(mod.PackageId);
            if (translationBlocked || downloadBlocked)
                Widgets.DrawBoxSolid(rowRect, WorkflowUiStyle.Selection);
            Widgets.DrawHighlightIfMouseover(rowRect);
            if (!mod.Active) GUI.color = new Color(0.68f, 0.68f, 0.68f);

            float nameWidth = (cloudDownloadOnly ? downloadColumnX : translationColumnX) - 14f;
            Rect nameRect = new Rect(rowRect.x + 4f, rowRect.y + 3f, nameWidth, rowRect.height - 6f);
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(nameRect, (mod.Name ?? mod.PackageId) + "\n<size=10><color=#888888>" + mod.PackageId + "</color></size>");
            Text.Anchor = TextAnchor.UpperLeft;
            GUI.color = Color.white;

            if (!cloudDownloadOnly)
            {
                Rect translationRect = new Rect(translationColumnX, rowRect.y, 140f, rowRect.height);
                DrawToggleCell(translationRect, translationBlocked,
                    value => AutoTranslatorMod.Settings.SetTranslationBlacklisted(mod.PackageId, value));
            }

            Rect downloadRect = new Rect(downloadColumnX, rowRect.y, 140f, rowRect.height);
            DrawToggleCell(downloadRect, downloadBlocked, value => AutoTranslatorMod.Settings.SetCloudDownloadBlacklisted(mod.PackageId, value));

            Widgets.DrawLineHorizontal(rowRect.x, rowRect.yMax - 1f, rowRect.width);
        }

        private static void DrawToggleCell(Rect rect, bool value, Action<bool> setter)
        {
            Rect buttonRect = new Rect(rect.x + 8f, rect.y + 7f, rect.width - 16f, rect.height - 14f);
            string label = value
                ? AutoTranslatorMod.WfText("已排除", "Excluded")
                : AutoTranslatorMod.WfText("允许", "Allowed");
            if (WorkflowUiStyle.Button(
                    buttonRect,
                    label,
                    value ? WorkflowButtonStyle.Stop : WorkflowButtonStyle.Quiet,
                    true,
                    GameFont.Tiny))
            {
                setter(!value);
                LoadedModManager.GetMod<AutoTranslatorMod>()?.WriteSettings();
            }
        }
    }
}
