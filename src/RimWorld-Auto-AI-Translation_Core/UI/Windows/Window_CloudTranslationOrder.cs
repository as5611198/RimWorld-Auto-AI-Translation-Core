using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace AutoTranslator_Core
{
    public sealed class Window_CloudTranslationOrder : Window
    {
        private const float RowHeight = 44f;
        private readonly List<OrderRow> _rows = new List<OrderRow>();
        private Vector2 _scrollPosition = Vector2.zero;
        private bool _dirty;

        public override Vector2 InitialSize => new Vector2(760f, 720f);

        public Window_CloudTranslationOrder()
        {
            doCloseX = true;
            forcePause = true;
            absorbInputAroundWindow = true;
            BuildRows(useSavedOrder: true);
        }

        public override void DoWindowContents(Rect inRect)
        {
            bool previousBypass = Patch_GUI_Label_GUIContent.BypassInterceptor;
            Patch_GUI_Label_GUIContent.BypassInterceptor = true;
            try
            {
                Text.Font = GameFont.Medium;
                Widgets.Label(new Rect(0f, 0f, inRect.width, 34f), "ATC_Cloud_OrderTitle".Translate());
                Text.Font = GameFont.Small;
                Widgets.Label(new Rect(0f, 38f, inRect.width, 42f), "ATC_Cloud_OrderSummary".Translate(_rows.Count));

                Rect listRect = new Rect(0f, 86f, inRect.width, inRect.height - 140f);
                Rect viewRect = new Rect(0f, 0f, listRect.width - 20f, Math.Max(listRect.height, _rows.Count * RowHeight));
                Widgets.BeginScrollView(listRect, ref _scrollPosition, viewRect);
                int firstVisible = Mathf.Max(0, Mathf.FloorToInt(_scrollPosition.y / RowHeight) - 2);
                int lastVisible = Mathf.Min(_rows.Count - 1, Mathf.CeilToInt((_scrollPosition.y + listRect.height) / RowHeight) + 2);
                for (int index = firstVisible; index <= lastVisible; index++)
                {
                    DrawRow(index, new Rect(0f, index * RowHeight, viewRect.width, RowHeight));
                }
                Widgets.EndScrollView();

                Rect resetRect = new Rect(0f, inRect.height - 42f, 190f, 36f);
                if (Widgets.ButtonText(resetRect, "ATC_Cloud_OrderReset".Translate()))
                {
                    BuildRows(useSavedOrder: false);
                    _dirty = true;
                }

                Rect applyRect = new Rect(inRect.width - 300f, inRect.height - 42f, 150f, 36f);
                GUI.color = _dirty ? new Color(0.65f, 1f, 0.7f) : Color.white;
                if (Widgets.ButtonText(applyRect, "ATC_Cloud_OrderApply".Translate())) ApplyAndClose();
                GUI.color = Color.white;

                Rect closeRect = new Rect(inRect.width - 140f, inRect.height - 42f, 140f, 36f);
                if (Widgets.ButtonText(closeRect, "ATC_ContactAuthor_Close".Translate())) Close();
            }
            finally
            {
                Text.Anchor = TextAnchor.UpperLeft;
                GUI.color = Color.white;
                Patch_GUI_Label_GUIContent.BypassInterceptor = previousBypass;
            }
        }

        private void DrawRow(int index, Rect rowRect)
        {
            OrderRow row = _rows[index];
            Widgets.DrawHighlightIfMouseover(rowRect);

            Rect priorityRect = new Rect(rowRect.x + 4f, rowRect.y + 5f, 45f, 32f);
            Text.Anchor = TextAnchor.MiddleCenter;
            Widgets.Label(priorityRect, (index + 1).ToString());
            Text.Anchor = TextAnchor.UpperLeft;

            Rect downRect = new Rect(rowRect.xMax - 38f, rowRect.y + 6f, 32f, 30f);
            Rect upRect = new Rect(downRect.x - 38f, rowRect.y + 6f, 32f, 30f);
            if (Widgets.ButtonText(upRect, "↑") && index > 0) Move(index, index - 1);
            if (Widgets.ButtonText(downRect, "↓") && index + 1 < _rows.Count) Move(index, index + 1);
            TooltipHandler.TipRegion(upRect, "ATC_Cloud_OrderMoveUp".Translate());
            TooltipHandler.TipRegion(downRect, "ATC_Cloud_OrderMoveDown".Translate());

            Rect nameRect = new Rect(rowRect.x + 55f, rowRect.y + 3f, upRect.x - rowRect.x - 65f, rowRect.height - 6f);
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(nameRect, row.DisplayName + "\n<size=10><color=#888888>" + row.PackageId + "</color></size>");
            Text.Anchor = TextAnchor.UpperLeft;
            Widgets.DrawLineHorizontal(rowRect.x, rowRect.yMax - 1f, rowRect.width);
        }

        private void Move(int from, int to)
        {
            if (from < 0 || from >= _rows.Count || to < 0 || to >= _rows.Count || from == to) return;
            OrderRow row = _rows[from];
            _rows.RemoveAt(from);
            _rows.Insert(to, row);
            _dirty = true;
        }

        private void ApplyAndClose()
        {
            AutoTranslatorMod.Settings.CloudTranslationPriority = _rows
                .Select(row => row.PackageId.ToLowerInvariant())
                .ToList();
            LoadedModManager.GetMod<AutoTranslatorMod>()?.WriteSettings();
            AutoTranslatorScanner.NotifyTranslationLoadOrderChanged();
            Messages.Message("ATC_Cloud_OrderApplied".Translate(), MessageTypeDefOf.PositiveEvent, false);
            _dirty = false;
            Close();
        }

        private void BuildRows(bool useSavedOrder)
        {
            Dictionary<string, ModMetaData> installed = ModLister.AllInstalledMods
                .Where(mod => mod != null && !string.IsNullOrWhiteSpace(mod.PackageId))
                .GroupBy(mod => mod.PackageId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            List<string> gameHighFirst = (LoadedModManager.RunningModsListForReading ?? new List<ModContentPack>())
                .Where(mod => mod != null && !string.IsNullOrWhiteSpace(mod.PackageId))
                .Select(mod => mod.PackageId)
                .Reverse()
                .ToList();
            List<string> saved = useSavedOrder
                ? (AutoTranslatorMod.Settings.CloudTranslationPriority ?? new List<string>())
                : new List<string>();
            List<string> ordered = saved.Concat(gameHighFirst)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            _rows.Clear();
            foreach (string packageId in ordered)
            {
                if (!installed.TryGetValue(packageId, out ModMetaData mod)) continue;
                if (!mod.Active && !saved.Contains(packageId, StringComparer.OrdinalIgnoreCase)) continue;
                if (!ModUpdateDetector.HasLocalTranslationFiles(mod) && !saved.Contains(packageId, StringComparer.OrdinalIgnoreCase)) continue;
                _rows.Add(new OrderRow
                {
                    PackageId = mod.PackageId,
                    DisplayName = string.IsNullOrWhiteSpace(mod.Name) ? mod.PackageId : mod.Name
                });
            }
            _scrollPosition = Vector2.zero;
        }

        private sealed class OrderRow
        {
            public string PackageId;
            public string DisplayName;
        }
    }
}
