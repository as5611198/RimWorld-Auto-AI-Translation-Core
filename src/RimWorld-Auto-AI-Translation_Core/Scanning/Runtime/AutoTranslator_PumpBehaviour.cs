using Newtonsoft.Json;
using RimWorld;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Verse;
using static AutoTranslator_Core.DeleteTranslationWindow;
// 這個檔案負責主選單階段的派發掛鉤。
// EN: This file pumps queued work while the game is at the main menu.

namespace AutoTranslator_Core
{


        // 這個類別負責 自動翻譯器PumpBehaviour 的主要流程與狀態。
        // EN: This class manages the main workflow and state for AutoTranslator_PumpBehaviour.
        public class AutoTranslator_PumpBehaviour : UnityEngine.MonoBehaviour
        {
            // 這個欄位保存 accumulator 的執行狀態或快取資料。
            // EN: This field stores accumulator runtime state or cached data.
            private float _accumulator = 0f;

            // 這個方法負責處理 Update 相關流程。
            // EN: This method handles update.
            private void Update()
            {

                _accumulator += UnityEngine.Time.unscaledDeltaTime;
                if (_accumulator >= 0.05f)
                {
                    _accumulator = 0f;
                    AutoTranslator_StartupHook.PollForLoadedPlayData();
                    ATC_Dispatcher.PumpPending();
                    AutoTranslatorScanner.PumpMainThreadDispatcher();
                }
            }
        }
}
