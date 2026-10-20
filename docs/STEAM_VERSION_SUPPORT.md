# Steam 版：RimWorld 1.5 / 1.6 / 1.7

同一個模組目錄透過 `LoadFolders.xml` 分別載入 `1.5`、`1.6` 或 `1.7`。共用 Defs／Languages；每個版本有獨立編譯的主 DLL。這次相容性工作從 Steam 現有版本對應的 d587640 接續，未整合 PR #3。

| 遊戲版本 | 專案 | 編譯目標 | 輸出 |
|---|---|---|---|
| 1.5 | RimWorld-Auto-AI-Translation_Core.RW15.csproj | .NET Framework 4.8 | bin/Release-RW15 |
| 1.6 | RimWorld-Auto-AI-Translation_Core.csproj | .NET Framework 4.8 | bin/Release |
| 1.7 | RimWorld-Auto-AI-Translation_Core.RW17.csproj | .NET 10 | bin/Release-RW17 |

1.7 參考遊戲根目錄的 `Assembly-CSharp.dll`／`UnityStubs.dll`／`Waylith.dll`，使用共享 Harmony 模組的 1.7 DLL。模組 About.xml 宣告依賴 `brrainz.harmony`；請更新並啟用 Harmony，排在本模組前面。1.7 資料夾只配送本模組與 Newtonsoft.Json，不配送遊戲／CoreCLR／Harmony DLL。

## 建置與同步到本機 Steam

需要 .NET 10 SDK、舊 .NET Framework 參考程序集、本機 packages 下的 Harmony 2.4.2 / Newtonsoft.Json 13.0.4，以及三版本遊戲參考。

```powershell
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
