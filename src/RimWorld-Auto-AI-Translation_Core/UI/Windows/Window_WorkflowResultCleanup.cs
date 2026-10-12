using AutoTranslator_Core.Workflow;
using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace AutoTranslator_Core
{
    internal sealed class Window_WorkflowResultCleanup : Window
    {
        private readonly List<string> _modIdentities;
        private readonly Action<WorkflowResultCleanupOptions> _confirm;
        private readonly bool _includeLocalAiTranslation;
        private readonly string _title;
        private bool _clearAiReview;
        private bool _clearManualClassification;
        private bool _clearLocalAiTranslation;

        public override Vector2 InitialSize => new Vector2(620f, _includeLocalAiTranslation ? 390f : 350f);

        internal Window_WorkflowResultCleanup(
            IEnumerable<string> modIdentities,
            Action<WorkflowResultCleanupOptions> confirm,
            bool includeLocalAiTranslation = true,
            string title = null)
        {
            _modIdentities = new List<string>(modIdentities ?? Array.Empty<string>());
            _confirm = confirm;
            _includeLocalAiTranslation = includeLocalAiTranslation;
            _title = string.IsNullOrWhiteSpace(title)
                ? AutoTranslatorMod.WfText("清除所选 Mod 的结果", "Clear selected mod results")
                : title;
            doCloseButton = false;
            doCloseX = true;
            closeOnAccept = false;
            closeOnCancel = true;
            forcePause = true;
            absorbInputAroundWindow = true;
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, 0f, inRect.width, 34f), _title);

            Text.Font = GameFont.Small;
            GUI.color = WorkflowUiStyle.MutedText;
            Widgets.Label(new Rect(0f, 42f, inRect.width, 48f),
                AutoTranslatorMod.WfText(
                    "将对当前选择的 " + _modIdentities.Count + " 个 Mod 执行清除。请选择要永久删除的数据类型：",
                    "This will clear results for " + _modIdentities.Count +
                    " selected mods. Choose the data types to delete permanently:"));
            GUI.color = Color.white;

            float optionsHeight = _includeLocalAiTranslation ? 126f : 90f;
            Rect options = new Rect(0f, 96f, inRect.width, optionsHeight);
            Widgets.DrawBoxSolid(options, WorkflowUiStyle.RaisedPanel);
            WorkflowUiStyle.DrawBorder(options, new Color(0.31f, 0.35f, 0.38f));
            Rect aiRect = new Rect(options.x + 14f, options.y + 10f, options.width - 28f, 28f);
            Rect manualRect = new Rect(aiRect.x, aiRect.yMax + 8f, aiRect.width, 28f);
            Widgets.CheckboxLabeled(aiRect,
                AutoTranslatorMod.WfText("AI 复核结果", "AI review results"), ref _clearAiReview);
            Widgets.CheckboxLabeled(manualRect,
                AutoTranslatorMod.WfText("手动分类结果", "Manual classification results"),
                ref _clearManualClassification);
            if (_includeLocalAiTranslation)
            {
                Rect translationRect = new Rect(aiRect.x, manualRect.yMax + 8f, aiRect.width, 28f);
                Widgets.CheckboxLabeled(translationRect,
                    AutoTranslatorMod.WfText("AI 翻译结果", "AI translation results"),
                    ref _clearLocalAiTranslation);
                TooltipHandler.TipRegion(translationRect, AutoTranslatorMod.WfText(
                    "仅删除 ATC 调用 AI 生成的译文记录和受管输出；不会删除云端下载、手工、Mod 原生或第三方译文。AI 提供方和模型另行记录。",
                    "Deletes only translations generated through ATC AI calls and their managed output. Cloud downloads, manual, mod-native, and third-party translations are preserved. AI provider and model are recorded separately."));
            }

            GUI.color = WorkflowUiStyle.ErrorText;
            Text.Font = GameFont.Tiny;
            Widgets.Label(new Rect(0f, options.yMax + 16f, inRect.width, 44f),
                AutoTranslatorMod.WfText(
                    "警告：删除的数据无法恢复，请谨慎操作。清除 AI 复核结果与手动分类结果后，有效分类将回到本地 XML/DLL 分析结果。",
                    "Warning: deleted data cannot be recovered. Clearing both AI review and manual classifications restores the effective classification to the local XML/DLL result."));
            GUI.color = Color.white;

            bool canConfirm = _modIdentities.Count > 0 &&
                               (_clearAiReview || _clearManualClassification ||
                                (_includeLocalAiTranslation && _clearLocalAiTranslation));
            const float buttonWidth = 150f;
            Rect cancelRect = new Rect(inRect.width - buttonWidth, inRect.height - 42f, buttonWidth, 36f);
            Rect confirmRect = new Rect(cancelRect.x - 10f - buttonWidth, cancelRect.y, buttonWidth, 36f);
            if (WorkflowUiStyle.Button(cancelRect,
                    AutoTranslatorMod.WfText("取消", "Cancel"), WorkflowButtonStyle.Quiet))
                Close();
            if (WorkflowUiStyle.Button(confirmRect,
                    AutoTranslatorMod.WfText("确认清除", "Clear results"),
                    WorkflowButtonStyle.Stop, canConfirm))
            {
                WorkflowResultCleanupOptions optionsValue = new WorkflowResultCleanupOptions
                {
                    ClearAiReviewResults = _clearAiReview,
                    ClearManualClassificationResults = _clearManualClassification,
                    ClearLocalAiTranslationResults = _includeLocalAiTranslation && _clearLocalAiTranslation
                };
                Close();
                _confirm?.Invoke(optionsValue);
            }
            Text.Font = GameFont.Small;
        }
    }
}
