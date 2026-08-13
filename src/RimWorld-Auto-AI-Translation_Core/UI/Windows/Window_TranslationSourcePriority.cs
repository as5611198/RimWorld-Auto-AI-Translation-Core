using RimWorld;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;

namespace AutoTranslator_Core
{
    internal sealed class Window_TranslationSourcePriority : Window
    {
        private readonly AutoTranslatorSettings _settings;
        private readonly List<ModMetaData> _mods;
        private List<TranslationSourceCategory> _globalOrder;
        private List<TranslationSourceCategory> _modOrder;
        private ModMetaData _selectedMod;
        private bool _useModOverride;

        public override Vector2 InitialSize => new Vector2(720f, 620f);

        internal Window_TranslationSourcePriority()
        {
            _settings = AutoTranslatorMod.Settings;
            _mods = ModLister.AllInstalledMods
                .Where(mod => mod != null &&
                              !string.IsNullOrWhiteSpace(mod.PackageId) &&
                              !AutoTranslatorScanner.IsOfficialBaseGameOrDlcPackage(mod.PackageId))
                .OrderBy(mod => mod.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            _globalOrder = TranslationSourcePriorityPolicy.ParseOrder(
                _settings.GlobalTranslationSourcePriority);
            _selectedMod = _mods.FirstOrDefault();
            LoadSelectedModOrder();
            doCloseX = true;
            doCloseButton = false;
            forcePause = true;
            absorbInputAroundWindow = true;
        }

        public override void PostClose()
        {
            SaveSettings();
            base.PostClose();
            LoadedModManager.GetMod<AutoTranslatorMod>()?.WriteSettings();
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, 0f, inRect.width, 34f), "ATC_SourcePriority_Title".Translate());
            Text.Font = GameFont.Tiny;
            Widgets.Label(new Rect(0f, 38f, inRect.width, 48f), "ATC_SourcePriority_Notice".Translate());
            Text.Font = GameFont.Small;

            float columnWidth = (inRect.width - 24f) / 2f;
            Rect globalRect = new Rect(0f, 96f, columnWidth, 390f);
            Rect modRect = new Rect(columnWidth + 24f, 96f, columnWidth, 390f);
            Widgets.DrawMenuSection(globalRect);
            Widgets.DrawMenuSection(modRect);

            Widgets.Label(new Rect(globalRect.x + 12f, globalRect.y + 10f, globalRect.width - 24f, 28f),
                "ATC_SourcePriority_Global".Translate());
            DrawOrder(_globalOrder, new Rect(globalRect.x + 12f, globalRect.y + 44f, globalRect.width - 24f, 290f), true);
            if (Widgets.ButtonText(
                    new Rect(globalRect.x + 12f, globalRect.yMax - 45f, globalRect.width - 24f, 32f),
                    "ATC_SourcePriority_ResetDefault".Translate()))
            {
                _globalOrder = TranslationSourcePriorityPolicy.ParseOrder(
                    TranslationSourcePriorityPolicy.DefaultOrder);
                if (!_useModOverride) _modOrder = new List<TranslationSourceCategory>(_globalOrder);
            }

            Rect modButton = new Rect(modRect.x + 12f, modRect.y + 10f, modRect.width - 24f, 30f);
            if (Widgets.ButtonText(modButton, _selectedMod != null
                    ? _selectedMod.Name + "  ▼"
                    : "ATC_SourcePriority_NoMods".Translate().ToString()))
            {
                List<FloatMenuOption> options = _mods.Select(mod =>
                {
                    ModMetaData captured = mod;
                    return new FloatMenuOption(mod.Name, () =>
                    {
                        SaveSelectedModOrder();
                        _selectedMod = captured;
                        LoadSelectedModOrder();
                    });
                }).ToList();
                if (options.Count > 0) Find.WindowStack.Add(new FloatMenu(options));
            }

            bool useOverride = _useModOverride;
            Rect overrideRect = new Rect(modRect.x + 12f, modRect.y + 48f, modRect.width - 24f, 30f);
            Widgets.CheckboxLabeled(overrideRect, "ATC_SourcePriority_UseOverride".Translate(), ref useOverride);
            if (useOverride != _useModOverride)
            {
                _useModOverride = useOverride;
                if (!_useModOverride) _modOrder = new List<TranslationSourceCategory>(_globalOrder);
            }

            GUI.color = _useModOverride ? Color.white : Color.grey;
            DrawOrder(_modOrder, new Rect(modRect.x + 12f, modRect.y + 84f, modRect.width - 24f, 250f), _useModOverride);
            GUI.color = Color.white;
            Text.Font = GameFont.Tiny;
            Widgets.Label(
                new Rect(modRect.x + 12f, modRect.yMax - 48f, modRect.width - 24f, 38f),
                "ATC_SourcePriority_ModHint".Translate());
            Text.Font = GameFont.Small;

            Rect saveRect = new Rect(inRect.width - 190f, inRect.height - 42f, 190f, 36f);
            if (Widgets.ButtonText(saveRect, "ATC_SourcePriority_SaveClose".Translate()))
            {
                SaveSettings();
                Close();
            }
        }

        private static void DrawOrder(
            List<TranslationSourceCategory> order,
            Rect rect,
            bool canEdit)
        {
            if (order == null) return;
            for (int index = 0; index < order.Count; index++)
            {
                float y = rect.y + index * 48f;
                Widgets.DrawHighlightIfMouseover(new Rect(rect.x, y, rect.width, 42f));
                Widgets.Label(
                    new Rect(rect.x + 8f, y + 8f, rect.width - 90f, 28f),
                    (index + 1) + ". " + GetCategoryLabel(order[index]));
                if (!canEdit) continue;
                if (index > 0 && Widgets.ButtonText(new Rect(rect.xMax - 76f, y + 5f, 32f, 30f), "↑"))
                {
                    TranslationSourceCategory value = order[index - 1];
                    order[index - 1] = order[index];
                    order[index] = value;
                }
                if (index < order.Count - 1 && Widgets.ButtonText(new Rect(rect.xMax - 38f, y + 5f, 32f, 30f), "↓"))
                {
                    TranslationSourceCategory value = order[index + 1];
                    order[index + 1] = order[index];
                    order[index] = value;
                }
            }
        }

        private void LoadSelectedModOrder()
        {
            string serialized = string.Empty;
            _useModOverride = _selectedMod != null &&
                _settings.ModTranslationSourcePriorityOverrides != null &&
                _settings.ModTranslationSourcePriorityOverrides.TryGetValue(
                    _selectedMod.PackageId,
                    out serialized);
            _modOrder = TranslationSourcePriorityPolicy.ParseOrder(
                _useModOverride ? serialized : TranslationSourcePriorityPolicy.SerializeOrder(_globalOrder));
        }

        private void SaveSelectedModOrder()
        {
            if (_selectedMod == null) return;
            if (_settings.ModTranslationSourcePriorityOverrides == null)
                _settings.ModTranslationSourcePriorityOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (_useModOverride)
                _settings.ModTranslationSourcePriorityOverrides[_selectedMod.PackageId] =
                    TranslationSourcePriorityPolicy.SerializeOrder(_modOrder);
            else
                _settings.ModTranslationSourcePriorityOverrides.Remove(_selectedMod.PackageId);
        }

        private void SaveSettings()
        {
            _settings.GlobalTranslationSourcePriority =
                TranslationSourcePriorityPolicy.SerializeOrder(_globalOrder);
            SaveSelectedModOrder();
        }

        private static string GetCategoryLabel(TranslationSourceCategory category)
        {
            return ("ATC_SourcePriority_" + category).Translate();
        }
    }
}
