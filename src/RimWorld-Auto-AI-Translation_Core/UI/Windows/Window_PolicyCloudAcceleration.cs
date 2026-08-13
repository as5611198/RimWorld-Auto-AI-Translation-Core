using System;
using System.Linq;
using System.Threading.Tasks;
using RimWorld;
using UnityEngine;
using Verse;

namespace AutoTranslator_Core
{
    internal sealed class Window_PolicyCloudAcceleration : Window
    {
        private readonly ModMetaData _mod;
        private readonly Action _selectForAnalysis;
        private bool _uploading;

        public override Vector2 InitialSize => new Vector2(620f, 430f);

        public Window_PolicyCloudAcceleration(ModMetaData mod, Action selectForAnalysis)
        {
            _mod = mod;
            _selectForAnalysis = selectForAnalysis;
            doCloseButton = true;
            doCloseX = true;
            forcePause = true;
            absorbInputAroundWindow = true;
        }

        public override void DoWindowContents(Rect inRect)
        {
            string packageId = _mod?.PackageId ?? string.Empty;
            PolicyAnalysisLocalState state = PolicyAnalysisLocalStateManager.Get(packageId);
            bool globallyEnabled = AutoTranslatorSettings.IsPolicyAnalysisCloudCacheAvailable &&
                                   AutoTranslatorMod.Settings.EnablePolicyAnalysisCloudCache;
            bool disabled = AutoTranslatorMod.Settings.IsPolicyCloudAccelerationDisabled(packageId);

            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, 0f, inRect.width, 32f), "ATC_PolicyCloud_ModTitle".Translate(_mod?.Name ?? packageId));
            Text.Font = GameFont.Small;

            string status = GetStatusText(globallyEnabled, disabled, state);
            Widgets.Label(new Rect(0f, 42f, inRect.width, 88f),
                "ATC_PolicyCloud_ModExplanation".Translate(packageId, status));

            if (state != null)
            {
                int pending = state.PendingAllowedCandidateIds?.Count ?? 0;
                Widgets.Label(new Rect(0f, 135f, inRect.width, 82f),
                    "ATC_PolicyCloud_ModDetails".Translate(
                        state.CandidateCount,
                        state.CloudAllowedCount,
                        pending,
                        state.UpdatedUtc ?? string.Empty));
            }

            float y = 230f;
            if (disabled)
            {
                if (Widgets.ButtonText(new Rect(0f, y, 220f, 38f), "ATC_PolicyCloud_Reenable".Translate()))
                {
                    AutoTranslatorMod.Settings.SetPolicyCloudAccelerationDisabled(packageId, false);
                    LoadedModManager.GetMod<AutoTranslatorMod>()?.WriteSettings();
                }
            }
            else
            {
                if (Widgets.ButtonText(new Rect(0f, y, 280f, 38f), "ATC_PolicyCloud_DisableAndReanalyze".Translate()))
                {
                    AutoTranslatorMod.Settings.SetPolicyCloudAccelerationDisabled(packageId, true);
                    LoadedModManager.GetMod<AutoTranslatorMod>()?.WriteSettings();
                    _selectForAnalysis?.Invoke();
                    Messages.Message("ATC_PolicyCloud_ReanalysisQueued".Translate(_mod?.Name ?? packageId), MessageTypeDefOf.TaskCompletion, false);
                    Close();
                }
            }

            bool hasPending = state != null &&
                string.Equals(state.Status, PolicyAnalysisLocalStateStore.PendingUploadStatus, StringComparison.Ordinal) &&
                state.PendingAllowedCandidateIds != null;
            GUI.color = hasPending && !_uploading ? Color.white : Color.grey;
            if (Widgets.ButtonText(new Rect(inRect.width - 280f, y, 280f, 38f),
                    _uploading ? "ATC_PolicyCloud_Uploading".Translate() : "ATC_PolicyCloud_UploadIncrement".Translate()) &&
                hasPending && !_uploading)
            {
                UploadPendingAsync(state);
            }
            GUI.color = Color.white;

            if (hasPending && Widgets.ButtonText(new Rect(inRect.width - 280f, y + 48f, 280f, 34f),
                    "ATC_PolicyCloud_DiscardPending".Translate()))
            {
                PolicyAnalysisLocalStateManager.DiscardPending(packageId);
            }

            TooltipHandler.TipRegion(new Rect(0f, y, inRect.width, 90f), "ATC_PolicyCloud_AppendOnlyTooltip".Translate());
        }

        private async void UploadPendingAsync(PolicyAnalysisLocalState state)
        {
            _uploading = true;
            bool uploaded = false;
            try
            {
                uploaded = await AutoTranslatorCloudClient.AppendPolicyAnalysisAsync(new PolicyAnalysisContribution
                {
                    PackageId = state.PackageId,
                    ModName = state.ModName,
                    GameVersion = state.GameVersion,
                    SourceFingerprint = state.SourceFingerprint,
                    PolicyVersion = state.PolicyVersion,
                    PromptVersion = state.PromptVersion,
                    ContributorId = AutoTranslatorMod.Settings.PolicyCloudContributorId,
                    ContributionId = state.PendingContributionId,
                    CandidateCount = state.CandidateCount,
                    AddAllowedCandidateIds = (state.PendingAllowedCandidateIds ?? new System.Collections.Generic.List<string>()).ToList(),
                    AnalyzedUtc = state.UpdatedUtc
                });
                if (uploaded) PolicyAnalysisLocalStateManager.MarkUploaded(state.PackageId);
            }
            catch
            {
                uploaded = false;
            }
            finally
            {
                _uploading = false;
                Messages.Message(
                    uploaded ? "ATC_PolicyCloud_UploadSuccess".Translate() : "ATC_PolicyCloud_UploadFailed".Translate(),
                    uploaded ? MessageTypeDefOf.TaskCompletion : MessageTypeDefOf.RejectInput,
                    false);
            }
        }

        private static string GetStatusText(bool globallyEnabled, bool disabled, PolicyAnalysisLocalState state)
        {
            if (!globallyEnabled) return "ATC_PolicyCloud_StatusGlobalOff".Translate();
            if (disabled) return "ATC_PolicyCloud_StatusDisabled".Translate();
            if (state == null) return "ATC_PolicyCloud_StatusNotUsed".Translate();
            if (string.Equals(state.Status, PolicyAnalysisLocalStateStore.AcceleratedStatus, StringComparison.Ordinal))
                return "ATC_PolicyCloud_StatusAccelerated".Translate();
            if (string.Equals(state.Status, PolicyAnalysisLocalStateStore.PendingUploadStatus, StringComparison.Ordinal))
                return "ATC_PolicyCloud_StatusPending".Translate();
            if (string.Equals(state.Status, PolicyAnalysisLocalStateStore.UploadedStatus, StringComparison.Ordinal))
                return "ATC_PolicyCloud_StatusUploaded".Translate();
            return "ATC_PolicyCloud_StatusNotUsed".Translate();
        }
    }
}
