using RimWorld;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using Verse;
using static AutoTranslator_Core.DeleteTranslationWindow;

namespace AutoTranslator_Core
{
    public class Window_AppliedCorrections : Window
    {
        private readonly ModMetaData _mod;
        private readonly string _packageId;
        private readonly string _modName;
        private readonly string _targetLangFolder;
        private List<AppliedTranslationCorrection> _corrections = new List<AppliedTranslationCorrection>();
        private Vector2 _scrollPos;
        private bool _isLoading = true;
        private string _error = "";

        public override Vector2 InitialSize => new Vector2(980f, 760f);

        public Window_AppliedCorrections(ModMetaData mod, string targetLangFolder)
        {
            _mod = mod;
            _packageId = mod != null ? mod.PackageId ?? "" : "";
            _modName = mod != null ? mod.Name ?? _packageId : _packageId;
            _targetLangFolder = targetLangFolder ?? "";
            doCloseButton = false;
            doCloseX = true;
            forcePause = true;
            absorbInputAroundWindow = true;
            LoadCorrections();
        }

        public override void DoWindowContents(Rect inRect)
        {
            Patch_GUI_Label_GUIContent.BypassInterceptor = true;
            try
            {
                Text.Font = GameFont.Medium;
                Widgets.Label(new Rect(0f, 0f, inRect.width, 32f), "ATC_Corrections_Title".Translate(_modName));
                Text.Font = GameFont.Small;
                Widgets.DrawLineHorizontal(0f, 36f, inRect.width);

                float y = 46f;
                DrawMetaLine(new Rect(0f, y, inRect.width, 22f), "ATC_Correction_MetaPackage".Translate(), _packageId);
                y += 24f;
                DrawMetaLine(new Rect(0f, y, inRect.width, 22f), "ATC_Cloud_SelectLang".Translate(), _targetLangFolder);
                y += 34f;

                if (_isLoading)
                {
                    GUI.color = Color.yellow;
                    Widgets.Label(new Rect(0f, y, inRect.width, 30f), "ATC_Corrections_Loading".Translate());
                    GUI.color = Color.white;
                    DrawBottomButtons(inRect);
                    return;
                }

                if (!string.IsNullOrWhiteSpace(_error))
                {
                    GUI.color = new Color(1f, 0.45f, 0.45f);
                    Widgets.Label(new Rect(0f, y, inRect.width, 60f), _error);
                    GUI.color = Color.white;
                    DrawBottomButtons(inRect);
                    return;
                }

                if (_corrections == null || _corrections.Count == 0)
                {
                    GUI.color = Color.gray;
                    Widgets.Label(new Rect(0f, y, inRect.width, 40f), "ATC_Corrections_Empty".Translate());
                    GUI.color = Color.white;
                    DrawBottomButtons(inRect);
                    return;
                }

                Rect outRect = new Rect(0f, y, inRect.width, inRect.height - y - 48f);
                const float rowHeight = 190f;
                const float rowGap = 8f;
                float viewHeight = _corrections.Count * (rowHeight + rowGap) + rowGap;
                Rect viewRect = new Rect(0f, 0f, outRect.width - 18f, viewHeight);
                Widgets.BeginScrollView(outRect, ref _scrollPos, viewRect);

                float rowY = rowGap;
                foreach (AppliedTranslationCorrection correction in _corrections)
                {
                    Rect rowRect = new Rect(0f, rowY, viewRect.width, rowHeight);
                    DrawCorrectionRow(rowRect, correction);
                    rowY += rowHeight + rowGap;
                }

                Widgets.EndScrollView();
                DrawBottomButtons(inRect);
            }
            finally
            {
                Text.Font = GameFont.Small;
                Text.Anchor = TextAnchor.UpperLeft;
                Text.WordWrap = true;
                GUI.color = Color.white;
                Patch_GUI_Label_GUIContent.BypassInterceptor = false;
            }
        }

        private void LoadCorrections()
        {
            _isLoading = true;
            _error = "";
            Task.Run(async () =>
            {
                List<AppliedTranslationCorrection> result = null;
                string error = "";
                try
                {
                    result = await AutoTranslatorCloudClient.FetchAppliedCorrectionsAsync(_packageId, _targetLangFolder);
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }

                ATC_Dispatcher.RunOnMainThread(() =>
                {
                    _isLoading = false;
                    _error = string.IsNullOrWhiteSpace(error) ? "" : "ATC_Corrections_LoadFailed".Translate(error).ToString();
                    _corrections = (result ?? new List<AppliedTranslationCorrection>())
                        .Where(c => c != null && string.Equals(c.Status, "applied", StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(c => c.AppliedAt ?? c.UpdatedAt ?? c.CreatedAt ?? DateTime.MinValue)
                        .ThenBy(c => c.EntryKey ?? "", StringComparer.OrdinalIgnoreCase)
                        .ToList();
                });
            });
        }

        private void DrawCorrectionRow(Rect rect, AppliedTranslationCorrection correction)
        {
            Widgets.DrawHighlightIfMouseover(rect);
            Widgets.DrawBox(rect, 1);

            Rect inner = rect.ContractedBy(8f);
            bool applied = AutoTranslatorCloudClient.IsSingleCorrectionOverlayApplied(_packageId, correction, _targetLangFolder);
            string contributor = string.IsNullOrWhiteSpace(correction.ContributorName)
                ? "ATC_Corrections_UnknownContributor".Translate().ToString()
                : correction.ContributorName;
            string entry = $"{correction.EntryType ?? correction.ScopeType} / {correction.EntryKey}";

            Text.Font = GameFont.Small;
            Text.WordWrap = false;
            Widgets.Label(new Rect(inner.x, inner.y, inner.width - 210f, 22f), entry);
            Text.Font = GameFont.Tiny;
            GUI.color = new Color(0.78f, 0.78f, 0.78f);
            Widgets.Label(new Rect(inner.x, inner.y + 22f, inner.width - 210f, 20f),
                "ATC_Corrections_Byline".Translate(contributor, FormatDate(correction.AppliedAt ?? correction.UpdatedAt ?? correction.CreatedAt)));
            GUI.color = Color.white;
            Text.WordWrap = true;

            float columnGap = 10f;
            float columnWidth = (inner.width - 210f - columnGap) / 2f;
            Rect currentRect = new Rect(inner.x, inner.y + 48f, columnWidth, 44f);
            Rect proposedRect = new Rect(inner.x + columnWidth + columnGap, inner.y + 48f, columnWidth, 44f);
            DrawTextBox(currentRect, "ATC_Correction_CurrentLabel".Translate().ToString(), correction.CurrentTranslation);
            DrawTextBox(proposedRect, "ATC_Correction_ProposedLabel".Translate().ToString(), correction.ProposedTranslation);

            Rect reasonRect = new Rect(inner.x, inner.y + 102f, inner.width - 210f, 64f);
            DrawTextBox(reasonRect, "ATC_Correction_ReasonLabel".Translate().ToString(), correction.Reason);

            Rect applyBtn = new Rect(inner.xMax - 190f, inner.y + 16f, 180f, 34f);
            GUI.color = applied ? new Color(1f, 0.65f, 0.45f) : new Color(0.45f, 1f, 0.55f);
            if (Widgets.ButtonText(applyBtn, applied ? "ATC_Corrections_RemoveBtn".Translate().ToString() : "ATC_Corrections_ApplyBtn".Translate().ToString()))
            {
                if (applied) RemoveCorrection(correction);
                else ApplyCorrection(correction);
            }
            GUI.color = Color.white;

            Rect sourceBtn = new Rect(inner.xMax - 190f, inner.y + 58f, 180f, 30f);
            if (Widgets.ButtonText(sourceBtn, "ATC_Corrections_SourceBtn".Translate()))
            {
                Find.WindowStack.Add(new Dialog_MessageBox(correction.SourceText ?? ""));
            }

            Rect statusRect = new Rect(inner.xMax - 190f, inner.y + 104f, 180f, 24f);
            GUI.color = applied ? new Color(0.55f, 1f, 0.55f) : Color.gray;
            Text.Anchor = TextAnchor.MiddleCenter;
            Widgets.Label(statusRect, applied ? "ATC_Corrections_Applied".Translate().ToString() : "ATC_Corrections_NotApplied".Translate().ToString());
            Text.Anchor = TextAnchor.UpperLeft;
            GUI.color = Color.white;
        }

        private void ApplyCorrection(AppliedTranslationCorrection correction)
        {
            int changed = AutoTranslatorCloudClient.ApplySingleCorrectionOverlay(_packageId, correction, _targetLangFolder);
            if (changed > 0)
            {
                AutoTranslatorScanner.RequestMemoryDropForPackage(_packageId);
                Messages.Message("ATC_Corrections_ApplySuccess".Translate(), MessageTypeDefOf.PositiveEvent, false);
            }
            else
            {
                Messages.Message("ATC_Corrections_ApplyFailed".Translate(), MessageTypeDefOf.RejectInput, false);
            }
        }

        private void RemoveCorrection(AppliedTranslationCorrection correction)
        {
            int changed = AutoTranslatorCloudClient.RemoveSingleCorrectionOverlay(_packageId, correction, _targetLangFolder);
            if (changed > 0)
            {
                Dictionary<string, HashSet<string>> clearKeys = BuildClearKeys(correction);
                AutoTranslatorScanner.RequestMemoryDropForPackage(_packageId, clearKeys);
                Messages.Message("ATC_Corrections_RemoveSuccess".Translate(), MessageTypeDefOf.NeutralEvent, false);
            }
            else
            {
                Messages.Message("ATC_Corrections_RemoveFailed".Translate(), MessageTypeDefOf.RejectInput, false);
            }
        }

        private static Dictionary<string, HashSet<string>> BuildClearKeys(AppliedTranslationCorrection correction)
        {
            string defType = string.Equals(correction.ScopeType, "Keyed", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(correction.EntryType, "Keyed", StringComparison.OrdinalIgnoreCase)
                ? "Keyed"
                : string.IsNullOrWhiteSpace(correction.EntryType) ? "General" : correction.EntryType.Trim();
            return new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
            {
                { defType, new HashSet<string>(new[] { correction.EntryKey ?? "" }, StringComparer.OrdinalIgnoreCase) }
            };
        }

        private void DrawBottomButtons(Rect inRect)
        {
            float y = inRect.height - 38f;
            if (Widgets.ButtonText(new Rect(0f, y, 130f, 34f), "ATC_Corrections_RefreshBtn".Translate()))
            {
                LoadCorrections();
            }

            if (Widgets.ButtonText(new Rect(inRect.width - 130f, y, 130f, 34f), "ATC_ContactAuthor_Close".Translate()))
            {
                Close();
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

        private void DrawTextBox(Rect rect, string label, string text)
        {
            Rect labelRect = new Rect(rect.x, rect.y, rect.width, 16f);
            Text.Font = GameFont.Tiny;
            GUI.color = Color.gray;
            Widgets.Label(labelRect, label ?? "");
            GUI.color = Color.white;

            Rect boxRect = new Rect(rect.x, rect.y + 16f, rect.width, rect.height - 16f);
            Widgets.DrawBoxSolid(boxRect, new Color(0.08f, 0.08f, 0.08f, 0.62f));
            Rect inner = boxRect.ContractedBy(4f);
            Text.WordWrap = true;
            try { Widgets.Label(inner, text ?? ""); }
            catch { Widgets.Label(inner, "[Invalid rich text]"); }
        }

        private static string FormatDate(DateTime? date)
        {
            return date.HasValue ? date.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "";
        }
    }
}
