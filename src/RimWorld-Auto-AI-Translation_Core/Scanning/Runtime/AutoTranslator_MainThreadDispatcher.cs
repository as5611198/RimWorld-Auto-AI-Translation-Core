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
// 這個檔案負責主執行緒派發器。
// EN: This file dispatches queued scanner work on the main thread.

namespace AutoTranslator_Core
{


        // 這個類別負責 自動翻譯器主畫面執行緒派發器 的主要流程與狀態。
        // EN: This class manages the main workflow and state for AutoTranslator_MainThreadDispatcher.
        public class AutoTranslator_MainThreadDispatcher : GameComponent
        {


            // 這個方法負責處理 自動翻譯器主畫面執行緒派發器 相關流程。
            // EN: This constructor initializes auto translator main thread dispatcher.
            public AutoTranslator_MainThreadDispatcher(Game game) { }

            // 這個方法負責處理 GameComponentUpdate 相關流程。
            // EN: This method handles game component update.
            public override void GameComponentUpdate()
            {
                ATC_Dispatcher.PumpPending();
                AutoTranslatorScanner.PumpMainThreadDispatcher();
            }
        }
}
