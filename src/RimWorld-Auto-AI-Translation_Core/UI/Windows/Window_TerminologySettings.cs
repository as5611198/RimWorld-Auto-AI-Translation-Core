using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace AutoTranslator_Core
{
    public sealed class Window_TerminologySettings : Window
    {
        private const float RowHeight = 54f;
        private string _searchText = string.Empty;
        private Vector2 _scroll = Vector2.zero;

        public override Vector2 InitialSize => new Vector2(900f, 760f);

        public Window_TerminologySettings()
        {
            doCloseX = true;
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
                Widgets.Label(new Rect(0f, 0f, inRect.width, 34f), "ATC_Terminology_Title".Translate());
                Text.Font = GameFont.Tiny;
                Widgets.Label(new Rect(0f, 36f, inRect.width, 48f), "ATC_Terminology_Notice".Translate());
                Text.Font = GameFont.Small;

                Rect searchRect = new Rect(0f, 90f, inRect.width, 30f);
                _searchText = Widgets.TextField(searchRect, _searchText ?? string.Empty);
                if (string.IsNullOrWhiteSpace(_searchText))
                {
                    GUI.color = Color.gray;
                    Widgets.Label(new Rect(6f, 92f, inRect.width - 12f, 26f), "ATC_MultiSelect_Search".Translate());
                    GUI.color = Color.white;
                }

                Rect header = new Rect(0f, 126f, inRect.width, 28f);
                Text.Anchor = TextAnchor.MiddleLeft;
                Widgets.Label(new Rect(8f, header.y, inRect.width - 360f, header.height), "ATC_Terminology_ModColumn".Translate());
                Widgets.Label(new Rect(inRect.width - 350f, header.y, 350f, header.height), "ATC_Terminology_GroupColumn".Translate());
                Widgets.DrawLineHorizontal(0f, header.yMax, inRect.width);

                List<ModMetaData> mods = GetMods();
                Rect outRect = new Rect(0f, 158f, inRect.width, inRect.height - 210f);
                Rect viewRect = new Rect(0f, 0f, outRect.width - 20f, mods.Count * RowHeight);
                Widgets.BeginScrollView(outRect, ref _scroll, viewRect);
                int first = Mathf.Max(0, Mathf.FloorToInt(_scroll.y / RowHeight) - 2);
                int last = Mathf.Min(mods.Count - 1, Mathf.CeilToInt((_scroll.y + outRect.height) / RowHeight) + 2);
                for (int index = first; index <= last; index++)
                    DrawRow(mods[index], new Rect(0f, index * RowHeight, viewRect.width, RowHeight));
                Widgets.EndScrollView();

                Text.Anchor = TextAnchor.MiddleLeft;
                Widgets.Label(new Rect(0f, inRect.height - 44f, inRect.width - 340f, 36f),
                    "ATC_Terminology_SelectedCount".Translate(AutoTranslatorMod.Settings.TerminologyEnabledPackageIds.Count));
                int reviewCount = Terminology.TerminologyRuntime.GetCache().GetReviewQueue().Count;
                if (Widgets.ButtonText(new Rect(inRect.width - 330f, inRect.height - 44f, 180f, 36f),
                    "ATC_Terminology_Review".Translate(reviewCount)))
                    Find.WindowStack.Add(new Window_TerminologyReview());
                if (Widgets.ButtonText(new Rect(inRect.width - 140f, inRect.height - 44f, 140f, 36f), "ATC_ContactAuthor_Close".Translate()))
                    Close();
            }
            finally
            {
                Text.Anchor = TextAnchor.UpperLeft;
                Text.Font = GameFont.Small;
                GUI.color = Color.white;
                Patch_GUI_Label_GUIContent.BypassInterceptor = previousBypass;
            }
        }

        private List<ModMetaData> GetMods()
        {
            string search = (_searchText ?? string.Empty).Trim();
            return ModLister.AllInstalledMods
                .Where(mod => mod != null && mod.Active &&
                    !string.IsNullOrWhiteSpace(mod.PackageId) &&
                    !AutoTranslatorScanner.IsOfficialBaseGameOrDlcPackage(mod.PackageId) &&
                    !string.Equals(mod.PackageId, "auto.aitranslation.core", StringComparison.OrdinalIgnoreCase) &&
                    (search.Length == 0 ||
                     (mod.Name ?? string.Empty).IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0 ||
                     mod.PackageId.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0))
                .OrderBy(mod => mod.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static void DrawRow(ModMetaData mod, Rect row)
        {
            Widgets.DrawHighlightIfMouseover(row);
            bool selected = AutoTranslatorMod.Settings.IsTerminologyEnabledForPackage(mod.PackageId);
            Rect toggleRect = new Rect(row.x + 4f, row.y + 4f, row.width - 360f, row.height - 8f);
            Widgets.CheckboxLabeled(toggleRect,
                (mod.Name ?? mod.PackageId) + "\n<size=10><color=#888888>" + mod.PackageId + "</color></size>",
                ref selected);
            AutoTranslatorMod.Settings.SetTerminologyEnabledForPackage(mod.PackageId, selected);

            Rect groupRect = new Rect(row.xMax - 350f, row.y + 11f, 340f, 30f);
            GUI.color = selected ? Color.white : Color.gray;
            string group = AutoTranslatorMod.Settings.GetTerminologyGroup(mod.PackageId);
            string next = Widgets.TextField(groupRect, group);
            if (selected && !string.Equals(next, group, StringComparison.Ordinal))
                AutoTranslatorMod.Settings.SetTerminologyGroup(mod.PackageId, next);
            if (selected && string.IsNullOrWhiteSpace(next))
            {
                GUI.color = Color.gray;
                Widgets.Label(new Rect(groupRect.x + 6f, groupRect.y + 2f, groupRect.width - 12f, groupRect.height),
                    "ATC_Terminology_GroupHint".Translate());
            }
            GUI.color = Color.white;
            Widgets.DrawLineHorizontal(row.x, row.yMax - 1f, row.width);
        }
    }
}
