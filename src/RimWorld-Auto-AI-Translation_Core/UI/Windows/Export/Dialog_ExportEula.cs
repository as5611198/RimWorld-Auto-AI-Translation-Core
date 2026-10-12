using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using Verse;
using RimWorld;
// 這個檔案負責導出條款確認對話框。
// EN: This file draws the export EULA confirmation dialog.

namespace AutoTranslator_Core
{
    // 這個類別負責 對話框導出Eula 的主要流程與狀態。
    // EN: This class manages the main workflow and state for Dialog_ExportEula.
    public class Dialog_ExportEula : Window
    {
        // 這個常數定義 COUNTDOWNSECONDS 的固定值。
        // EN: This constant defines the fixed value for countdown seconds.
        private const float COUNTDOWN_SECONDS = 5f;

        // 這個欄位保存 countdownRemaining 的執行狀態或快取資料。
        // EN: This field stores countdown remaining runtime state or cached data.
        private float _countdownRemaining = COUNTDOWN_SECONDS;
        // 這個欄位保存 check1 的執行狀態或快取資料。
        // EN: This field stores check1 runtime state or cached data.
        private bool _check1 = false;
        // 這個欄位保存 check2 的執行狀態或快取資料。
        // EN: This field stores check2 runtime state or cached data.
        private bool _check2 = false;
        // 這個欄位保存 check3 的執行狀態或快取資料。
        // EN: This field stores check3 runtime state or cached data.
        private bool _check3 = false;
        // 這個欄位保存 scrollPos 的執行狀態或快取資料。
        // EN: This field stores scroll pos runtime state or cached data.
        private Vector2 _scrollPos = Vector2.zero;
        // 這個欄位保存 onAccept 的執行狀態或快取資料。
        // EN: This field stores on accept runtime state or cached data.
        private readonly Action _onAccept;

        // 這個屬性提供 InitialSize 的讀寫或計算結果。
        // EN: This method handles vector2.
        public override Vector2 InitialSize => new Vector2(700f, 750f);

        // 這個方法負責處理 對話框導出Eula 相關流程。
        // EN: This constructor initializes dialog export eula.
        public Dialog_ExportEula(Action onAccept)
        {
            _onAccept = onAccept;
            doCloseButton = false;
            doCloseX = true;
            forcePause = true;
            absorbInputAroundWindow = true;
            closeOnAccept = false;
            closeOnCancel = false;
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


            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0, 0, inRect.width, 40f),
                "ATC_ExportEula_Title".Translate(ExportEulaVersion.CurrentVersion));
            Text.Font = GameFont.Small;
            Widgets.DrawLineHorizontal(0, 38f, inRect.width);


            float scrollHeight = inRect.height - 250f;
            Rect scrollOuter = new Rect(0, 48f, inRect.width, scrollHeight);
            string fullText = "ATC_ExportEula_FullText".Translate();
            float textHeight = Text.CalcHeight(fullText, scrollOuter.width - 20f);
            Rect scrollInner = new Rect(0, 0, scrollOuter.width - 20f, Mathf.Max(textHeight + 20f, scrollOuter.height));
            Widgets.BeginScrollView(scrollOuter, ref _scrollPos, scrollInner);
            Widgets.Label(new Rect(5f, 5f, scrollInner.width - 10f, textHeight), fullText);
            Widgets.EndScrollView();


            float yCursor = scrollOuter.yMax + 10f;
            Rect countdownRect = new Rect(0, yCursor, inRect.width, 25f);
            if (countdownDone)
            {
                GUI.color = new Color(0.6f, 1f, 0.6f);
                Widgets.Label(countdownRect, "✅ " + "ATC_ExportEula_CountdownDone".Translate());
            }
            else
            {
                GUI.color = new Color(1f, 0.7f, 0.3f);
                Widgets.Label(countdownRect, "⏱️ " +
                    "ATC_ExportEula_CountdownLabel".Translate(Mathf.CeilToInt(_countdownRemaining)));
            }
            GUI.color = Color.white;
            yCursor += 30f;


            if (!countdownDone) GUI.color = new Color(0.5f, 0.5f, 0.5f, 0.7f);

            Rect check1Rect = new Rect(0, yCursor, inRect.width, 24f);
            bool check1Backup = _check1;
            Widgets.CheckboxLabeled(check1Rect, "ATC_ExportEula_Check1".Translate(), ref _check1);
            if (!countdownDone) _check1 = check1Backup;
            yCursor += 28f;

            Rect check2Rect = new Rect(0, yCursor, inRect.width, 24f);
            bool check2Backup = _check2;
            Widgets.CheckboxLabeled(check2Rect, "ATC_ExportEula_Check2".Translate(), ref _check2);
            if (!countdownDone) _check2 = check2Backup;
            yCursor += 28f;

            Rect check3Rect = new Rect(0, yCursor, inRect.width, 24f);
            bool check3Backup = _check3;
            Widgets.CheckboxLabeled(check3Rect, "ATC_ExportEula_Check3".Translate(), ref _check3);
            if (!countdownDone) _check3 = check3Backup;
            yCursor += 35f;

            GUI.color = Color.white;


            bool allChecked = _check1 && _check2 && _check3;
            bool canConfirm = countdownDone && allChecked;

            float btnWidth = (inRect.width - 20f) / 2f;
            Rect cancelBtnRect = new Rect(0, yCursor, btnWidth, 40f);
            Rect confirmBtnRect = new Rect(btnWidth + 20f, yCursor, btnWidth, 40f);

            if (WorkflowUiStyle.Button(cancelBtnRect, "ATC_ExportEula_CancelBtn".Translate(),
                    WorkflowButtonStyle.Quiet))
            {
                Close();
            }

            if (WorkflowUiStyle.Button(confirmBtnRect, "ATC_ExportEula_ConfirmBtn".Translate(),
                    WorkflowButtonStyle.Primary, canConfirm))
            {
                if (!canConfirm)
                {
                    Messages.Message("ATC_ExportEula_NeedAllChecks".Translate(),
                        MessageTypeDefOf.RejectInput, false);
                }
                else
                {
                    Close();
                    _onAccept?.Invoke();
                }
            }
            GUI.color = Color.white;
        }
    }
}
