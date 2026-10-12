using RimWorld;
using System;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using Verse;
using static AutoTranslator_Core.DeleteTranslationWindow;

namespace AutoTranslator_Core
{
    public class Window_CorrectionSubmit : Window
    {
        private readonly ModMetaData _mod;
        private readonly string _category;
        private readonly string _entryKey;
        private readonly string _sourceText;
        private readonly string _currentTranslation;
        private string _proposedTranslation;
        private string _reason = "";
        private bool _isSubmitting;

        public override Vector2 InitialSize => new Vector2(900f, 720f);

        public Window_CorrectionSubmit(ModMetaData mod, string category, string entryKey, string sourceText, string currentTranslation, string proposedTranslation)
        {
            _mod = mod;
            _category = category ?? "";
            _entryKey = entryKey ?? "";
            _sourceText = sourceText ?? "";
            _currentTranslation = currentTranslation ?? "";
            _proposedTranslation = proposedTranslation ?? currentTranslation ?? "";
            doCloseButton = false;
            doCloseX = true;
            forcePause = true;
            absorbInputAroundWindow = true;
        }

        public override void DoWindowContents(Rect inRect)
        {
            Patch_GUI_Label_GUIContent.BypassInterceptor = true;
            try
            {
                Text.Font = GameFont.Medium;
                Widgets.Label(new Rect(0f, 0f, inRect.width, 34f), "ATC_Correction_Title".Translate(_mod != null ? _mod.Name : ""));
                Text.Font = GameFont.Small;
                Widgets.DrawLineHorizontal(0f, 36f, inRect.width);

                float y = 46f;
                DrawMetaLine(new Rect(0f, y, inRect.width, 22f), "ATC_Correction_MetaPackage".Translate(), _mod != null ? _mod.PackageId : "");
                y += 24f;
                DrawMetaLine(new Rect(0f, y, inRect.width, 22f), "ATC_Correction_MetaEntry".Translate(), $"{_category} / {_entryKey}");
                y += 30f;

                float columnGap = 12f;
                float columnWidth = (inRect.width - columnGap) / 2f;
                Rect sourceRect = new Rect(0f, y + 22f, columnWidth, 120f);
                Rect currentRect = new Rect(columnWidth + columnGap, y + 22f, columnWidth, 120f);

                Widgets.Label(new Rect(sourceRect.x, y, sourceRect.width, 22f), "ATC_Correction_SourceLabel".Translate());
                Widgets.Label(new Rect(currentRect.x, y, currentRect.width, 22f), "ATC_Correction_CurrentLabel".Translate());
                DrawReadOnlyBox(sourceRect, _sourceText);
                DrawReadOnlyBox(currentRect, _currentTranslation);
                y += 150f;

                Widgets.Label(new Rect(0f, y, inRect.width, 22f), "ATC_Correction_ProposedLabel".Translate());
                y += 24f;
                Rect proposedRect = new Rect(0f, y, inRect.width, 130f);
                _proposedTranslation = Widgets.TextArea(proposedRect, _proposedTranslation ?? "");
                y += 140f;

                Widgets.Label(new Rect(0f, y, inRect.width, 22f), "ATC_Correction_ReasonLabel".Translate());
                y += 24f;
                Rect reasonRect = new Rect(0f, y, inRect.width, 92f);
                _reason = Widgets.TextArea(reasonRect, _reason ?? "");
                if (string.IsNullOrWhiteSpace(_reason))
                {
                    GUI.color = Color.gray;
                    Widgets.Label(new Rect(reasonRect.x + 5f, reasonRect.y + 3f, reasonRect.width - 10f, reasonRect.height - 6f), "ATC_Correction_ReasonHint".Translate());
                    GUI.color = Color.white;
                }

                y = inRect.height - 42f;
                GUI.color = new Color(1f, 0.5f, 0.5f);
                if (WorkflowUiStyle.Button(new Rect(0f, y, 130f, 36f), "ATC_Btn_Cancel".Translate(),
                        WorkflowButtonStyle.Quiet))
                {
                    Close();
                }

                GUI.color = _isSubmitting ? Color.yellow : new Color(0.4f, 1f, 0.4f);
                if (WorkflowUiStyle.Button(
                        new Rect(inRect.width - 190f, y, 190f, 36f),
                        _isSubmitting ? "ATC_Correction_Submitting".Translate().ToString() : "ATC_Correction_SubmitBtn".Translate().ToString(),
                        WorkflowButtonStyle.Primary,
                        !_isSubmitting))
                {
                    TrySubmit();
                }
                GUI.color = Color.white;
            }
            finally
            {
                Text.Font = GameFont.Small;
                Text.Anchor = TextAnchor.UpperLeft;
                GUI.color = Color.white;
                Patch_GUI_Label_GUIContent.BypassInterceptor = false;
            }
        }

        private void DrawMetaLine(Rect rect, string label, string value)
        {
            GUI.color = Color.gray;
            Widgets.Label(new Rect(rect.x, rect.y, 120f, rect.height), label);
            GUI.color = Color.white;
            Text.WordWrap = false;
            Widgets.Label(new Rect(rect.x + 125f, rect.y, rect.width - 125f, rect.height), value ?? "");
            Text.WordWrap = true;
        }

        private void DrawReadOnlyBox(Rect rect, string text)
        {
            Widgets.DrawBoxSolid(rect, new Color(0.08f, 0.08f, 0.08f, 0.75f));
            Widgets.DrawBox(rect, 1);
            Rect inner = rect.ContractedBy(6f);
            try { Widgets.Label(inner, text ?? ""); }
            catch { Widgets.Label(inner, "[Invalid rich text]"); }
        }

        private void TrySubmit()
        {
            if (_isSubmitting) return;

            string proposed = (_proposedTranslation ?? "").Trim();
            string current = (_currentTranslation ?? "").Trim();
            string reason = (_reason ?? "").Trim();

            if (string.IsNullOrWhiteSpace(proposed))
            {
                Messages.Message("ATC_Correction_Msg_EmptyProposed".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }

            if (string.Equals(proposed, current, StringComparison.Ordinal))
            {
                Messages.Message("ATC_Correction_Msg_Unchanged".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }

            if (reason.Length < 8)
            {
                Messages.Message("ATC_Correction_Msg_ReasonRequired".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }

            _isSubmitting = true;
            TranslationCorrectionSubmission submission = BuildSubmission(proposed, reason);
            string token = AutoTranslatorMod.Settings != null ? AutoTranslatorMod.Settings.CloudAdminToken : "";

            Task.Run(async () =>
            {
                bool success = await AutoTranslatorCloudClient.SubmitCorrectionAsync(submission, token);
                ATC_Dispatcher.RunOnMainThread(() =>
                {
                    _isSubmitting = false;
                    if (success)
                    {
                        Messages.Message("ATC_Correction_Msg_Submitted".Translate(), MessageTypeDefOf.PositiveEvent, false);
                        Close();
                    }
                    else
                    {
                        Messages.Message("ATC_Correction_Msg_SubmitFailedOutbox".Translate(), MessageTypeDefOf.RejectInput, false);
                    }
                });
            });
        }

        private TranslationCorrectionSubmission BuildSubmission(string proposed, string reason)
        {
            string packageId = _mod != null ? _mod.PackageId ?? "" : "";
            string targetLangFolder = AutoTranslatorScanner.GetFolderNameByLanguage(AutoTranslatorMod.Settings.TargetLang);
            bool isOfficialGameText = packageId.StartsWith("ludeon.rimworld", StringComparison.OrdinalIgnoreCase);
            string modLastUpdated = "";

            try
            {
                if (_mod != null && _mod.RootDir != null && Directory.Exists(_mod.RootDir.FullName))
                    modLastUpdated = new DirectoryInfo(_mod.RootDir.FullName).LastWriteTimeUtc.ToString("O");
            }
            catch { }

            return new TranslationCorrectionSubmission
            {
                ClientSubmissionId = Guid.NewGuid().ToString("N"),
                PackageId = packageId,
                Language = targetLangFolder,
                ModName = _mod != null ? _mod.Name ?? "" : "",
                GameVersion = RimWorld.VersionControl.CurrentVersionStringWithoutBuild,
                ModLastUpdated = modLastUpdated,
                ScopeType = string.Equals(_category, "Keyed", StringComparison.OrdinalIgnoreCase) ? "Keyed" : "DefInjected",
                EntryType = _category,
                EntryKey = _entryKey,
                SourceText = _sourceText,
                CurrentTranslation = _currentTranslation,
                ProposedTranslation = proposed,
                Reason = reason,
                ContributorId = UnityEngine.SystemInfo.deviceUniqueIdentifier,
                ContributorName = AutoTranslatorMod.Settings != null ? AutoTranslatorMod.Settings.CloudNickname ?? "" : "",
                QualityTier = "InGameCorrection",
                StatusHint = isOfficialGameText ? "pending_official_review" : "pending",
                IsOfficialGameText = isOfficialGameText,
                CreatedAt = DateTime.UtcNow
            };
        }
    }
}
