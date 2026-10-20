# 工作台批次 XML 匯出與匯入

入口：模組設定 → 全域工作台（Global Editor）→「批次 XML 匯出／匯入」（Batch XML export / import）。清單使用工作台目前可處理的已啟用模組；可依名稱或 Package ID 搜尋、勾選多個模組、全選目前搜尋結果或清除選取。舊的單模組 XML 功能與主畫面的成品打包功能照常使用。

V4.2 BETA 起，批次視窗右上角的「使用教學」可直接開啟本功能的步驟說明；主畫面的「使用教學」也包含同一段內容。

## 操作

1. 先在模組設定選好目標語言，再開啟批次視窗。視窗上方會顯示這次使用的語言。
2. 勾選模組，按「匯出原文 XML」。每次建立新的時間戳記資料夾並自動開啟，不覆蓋前次外部編輯內容。
3. 編輯各模組資料夾內的 XML，保留 XML 鍵名、分類、格式參數及資料夾結構；保留批次根目錄的 `manifest.json`。
4. 回到視窗，使用目前路徑或「選擇已有批次」，按「預覽匯入變更」。也可以將搬移後的批次根目錄路徑貼進路徑欄。
5. 逐模組確認變更、未變更、略過、缺少檔案及錯誤摘要，再按「儲存並套用」。預覽本身不寫入翻譯包。

輸出範例：

```text
!Translation_AI_Pack/Manual_XML_Batches/20261020-231029-37fedb/
  manifest.json
  author.mod.one/ChineseTraditional/Keyed/ManualTranslation.xml
  author.mod.one/ChineseTraditional/DefInjected/ThingDef/ManualTranslation.xml
  author.mod.two/ChineseTraditional/Keyed/ManualTranslation.xml
```

這個資料夾用於外部編輯，不是可直接發布的翻譯包。批次原文排除於翻譯快取和雲端上傳的檔案篩選；確認匯入後，結果會透過原有工作台流程寫入正式翻譯包及 `Upload_Workspace`，並保留手動修改的來源紀錄。

## 匯入規則

- 核對批次格式、目標語言、模組 ID、分類、鍵名及匯出時的原文雜湊。同名鍵會依模組與分類分開處理。
- 未編輯的原文、不變的譯文和空值不會覆蓋或刪除目前翻譯。
- 原文已更新、無對應鍵名或格式參數／保護符號不相符時，略過該詞條。
- 缺少 XML 檔案不會刪除既有詞條。XML 無效、重複鍵、未跳脫的巢狀標籤或外部實體宣告會阻擋該模組；其他可處理模組仍可匯入。
- 套用前重新載入資料，核對預覽時的原文和舊譯文；已被其他操作修改的詞條會略過，避免覆寫新內容。
- 儲存沿用工作台的逐檔原子寫入、原作者翻譯參考隔離與記憶體刷新。這不是跨所有模組的單一交易；失敗會顯示模組與原因，已成功的模組仍保留。
- 翻譯產線執行中停用批次操作；批次處理在背景執行，畫面顯示進度及目前模組。

## 驗證

2026-10-20：1.5、1.6、1.7 Release 建置通過。`ManualXmlBatchSelfTest` 在 net48 與 net10.0 各通過 26 項檢查，涵蓋雙模組／分類、Unicode／富文本往返、預覽唯讀、原文與譯文衝突、未知鍵、空值、語言核對、舊批次保留、缺檔、無效 XML、實體宣告及路徑驗證。批次功能與教學的九種語言各 20 鍵，XML 與格式參數集合檢查通過。

1.7 的遊戲自帶 CoreCLR 測試載入實際配送 DLL，在臨時目錄覆寫翻譯包路徑，跑兩個模組的工作台載入、匯出、匯入計畫、正式儲存與重新讀取；確認未修改詞條、其他模組檔案、原作者唯讀翻譯及來源紀錄均保留。包含載入、Harmony、批次及格式檢查共 29 項通過。此測試不存取玩家存檔，也不呼叫 AI。

遊戲內已確認批次入口、視窗、全選及 Core 原文匯出：37,570 條原文、137 個 XML。未編輯直接預覽顯示 0 條變更、37,570 條未變更，沒有寫入玩家翻譯包。玩家目前啟用清單只有 Core 具備可處理翻譯來源，因此遊戲內清單為一個模組；雙模組儲存以實際 DLL 的隔離測試驗證，不能視為已在遊戲中完成多個第三方模組的手動驗收。

批次 XML 功能本機部署：`backups/steam-three-version-20261020-231315-985/verification.json`，52 個檔案核對 SHA-256 通過，採用 `LocalUiLanguage=Default`，未重新套用本機繁中覆寫。此驗證快照尚未包含 Steam Workshop 更新；後續 4.2 BETA 整合驗證見 [版本支援紀錄](STEAM_VERSION_SUPPORT.md)。

```powershell
dotnet build tests/ManualXmlBatchSelfTest/ManualXmlBatchSelfTest.csproj -c Release
dotnet tests/ManualXmlBatchSelfTest/bin/Release/net10.0/ManualXmlBatchSelfTest.dll
& tests/ManualXmlBatchSelfTest/bin/Release/net48/ManualXmlBatchSelfTest.exe
```
