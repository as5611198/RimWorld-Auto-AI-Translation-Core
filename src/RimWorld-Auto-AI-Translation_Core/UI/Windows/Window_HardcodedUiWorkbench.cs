using AutoTranslator_Core.TargetedHardcodedUi;
using AutoTranslator_Core.TranslationPolicy;
using Newtonsoft.Json;
using RimWorld;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using Verse;

namespace AutoTranslator_Core
{
    internal sealed class Window_HardcodedUiWorkbench : Window
    {
        private readonly List<ModMetaData> _mods;
        private readonly Dictionary<string, string> _translations =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, TranslationPolicyAgentCandidateOutcome> _agentOutcomes =
            new Dictionary<string, TranslationPolicyAgentCandidateOutcome>(StringComparer.Ordinal);
        private ModMetaData _selectedMod;
        private HardcodedUiScanResult _scanResult;
        private Vector2 _scroll;
        private bool _busy;
        private string _status = string.Empty;
        private List<HardcodedUiPatchEntry> _visibleEntries;
        private DecisionFilter _decisionFilter;
        private TranslationFilter _translationFilter;

        private enum DecisionFilter
        {
            All,
            Translate,
            DoNotTranslate,
            Uncertain
        }

        private enum TranslationFilter
        {
            All,
            Translated,
            Untranslated
        }

        public override Vector2 InitialSize => new Vector2(1080f, 760f);

        internal Window_HardcodedUiWorkbench(ModMetaData initialMod = null)
        {
            _mods = ModLister.AllInstalledMods
                .Where(mod => mod != null && mod.Active && mod.RootDir != null &&
                    !string.IsNullOrWhiteSpace(mod.PackageId) &&
                    !AutoTranslatorScanner.IsOfficialBaseGameOrDlcPackage(mod.PackageId) &&
                    !string.Equals(mod.PackageId, "Auto.AITranslation.Core", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(mod.PackageId, "Auto.AITranslation.Core.dev", StringComparison.OrdinalIgnoreCase))
                .OrderBy(mod => mod.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            _selectedMod = initialMod == null
                ? _mods.FirstOrDefault()
                : _mods.FirstOrDefault(mod => string.Equals(
                      mod.PackageId,
                      initialMod.PackageId,
                      StringComparison.OrdinalIgnoreCase)) ?? _mods.FirstOrDefault();
            if (_selectedMod != null &&
                HardcodedUiBatchScanCoordinator.TryGet(
                    _selectedMod.PackageId,
                    out HardcodedUiBatchScanSummary cached) &&
                cached.Result != null)
            {
                _scanResult = cached.Result;
                LoadExistingTranslations();
            }
            doCloseX = true;
            doCloseButton = false;
            forcePause = true;
            absorbInputAroundWindow = true;
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, 0f, inRect.width, 34f), "ATC_HardcodedUi_WorkbenchTitle".Translate());
            Text.Font = GameFont.Tiny;
            Widgets.Label(new Rect(0f, 36f, inRect.width, 42f), "ATC_HardcodedUi_WorkbenchNotice".Translate());
            Text.Font = GameFont.Small;

            Rect modRect = new Rect(0f, 84f, inRect.width * 0.34f, 34f);
            GUI.color = _busy ? Color.grey : Color.white;
            if (Widgets.ButtonText(modRect, _selectedMod != null ? _selectedMod.Name : "ATC_HardcodedUi_NoActiveMods".Translate().ToString()) && !_busy)
            {
                Find.WindowStack.Add(new FloatMenu(_mods.Select(mod =>
                {
                    ModMetaData captured = mod;
                    return new FloatMenuOption(mod.Name, () =>
                    {
                        _selectedMod = captured;
                        _scanResult = null;
                        _translations.Clear();
                        _agentOutcomes.Clear();
                        _visibleEntries = null;
                        _status = string.Empty;
                    });
                }).ToList()));
            }
            AddDisabledReasonTooltip(modRect, _busy, "ATC_Disabled_WorkbenchBusy".Translate().ToString());
            GUI.color = Color.white;

            Rect scanButtonRect = new Rect(inRect.width * 0.35f, 84f, inRect.width * 0.10f, 34f);
            GUI.color = _busy || _selectedMod == null ? Color.grey : Color.white;
            if (Widgets.ButtonText(scanButtonRect, "ATC_HardcodedUi_Scan".Translate()) && !_busy && _selectedMod != null)
                StartScan();
            AddDisabledReasonTooltip(scanButtonRect, _busy || _selectedMod == null,
                _busy ? "ATC_Disabled_WorkbenchBusy".Translate().ToString() : "ATC_Disabled_NoModSelected".Translate().ToString());
            GUI.color = Color.white;

            int enabledCount = _scanResult == null ? 0 : _scanResult.Entries.Count(IsEffectivelyEnabled);
            Rect agentButtonRect = new Rect(inRect.width * 0.46f, 84f, inRect.width * 0.12f, 34f);
            GUI.color = _busy || _scanResult == null ? Color.grey : Color.white;
            if (Widgets.ButtonText(agentButtonRect, "ATC_HardcodedUi_AgentAnalyze".Translate()) && !_busy && _scanResult != null)
                StartAgentAnalysis();
            AddDisabledReasonTooltip(agentButtonRect, _busy || _scanResult == null,
                _busy ? "ATC_Disabled_WorkbenchBusy".Translate().ToString() : "ATC_Disabled_NoScanResult".Translate().ToString());
            Rect translateButtonRect = new Rect(inRect.width * 0.59f, 84f, inRect.width * 0.12f, 34f);
            GUI.color = _busy || enabledCount == 0 ? Color.grey : Color.white;
            if (Widgets.ButtonText(translateButtonRect, "ATC_HardcodedUi_AiTranslate".Translate()) && !_busy && enabledCount > 0)
                StartAiTranslation();
            AddDisabledReasonTooltip(translateButtonRect, _busy || enabledCount == 0,
                _busy ? "ATC_Disabled_WorkbenchBusy".Translate().ToString() : "ATC_Disabled_NoTranslatableEntries".Translate().ToString());
            int overrideCount = _scanResult == null
                ? 0
                : _scanResult.Decisions.Values.Count(record =>
                    record.UserOverride != HardcodedUiUserOverride.None);
            Rect restoreDefaultsRect = new Rect(inRect.width * 0.72f, 84f, inRect.width * 0.12f, 34f);
            GUI.color = _busy || _scanResult == null || overrideCount == 0 ? Color.grey : Color.white;
            if (Widgets.ButtonText(restoreDefaultsRect, "ATC_HardcodedUi_RestoreModDefaults".Translate()) &&
                !_busy && _scanResult != null && overrideCount > 0)
                ConfirmRestoreCurrentModDefaults(overrideCount);
            AddDisabledReasonTooltip(
                restoreDefaultsRect,
                _busy || _scanResult == null || overrideCount == 0,
                _busy
                    ? "ATC_Disabled_WorkbenchBusy".Translate().ToString()
                    : _scanResult == null
                        ? "ATC_Disabled_NoScanResult".Translate().ToString()
                        : "ATC_HardcodedUi_NoModOverrides".Translate().ToString());

            Rect saveButtonRect = new Rect(inRect.width * 0.85f, 84f, inRect.width * 0.15f, 34f);
            GUI.color = _busy || _scanResult == null ? Color.grey : Color.white;
            if (Widgets.ButtonText(saveButtonRect, "ATC_HardcodedUi_SaveApply".Translate()) && !_busy && _scanResult != null)
                SaveAndApply();
            AddDisabledReasonTooltip(saveButtonRect, _busy || _scanResult == null,
                _busy ? "ATC_Disabled_WorkbenchBusy".Translate().ToString() : "ATC_Disabled_NoScanResult".Translate().ToString());
            GUI.color = Color.white;

            string summary = _busy ? "ATC_HardcodedUi_Working".Translate().ToString() : _status;
            if (_scanResult != null)
            {
                summary = "ATC_HardcodedUi_ScanSummary".Translate(
                    _scanResult.AssemblyCount,
                    _scanResult.MethodCount,
                    _scanResult.Entries.Count,
                    _scanResult.Decisions.Values.Count(record =>
                        record.EffectiveDecision == HardcodedUiAutomaticDecision.Translate),
                    _scanResult.Decisions.Values.Count(record =>
                        record.EffectiveDecision == HardcodedUiAutomaticDecision.DoNotTranslate),
                    _scanResult.Decisions.Values.Count(record =>
                        record.EffectiveDecision == HardcodedUiAutomaticDecision.Uncertain)) +
                    (string.IsNullOrWhiteSpace(summary) ? string.Empty : "  " + summary);
            }
            string summaryText = summary ?? string.Empty;
            float summaryHeight = Mathf.Clamp(Text.CalcHeight(summaryText, inRect.width), 28f, 58f);
            Widgets.Label(new Rect(0f, 125f, inRect.width, summaryHeight), summaryText);

            float filterY = 125f + summaryHeight + 4f;
            DrawFilters(new Rect(0f, filterY, inRect.width, 26f));

            float listTop = filterY + 32f;
            Rect listOuter = new Rect(0f, listTop, inRect.width, inRect.height - listTop - 50f);
            Widgets.DrawMenuSection(listOuter);
            if (_scanResult == null || _scanResult.Entries.Count == 0)
            {
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(
                    listOuter.ContractedBy(12f),
                    (_scanResult == null
                        ? "ATC_HardcodedUi_Empty"
                        : "ATC_HardcodedUi_NoCandidates").Translate());
                Text.Anchor = TextAnchor.UpperLeft;
            }
            else
            {
                List<HardcodedUiPatchEntry> visibleEntries = GetVisibleEntries();
                float rowHeight = 108f;
                Rect view = new Rect(0f, 0f, listOuter.width - 20f, visibleEntries.Count * rowHeight + 8f);
                Widgets.BeginScrollView(listOuter.ContractedBy(4f), ref _scroll, view);
                for (int i = 0; i < visibleEntries.Count; i++)
                    DrawEntry(new Rect(4f, i * rowHeight + 4f, view.width - 8f, rowHeight - 6f), visibleEntries[i]);
                Widgets.EndScrollView();
            }

            if (Widgets.ButtonText(new Rect(inRect.width - 160f, inRect.height - 42f, 160f, 36f), "CloseButton".Translate()))
                Close();
        }

        private void DrawFilters(Rect rect)
        {
            const float gap = 8f;
            float buttonWidth = Mathf.Min(230f, (rect.width - gap) * 0.5f);
            string decisionLabel = _decisionFilter == DecisionFilter.Translate
                ? "ATC_Filter_DecisionTranslate".Translate().ToString()
                : _decisionFilter == DecisionFilter.DoNotTranslate
                    ? "ATC_Filter_DecisionDoNotTranslate".Translate().ToString()
                    : _decisionFilter == DecisionFilter.Uncertain
                        ? "ATC_Filter_DecisionUncertain".Translate().ToString()
                        : "ATC_Filter_AllDecisions".Translate().ToString();
            if (Widgets.ButtonText(new Rect(rect.x, rect.y, buttonWidth, rect.height), decisionLabel))
            {
                Find.WindowStack.Add(new FloatMenu(new List<FloatMenuOption>
                {
                    new FloatMenuOption("ATC_Filter_AllDecisions".Translate(), () => SetDecisionFilter(DecisionFilter.All)),
                    new FloatMenuOption("ATC_Filter_DecisionTranslate".Translate(), () => SetDecisionFilter(DecisionFilter.Translate)),
                    new FloatMenuOption("ATC_Filter_DecisionDoNotTranslate".Translate(), () => SetDecisionFilter(DecisionFilter.DoNotTranslate)),
                    new FloatMenuOption("ATC_Filter_DecisionUncertain".Translate(), () => SetDecisionFilter(DecisionFilter.Uncertain))
                }));
            }

            string translationLabel = _translationFilter == TranslationFilter.Translated
                ? "ATC_Filter_Translated".Translate().ToString()
                : _translationFilter == TranslationFilter.Untranslated
                    ? "ATC_Filter_Untranslated".Translate().ToString()
                    : "ATC_Filter_AllTranslationStates".Translate().ToString();
            if (Widgets.ButtonText(new Rect(rect.x + buttonWidth + gap, rect.y, buttonWidth, rect.height), translationLabel))
            {
                Find.WindowStack.Add(new FloatMenu(new List<FloatMenuOption>
                {
                    new FloatMenuOption("ATC_Filter_AllTranslationStates".Translate(), () => SetTranslationFilter(TranslationFilter.All)),
                    new FloatMenuOption("ATC_Filter_Translated".Translate(), () => SetTranslationFilter(TranslationFilter.Translated)),
                    new FloatMenuOption("ATC_Filter_Untranslated".Translate(), () => SetTranslationFilter(TranslationFilter.Untranslated))
                }));
            }

            Text.Anchor = TextAnchor.MiddleRight;
            int visibleCount = _visibleEntries?.Count ?? _scanResult?.Entries.Count ?? 0;
            Widgets.Label(new Rect(rect.x + buttonWidth * 2f + gap * 2f, rect.y, rect.width - buttonWidth * 2f - gap * 2f, rect.height),
                "ATC_Filter_VisibleCount".Translate(visibleCount));
            Text.Anchor = TextAnchor.UpperLeft;
        }

        private void SetDecisionFilter(DecisionFilter value)
        {
            _decisionFilter = value;
            _visibleEntries = null;
            _scroll = Vector2.zero;
        }

        private void SetTranslationFilter(TranslationFilter value)
        {
            _translationFilter = value;
            _visibleEntries = null;
            _scroll = Vector2.zero;
        }

        private List<HardcodedUiPatchEntry> GetVisibleEntries()
        {
            if (_visibleEntries != null) return _visibleEntries;
            IEnumerable<HardcodedUiPatchEntry> entries = _scanResult != null
                ? (IEnumerable<HardcodedUiPatchEntry>)_scanResult.Entries
                : Enumerable.Empty<HardcodedUiPatchEntry>();
            if (_decisionFilter != DecisionFilter.All)
            {
                HardcodedUiAutomaticDecision expected = _decisionFilter == DecisionFilter.Translate
                    ? HardcodedUiAutomaticDecision.Translate
                    : _decisionFilter == DecisionFilter.DoNotTranslate
                        ? HardcodedUiAutomaticDecision.DoNotTranslate
                        : HardcodedUiAutomaticDecision.Uncertain;
                entries = entries.Where(entry => GetDecision(entry).EffectiveDecision == expected);
            }
            if (_translationFilter != TranslationFilter.All)
            {
                entries = entries.Where(entry =>
                {
                    bool translated = _translations.TryGetValue(entry.EntryId, out string value) && !string.IsNullOrWhiteSpace(value);
                    return _translationFilter == TranslationFilter.Translated ? translated : !translated;
                });
            }
            _visibleEntries = entries.ToList();
            return _visibleEntries;
        }

        private void DrawEntry(Rect rect, HardcodedUiPatchEntry entry)
        {
            Widgets.DrawHighlightIfMouseover(rect);
            HardcodedUiDecisionRecord decision = GetDecision(entry);
            bool enabled = decision.EffectiveDecision == HardcodedUiAutomaticDecision.Translate;
            Rect decisionButtonRect = new Rect(rect.x + 4f, rect.y + 5f, 26f, 26f);
            GUI.color = decision.EffectiveDecision == HardcodedUiAutomaticDecision.Translate
                ? Color.green
                : decision.EffectiveDecision == HardcodedUiAutomaticDecision.DoNotTranslate
                    ? Color.red
                    : Color.yellow;
            string decisionSymbol = decision.EffectiveDecision == HardcodedUiAutomaticDecision.Translate
                ? "✓"
                : decision.EffectiveDecision == HardcodedUiAutomaticDecision.DoNotTranslate ? "✕" : "?";
            bool decisionClicked = Widgets.ButtonText(decisionButtonRect, decisionSymbol);
            GUI.color = Color.white;
            TooltipHandler.TipRegion(decisionButtonRect, "ATC_HardcodedUi_ChangeDecision".Translate());
            AddDisabledReasonTooltip(decisionButtonRect, _busy, "ATC_Disabled_WorkbenchBusy".Translate().ToString());
            if (decisionClicked && !_busy)
            {
                Find.WindowStack.Add(new FloatMenu(GetDecisionOptions(entry, decision)));
            }
            entry.Enabled = decision.EffectiveDecision == HardcodedUiAutomaticDecision.Translate;
            string call = !string.IsNullOrWhiteSpace(entry.CallDeclaringType)
                ? entry.CallDeclaringType + "." + entry.CallMethodName
                : entry.DiscoveryKind;
            Text.Font = GameFont.Tiny;
            Widgets.Label(new Rect(rect.x + 34f, rect.y + 2f, rect.width - 38f, 20f),
                entry.AssemblyRelativePath + "  |  " + call + "  |  " + entry.DeclaringType + "." + entry.MethodName);
            GUI.color = decision.EffectiveDecision == HardcodedUiAutomaticDecision.Translate
                ? Color.green
                : decision.EffectiveDecision == HardcodedUiAutomaticDecision.DoNotTranslate
                    ? Color.red
                    : Color.yellow;
            string overrideText = decision.UserOverride == HardcodedUiUserOverride.None
                ? string.Empty
                : " | " + "ATC_HardcodedUi_UserOverride".Translate(
                    GetDecisionLabel(decision.UserOverride == HardcodedUiUserOverride.Translate
                        ? HardcodedUiAutomaticDecision.Translate
                        : decision.UserOverride == HardcodedUiUserOverride.DoNotTranslate
                            ? HardcodedUiAutomaticDecision.DoNotTranslate
                            : HardcodedUiAutomaticDecision.Uncertain)).ToString();
            Widgets.Label(new Rect(rect.x + 34f, rect.y + 20f, rect.width - 156f, 18f),
                "ATC_HardcodedUi_DecisionLine".Translate(
                    GetDecisionLabel(decision.EffectiveDecision),
                    decision.AutomaticReasonCode) + overrideText);
            GUI.color = Color.white;
            if (decision.UserOverride != HardcodedUiUserOverride.None &&
                Widgets.ButtonText(new Rect(rect.xMax - 116f, rect.y + 18f, 112f, 22f),
                    "ATC_HardcodedUi_RestoreAutomatic".Translate()))
            {
                decision.RestoreAutomaticDecision();
                entry.Enabled = decision.EffectiveDecision == HardcodedUiAutomaticDecision.Translate;
                OnManualDecisionChanged();
            }
            if (_agentOutcomes.TryGetValue(entry.EntryId, out TranslationPolicyAgentCandidateOutcome outcome))
            {
                GUI.color = outcome.Decision == TranslationPolicyAgentDecision.Allow
                    ? Color.green
                    : outcome.Decision == TranslationPolicyAgentDecision.Deny
                        ? Color.red
                        : Color.yellow;
                TooltipHandler.TipRegion(
                    new Rect(rect.x + 34f, rect.y + 18f, rect.width - 38f, 22f),
                    "Agent: " + outcome.Decision + " — " + outcome.Reason);
                GUI.color = Color.white;
            }
            Text.Font = GameFont.Small;
            Widgets.Label(new Rect(rect.x + 34f, rect.y + 40f, rect.width * 0.43f, 56f), entry.Literal);
            string current;
            _translations.TryGetValue(entry.EntryId, out current);
            GUI.color = enabled && !_busy ? Color.white : Color.grey;
            current = Widgets.TextArea(new Rect(rect.x + rect.width * 0.49f, rect.y + 40f, rect.width * 0.50f, 56f), current ?? string.Empty);
            GUI.color = Color.white;
            _translations[entry.EntryId] = current;
        }

        private List<FloatMenuOption> GetDecisionOptions(
            HardcodedUiPatchEntry entry,
            HardcodedUiDecisionRecord decision)
        {
            var options = new List<FloatMenuOption>();
            AddDecisionOption(options, entry, decision, HardcodedUiAutomaticDecision.Translate,
                HardcodedUiUserOverride.Translate, "ATC_Filter_DecisionTranslate");
            AddDecisionOption(options, entry, decision, HardcodedUiAutomaticDecision.DoNotTranslate,
                HardcodedUiUserOverride.DoNotTranslate, "ATC_Filter_DecisionDoNotTranslate");
            AddDecisionOption(options, entry, decision, HardcodedUiAutomaticDecision.Uncertain,
                HardcodedUiUserOverride.Uncertain, "ATC_Filter_DecisionUncertain");
            return options;
        }

        private void AddDecisionOption(
            List<FloatMenuOption> options,
            HardcodedUiPatchEntry entry,
            HardcodedUiDecisionRecord decision,
            HardcodedUiAutomaticDecision automaticDecision,
            HardcodedUiUserOverride userOverride,
            string labelKey)
        {
            if (decision.EffectiveDecision == automaticDecision) return;
            options.Add(new FloatMenuOption(labelKey.Translate(), () =>
            {
                decision.SetUserOverride(userOverride);
                entry.Enabled = decision.EffectiveDecision == HardcodedUiAutomaticDecision.Translate;
                OnManualDecisionChanged();
            }));
        }

        private void OnManualDecisionChanged()
        {
            // Keep the current visible snapshot so a row does not disappear while the user is correcting it.
            // Clearing the controls communicates that the next explicit filter choice will rebuild the list.
            _decisionFilter = DecisionFilter.All;
            _translationFilter = TranslationFilter.All;
            if (_selectedMod != null &&
                HardcodedUiBatchScanCoordinator.TryGet(_selectedMod.PackageId, out HardcodedUiBatchScanSummary summary))
                HardcodedUiBatchScanCoordinator.RefreshDecisionCounts(summary);
        }

        private void ConfirmRestoreCurrentModDefaults(int overrideCount)
        {
            string modName = _selectedMod != null ? _selectedMod.Name : string.Empty;
            Find.WindowStack.Add(new Dialog_MessageBox(
                "ATC_HardcodedUi_RestoreModDefaultsConfirm".Translate(modName, overrideCount),
                "ATC_Btn_Confirm".Translate(),
                RestoreCurrentModDefaults,
                "ATC_Btn_Cancel".Translate(),
                null,
                "ATC_HardcodedUi_RestoreModDefaults".Translate()));
        }

        private void RestoreCurrentModDefaults()
        {
            if (_busy || _scanResult == null) return;
            int restored = 0;
            foreach (HardcodedUiDecisionRecord decision in _scanResult.Decisions.Values)
            {
                if (decision.UserOverride == HardcodedUiUserOverride.None) continue;
                decision.RestoreAutomaticDecision();
                restored++;
            }
            foreach (HardcodedUiPatchEntry entry in _scanResult.Entries)
            {
                if (_scanResult.Decisions.TryGetValue(entry.EntryId, out HardcodedUiDecisionRecord decision))
                    entry.Enabled = decision.EffectiveDecision == HardcodedUiAutomaticDecision.Translate;
            }
            HardcodedUiDecisionState.Persist(_scanResult.Decisions.Values);
            OnManualDecisionChanged();
            _status = "ATC_HardcodedUi_RestoreModDefaultsDone".Translate(restored).ToString();
        }

        private async void StartAgentAnalysis()
        {
            AutoTranslatorSettings settings = AutoTranslatorMod.Settings;
            if (settings == null || !settings.EnableTranslationPolicyAgent || !AutoTranslatorAPI.HasAnyPolicyAgentConfig())
            {
                _status = "ATC_HardcodedUi_AgentNotReady".Translate().ToString();
                return;
            }

            _busy = true;
            _agentOutcomes.Clear();
            long runId = TranslationPolicyAgentCoordinator.BeginRun(settings, false, true);
            bool completed = false;
            try
            {
                List<HardcodedUiPatchEntry> pendingEntries =
                    HardcodedUiPolicyBridge.GetAgentCandidates(_scanResult);
                List<TranslationPolicyCandidate> candidates = pendingEntries
                    .Select(entry => HardcodedUiPolicyBridge.CreateCandidate(entry, _selectedMod.Name))
                    .ToList();
                Dictionary<string, TranslationPolicyAgentCandidateOutcome> outcomes =
                    await TranslationPolicyAgentCoordinator.ResolveCandidatesAsync(
                        _selectedMod.PackageId,
                        candidates,
                        false);
                foreach (HardcodedUiPatchEntry entry in pendingEntries)
                {
                    if (!outcomes.TryGetValue(entry.EntryId, out TranslationPolicyAgentCandidateOutcome outcome)) continue;
                    _agentOutcomes[entry.EntryId] = outcome;
                }
                HardcodedUiPolicyBridge.ApplyAgentOutcomes(
                    _scanResult,
                    pendingEntries,
                    outcomes);
                int allowed = _agentOutcomes.Values.Count(outcome => outcome.Decision == TranslationPolicyAgentDecision.Allow);
                int review = _agentOutcomes.Values.Count(outcome => outcome.Decision == TranslationPolicyAgentDecision.Review);
                int denied = _agentOutcomes.Values.Count(outcome => outcome.Decision == TranslationPolicyAgentDecision.Deny);
                _status = "ATC_HardcodedUi_AgentSummary".Translate(allowed, review, denied).ToString();
                completed = true;
            }
            catch (Exception ex)
            {
                _status = "ATC_HardcodedUi_AgentFailed".Translate(ex.Message).ToString();
            }
            finally
            {
                await TranslationPolicyAgentCoordinator.EndRunAsync(runId, completed);
                _busy = false;
            }
        }

        private void StartScan()
        {
            _busy = true;
            _status = string.Empty;
            ModMetaData target = _selectedMod;
            Task.Run(() => HardcodedUiRuntimeScanner.Scan(target)).ContinueWith(task =>
                ATC_Dispatcher.RunOnMainThread(() =>
                {
                    _busy = false;
                    if (task.IsFaulted)
                    {
                        _status = task.Exception?.GetBaseException().Message ?? "scan failed";
                        return;
                    }
                    _scanResult = task.Result;
                    LoadExistingTranslations();
                    _visibleEntries = null;
                    _status = _scanResult.AssemblyCount == 0
                        ? "ATC_HardcodedUi_NoLoadedAssembly".Translate().ToString()
                        : string.Empty;
                }));
        }

        private async void StartAiTranslation()
        {
            List<HardcodedUiPatchEntry> targets = _scanResult.Entries
                .Where(entry => IsEffectivelyEnabled(entry) &&
                    (!_translations.TryGetValue(entry.EntryId, out string value) || string.IsNullOrWhiteSpace(value)))
                .ToList();
            if (targets.Count == 0)
            {
                _status = "ATC_HardcodedUi_NoPending".Translate().ToString();
                return;
            }
            _busy = true;
            try
            {
                for (int offset = 0; offset < targets.Count; offset += 20)
                {
                    List<HardcodedUiPatchEntry> batch = targets.Skip(offset).Take(20).ToList();
                    List<string> translated = await AutoTranslatorAPI.TranslateBatchAsync(
                        batch.Select(entry => entry.Literal).ToList(),
                        packageId: _selectedMod.PackageId,
                        requestScope: "hardcoded-ui-workbench-" + offset,
                        requestPurpose: "review");
                    if (translated == null || translated.Count != batch.Count)
                    {
                        _status = "ATC_HardcodedUi_AiFailed".Translate().ToString();
                        break;
                    }
                    for (int i = 0; i < batch.Count; i++)
                    {
                        if (AutoTranslatorScanner.TryAcceptTranslatedValue(
                                translated[i],
                                batch[i].Literal,
                                out string accepted,
                                out _,
                                out _))
                            _translations[batch[i].EntryId] = accepted;
                    }
                }
                if (string.IsNullOrWhiteSpace(_status))
                    _status = "ATC_HardcodedUi_AiDone".Translate().ToString();
            }
            catch (Exception ex)
            {
                _status = ex.Message;
            }
            finally
            {
                _busy = false;
            }
        }

        private void LoadExistingTranslations()
        {
            _translations.Clear();
            try
            {
                string path = HardcodedUiTargetedPatchManager.ManifestPath;
                if (!File.Exists(path)) return;
                HardcodedUiPatchManifest manifest = JsonConvert.DeserializeObject<HardcodedUiPatchManifest>(File.ReadAllText(path));
                string folder = AutoTranslatorScanner.GetFolderNameByLanguage(AutoTranslatorMod.Settings.TargetLang);
                Dictionary<string, HardcodedUiPatchEntry> existing = (manifest?.Entries ?? new List<HardcodedUiPatchEntry>())
                    .Where(entry => entry != null).GroupBy(entry => entry.EntryId)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
                foreach (HardcodedUiPatchEntry entry in _scanResult.Entries)
                {
                    if (existing.TryGetValue(entry.EntryId, out HardcodedUiPatchEntry saved))
                    {
                        if (saved.Enabled)
                        {
                            HardcodedUiDecisionRecord decision = GetDecision(entry);
                            if (decision.UserOverride == HardcodedUiUserOverride.None &&
                                decision.AutomaticDecision != HardcodedUiAutomaticDecision.Translate)
                                decision.SetUserOverride(HardcodedUiUserOverride.Translate);
                        }
                        entry.Enabled = IsEffectivelyEnabled(entry);
                        entry.Translations = saved.Translations != null
                            ? new Dictionary<string, string>(saved.Translations, StringComparer.OrdinalIgnoreCase)
                            : new Dictionary<string, string>();
                        _translations[entry.EntryId] = entry.Translations.TryGetValue(
                            folder,
                            out string value)
                            ? value
                            : string.Empty;
                    }
                    else _translations[entry.EntryId] = string.Empty;
                }
            }
            catch (Exception ex)
            {
                _status = "Existing manifest: " + ex.Message;
            }
        }

        private void SaveAndApply()
        {
            try
            {
                string folder = AutoTranslatorScanner.GetFolderNameByLanguage(AutoTranslatorMod.Settings.TargetLang);
                string path = HardcodedUiTargetedPatchManager.ManifestPath;
                HardcodedUiPatchManifest manifest = null;
                if (File.Exists(path))
                    manifest = JsonConvert.DeserializeObject<HardcodedUiPatchManifest>(File.ReadAllText(path));
                manifest = manifest ?? new HardcodedUiPatchManifest();
                HardcodedUiDecisionState.Persist(_scanResult.Decisions.Values);
                manifest.Approved = true;
                manifest.Entries = (manifest.Entries ?? new List<HardcodedUiPatchEntry>())
                    .Where(entry => entry != null && !string.Equals(entry.PackageId, _selectedMod.PackageId, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                foreach (HardcodedUiPatchEntry entry in _scanResult.Entries.Where(IsEffectivelyEnabled))
                {
                    if (_translations.TryGetValue(entry.EntryId, out string value))
                    {
                        if (string.IsNullOrWhiteSpace(value) ||
                            string.Equals(value.Trim(), entry.Literal, StringComparison.Ordinal))
                            entry.Translations.Remove(folder);
                        else
                            entry.Translations[folder] = value.Trim();
                    }
                    if (entry.Translations.Count > 0) manifest.Entries.Add(entry);
                }
                if (manifest.Entries.Count > 10000) throw new InvalidOperationException("Approved manifest exceeds 10000 entries.");
                string directory = Path.GetDirectoryName(path);
                Directory.CreateDirectory(directory);
                string temp = path + ".tmp";
                File.WriteAllText(temp, JsonConvert.SerializeObject(manifest, Formatting.Indented));
                if (File.Exists(path)) File.Replace(temp, path, path + ".bak", true);
                else File.Move(temp, path);

                AutoTranslatorMod.Settings.EnableUIInterceptor = false;
                AutoTranslatorMod.Settings.EnableHardcodedUiPrototype = true;
                LoadedModManager.GetMod<AutoTranslatorMod>()?.WriteSettings();
                HardcodedUiTargetedPatchManager.RequestReload();
                _status = "ATC_HardcodedUi_Saved".Translate().ToString();
            }
            catch (Exception ex)
            {
                _status = "ATC_HardcodedUi_SaveFailed".Translate(ex.Message).ToString();
            }
        }

        private HardcodedUiDecisionRecord GetDecision(HardcodedUiPatchEntry entry)
        {
            if (entry == null) return new HardcodedUiDecisionRecord();
            if (_scanResult != null && _scanResult.Decisions.TryGetValue(
                    entry.EntryId,
                    out HardcodedUiDecisionRecord decision))
                return decision;
            decision = HardcodedUiBaselineDecisionAnalyzer.Analyze(entry);
            _scanResult?.Decisions.Add(entry.EntryId, decision);
            return decision;
        }

        private bool IsEffectivelyEnabled(HardcodedUiPatchEntry entry)
        {
            return GetDecision(entry).EffectiveDecision == HardcodedUiAutomaticDecision.Translate;
        }

        private static string GetDecisionLabel(HardcodedUiAutomaticDecision decision)
        {
            return (decision == HardcodedUiAutomaticDecision.Translate
                ? "ATC_PolicyPreflight_DecisionAllow"
                : decision == HardcodedUiAutomaticDecision.DoNotTranslate
                    ? "ATC_PolicyPreflight_DecisionDeny"
                    : "ATC_PolicyPreflight_DecisionUncertain").Translate();
        }

        private static void AddDisabledReasonTooltip(Rect rect, bool disabled, string reason)
        {
            if (!disabled || string.IsNullOrWhiteSpace(reason)) return;
            TooltipHandler.TipRegion(rect, "ATC_DisabledReason".Translate(reason));
        }
    }
}
