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
```

可用 `-RimWorldRoot`、`-RimWorld15ManagedDir`、`-RimWorld16ManagedDir`、`-Harmony17Path` 指定其他路徑。1.6 若沒有舊 Managed 安裝，專案會使用本機 NuGet 快取的 `Krafs.Rimworld.Ref 1.6.4871`；它只供編譯，不可當成執行時依賴配送。

部署前必須關閉 RimWorld。腳本先完成所有建置，備份受影響的版本 DLL／PDB／JSON 與 Steam metadata，再逐檔同步並比對 SHA-256。備份及 verification.json 位於 `backups/steam-three-version-日期時間/`。若本機已存在已知 packageId 的 `!Translation_AI_Pack`，腳本另備份並更新它的支援版本 metadata，不改翻譯內容。腳本不會發布 Workshop、推送 GitHub 或處理 PR。

## 1.7 的適配內容

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
