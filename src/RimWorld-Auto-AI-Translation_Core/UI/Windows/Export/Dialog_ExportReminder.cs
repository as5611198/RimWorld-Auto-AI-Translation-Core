using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using Verse;
using RimWorld;
// 這個檔案負責導出前提醒對話框。
// EN: This file draws the pre-export reminder dialog.

namespace AutoTranslator_Core
{
    // 這個類別負責 對話框導出Reminder 的主要流程與狀態。
    // EN: This class manages the main workflow and state for Dialog_ExportReminder.
    public class Dialog_ExportReminder : Window
    {
        // 這個常數定義 COUNTDOWNSECONDS 的固定值。
        // EN: This constant defines the fixed value for countdown seconds.
        private const float COUNTDOWN_SECONDS = 2f;

        // 這個欄位保存 countdownRemaining 的執行狀態或快取資料。
        // EN: This field stores countdown remaining runtime state or cached data.
        private float _countdownRemaining = COUNTDOWN_SECONDS;
        // 這個欄位保存 onConfirm 的執行狀態或快取資料。
        // EN: This field stores on confirm runtime state or cached data.
        private readonly Action _onConfirm;

        // 這個屬性提供 InitialSize 的讀寫或計算結果。
        // EN: This method handles vector2.
        public override Vector2 InitialSize => new Vector2(550f, 350f);

        // 這個方法負責處理 對話框導出Reminder 相關流程。
        // EN: This constructor initializes dialog export reminder.
        public Dialog_ExportReminder(Action onConfirm)
        {
            _onConfirm = onConfirm;
            doCloseButton = false;
            doCloseX = true;
            forcePause = true;
            absorbInputAroundWindow = true;
        }

        // 這個方法負責處理 Do視窗Contents 相關流程。
        // EN: This method handles do window contents.
        public override void DoWindowContents(Rect inRect)
        {
            if (_countdownRemaining > 0f)
            {
                _countdownRemaining -= Time.unscaledDeltaTime;
                if (_countdownRemaining < 0f) _countdownRemaining = 0f;
            }
            bool countdownDone = _countdownRemaining <= 0f;

            var settings = AutoTranslatorMod.Settings;

            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0, 0, inRect.width, 35f),
                "ATC_ExportReminder_Title".Translate());
            Text.Font = GameFont.Small;
            Widgets.DrawLineHorizontal(0, 35f, inRect.width);

            float y = 50f;


            string dateStr = "";
            if (DateTime.TryParse(settings.EulaAcceptedTimestamp, out DateTime accepted))
            {
                dateStr = accepted.ToString("yyyy-MM-dd");
            }
            Widgets.Label(new Rect(0, y, inRect.width, 22f),
                "ATC_ExportReminder_AlreadyAccepted".Translate(dateStr, settings.EulaAcceptedVersion));
            y += 25f;

            int daysLeft = settings.GetEulaRemainingDays();
            GUI.color = daysLeft > 7 ? new Color(0.6f, 1f, 0.6f) : new Color(1f, 0.7f, 0.3f);
            Widgets.Label(new Rect(0, y, inRect.width, 22f),
                "ATC_ExportReminder_DaysLeft".Translate(daysLeft));
            GUI.color = Color.white;
            y += 30f;

            Text.Font = GameFont.Small;
            Widgets.Label(new Rect(0, y, inRect.width, 22f),
                "ATC_ExportReminder_KeyPoints".Translate());
            y += 25f;

            Widgets.Label(new Rect(20f, y, inRect.width - 20f, 22f),
                "ATC_ExportReminder_Rule1".Translate());
            y += 22f;
            Widgets.Label(new Rect(20f, y, inRect.width - 20f, 22f),
                "ATC_ExportReminder_Rule2".Translate());
            y += 22f;
            Widgets.Label(new Rect(20f, y, inRect.width - 20f, 22f),
                "ATC_ExportReminder_Rule3".Translate());
            y += 35f;


            if (!countdownDone)
            {
                GUI.color = new Color(1f, 0.7f, 0.3f);
                Widgets.Label(new Rect(0, y, inRect.width, 22f),
                    "⏱️ " + "ATC_ExportEula_CountdownLabel".Translate(Mathf.CeilToInt(_countdownRemaining)));
                GUI.color = Color.white;
            }
            y += 30f;


            float btnY = inRect.height - 45f;
            float btnWidth = (inRect.width - 20f) / 2f;
            Rect rereadRect = new Rect(0, btnY, btnWidth, 40f);
            Rect continueRect = new Rect(btnWidth + 20f, btnY, btnWidth, 40f);

            if (WorkflowUiStyle.Button(rereadRect, "ATC_ExportReminder_RereadEula".Translate(),
                    WorkflowButtonStyle.Quiet))
            {
                Close();
                Find.WindowStack.Add(new Dialog_ExportEula(() =>
                {

                    var s = AutoTranslatorMod.Settings;
                    s.HasAcceptedExportEula = true;
                    s.EulaAcceptedTimestamp = DateTime.Now.ToString("o");
                    s.EulaAcceptedVersion = ExportEulaVersion.CurrentVersion;
                    s.EulaAcceptCount++;
                    LoadedModManager.GetMod<AutoTranslatorMod>().WriteSettings();
                    Find.WindowStack.Add(new Window_Export());
                }));
                return;
            }

            if (WorkflowUiStyle.Button(continueRect, "ATC_ExportReminder_Continue".Translate(),
                    WorkflowButtonStyle.Primary, countdownDone))
            {
                Close();
                _onConfirm?.Invoke();
            }
            GUI.color = Color.white;
        }
    }
}
