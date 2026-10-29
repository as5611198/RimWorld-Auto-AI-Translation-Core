# Steam 版：RimWorld 1.5 / 1.6 / 1.7

2026-10-24 批量 XML 玩家回報修正（三版共用）：縮短原子寫入暫存／回滾備份檔名，修復有效目標被暫存名推過 MAX_PATH 的情況；數字開頭的 DefInjected 鍵改用原生支援的 rep/path/trans，批量匯入、單獨輸出與重新掃描讀取同步支援。三版重新編譯，產品整合檢查通過 72／65／72 項，批量 38 項與原子寫入 3 組在 net48／net10.0 均通過。仍未啟動遊戲；1.6 借用 1.5 執行庫的限制不變。交付 `.codex/artifacts/atc-batch-export-fix-1.5-1.6-1.7-20261024-player.zip`，詳見 [查核與修正報告](reports/batch-xml-export-audit-and-fix-20261024.md)。

同一個模組目錄透過 `LoadFolders.xml` 分別載入 `1.5`、`1.6` 或 `1.7`。共用 Defs／Languages；每個版本有獨立編譯的主 DLL。這次相容性工作從 Steam 現有版本對應的 d587640 接續，未整合 PR #3。

目前以 1.6 為主要版本，1.7 為新版遊戲適配。三版共用 API 思考模式、翻譯／審查獨立設定、自訂翻譯要求、手動取得自訂 OpenAI 模型清單及相關解析修正；1.5／1.6 保留 UnityWebRequest，只有 1.7 改用原生 HttpClient。以下較早的「只更新 1.7／舊版 DLL 不變」紀錄為當時驗證快照，已由三版共用功能更新接續。

2026-10-24 本地功能三版補齊：XML 父節點上下文、未解決來源比對／漢化包排除，以及智能補譯「勾選未翻譯模組」共用於 1.5／1.6／1.7，並非 PR #3 的相容測試包。修正 1.5 不存在 `ModDependency.alternativePackageIds` 的編譯問題，依其原生模型只檢查必要依賴；1.6／1.7 保留替代依賴。三版重新編譯通過（1.5／1.7 無警告，1.6 保留兩項既有過時 API 警告），直接載入各版新 DLL 的掃描／狀態／接線檢查分別通過 60／53／60 項，API 回歸各通過 214 項。1.6 是以 1.6.4871 參考組件編譯、以 1.5 執行庫提供離線相容宿主；其中三項替代依賴及四項原生版本目錄檢查明確跳過，不能當成 1.6 遊戲內驗證。交付 `.codex/artifacts/atc-local-features-1.5-1.6-1.7-20261024-player.zip`，未啟動遊戲、未同步至遊戲安裝目錄、未發布。詳見 [本次三版報告](reports/local-features-three-version-20261024.md)。

2026-10-22 使用教學與功能分區：三版操作導覽整理為「翻譯任務／翻譯設定／全域工作台／雲端共享」。第一次進入本模組設定時自動顯示四分頁教學，狀態存入 `HasSeenUsageGuide`，各頁保留手動入口與前往功能的捷徑；既有批次 XML 專用教學仍保留。九語系各新增 16 個教學鍵，更新舊全文教學與 README，文字版見 [完整使用教學](USAGE_GUIDE.md)。三版編譯與各 214 項離線 API／導覽路由檢查通過，1.7 的 224 項本機 HTTP／設定檢查包含首次導覽狀態的 Scribe 存讀；80 個配送檔案核對完成。已在實際 1.7.4985 繁中介面確認首次自動顯示、四頁內容、各功能捷徑、手動重開，以及重新開啟設定／重啟遊戲皆不重複跳出；最終主畫面僅保留一個教學入口。1.5／1.6 新畫面尚未遊戲內驗收。備份與驗證：`backups/steam-three-version-20261022-205703-457/verification.json`、`usage-guide-validation.json`。未手動執行翻譯任務或雲端上傳／下載，未推送 GitHub 或發布工作坊。

2026-10-21 三版共用 API 更新驗證：三版 Release 編譯並部署完成，各配送 DLL 與本機安裝的 SHA-256 相同；共核對 71 個檔案。三版 DLL 各通過 210 項離線 API／請求流程檢查，流程測試在獨立宿主中攔截傳輸邊界、使用合成回應。1.6 使用官方 1.6.4871 參考組件編譯，離線測試宿主載入實際 1.5 Managed 相容依賴，不能視為真正 1.6 遊戲內連線／UI／存檔驗收。1.5 通過 7 項型別與版本 smoke 檢查，1.7 通過 29 項；另有 222 項 1.7 本機 HTTP 傳輸／玩家設定檢查、25 組審查規則檢查及九語系鍵／XML 檢查通過。備份與驗證：`backups/steam-three-version-20261021-233252-629/verification.json`、`shared-api-features-validation.json`。本次未發布 GitHub 或工作坊，未以正式供應商金鑰送出請求。

| 遊戲版本 | 專案 | 編譯目標 | 輸出 |
|---|---|---|---|
| 1.5 | RimWorld-Auto-AI-Translation_Core.RW15.csproj | .NET Framework 4.8 | bin/Release-RW15 |
| 1.6 | RimWorld-Auto-AI-Translation_Core.csproj | .NET Framework 4.8 | bin/Release |
| 1.7 | RimWorld-Auto-AI-Translation_Core.RW17.csproj | .NET 10 | bin/Release-RW17 |

1.7 參考遊戲根目錄的 `Assembly-CSharp.dll`／`UnityStubs.dll`／`Waylith.dll`，使用共享 Harmony 模組的 1.7 DLL。模組 About.xml 宣告依賴 `brrainz.harmony`；請更新並啟用 Harmony，排在本模組前面。1.7 資料夾只配送本模組與 Newtonsoft.Json，不配送遊戲／CoreCLR／Harmony DLL。

## 建置與同步到本機 Steam

需要 .NET 10 SDK、舊 .NET Framework 參考程序集、本機 packages 下的 Harmony 2.4.2 / Newtonsoft.Json 13.0.4，以及三版本遊戲參考。

```powershell
# 只建置並部署 1.7；不替換 1.5／1.6 的 DLL。
pwsh -NoProfile -File tools/Build-SteamMod.ps1 -Versions 1.7 -Deploy

# 建置三版本並更新 repo 內的可配送模組；尚不部署 Steam。
pwsh -NoProfile -File tools/Build-SteamMod.ps1

# 建置、備份並同步到本機 Steam 模組目錄。
pwsh -NoProfile -File tools/Build-SteamMod.ps1 -Deploy

# 英文限定的預覽版：只把本機模組介面改成繁中，發行包保留原始英文。
pwsh -NoProfile -File tools/Build-SteamMod.ps1 -Deploy -LocalUiLanguage ChineseTraditional
```

可用 `-RimWorldRoot`、`-RimWorld15ManagedDir`、`-RimWorld16ManagedDir`、`-Harmony17Path` 指定其他路徑。1.6 若沒有舊 Managed 安裝，專案會使用本機 NuGet 快取的 `Krafs.Rimworld.Ref 1.6.4871`；它只供編譯，不可當成執行時依賴配送。

部署前必須關閉 RimWorld。腳本先完成所有建置，備份受影響的版本 DLL／PDB／JSON 與 Steam metadata，再逐檔同步並比對 SHA-256。備份及 verification.json 位於 `backups/steam-three-version-日期時間/`。若本機已存在已知 packageId 的 `!Translation_AI_Pack`，腳本另備份並更新它的支援版本 metadata，不改翻譯內容。腳本不會發布 Workshop、推送 GitHub 或處理 PR。

`-Deploy` 也會備份並同步模組的 `Languages` 檔案，確保新版 UI 的翻譯鍵一起交付。選用 `-LocalUiLanguage ChineseTraditional` 時，僅在 Steam 安裝目錄的英文語系中套用本模組的繁中翻譯，讓只有英文的 1.7 預覽版也能操作中文模組介面；不修改遊戲 Core、Prefs、API 設定或 repo 發行包的英文語系。`verification.json` 會另外記錄本機覆蓋檔的預期 SHA-256。日後重新執行一般 `-Deploy` 即可恢復模組正常的英文顯示。

## 1.7 的適配內容

2026-10-21 玩家進階設定：每組翻譯 API／審查 API 各自保存「自動／盡量關閉／啟用思考」，並顯示必要思考、未知端點、Player2 與 DeepL 的限制。設定頁新增額外翻譯要求的編輯、完整預覽、清空與儲存／取消，最多 4000 字元；保留內建格式保護，不套用至審查與 DeepL。更換思考模式會區分審查快取與連線測試身分；自訂要求更換亦使一般翻譯的舊連線診斷失效。九語系各補 24 個鍵。實際安裝的 1.7 DLL 通過 220 項傳輸／思考／玩家設定檢查（含遊戲 Scribe 存讀、舊設定與無效選項回復），另通過 122 項 API 檢查、25 組審查規則及 29 項執行期 smoke 檢查；65 個配送檔案核對完成。1.5／1.6 配送及安裝 DLL 均與目前 HEAD 相同。備份與驗證：`backups/steam-three-version-20261021-223027-967/`。未執行正式供應商付費請求或遊戲內新畫面驗收，未推送 GitHub／發布工作坊。使用方式見 [思考模式與自訂翻譯要求](API_THINKING_POLICY.md)。

2026-10-21 網路傳輸調整：1.7 的翻譯／連線測試／Policy Agent、模型清單，以及雲端清單／上傳／刪除／修正入口改用原生 .NET `HttpClient`，既有 ZIP 串流下載維持原路徑。配送 DLL 不再參照 `UnityEngine.Networking`，也不編入 Coroutine 網路引擎。共用長效連線池並定期更新連線，每個請求獨立設定驗證標頭；明確使用 UTF-8，逾時涵蓋回應本文讀取，取消會中止實際傳輸。翻譯取消不影響獨立的雲端操作。1.5／1.6 保留原 `UnityWebRequest` 分支，未替換其配送 DLL。重新導向不自動跟隨，API 應設定直接端點。

本機驗證：三版本編譯通過（1.6 僅既有的兩個過時 API 警告）；1.7 使用遊戲自帶 CoreCLR 10.0.13 通過 44 項本機 HTTP 傳輸檢查、120 項 API 設定檢查與 29 項完整模組 smoke 檢查，1.5 另通過 120 項 API 設定檢查。已用 `-Versions 1.7 -Deploy` 部署並核對 56 個檔案，實際安裝 DLL 的 44 項傳輸檢查也通過；repo 與遊戲安裝目錄的 1.5／1.6 主 DLL 均與已發布 Git 提交的 blob 相同。備份與驗證：`backups/steam-three-version-20261021-201602-154/verification.json`、`native-http-validation.json`。尚未進行遊戲內真實供應商金鑰翻譯、正式雲端寫入驗收或發布更新。

同日設定流程修正：1.7 的自訂 OpenAI 改為按「重新獲取模型」才抓取模型，翻譯與 Policy Agent 共用此規則；輸入網址／金鑰時不送出請求，也不排程後續背景抓取。免金鑰服務的手動抓取、`/v1/models` 回傳 404 後重試原網址，以及其他供應商的自動抓取均保留。48 項本機傳輸檢查與 122 項 API 檢查通過，1.5 發行 DLL 另通過 122 項檢查。已重新部署本機 1.7 並核對 56 個檔案，1.5／1.6 的 repo 與安裝 DLL 雜湊均未變；備份：`backups/steam-three-version-20261021-202357-998/`，附 `manual-model-fetch-validation.json`。未更新 GitHub／Workshop，未進行真實金鑰呼叫或遊戲內 UI 驗收。

同日思考模式調整：1.7 的一般翻譯與 Policy Agent 使用分開的模型能力規則；可關閉的普通翻譯關閉思考，必要思考型號採支援的較低程度，審查與審查設定的連線測試保留思考。OpenRouter 使用模型清單回傳的能力，未知模型／轉接站與 Player2 保留服務設定。審查預留思考 Token 並納入原有預算限制，快取版本改為 4；思考耗盡輸出上限時不放行。九語系 MiMo 說明新增 1.7 專用文字，舊版說明保留。164 項實際 DLL 的本機傳輸／思考檢查、122 項 API 檢查、25 組審查規則與 29 項模組 smoke 檢查通過；九語系 XML 與參數檢查通過。最新本機部署核對 56 個檔案，1.5／1.6 的 repo 與安裝 DLL 仍等於已發布 Git blob；備份：`backups/steam-three-version-20261021-210506-572/`，附 `thinking-policy-validation.json`。未執行正式供應商金鑰呼叫、正式雲端寫入或遊戲內 UI 驗收，尚未發布。完整規則與官方來源見 [思考模式說明](API_THINKING_POLICY.md)。

驗證命令（只連本機測試 HTTP server，使用合成憑證，不寫入正式雲端）：

```powershell
dotnet build tests/NativeHttpTransportSelfTest/NativeHttpTransportSelfTest.csproj -c Release
$gameRoot = 'E:\SteamLibrary\steamapps\common\RimWorld'
dotnet exec --runtimeconfig "$gameRoot\RimWorld.runtimeconfig.json" --depsfile "$gameRoot\RimWorld.deps.json" tests/NativeHttpTransportSelfTest/bin/Release/net10.0/NativeHttpTransportSelfTest.dll "$gameRoot\Mods\AI_TranslationCore\1.7\Assemblies\RimWorld_Auto_AI_Translation_Core.dll" $gameRoot 'E:\SteamLibrary\steamapps\workshop\content\294100\2009463077\1.7\Assemblies\0Harmony.dll' rimworld-mods/AI_TranslationCore/1.5/Assemblies/RimWorld_Auto_AI_Translation_Core.dll rimworld-mods/AI_TranslationCore/1.6/Assemblies/RimWorld_Auto_AI_Translation_Core.dll
```

2026-10-20 本機測試部署：執行 `-Deploy -LocalUiLanguage ChineseTraditional`，新版連線診斷 DLL 與語系已同步至 `E:\SteamLibrary\steamapps\common\RimWorld\Mods\AI_TranslationCore`。43 個配送檔案核對 SHA-256 通過；安裝目錄的英文語系已套用全部 744 個繁中 Keyed 鍵，主選單按鈕的 DefInjected 標籤也改用繁中。repo 發行包的英文檔保留原文。本機實際 1.7 DLL 通過 91 項離線 API 設定／診斷檢查，未啟動遊戲或進行 API 帶金鑰實連驗收。備份與 manifest：`backups/steam-three-version-20261020-214039-355/`。

當晚 Steam 更新遊戲至 `1.7.4982 rev1280`／Waylith CoreCLR `10.0.13` 後，重新編譯並部署 1.7 與共用語系，37 個檔案核對 SHA-256 通過；實際安裝 DLL 的 91 項 API 檢查與遊戲自帶 CoreCLR 的 11 項載入／Harmony smoke 檢查通過。未啟動遊戲。檢查到使用者的啟用設定仍只有 Core；已在遊戲關閉時備份 `ModsConfig.xml`，新增 Harmony 與本機 `auto.aitranslation.core`，其他模組維持停用。備份：`backups/steam-three-version-20261020-222217-209/`，啟用前後清單另存於 `game-config/activation.json`。遊戲 Mods 清單顯示名稱為 `AI 智能漢化網絡(AI Translation Network)`；設定頁名稱為 `AI 智能漢化網絡 (AI Translation Network)V4.1 BETA`。清單搜尋僅比對顯示名稱，不比對資料夾名；尚未以遊戲畫面確認原先找不到的具體原因，後續顯示與 API 實連仍待玩家驗收。

22:32 後續部署：使用者已回報兩站 API 各自配對可用。依回饋移除連線失敗自動診斷彈窗，改為左上角單則 HTTP／原因通知，仍可手動查看詳細結果。三版本編譯通過；1.5 與實際安裝 1.7 各通過 96 項離線檢查；九種語言 XML 與鍵集合檢查通過。43 個配送檔案核對 SHA-256，保留本機繁中介面。備份與驗證：`backups/steam-three-version-20261020-223210-545/`。本次未更動玩家 API 設定，亦未啟動遊戲驗收新版通知畫面。

22:51 測試結束：使用者確認新版行為正常，執行 `-Deploy -LocalUiLanguage Default` 移除本機繁中覆寫，恢復各語言原有內容。三版本重新編譯及 43 個配送檔案 SHA-256 核對通過；Steam 安裝目錄的四個英文語系檔與 repo 發行包逐一相同。備份與驗證：`backups/steam-three-version-20261020-225109-489/`。

V4.2 BETA 本機候選版：九語系的模組顯示標題與使用教學改為 4.2，更新日誌在舊紀錄之前加入 2026-10-20 的 4.2 條目；批次 XML 視窗新增教學按鈕，主畫面教學也共用同一章節。批次教學涵蓋目標語言、多選／全選、匯出、外部編輯時保留鍵名與 manifest.json、匯入預覽、確認套用及衝突／錯誤處理。三版本編譯與 1.7 CoreCLR 的 29 項 smoke 檢查通過。62 個配送檔案核對 SHA-256，使用正常語系，備份：`backups/steam-three-version-20261020-233757-840/`。此驗證快照尚未包含 Workshop 更新。

同晚遊戲內 UI 檢查：標題實際顯示 V4.2 BETA；更新日誌最上方顯示 4.2 並保留 4.1 歷史；「全域工作台 → 批次 XML」右上角的 How to use 按鈕可開啟完整七步教學，文字換行與排版正常。遊戲採正常英文語系，未套用測試用繁中覆寫。

- 增加 `GUI.Label(Rect,string,GUIStyle)` 攔截；Waylith 的字串重載不再經過 GUIContent 重載。沿用既有啟用／停用、日誌排除與原文提示設定。
- GUIContent → string、Widgets → GUI 的巢狀呼叫使用同一層級防護，避免重複翻譯；例外發生時由 Finalizer 還原。
- 雲端 ZIP 使用 HttpClient 串流寫入檔案，保留替代記錄回應標頭；HTTP 失敗、截斷與逾時會清除殘檔。
- 因 Waylith 沒有 Unity 的 deviceUniqueIdentifier，1.7 首次需要雲端身分時在遊戲 Config 目錄建立 `ATC_CloudClientId.txt`，後續沿用同一個隨機 ID。1.5／1.6 繼續使用舊 Unity ID。
- **舊雲端上傳的「本人」歸屬無法由新引擎自動還原**，因此 1.7 的本人清單可能不包含舊 Unity ID 的上傳。一般下載及本機翻譯資料不受這個 ID 變化影響。此 ID 不是雲端登入憑證。
- 掃描使用當前版本的 LoadFolders／Defs／Languages；匯出的 metadata 與版本資料夾也依遊戲版本產生。
- 自動產生的 AITranslation.Pack 增補 1.5／1.6／1.7 支援宣告，保留既有 metadata 和翻譯檔。

## 驗證範圍

```powershell
dotnet build tests/WaylithCompatibilitySelfTest/WaylithCompatibilitySelfTest.csproj -c Release
dotnet build tests/ModVersionRuntimeSmoke/ModVersionRuntimeSmoke.csproj -c Release

$gameRoot = 'E:\SteamLibrary\steamapps\common\RimWorld'
dotnet exec --runtimeconfig "$gameRoot\RimWorld.runtimeconfig.json" --depsfile "$gameRoot\RimWorld.deps.json" tests/WaylithCompatibilitySelfTest/bin/Release/net10.0/WaylithCompatibilitySelfTest.dll

dotnet exec --runtimeconfig "$gameRoot\RimWorld.runtimeconfig.json" --depsfile "$gameRoot\RimWorld.deps.json" tests/ModVersionRuntimeSmoke/bin/Release/net10.0/ModVersionRuntimeSmoke.dll "$gameRoot\Mods\AI_TranslationCore\1.7\Assemblies\RimWorld_Auto_AI_Translation_Core.dll" $gameRoot 'E:\SteamLibrary\steamapps\workshop\content\294100\2009463077\1.7\Assemblies\0Harmony.dll' 1.7
```

WaylithCompatibilitySelfTest 連結正式的補丁、下載及身分程式碼；以應用層 stub 記錄翻譯次數，GUI 與 Harmony 使用真正遊戲 DLL，網路只連測試建立的本機 HTTP server。ModVersionRuntimeSmoke 另載入完整配送 DLL，檢查型別、版本路徑、匯出與 Harmony 掛載，不啟動背景翻譯、AI 呼叫或存取玩家存檔。

1.5／1.6 的 `net48` smoke executable 使用 Windows .NET Framework 與相應遊戲 DLL 複本；這不是 Unity/Mono 遊戲進程驗收。1.7 smoke 則使用遊戲自帶客製 CoreCLR。完整遊戲畫面、字型、翻譯工作與存檔重載仍需要遊戲內驗收。

## 2026-10-15 本機交付

- Git 分支：`codex/rimworld-1.7-support`；PR #3 的開發保持獨立。
- Steam 安裝：`E:\SteamLibrary\steamapps\common\RimWorld\Mods\AI_TranslationCore`。
- 本次遊戲版本：`1.7.4979 rev109`。
- 備份：`backups/steam-three-version-20261015-175814-792/`。
- 通過：三版本 Release 建置；1.5／1.6 的兩個 UnityWebRequest Obsolete 警告為既有 API 參考差異，1.7 零警告。
- 通過：Steam 實際 DLL 載入與版本／路徑／匯出檢查，1.5 共 7 項、1.6 共 7 項、1.7 共 11 項；1.7 包含 8 個 Harmony 目標掛載與翻譯包 metadata 遷移。
- 通過：Waylith 相容性 16 項，以及既有核心／策略／動態數字／未解決項目／語言品質／XML 檔案測試六套、1.5／1.6 路徑解析測試。
- 通過：三版本共 9 個 DLL／PDB／JSON 檔案，建置、repo 套件、Steam 安裝 SHA-256 相符。
- 未測：實際遊戲畫面、遊戲內翻譯與雲端上傳、載入存檔和存檔重載、1.5／1.6 的 Mono 遊戲進程。

主 DLL 的 SHA-256：

```text
1.5 DABDB03AB0499235F71238372936C51E657DC962D82BD718E5513FE5D6DFC24F
1.6 7830ACB7833853AE042B2300E355FFF7D8FDA52C6D84FB378F7ABD3FD59165FA
1.7 F873909F865C7398A4E9D74B9CF0ABE3F590928BF6E3E2211D57BCDCEE3EAB0F
```

首次遊戲內驗收建議依序檢查：模組清單與 Harmony 載入、設定頁與標籤翻譯、選一個第三方模組掃描、翻譯結果套用、雲端下載、存檔重載。任何錯誤請保留對應 Player.log 的完整例外。

## 上傳到 GitHub 與 Steam Workshop

GitHub 和 Steam Workshop 是兩個獨立發布動作。

目前 Git 分支是 `codex/rimworld-1.7-support`，遠端是 `origin`。在 repo 根目錄用 PowerShell 執行：

```powershell
git add README.md docs/STEAM_VERSION_SUPPORT.md tools/Build-SteamMod.ps1 `
  RimWorld-Auto-AI-Translation_Core `
  rimworld-mods/AI_TranslationCore `
  src tests/ModVersionRuntimeSmoke tests/WaylithCompatibilitySelfTest
git status
git commit -m "Add RimWorld 1.7 Waylith support"
git push -u origin codex/rimworld-1.7-support
```

不要加入 `.codex/`、`backups/` 或本機報告檔。推送完成後，在 GitHub 建立 Pull Request，來源選 `codex/rimworld-1.7-support`，目標選你目前開發線；PR #3 不需要合併進這次分支。

Steam Workshop 使用目前既有項目：

```text
https://steamcommunity.com/sharedfiles/filedetails/?id=3721659501
```

發布步驟：

1. 確認 RimWorld 已關閉，先執行 `tools/Build-SteamMod.ps1 -Deploy`。
2. 啟動 Steam 的 RimWorld 1.7 unstable，登入要擁有該 Workshop 項目的 Steam 帳號。
3. 在遊戲主選單開啟 **Mods**，選取 `AI 智能漢化網絡(AI Translation Network)`。
4. 選擇 **Upload/Update to Steam Workshop**；若遊戲顯示既有項目，確認 ID 是 `3721659501`，然後確認更新。
5. 發布後開啟上面的 Workshop URL，確認描述顯示支援 `1.5`、`1.6`、`1.7`，並確認下載內容含三個版本資料夾。

不要刪除或重新產生 `About/PublishedFileId.txt`，也不要使用「Create new Workshop item」；這會造成新的項目，讓訂閱者分裂。Steam 上傳會更新同一個項目，保留原有訂閱與評論。

ATC 設定頁裡的「雲端上傳」是另一件事。它把翻譯包上傳到 ATC 雲端服務，1.7 會使用新的 `ATC_CloudClientId.txt`；這只會影響雲端的「本人上傳」篩選，不影響 Steam Workshop，也不會改變 `PublishedFileId.txt`。舊雲端記錄若要重新歸戶，需要後端提供帳號合併或管理員遷移，不能靠 Steam 發布解決。
