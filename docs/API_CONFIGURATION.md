# API 網址與選用密鑰

自訂 OpenAI 相容服務使用 `Custom_OpenAI`。填寫服務提供的完整 `http://` 或 `https://` 網址和模型 ID；如果服務不要求密鑰，密鑰欄可以留空。需要驗證的服務仍須填有效密鑰，模組不會替服務省略驗證。

例如 Base URL 是 `http://127.0.0.1:4315`：

- 取得模型清單：`GET http://127.0.0.1:4315/models`
- 實際翻譯／測試連線：`POST http://127.0.0.1:4315/chat/completions`
- 只有服務要求 `/v1` 時，才將 Base URL 改成 `http://127.0.0.1:4315/v1`。
- 提供完整 `/models` 或 `/chat/completions` 位址時，模組會先去掉該尾段，再組合對應接口。
- 服務沒有模型清單接口時，可以手填模型 ID，再測試連線；模型清單失敗不等於翻譯失敗。

這是通用 OpenAI 相容接口的設定說明，未代表已完成 Player2 帳號、授權與實際翻譯驗收。

## 修正內容

- 移除「把任意輸入補成 http:// 網址」的處理；既有省略協定的自訂網址須補上 `http://` 或 `https://`。
- 在設定畫面與送出前檢查網址，攔截密鑰誤貼、非 HTTP(S)、登入資訊、查詢參數、片段及內部空白。錯誤訊息不包含輸入值。
- 翻譯池、測試連線、模型清單和 Policy Agent 共用密鑰／網址檢查。`Custom_OpenAI` 有有效網址時允許空密鑰；其他供應商仍要求密鑰。
- 空密鑰不會產生 Authorization 標頭；有密鑰時沿用既有 Bearer／供應商驗證。
- 模型清單派發逾時日誌不再輸出網址，避免再次把誤填的密鑰寫進日誌。
- 設定欄位 `Key` 和 `CustomBaseUrl` 分開序列化；未發現互相覆蓋，這次不自動搬移或改寫玩家的設定。

## 驗證

`tests/ApiConfigurationSelfTest` 載入實際編譯出的模組 DLL，離線驗證網址正規化、錯誤密鑰欄位、自訂服務空密鑰、模型清單路徑及既有供應商的密鑰要求。所有密鑰都是合成測試資料，不發送網路請求。

```powershell
dotnet build tests/ApiConfigurationSelfTest/ApiConfigurationSelfTest.csproj -c Release
```

執行參數：`<mod.dll> <game assemblies directory> <harmony.dll>`。1.5／1.6 DLL 用 net48 測試程式；1.7 DLL 用 net10.0 測試程式。

本機 1.6 僅有編譯參考程序集，因此其設定測試以 1.5 遊戲依賴解析共用型別，不能視為在 1.6 Unity/Mono 遊戲中的驗收。1.7 測試使用本機 .NET 10 與遊戲 DLL，也不是 Waylith 遊戲內操作驗收。實際 Player2 服務的路徑、授權、HTTP 標頭與翻譯結果仍需端到端測試。
