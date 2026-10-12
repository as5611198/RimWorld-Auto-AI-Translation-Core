using RimWorld;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using Verse;
// 這個檔案負責設定分頁的 UI 與參數編輯。
// EN: This file draws the settings tab and edits runtime options.

namespace AutoTranslator_Core
{
    // 這個類別負責 自動翻譯器模組 的主要流程與狀態。
    // EN: This class manages the main workflow and state for AutoTranslatorMod.
    public partial class AutoTranslatorMod : Mod
    {
        private static bool _settingsLegacyRepairRunning;
        private static bool _settingsRestoreBackupRunning;


        // 這個方法負責繪製 設定分頁 介面。
        // EN: This method draws config tab.
        private void DrawConfigTab(Listing_Standard l, Rect viewRect)
        {
            if (DrawSettingsSectionHeader(l, WfText("常用与界面显示", "General and display"),
                    ref Settings.SettingsGeneralExpanded))
                DrawGeneralDisplaySettings(l);
            l.Gap(12f);
            if (WorkflowUiStyle.Button(l.GetRect(34f), WfText("参考字典（内置与用户维护）", "Reference dictionary (built-in and user-maintained)"),
                WorkflowButtonStyle.Quiet, true))
                Find.WindowStack.Add(new Window_ReferenceDictionary());
            l.Gap(12f);

            if (DrawSettingsSectionHeader(l, WfText("AI 接口", "AI providers"),
                    ref Settings.SettingsAiExpanded))
                DrawAiProviderSettings(l);


            l.Gap(12f);
            if (DrawSettingsSectionHeader(l, WfText("性能与诊断", "Performance and diagnostics"),
                    ref Settings.SettingsPerformanceExpanded))
                DrawPerformanceAndDiagnosticsSettings(l, viewRect);
            l.Gap(12f);

            if (DrawSettingsSectionHeader(l, WfText("高级兼容模式", "Advanced compatibility"),
                    ref Settings.SettingsCompatibilityExpanded))
                DrawCompatibilitySettings(l);
            l.Gap(12f);

            if (DrawSettingsSectionHeader(l, WfText("维护与恢复", "Maintenance and recovery"),
                    ref Settings.SettingsMaintenanceExpanded))
                DrawMaintenanceSettings(l);
            GUI.color = Color.white;
        }

        private void DrawAiProviderSettings(Listing_Standard l)
        {
            float sectionStartY = l.CurHeight;
            DrawTranslationUsageBudgetSettings(l, false);
            l.Gap(10f);
            DrawSettingsSubsectionHeader(l,
                WfText("API 接口设置（支持多组并发轮询）", "API providers (supports concurrent rotation)"));

            for (int i = 0; i < Settings.ApiConfigs.Count; i++)
            {
                if (!DrawApiConfigCard(l, Settings.ApiConfigs[i], i)) break;
                l.Gap(12f);
            }

            Rect addApiRow = l.GetRect(34f);
            string addApiLabel = "ATC_AddApiBtn".Translate();
            float addApiWidth = Mathf.Min(addApiRow.width,
                Mathf.Max(220f, Text.CalcSize(addApiLabel).x + 28f));
            if (WorkflowUiStyle.Button(
                    new Rect(addApiRow.x + 14f, addApiRow.y, addApiWidth, 32f),
                    addApiLabel,
                    WorkflowButtonStyle.Primary,
                    !AutoTranslatorSettings.IsRunning))
            {
                Settings.ApiConfigs.Add(new ApiKeyConfig());
            }

            // “AI 接口”是一个完整章节：预算、二级标题、所有 API 卡片与新增按钮共用一条章节色轨。
            Widgets.DrawBoxSolid(
                new Rect(0f, sectionStartY, 4f, Mathf.Max(0f, l.CurHeight - sectionStartY)),
                WorkflowUiStyle.GoodText);
            GUI.color = Color.white;
        }

        private static void DrawSettingsSubsectionHeader(Listing_Standard l, string title)
        {
            Rect rect = l.GetRect(30f);
            TextAnchor previousAnchor = Text.Anchor;
            GameFont previousFont = Text.Font;
            Text.Anchor = TextAnchor.MiddleLeft;
            Text.Font = GameFont.Small;
            Widgets.Label(new Rect(rect.x + 14f, rect.y, rect.width - 14f, rect.height), title ?? string.Empty);
            Widgets.DrawLineHorizontal(rect.x + 14f, rect.yMax - 1f, rect.width - 14f);
            Text.Font = previousFont;
            Text.Anchor = previousAnchor;
        }

        private bool DrawApiConfigCard(Listing_Standard l, ApiKeyConfig config, int index)
        {
            bool canEdit = !AutoTranslatorSettings.IsRunning;
            Rect card = l.GetRect(224f);
            Widgets.DrawBoxSolid(card, WorkflowUiStyle.Panel);
            WorkflowUiStyle.DrawBorder(card, new Color(0.31f, 0.35f, 0.38f));
            Rect content = card.ContractedBy(12f);

            Rect header = new Rect(content.x, content.y, content.width, 32f);
            const float deleteWidth = 36f;
            const float enabledWidth = 112f;
            const float headerGap = 10f;
            Rect deleteRect = new Rect(header.xMax - deleteWidth, header.y + 3f, deleteWidth, 27f);
            Rect enabledRect = new Rect(deleteRect.x - headerGap - enabledWidth, header.y, enabledWidth, 32f);
            Rect apiTitleRect = new Rect(header.x, header.y, 74f, header.height);
            DrawSettingsThirdLevelTitle(apiTitleRect, WfText("API ", "API ") + (index + 1));

            const float noteLabelWidth = 50f;
            float noteWidth = Mathf.Min(340f, Mathf.Max(120f, enabledRect.x - apiTitleRect.xMax - noteLabelWidth - 28f));
            Rect noteLabelRect = new Rect(apiTitleRect.xMax + 12f, header.y, noteLabelWidth, header.height);
            Rect noteRect = new Rect(noteLabelRect.xMax, header.y + 2f, noteWidth, header.height - 4f);
            DrawSettingsFieldLabel(noteLabelRect, WfText("备注", "Note"));
            GUI.color = canEdit ? Color.white : Color.grey;
            config.Label = Widgets.TextField(noteRect, config.Label ?? string.Empty);
            if (string.IsNullOrEmpty(config.Label))
            {
                GUI.color = Color.gray;
                Text.Font = GameFont.Tiny;
                Widgets.Label(new Rect(noteRect.x + 5f, noteRect.y + 3f, noteRect.width - 10f, noteRect.height),
                    "ATC_ApiKeyNoteHint".Translate());
                Text.Font = GameFont.Small;
            }

            bool enabled = config.Enabled;
            DrawInlineSettingsCheckbox(enabledRect,
                enabled ? WfText("已启用", "Enabled") : WfText("已停用", "Disabled"),
                ref enabled, canEdit);
            if (canEdit) config.Enabled = enabled;

            bool canDelete = Settings.ApiConfigs.Count > 1 && canEdit;
            bool deleteClicked = WorkflowUiStyle.Button(
                deleteRect, string.Empty, WorkflowButtonStyle.Stop, canDelete, GameFont.Small);
            DrawTrashCanIcon(deleteRect, canDelete ? Color.white : new Color(1f, 1f, 1f, 0.38f));
            TooltipHandler.TipRegion(deleteRect,
                Settings.ApiConfigs.Count > 1 ? WfText("删除此 API", "Delete this API") :
                    WfText("至少保留一个 API", "At least one API must remain"));
            if (deleteClicked)
            {
                Settings.ApiConfigs.RemoveAt(index);
                GUI.color = Color.white;
                return false;
            }

            GUI.color = config.Enabled ? (canEdit ? Color.white : Color.grey) :
                new Color(0.55f, 0.55f, 0.55f, 0.85f);
            const float columnGap = 28f;
            float leftWidth = Mathf.Clamp(content.width * 0.36f, 500f, 580f);
            float rightX = content.x + leftWidth + columnGap;
            float rightWidth = content.xMax - rightX;
            const float leftLabelWidth = 250f;
            const float rightLabelWidth = 105f;
            float firstRowY = header.yMax + 6f;

            Rect rowA = new Rect(content.x, firstRowY, content.width, 36f);
            Rect leftA = new Rect(rowA.x, rowA.y, leftWidth, rowA.height);
            Rect rightA = new Rect(rightX, rowA.y, rightWidth, rowA.height);
            Rect providerRect = DrawFixedLabeledControl(leftA, "ATC_Provider".Translate(), leftLabelWidth);
            if (WorkflowUiStyle.Button(providerRect, config.Provider + "  ▾",
                    WorkflowButtonStyle.Dropdown, canEdit))
            {
                List<FloatMenuOption> options = new List<FloatMenuOption>();
                foreach (TranslatorProvider provider in Enum.GetValues(typeof(TranslatorProvider)))
                {
                    TranslatorProvider captured = provider;
                    options.Add(new FloatMenuOption(captured.ToString(), () =>
                    {
                        config.Provider = captured;
                        config.SelectedModel = string.Empty;
                        AutoTranslatorAPI.ResetModelFetchState(config, clearModels: true);
                    }));
                }
                Find.WindowStack.Add(new FloatMenu(options));
            }
            Rect urlRect = DrawFixedLabeledControl(rightA, "Base URL", rightLabelWidth);
            if (config.Provider != TranslatorProvider.Google)
            {
                config.CustomBaseUrl = Widgets.TextField(urlRect, config.CustomBaseUrl);
                if (string.IsNullOrEmpty(config.CustomBaseUrl))
                    Widgets.Label(urlRect, "  " + "ATC_CustomUrlOptional".Translate());
            }

            Rect rowB = new Rect(content.x, rowA.yMax, content.width, 36f);
            Rect leftB = new Rect(rowB.x, rowB.y, leftWidth, rowB.height);
            Rect rightB = new Rect(rightX, rowB.y, rightWidth, rowB.height);
            DrawStructuredOutputSelector(
                DrawFixedLabeledControl(leftB, "ATC_StructuredOutput_Label".Translate(), leftLabelWidth),
                config, canEdit);
            Rect keyRect = DrawFixedLabeledControl(rightB, "API Key", rightLabelWidth);
            config.Key = Widgets.TextField(keyRect, config.Key);
            if (string.IsNullOrEmpty(config.Key)) Widgets.Label(keyRect, "  " + "ATC_PasteKey".Translate());

            Rect rowC = new Rect(content.x, rowB.yMax, content.width, 36f);
            Rect leftC = new Rect(rowC.x, rowC.y, leftWidth, rowC.height);
            Rect rightC = new Rect(rightX, rowC.y, rightWidth, rowC.height);
            DrawTranslationTaskTierSelector(
                DrawFixedLabeledControl(leftC, "ATC_TaskTier_Label".Translate(), leftLabelWidth),
                config, canEdit, false);
            DrawApiModelRow(rightC, config, canEdit, rightLabelWidth);

            Rect rowD = new Rect(content.x, rowC.yMax, content.width, 36f);
            Rect leftD = new Rect(rowD.x, rowD.y, leftWidth, rowD.height);
            Rect rightD = new Rect(rightX, rowD.y, rightWidth, rowD.height);
            DrawOutputTokenLimitControl(leftD, config, canEdit, leftLabelWidth);
            DrawApiConnectionTestButton(rightD, config, canEdit);

            GUI.color = Color.white;
            return true;
        }

        private static void DrawSettingsThirdLevelTitle(Rect rect, string title)
        {
            TextAnchor previousAnchor = Text.Anchor;
            GameFont previousFont = Text.Font;
            Color previousColor = GUI.color;
            Text.Anchor = TextAnchor.MiddleLeft;
            Text.Font = GameFont.Small;
            GUI.color = WorkflowUiStyle.GoodText;
            Widgets.Label(rect, title ?? string.Empty);
            GUI.color = previousColor;
            Text.Font = previousFont;
            Text.Anchor = previousAnchor;
        }

        private void DrawApiModelRow(Rect row, ApiKeyConfig config, bool canEdit, float labelWidth)
        {
            Rect modelArea = DrawFixedLabeledControl(row, WfText("模型", "Model"), labelWidth);
            const float gap = 6f;
            const float selectWidth = 38f;
            const float refreshWidth = 38f;
            Rect refreshRect = new Rect(modelArea.xMax - refreshWidth, modelArea.y, refreshWidth, modelArea.height);
            Rect selectRect = new Rect(refreshRect.x - gap - selectWidth, modelArea.y, selectWidth, modelArea.height);
            Rect inputRect = new Rect(modelArea.x, modelArea.y,
                Mathf.Max(60f, selectRect.x - gap - modelArea.x), modelArea.height);

            if (config.IsFetching)
            {
                GUI.color = Color.yellow;
                Widgets.Label(inputRect, "📡 " + "ATC_FetchingModel".Translate());
            }
            else
            {
                GUI.color = canEdit ? Color.white : Color.grey;
                config.SelectedModel = Widgets.TextField(inputRect, config.SelectedModel);
                if (string.IsNullOrEmpty(config.SelectedModel))
                {
                    GUI.color = Color.gray;
                    Text.Font = GameFont.Tiny;
                    Widgets.Label(new Rect(inputRect.x + 5f, inputRect.y + 2f, inputRect.width - 10f, inputRect.height),
                        "ATC_InputOrSelectModel".Translate());
                    Text.Font = GameFont.Small;
                }
            }

            GUI.color = canEdit ? Color.white : Color.grey;
            if (WorkflowUiStyle.Button(selectRect, "▼", WorkflowButtonStyle.Dropdown,
                    canEdit && !config.IsFetching, GameFont.Tiny))
            {
                if (config.FetchedModels.Count > 0)
                {
                    List<FloatMenuOption> options = new List<FloatMenuOption>();
                    foreach (string model in config.FetchedModels)
                    {
                        string captured = model;
                        options.Add(new FloatMenuOption(captured, () => config.SelectedModel = captured));
                    }
                    Find.WindowStack.Add(new FloatMenu(options));
                }
                else
                {
                    Messages.Message("ATC_Msg_NoModelListManualInput".Translate().ToString(),
                        MessageTypeDefOf.RejectInput, false);
                }
            }

            bool canRequest = canEdit && !AutoTranslatorAPI.HasOutstandingTranslationWork;
            if (WorkflowUiStyle.Button(refreshRect, "↻", WorkflowButtonStyle.Quiet,
                    canRequest && !config.IsFetching, GameFont.Small))
            {
                if (!config.Enabled)
                    Messages.Message("ATC_Msg_ApiKeyDisabled".Translate().ToString(), MessageTypeDefOf.RejectInput, false);
                else if (string.IsNullOrEmpty(config.Key) || config.Key.Length <= 10)
                    Messages.Message("ATC_EmptyConfigWarning".Translate().ToString(), MessageTypeDefOf.RejectInput, false);
                else
                    AutoTranslatorAPI.AutoFetchForConfig(config, true);
            }
            TooltipHandler.TipRegion(refreshRect, "ATC_RefetchModels".Translate());
        }

        private void DrawApiConnectionTestButton(Rect row, ApiKeyConfig config, bool canEdit)
        {
            const float testWidth = 178f;
            Rect testRect = new Rect(row.xMax - testWidth, row.y + 2f, testWidth, row.height - 4f);
            bool canRequest = canEdit && !AutoTranslatorAPI.HasOutstandingTranslationWork;
            if (config.IsTesting)
            {
                GUI.color = Color.yellow;
                TextAnchor previousAnchor = Text.Anchor;
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(testRect, "ATC_Testing".Translate());
                Text.Anchor = previousAnchor;
            }
            else if (WorkflowUiStyle.Button(testRect, "ATC_TestConnection".Translate(),
                         WorkflowButtonStyle.Primary, canRequest, GameFont.Tiny))
            {
                if (!config.Enabled)
                    Messages.Message("ATC_Msg_ApiKeyDisabled".Translate().ToString(), MessageTypeDefOf.RejectInput, false);
                else if (string.IsNullOrEmpty(config.Key) || string.IsNullOrEmpty(config.SelectedModel))
                    Messages.Message("ATC_EmptyConfigWarning".Translate().ToString(), MessageTypeDefOf.RejectInput, false);
                else
                    AutoTranslatorAPI.RunConnectionTest(config);
            }
        }

        private void DrawOutputTokenLimitControl(Rect row, ApiKeyConfig config, bool canEdit, float labelWidth)
        {
            Rect area = DrawFixedLabeledControl(row, "ATC_ApiMaxOutputTokens".Translate(), labelWidth);
            const float unitWidth = 62f;
            const float gap = 6f;
            Rect unitRect = new Rect(area.xMax - unitWidth, area.y, unitWidth, area.height);
            Rect inputRect = new Rect(area.x, area.y, Mathf.Max(50f, unitRect.x - gap - area.x), area.height);
            GUI.color = canEdit ? Color.white : Color.grey;
            string edited = Widgets.TextField(inputRect, config.AtcMaxOutputTokensBuffer ?? string.Empty);
            if (canEdit && edited != config.AtcMaxOutputTokensBuffer)
            {
                config.AtcMaxOutputTokensBuffer = edited;
                if (double.TryParse(edited, NumberStyles.Float, CultureInfo.CurrentCulture, out double value) ||
                    double.TryParse(edited, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                {
                    double tokens = value * GetOutputTokenUnitMultiplier(config.AtcMaxOutputTokenUnit);
                    config.AtcMaxOutputTokens = (int)Math.Max(1d, Math.Min(int.MaxValue, Math.Round(tokens)));
                }
            }

            if (WorkflowUiStyle.Button(unitRect,
                    GetOutputTokenUnitLabel(config.AtcMaxOutputTokenUnit) + "  ▾",
                    WorkflowButtonStyle.Dropdown, canEdit, GameFont.Tiny))
            {
                List<FloatMenuOption> options = new List<FloatMenuOption>();
                foreach (OutputTokenDisplayUnit unit in Enum.GetValues(typeof(OutputTokenDisplayUnit)))
                {
                    OutputTokenDisplayUnit captured = unit;
                    options.Add(new FloatMenuOption(GetOutputTokenUnitLabel(captured), () =>
                    {
                        config.AtcMaxOutputTokenUnit = captured;
                        config.AtcMaxOutputTokensBuffer = FormatOutputTokenValue(config.AtcMaxOutputTokens, captured);
                    }));
                }
                Find.WindowStack.Add(new FloatMenu(options));
            }
            TooltipHandler.TipRegion(unitRect, WfText(
                "显示单位：个 = 原始数量，K = 千，M = 百万。切换单位不会改变实际 Token 总数。",
                "Display unit: 1 = tokens, K = thousands, M = millions. Changing units keeps the same token total."));

            int? known = config.GetKnownOutputTokenLimit(config.SelectedModel);
            TooltipHandler.TipRegion(row,
                "ATC_ApiMaxOutputTokensTooltip".Translate() + "\n" +
                (known.HasValue
                    ? "ATC_ModelOutputCapabilityKnown".Translate(
                        known.Value, config.ResolveActualOutputTokenLimit(config.SelectedModel))
                    : "ATC_ModelOutputCapabilityUnknown".Translate()));
        }

        private static double GetOutputTokenUnitMultiplier(OutputTokenDisplayUnit unit)
        {
            return unit == OutputTokenDisplayUnit.Millions ? 1000000d :
                unit == OutputTokenDisplayUnit.Thousands ? 1000d : 1d;
        }

        private static string GetOutputTokenUnitLabel(OutputTokenDisplayUnit unit)
        {
            return unit == OutputTokenDisplayUnit.Millions ? "M" :
                unit == OutputTokenDisplayUnit.Thousands ? "K" : WfText("个", "1");
        }

        private static string FormatOutputTokenValue(int tokens, OutputTokenDisplayUnit unit)
        {
            double value = Math.Max(1, tokens) / GetOutputTokenUnitMultiplier(unit);
            return value.ToString(value >= 100d ? "0" : value >= 10d ? "0.#" : "0.###",
                CultureInfo.CurrentCulture);
        }

        private void DrawMaintenanceSettings(Listing_Standard l)
        {
            Rect panel = l.GetRect(82f);
            Rect content = DrawSettingsSectionBody(panel);
            Rect repairRow = new Rect(content.x, content.y, content.width, 36f);
            GUI.color = _settingsLegacyRepairRunning ? Color.grey : new Color(0.6f, 0.9f, 0.75f);
            string repairLegacyLabel = _settingsLegacyRepairRunning
                ? "ATC_CheckingModStatus".Translate().ToString()
                : "🧰 " + "ATC_Btn_RepairLegacyTranslations".Translate().ToString();
            Rect repairLegacyBtnRect = DrawInlineLabeledControl(repairRow,
                WfText("修复旧译文格式", "Repair legacy translation format"),
                Mathf.Max(260f, Text.CalcSize(repairLegacyLabel).x + 28f), 210f);
            if (WorkflowUiStyle.Button(repairLegacyBtnRect, repairLegacyLabel,
                    WorkflowButtonStyle.Quiet, !_settingsLegacyRepairRunning) && !_settingsLegacyRepairRunning)
            {
                Find.WindowStack.Add(new Window_AtcDialog(
                    WfText(
                        "将检查并直接修改当前目标语系生成包中的旧格式 XML 译文；如果有文件发生变化，随后会执行热重载。\n\n该操作不会调用 AI，但可能改写多个译文文件。是否继续？",
                        "This checks and directly updates legacy XML translations in the current target-language pack, then hot-reloads changed files. No AI is used, but multiple translation files may be modified. Continue?"),
                    WfText("修复", "Repair"),
                    QueueLegacyRepairFromSettings,
                    WfText("取消", "Cancel"),
                    null,
                    WfText("确认修复旧译文格式", "Confirm legacy translation repair"),
                    true,
                    false,
                    new Vector2(720f, 420f)));
            }

            Rect restoreRow = new Rect(content.x, repairRow.yMax, content.width, 36f);
            GUI.color = _settingsRestoreBackupRunning ? Color.grey : new Color(0.5f, 0.8f, 1f);
            string restoreLabel = _settingsRestoreBackupRunning
                ? "ATC_CheckingModStatus".Translate().ToString()
                : "↩ " + "ATC_Btn_RestoreLatestBackup".Translate().ToString();
            Rect restoreBtnRect = DrawInlineLabeledControl(restoreRow,
                WfText("恢复最近备份", "Restore latest backup"),
                Mathf.Max(260f, Text.CalcSize(restoreLabel).x + 28f), 210f);
            if (WorkflowUiStyle.Button(restoreBtnRect, restoreLabel,
                    WorkflowButtonStyle.Quiet, !_settingsRestoreBackupRunning) && !_settingsRestoreBackupRunning)
            {
                Find.WindowStack.Add(new Window_AtcDialog(
                    "ATC_Msg_ConfirmRestoreLatestBackup".Translate(),
                    WfText("确认还原", "Restore backup"),
                    () => {
                        QueueRestoreLatestBackupsFromSettings();
                    },
                    "ATC_Btn_Cancel".Translate(),
                    null,
                    WfText("确认还原最近翻译备份", "Confirm restore latest translation backup"),
                    true,
                    false,
                    new Vector2(720f, 420f)
                ));
            }
            GUI.color = Color.white;
        }

        private void DrawGeneralDisplaySettings(Listing_Standard l)
        {
            Rect panel = l.GetRect(46f);
            Rect content = DrawSettingsSectionBody(panel);
            const float gap = 12f;
            float controlWidth = (content.width - gap * 2f) / 3f;
            Rect languageRect = new Rect(content.x, content.y + 3f, controlWidth, 34f);
            Rect shortcutRect = new Rect(languageRect.xMax + gap, languageRect.y, controlWidth, languageRect.height);
            Rect modNameRect = new Rect(shortcutRect.xMax + gap, languageRect.y, controlWidth, languageRect.height);

            DrawSettingsModeToggle(shortcutRect,
                "ATC_ShowWorldMainButton".Translate(), ref Settings.ShowWorldMainButton);

            bool canEdit = !AutoTranslatorSettings.IsRunning;
            GUI.color = canEdit ? Color.white : Color.grey;
            if (WorkflowUiStyle.Button(languageRect,
                    WfText("当前语言（Language）", "Language") + "  ·  " + GetLangLabel(Settings.TargetLang) + "  ▾",
                    WorkflowButtonStyle.Dropdown, canEdit))
            {
                List<FloatMenuOption> options = new List<FloatMenuOption>();
                foreach (TargetLanguage lang in Enum.GetValues(typeof(TargetLanguage)))
                {
                    TargetLanguage capturedLang = lang;
                    options.Add(new FloatMenuOption(GetLangLabel(lang), () => SetTargetLanguage(capturedLang)));
                }
                Find.WindowStack.Add(new FloatMenu(options));
            }
            TooltipHandler.TipRegion(languageRect, WfText(
                "本模组当前使用的语言。现阶段同时决定 AI 翻译与云端译文的目标语言；后续全部按钮、标题、提示和弹窗也将统一跟随这里切换。",
                "The language used by this mod. It currently also selects the target language for AI and cloud translations; all buttons, titles, tips, and dialogs will follow this setting in a future interface-language update."));
            GUI.color = Color.white;

            Widgets.Label(modNameRect, WfText("Mod 列表保留原始名称。", "The Mod list keeps original names."));
        }

        private static Rect DrawSettingsSectionBody(Rect panel, bool drawRail = true)
        {
            Widgets.DrawBoxSolid(panel, WorkflowUiStyle.Panel);
            if (drawRail)
                Widgets.DrawBoxSolid(new Rect(panel.x, panel.y, 4f, panel.height), WorkflowUiStyle.GoodText);
            return new Rect(panel.x + 16f, panel.y + 3f, panel.width - 28f, panel.height - 6f);
        }

        private static void DrawInlineSettingsCheckbox(Rect row, string label, ref bool value, bool canEdit)
        {
            bool previous = value;
            const float checkboxSize = 22f;
            Widgets.Checkbox(new Vector2(row.x, row.y + (row.height - checkboxSize) * 0.5f),
                ref value, checkboxSize, !canEdit);
            TextAnchor previousAnchor = Text.Anchor;
            Text.Anchor = TextAnchor.MiddleLeft;
            GUI.color = canEdit ? Color.white : WorkflowUiStyle.MutedText;
            Rect labelRect = new Rect(row.x + checkboxSize + 7f, row.y,
                Mathf.Max(0f, row.width - checkboxSize - 7f), row.height);
            Widgets.Label(labelRect, label ?? string.Empty);
            if (canEdit && Widgets.ButtonInvisible(labelRect)) value = !value;
            GUI.color = Color.white;
            Text.Anchor = previousAnchor;
            if (!canEdit) value = previous;
        }

        private static Rect DrawInlineLabeledControl(Rect row, string label, float controlWidth,
            float minimumLabelWidth = 0f)
        {
            float measuredLabelWidth = Text.CalcSize(label ?? string.Empty).x + 14f;
            float labelWidth = Mathf.Max(minimumLabelWidth, measuredLabelWidth);
            labelWidth = Mathf.Min(labelWidth, Mathf.Max(0f, row.width - controlWidth - 8f));
            DrawSettingsFieldLabel(new Rect(row.x, row.y, labelWidth, row.height), label);
            return new Rect(row.x + labelWidth, row.y + 2f,
                Mathf.Min(controlWidth, Mathf.Max(0f, row.width - labelWidth)), row.height - 4f);
        }

        private static Rect DrawFixedLabeledControl(Rect row, string label, float labelWidth,
            float maximumControlWidth = float.MaxValue)
        {
            labelWidth = Mathf.Min(labelWidth, Mathf.Max(0f, row.width - 8f));
            DrawSettingsFieldLabel(new Rect(row.x, row.y, labelWidth, row.height), label);
            return new Rect(row.x + labelWidth, row.y + 2f,
                Mathf.Min(maximumControlWidth, Mathf.Max(0f, row.width - labelWidth)), row.height - 4f);
        }

        private static void DrawSettingsFieldLabel(Rect rect, string label)
        {
            TextAnchor previousAnchor = Text.Anchor;
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(new Rect(rect.x, rect.y, rect.width - 6f, rect.height), label ?? string.Empty);
            Text.Anchor = previousAnchor;
        }

        private bool DrawSettingsSectionHeader(Listing_Standard l, string title, ref bool expanded)
        {
            Rect headerRect = l.GetRect(40f);
            Widgets.DrawBoxSolid(headerRect, WorkflowUiStyle.Header);
            WorkflowUiStyle.DrawBorder(
                headerRect,
                new Color(0.31f, 0.35f, 0.38f, 1f));
            Widgets.DrawBoxSolid(
                new Rect(headerRect.x, headerRect.y, 4f, headerRect.height),
                WorkflowUiStyle.GoodText);

            GameFont previousFont = Text.Font;
            TextAnchor previousAnchor = Text.Anchor;
            Color previousColor = GUI.color;
            Text.Font = GameFont.Medium;
            Text.Anchor = TextAnchor.MiddleLeft;
            GUI.color = Color.white;
            const float toggleWidth = 46f;
            Widgets.Label(
                new Rect(
                    headerRect.x + 15f,
                    headerRect.y + 1f,
                    headerRect.width - toggleWidth - 30f,
                    headerRect.height - 2f),
                title ?? string.Empty);
            GUI.color = previousColor;
            Text.Anchor = previousAnchor;
            Text.Font = previousFont;

            Rect toggleRect = new Rect(
                headerRect.xMax - toggleWidth - 7f,
                headerRect.y + 5f,
                toggleWidth,
                headerRect.height - 10f);
            if (WorkflowUiStyle.Button(toggleRect, expanded ? "▼" : "▶",
                    WorkflowButtonStyle.Quiet, true, GameFont.Tiny))
            {
                expanded = !expanded;
                WriteSettings();
            }
            TooltipHandler.TipRegion(toggleRect,
                expanded ? WfText("收起本章节", "Collapse section") :
                    WfText("展开本章节", "Expand section"));
            return expanded;
        }

        private static void DrawTrashCanIcon(Rect rect, Color color)
        {
            Color previous = GUI.color;
            GUI.color = color;
            float centerX = rect.x + rect.width * 0.5f;
            float top = rect.y + Mathf.Max(1f, (rect.height - 16f) * 0.5f - 1f);
            Widgets.DrawBoxSolid(new Rect(centerX - 6f, top + 3f, 12f, 2f), color);
            Widgets.DrawBoxSolid(new Rect(centerX - 3.5f, top, 7f, 2f), color);
            WorkflowUiStyle.DrawBorder(
                new Rect(centerX - 5f, top + 6f, 10f, 10f), color);
            Widgets.DrawBoxSolid(new Rect(centerX - 1.75f, top + 8f, 1f, 6f), color);
            Widgets.DrawBoxSolid(new Rect(centerX + 1.25f, top + 8f, 1f, 6f), color);
            GUI.color = previous;
        }

        private void DrawPerformanceAndDiagnosticsSettings(Listing_Standard l, Rect viewRect)
        {
            bool canEdit = !AutoTranslatorSettings.IsRunning;
            Rect panel = l.GetRect(158f);
            Rect content = DrawSettingsSectionBody(panel);
            GUI.color = canEdit ? Color.white : Color.grey;

            Rect threadRow = new Rect(content.x, content.y, content.width, 38f);
            Rect threadControl = DrawSettingsSliderRow(threadRow,
                WfText("AI API 并发请求数", "Concurrent AI API requests"), Settings.MaxThreads.ToString());
            int selectedThreads = (int)Widgets.HorizontalSlider(
                threadControl, Settings.MaxThreads, 1f, 30f, false, string.Empty);
            if (canEdit) Settings.MaxThreads = selectedThreads;
            TooltipHandler.TipRegion(threadRow, "ATC_MaxThreadsTip".Translate());

            Rect dllRow = new Rect(content.x, threadRow.yMax, content.width, 38f);
            int dllConcurrency = Math.Max(1, Math.Min(8, Settings.DllAnalysisMaxConcurrency));
            Rect dllControl = DrawSettingsSliderRow(dllRow,
                WfText("DLL 分析并发 Mod 数", "Concurrent DLL-analysis Mods"), dllConcurrency.ToString());
            int selected = (int)Widgets.HorizontalSlider(
                dllControl, dllConcurrency, 1f, 8f, false, string.Empty);
            if (canEdit) Settings.DllAnalysisMaxConcurrency = Math.Max(1, Math.Min(8, selected));
            TooltipHandler.TipRegion(
                dllRow,
                WfText(
                    "限制同时分析的 Mod 数量；每个 Mod 内的 DLL 仍按顺序分析，运行时 Harmony 扫描仍全局串行。本设置不影响大模型 API 并发。",
                    "Limits concurrent Mods. DLLs inside a Mod remain sequential and runtime Harmony scanning remains globally serialized."));

            Rect timeoutRow = new Rect(content.x, dllRow.yMax, content.width, 38f);
            Rect timeoutControl = DrawSettingsSliderRow(timeoutRow,
                WfText("API 请求超时上限", "API request timeout"), Settings.TimeoutSeconds + WfText(" 秒", " s"));
            int selectedTimeout = (int)Widgets.HorizontalSlider(
                timeoutControl, Settings.TimeoutSeconds, 15f, 600f, false, string.Empty);
            if (canEdit) Settings.TimeoutSeconds = selectedTimeout;
            TooltipHandler.TipRegion(timeoutRow, "ATC_Setting_Timeout_Tooltip".Translate());

            Rect logRow = new Rect(content.x, timeoutRow.yMax, content.width, 34f);
            Rect logControl = DrawInlineLabeledControl(logRow,
                "ATC_LogLevel".Translate(), 240f, 120f);
            if (WorkflowUiStyle.Button(
                    logControl,
                    GetLogLevelLabel(Settings.LogLevel) + "  ▾",
                    WorkflowButtonStyle.Dropdown, canEdit))
            {
                List<FloatMenuOption> options = new List<FloatMenuOption>();
                foreach (AtcLogLevel level in Enum.GetValues(typeof(AtcLogLevel)))
                {
                    AtcLogLevel captured = level;
                    options.Add(new FloatMenuOption(GetLogLevelLabel(captured), () =>
                    {
                        Settings.LogLevel = captured;
                        Settings.EnableDevelopmentDebugLogging = captured == AtcLogLevel.Debug;
                    }));
                }
                Find.WindowStack.Add(new FloatMenu(options));
            }
            TooltipHandler.TipRegion(logRow, "ATC_LogLevelTooltip".Translate());
            GUI.color = Color.white;

            if (Settings.LogLevel == AtcLogLevel.Debug)
            {
                l.Gap(10f);
                DrawRuntimeProfilePanel(l, viewRect);
            }
        }

        private static Rect DrawSettingsSliderRow(Rect row, string label, string value)
        {
            const float labelWidth = 275f;
            const float valueWidth = 72f;
            DrawSettingsFieldLabel(new Rect(row.x, row.y, labelWidth, row.height), label);
            TextAnchor previousAnchor = Text.Anchor;
            Color previousColor = GUI.color;
            Text.Anchor = TextAnchor.MiddleRight;
            GUI.color = new Color(0.48f, 0.78f, 0.94f);
            Widgets.Label(new Rect(row.x + labelWidth, row.y, valueWidth, row.height), value ?? string.Empty);
            GUI.color = previousColor;
            Text.Anchor = previousAnchor;
            return new Rect(row.x + labelWidth + valueWidth + 18f, row.y + 6f,
                Mathf.Max(80f, row.width - labelWidth - valueWidth - 18f), row.height - 12f);
        }

        private void DrawCompatibilitySettings(Listing_Standard l)
        {
            Rect panel = l.GetRect(188f);
            Rect content = DrawSettingsSectionBody(panel);
            Rect modeRow = new Rect(content.x, content.y, content.width, 34f);
            const float modeGap = 10f;
            float captureWidth = Mathf.Min(390f, modeRow.width * 0.42f);
            float applyWidth = Mathf.Min(340f, modeRow.width - captureWidth - modeGap);
            Rect captureRect = new Rect(modeRow.x, modeRow.y, captureWidth, 32f);
            Rect applyRect = new Rect(captureRect.xMax + modeGap, modeRow.y, applyWidth, 32f);

            bool previousInterceptor = Settings.EnableUIInterceptor;
            bool captureEnabled = previousInterceptor;
            DrawSettingsModeToggle(captureRect,
                WfText("DLL UI 临时采集", "Temporary DLL UI capture"), ref captureEnabled);
            if (previousInterceptor != captureEnabled)
            {
                Settings.EnableUIInterceptor = captureEnabled;
                TargetedHardcodedUi.HardcodedUiCaptureSession.NotifySettingsChanged(true);
                WriteSettings();
            }
            TooltipHandler.TipRegion(captureRect, WfText(
                "只采集 DLL 界面文字证据；可以与已保存译文同时启用。采集会增加界面调用开销。",
                "Collects DLL UI-text evidence only. It can remain enabled while saved translations are applied.") +
                "\n" + WfText("采集、Mod 匹配与归类流水线版本：", "Capture, Mod attribution and classification pipeline: ") +
                AutoTranslator_Core.Workflow.WorkflowIdentity.UiObservationPipelineVersion);

            bool previousApply = Settings.EnableHardcodedUiPrototype;
            bool applyEnabled = previousApply;
            DrawSettingsModeToggle(applyRect,
                WfText("应用已保存的 DLL UI 译文", "Apply saved DLL UI translations"), ref applyEnabled);
            if (applyEnabled != previousApply)
            {
                Settings.EnableHardcodedUiPrototype = applyEnabled;
                TargetedHardcodedUi.HardcodedUiTargetedPatchManager.RequestReload();
                WriteSettings();
            }
            TooltipHandler.TipRegion(applyRect, "ATC_HardcodedUi_EnablePrototypeTooltip".Translate());

            GUI.color = captureEnabled ? Color.white : WorkflowUiStyle.MutedText;
            Rect timeoutRow = new Rect(content.x, modeRow.yMax + 2f, content.width, 36f);
            Rect timeoutRect = DrawSettingsSliderRow(timeoutRow,
                WfText("采集时限", "Capture duration"),
                TargetedHardcodedUi.HardcodedUiCaptureSession.GetDurationLabel());
            int previousDuration = Settings.UIInterceptorAutoDisableMinutes;
            int selectedDuration = Mathf.RoundToInt(Widgets.HorizontalSlider(
                timeoutRect, previousDuration, 0f, 30f, false, string.Empty));
            if (selectedDuration == 1) selectedDuration = 2;
            if (selectedDuration != previousDuration)
            {
                Settings.UIInterceptorAutoDisableMinutes = selectedDuration;
                if (captureEnabled)
                    TargetedHardcodedUi.HardcodedUiCaptureSession.NotifySettingsChanged(true);
            }
            TooltipHandler.TipRegion(timeoutRow, WfText(
                "临时采集默认持续 5 分钟后自动停止。选择“不设时限”后会一直采集，直到你手动关闭。采集只记录 DLL 界面文字证据，不在渲染时翻译。",
                "Temporary capture stops after 5 minutes by default. With no time limit, it continues until you turn it off manually. It records DLL UI-text evidence only."));
            GUI.color = Color.white;

            Rect evidenceRow = new Rect(content.x, timeoutRow.yMax, content.width, 24f);
            Text.Font = GameFont.Tiny;
            GUI.color = Color.grey;
            Widgets.Label(evidenceRow, WfText("当前版本采集证据：", "Current-version observations: ") +
                TargetedHardcodedUi.HardcodedUiRuntimeObservationStore.GetObservationCount() +
                WfText(" 条；结束采集后重新运行 DLL 分析。", "; rerun DLL analysis after capture ends."));
            GUI.color = Color.white;
            Text.Font = GameFont.Small;

            Rect actionRow = new Rect(content.x, evidenceRow.yMax + 2f, content.width, 34f);
            string manageLabel = WfText("打开新版译文编辑器", "Open translation editor");
            float manageWidth = Mathf.Min(300f, Mathf.Max(190f, Text.CalcSize(manageLabel).x + 28f));
            Rect uiManagerRect = new Rect(actionRow.x, actionRow.y, manageWidth, 32f);
            if (WorkflowUiStyle.Button(
                    uiManagerRect,
                    manageLabel,
                    WorkflowButtonStyle.Quiet))
            {
                AutoTranslatorSettings.ActiveTab = AutoTranslatorSettings.EditorTabIndex;
            }
            TooltipHandler.TipRegion(uiManagerRect, WfText("采集结束并完成 DLL 分析后，在新版编辑器管理对应条目。", "After capture and DLL analysis, manage the corresponding entries in the workflow editor."));

            string clearLabel = WfText("打开历史数据文件夹", "Open historical data folder");
            float clearWidth = Mathf.Min(340f, Mathf.Max(210f, Text.CalcSize(clearLabel).x + 28f));
            Rect clearRect = new Rect(uiManagerRect.xMax + 12f, actionRow.y, clearWidth, 32f);
            GUI.color = new Color(1f, 0.7f, 0.3f);
            if (WorkflowUiStyle.Button(clearRect, clearLabel, WorkflowButtonStyle.Quiet))
            {
                System.Diagnostics.Process.Start(AutoTranslatorScanner.GetLocalPackPath());
            }
            GUI.color = Color.white;

            Rect statusRow = new Rect(content.x, actionRow.yMax + 2f, content.width, 22f);
            Text.Font = GameFont.Tiny;
            GUI.color = WorkflowUiStyle.MutedText;
            Widgets.Label(statusRow, "ATC_HardcodedUi_Status".Translate(
                TargetedHardcodedUi.HardcodedUiTargetedPatchManager.GetStatusLine()));
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
        }

        private static void DrawSettingsModeToggle(Rect rect, string label, ref bool enabled)
        {
            string state = enabled ? WfText("开启", "On") : WfText("关闭", "Off");
            if (WorkflowUiStyle.Button(rect,
                    label + "  ·  " + state,
                    enabled ? WorkflowButtonStyle.Primary : WorkflowButtonStyle.Quiet,
                    true,
                    GameFont.Small))
                enabled = !enabled;
        }

        private void DrawTranslationUsageBudgetSettings(Listing_Standard l, bool drawRail = true)
        {
            bool canEdit = !AutoTranslatorSettings.IsRunning;
            bool enabled = Settings.EnableTranslationUsageBudget;
            long characters = Math.Min(
                10000000L,
                Math.Max(100000L, Settings.TranslationBudgetSourceCharactersPerRun));

            Rect panel = l.GetRect(102f);
            Rect content = DrawSettingsSectionBody(panel, drawRail);

            Rect enableRect = new Rect(content.x, content.y, content.width, 30f);
            DrawInlineSettingsCheckbox(enableRect, "ATC_UsageBudget_Enable".Translate(), ref enabled, canEdit);
            TooltipHandler.TipRegion(enableRect, "ATC_UsageBudget_EnableTooltip".Translate());

            GUI.color = canEdit && enabled ? Color.white : Color.grey;
            Rect characterRow = new Rect(content.x, enableRect.yMax + 2f, content.width, 30f);
            Rect characterRect = DrawInlineLabeledControl(characterRow,
                WfText("本次任务用量上限", "Per-run usage limit"), 620f, 210f);
            float sliderValue = Widgets.HorizontalSlider(
                characterRect,
                characters,
                100000f,
                10000000f,
                false,
                "ATC_UsageBudget_Characters".Translate(FormatUsageCharacterCount(characters)),
                FormatUsageCharacterCount(100000L),
                FormatUsageCharacterCount(10000000L));
            characters = Math.Max(100000L, (long)Math.Round(sliderValue / 100000f) * 100000L);

            Text.Font = GameFont.Tiny;
            GUI.color = WorkflowUiStyle.MutedText;
            Widgets.Label(new Rect(content.x, characterRow.yMax + 3f, content.width, 24f),
                "ATC_UsageBudget_Notice".Translate());
            Text.Font = GameFont.Small;

            if (canEdit)
            {
                Settings.EnableTranslationUsageBudget = enabled;
                Settings.TranslationBudgetSourceCharactersPerRun = characters;
                Settings.TranslationBudgetEstimatedTokensPerRun = Math.Max(
                    1000L,
                    (characters * 5L + 8L) / 9L);
            }
            GUI.color = Color.white;
        }

        private static string FormatUsageCharacterCount(long count)
        {
            return count > 10000L
                ? (count / 10000d).ToString("0.##", CultureInfo.InvariantCulture) + "万"
                : count.ToString(CultureInfo.InvariantCulture);
        }

        private static string GetLogLevelLabel(AtcLogLevel level)
        {
            switch (level)
            {
                case AtcLogLevel.Error: return "ATC_LogLevel_Error".Translate();
                case AtcLogLevel.Debug: return "ATC_LogLevel_Debug".Translate();
                default: return "ATC_LogLevel_Info".Translate();
            }
        }

        private void DrawStructuredOutputSelector(Rect selectorRect, ApiKeyConfig config, bool canEdit)
        {
            if (config == null) return;

            bool supported = config.Provider != TranslatorProvider.DeepL;
            GUI.color = canEdit && supported ? Color.white : Color.grey;

            string preferenceLabel = GetStructuredOutputPreferenceLabel(config.StructuredOutput);
            if (WorkflowUiStyle.Button(
                    selectorRect,
                    preferenceLabel + "  ▾",
                    WorkflowButtonStyle.Dropdown, canEdit && supported, GameFont.Tiny) &&
                canEdit && supported)
            {
                List<FloatMenuOption> options = new List<FloatMenuOption>();
                foreach (StructuredOutputPreference value in Enum.GetValues(typeof(StructuredOutputPreference)))
                {
                    StructuredOutputPreference captured = value;
                    options.Add(new FloatMenuOption(
                        GetStructuredOutputPreferenceLabel(captured),
                        () => config.StructuredOutput = captured));
                }
                Find.WindowStack.Add(new FloatMenu(options));
            }
            TooltipHandler.TipRegion(selectorRect,
                "ATC_StructuredOutput_Tooltip".Translate() + "\n" +
                "ATC_StructuredOutput_Effective".Translate(GetEffectiveStructuredOutputLabel(config)));
            GUI.color = Color.white;
        }


        private void DrawTranslationTaskTierSelector(Rect row, ApiKeyConfig config, bool canEdit,
            bool includeLabel = true)
        {
            if (config == null) return;

            GUI.color = canEdit ? Color.white : Color.grey;
            if (WorkflowUiStyle.Button(
                    row,
                    (includeLabel ? "ATC_TaskTier_Label".Translate().ToString() + ": " : string.Empty) +
                    GetTranslationTaskTierLabel(config.TaskTier) + "  ▾",
                    WorkflowButtonStyle.Dropdown, canEdit, GameFont.Tiny) &&
                canEdit)
            {
                List<FloatMenuOption> options = new List<FloatMenuOption>
                {
                    new FloatMenuOption(
                        GetTranslationTaskTierLabel(TranslationTaskTier.Bulk),
                        () => config.TaskTier = TranslationTaskTier.Bulk)
                };
                    options.Add(new FloatMenuOption(
                        GetTranslationTaskTierLabel(TranslationTaskTier.Standard),
                        () => config.TaskTier = TranslationTaskTier.Standard));
                    options.Add(new FloatMenuOption(
                        GetTranslationTaskTierLabel(TranslationTaskTier.Precision),
                        () => config.TaskTier = TranslationTaskTier.Precision));
                Find.WindowStack.Add(new FloatMenu(options));
            }
            TooltipHandler.TipRegion(
                row,
                AutoTranslatorMod.WfText(
                    "新版工作流优先选用精细档，其次标准档，再其次批量档；不要求必须配置批量档。",
                    "The workflow prefers Precision, then Standard, then Bulk. A Bulk configuration is not required."));
            GUI.color = Color.white;
        }

        private static string GetTranslationTaskTierLabel(TranslationTaskTier tier)
        {
            switch (tier)
            {
                case TranslationTaskTier.Standard:
                    return "ATC_TaskTier_Standard".Translate();
                case TranslationTaskTier.Precision:
                    return "ATC_TaskTier_Precision".Translate();
                default:
                    return "ATC_TaskTier_Bulk".Translate();
            }
        }

        private static string GetStructuredOutputPreferenceLabel(StructuredOutputPreference preference)
        {
            switch (preference)
            {
                case StructuredOutputPreference.PromptOnly:
                    return "ATC_StructuredOutput_PromptOnly".Translate();
                case StructuredOutputPreference.JsonObject:
                    return "ATC_StructuredOutput_JsonObject".Translate();
                case StructuredOutputPreference.JsonSchema:
                    return "ATC_StructuredOutput_JsonSchema".Translate();
                default:
                    return "ATC_StructuredOutput_Auto".Translate();
            }
        }

        private static string GetEffectiveStructuredOutputLabel(ApiKeyConfig config)
        {
            if (config == null) return string.Empty;
            if (config.Provider == TranslatorProvider.DeepL)
                return WfText("不支持新版 JSON 工作流", "Unsupported by the JSON workflow");
            if (config.Provider == TranslatorProvider.Google)
                return WfText("JSON 响应", "JSON response");
            return StructuredTranslationProviderAdapter.ResolveMode(config) == StructuredTranslationMode.PromptOnly
                ? "ATC_StructuredOutput_PromptOnly".Translate().ToString()
                : "ATC_StructuredOutput_JsonObject".Translate().ToString();
        }


        private void DrawHardcodedUiPrototypeSettings(Listing_Standard l)
        {
            bool previousEnabled = Settings.EnableHardcodedUiPrototype;
            bool enabled = previousEnabled;
            Rect enableRect = l.GetRect(30f);
            Widgets.CheckboxLabeled(
                enableRect,
                WfText("应用已保存的 DLL UI 译文", "Apply saved DLL UI translations"),
                ref enabled);
            if (Mouse.IsOver(enableRect))
            {
                TooltipHandler.TipRegion(enableRect, "ATC_HardcodedUi_EnablePrototypeTooltip".Translate());
            }

            if (enabled != previousEnabled)
            {
                Settings.EnableHardcodedUiPrototype = enabled;
                TargetedHardcodedUi.HardcodedUiTargetedPatchManager.RequestReload();
                WriteSettings();
            }

            Text.Font = GameFont.Tiny;
            Widgets.Label(l.GetRect(24f), "ATC_HardcodedUi_Status".Translate(
                TargetedHardcodedUi.HardcodedUiTargetedPatchManager.GetStatusLine()));
            Text.Font = GameFont.Small;

        }

        private static void QueueLegacyRepairFromSettings()
        {
            if (_settingsLegacyRepairRunning) return;
            _settingsLegacyRepairRunning = true;

            Task.Run(async () =>
            {
                TranslationFileRepairService.RepairSummary summary = null;
                Exception failure = null;
                try
                {
                    summary = await AutoTranslator_Core.Workflow.WorkflowBackendRuntime.GetOrCreate().RunTranslationFileRepairAsync();
                }
                catch (Exception ex)
                {
                    failure = ex;
                }

                ATC_Dispatcher.RunOnMainThread(() =>
                {
                    _settingsLegacyRepairRunning = false;
                    if (failure != null)
                    {
                        Log.Warning($"[AutoTranslationCore] Legacy repair failed: {failure.Message}");
                        AutoTranslatorSettings.AddErrorLog("Legacy repair failed: " + failure.Message);
                        return;
                    }

                    summary = summary ?? new TranslationFileRepairService.RepairSummary();
                    Messages.Message(
                        "ATC_Msg_RepairLegacyTranslationsDone".Translate(summary.FilesTouched, summary.EntriesFixed, summary.StructureWarnings),
                        summary.FilesTouched > 0 ? MessageTypeDefOf.PositiveEvent : MessageTypeDefOf.NeutralEvent,
                        false);
                });
            });
        }

        private static void QueueRestoreLatestBackupsFromSettings()
        {
            if (_settingsRestoreBackupRunning) return;
            _settingsRestoreBackupRunning = true;

            List<AutoTranslatorScanner.LocalTranslationRestoreTarget> targets = Verse.ModLister.AllInstalledMods
                .Where(m => m != null && m.Active && !string.IsNullOrWhiteSpace(m.PackageId))
                .Select(m => new AutoTranslatorScanner.LocalTranslationRestoreTarget { PackageId = m.PackageId })
                .ToList();

            Task.Run(() =>
            {
                int restored = 0;
                Exception failure = null;
                try
                {
                    restored = AutoTranslatorScanner.RestoreLatestBackups(targets);
                }
                catch (Exception ex)
                {
                    failure = ex;
                }

                ATC_Dispatcher.RunOnMainThread(() =>
                {
                    _settingsRestoreBackupRunning = false;
                    if (failure != null)
                    {
                        Log.Warning($"[AutoTranslationCore] Restore latest backups failed: {failure.Message}");
                        AutoTranslatorSettings.AddErrorLog("Restore latest backups failed: " + failure.Message);
                        return;
                    }

                    Messages.Message("ATC_Msg_RestoreLatestBackupDone".Translate(restored), restored > 0 ? MessageTypeDefOf.PositiveEvent : MessageTypeDefOf.NeutralEvent, false);
                });
            });
        }


// 這個方法負責繪製 執行期ProfilePanel 介面。
// EN: This method draws runtime profile panel.
private void DrawRuntimeProfilePanel(Listing_Standard l, Rect viewRect)
        {
            Rect panelRect = l.GetRect(78f);
            Widgets.DrawBoxSolid(panelRect, WorkflowUiStyle.RaisedPanel);
            WorkflowUiStyle.DrawBorder(panelRect, new Color(0.31f, 0.35f, 0.38f));

            Text.Font = GameFont.Tiny;
            Rect left = new Rect(panelRect.x + 8f, panelRect.y + 6f, panelRect.width * 0.5f - 10f, panelRect.height - 8f);
            Rect right = new Rect(panelRect.x + panelRect.width * 0.52f, panelRect.y + 6f, panelRect.width * 0.48f - 10f, panelRect.height - 8f);

            Widgets.Label(left,
                WfText("模型请求由新版工作流统一调度。", "Model requests are managed by the workflow.") + "\n" +
                WfText("响应超时：", "Response timeout: ") + Settings.TimeoutSeconds + WfText(" 秒", " seconds"));

            Widgets.Label(right,
                "📡 " + "ATC_Perf_Api".Translate(
                    AutoTranslatorPerf.ActiveApiRequests.ToString(),
                    AutoTranslatorPerf.AverageApiMs.ToString(),
                    AutoTranslatorPerf.LastApiMs.ToString()) + "\n" +
                "🪂 " + "ATC_Perf_MemoryDrop".Translate(
                    AutoTranslatorPerf.LastMemoryDropMs.ToString(),
                    AutoTranslatorPerf.LastMemoryDropKeyed.ToString(),
                    AutoTranslatorPerf.LastMemoryDropDefs.ToString()) + "\n" +
                WfText("DLL 采集证据：", "DLL observations: ") +
                    TargetedHardcodedUi.HardcodedUiRuntimeObservationStore.GetObservationCount());

            Text.Font = GameFont.Small;
        }
    }
}
