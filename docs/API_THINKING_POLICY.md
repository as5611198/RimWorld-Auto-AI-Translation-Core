# 翻譯與審查的思考模式（1.5／1.6／1.7）

預設「自動」模式：一般翻譯優先關閉可關閉的思考，降低等待與用量；Policy Agent 判斷模糊欄位是否可翻譯，保留思考能力。能調整強度時採較低的支援程度，不能關閉的模型不會被送入不合法的「關閉」設定。這是 API 請求設定，不能保證第三方服務有遵守參數；隱藏思考內容也不等於模型沒有思考。

思考模式、自訂翻譯要求、手動取得自訂 OpenAI 模型清單與相關回應解析由三版共用。以 1.6 為主要版本；1.5／1.6 保留 UnityWebRequest，1.7 另以 HttpClient 適配新版執行環境。網路底層不影響這些玩家設定的提供。

## 玩家設定

設定頁中，各組翻譯 API 與審查 Agent 的獨立 API 下方都有可展開的「進階設定」。思考模式分別儲存，舊設定自動採用「自動（推薦）」。

- **自動（推薦）**：依用途採下表設定。
- **盡量關閉**：對此組 API 的請求使用已知可用的關閉方式。必要思考模型仍保留最低可用程度或模型預設，介面會說明限制。
- **啟用思考**：支援時明確啟用；採已知支援的較低程度，可能增加等待與用量。

未知模型／轉接站保留服務預設，選項不代表服務一定支援該模式。OpenRouter 依目前取得的模型能力判斷，可按「重新獲取模型」更新。Player2 的模型與思考仍由應用程式管理，DeepL 無此設定。

翻譯進行中、模型清單取得中或連線測試中不能更換該組 API 的思考模式。更換模式會讓舊連線測試結果失效，也會區分 Policy Agent 的快取身分，避免沿用其他模式的審查結果。審查設定「盡量關閉」且模型可關閉時，不額外預留思考 Token；必要思考型號仍會預留。

## 自訂翻譯要求

設定頁按 **「進階：自訂翻譯要求…」**，填寫額外的用語、語氣、名稱或專有名詞要求，例如：

```text
使用台灣繁體中文的用語。
人名保留原文。
將 pawn 統一譯為殖民者。
```

最多 4000 個字元，可查看目前目標語言的完整提示詞預覽。「清空／還原預設」清除草稿，按「儲存並關閉」才生效；取消或右上角關閉會保留原設定。預覽可選取複製，不能修改內建規則。

額外要求套用到所有一般 AI 翻譯的新請求（包括其連線測試），不影響 Policy Agent 的審查提示詞、審查測試或 DeepL。目標語言、JSON 陣列格式、數量與順序、變數與標籤保護仍保留在提示詞中，原有回應驗證亦保留。提示詞無法保證模型遵守所有要求。

儲存後不會自動重翻既有譯文；若要改既有內容，需自行重新翻譯。翻譯執行中不能修改額外要求。

| 供應商／模型（自動模式） | 一般翻譯 | Policy Agent |
| --- | --- | --- |
| MiMo 已知混合思考型號 | `thinking.type=disabled` | `thinking.type=enabled` |
| DeepSeek 已知混合思考型號 | `thinking.type=disabled` | 開啟思考；支援時用 `reasoning_effort=low` |
| DeepSeek R1／舊 reasoner | 保留模型自身思考 | 保留模型自身思考 |
| GLM 4.5～5.2 已知混合型號 | `thinking.type=disabled` | 開啟思考；5.2 採實際最低的 `high` |
| GLM 5.3 系列 | 不關閉，採 `low` | 開啟思考，採 `low` |
| 百煉已知混合型號（千問、部分 DeepSeek／MiMo／GLM／Kimi） | `enable_thinking=false` | `enable_thinking=true`；已知千問型號另設 1024 思考 Token 預算 |
| 百煉僅思考型號、舊版不支援開關的快照 | 保留服務設定 | 保留服務設定 |
| OpenAI GPT-5.1／5.2／5.4／5.5／5.6 已知一般推理型號 | `reasoning_effort=none` | `low` |
| OpenAI 原 GPT-5、o 系列與已知 GPT-6 型號 | 採最低支援程度，保留必要思考 | 保留思考，採 `low` |
| OpenAI Responses 專用 Pro 型號 | 不新增思考開關；此 Chat Completions 接口不因此獲得 Responses 支援 | 同左 |
| Grok 4.3 | `none` | `low` |
| Grok 4.5／4.6／4.7、3-mini | 保留必要思考，採 `low` | `low` |
| Gemini 2.5 Flash／Flash-Lite | `thinkingBudget=0` | `thinkingBudget=1024` |
| Gemini 2.5 Pro | 保留必要思考，預算 128 | 預算 1024 |
| 已知 Gemini 3／4 型號 | 支援的 `minimal` 或 `low`；不宣稱完全關閉 | 支援的正向思考程度 |
| OpenRouter | 使用模型清單的 `reasoning` 能力資料；可關才送 `enabled=false` | 啟用思考；依能力選低強度或支援的預算 |
| 未知模型／服務、Player2 | 不猜測開關協定，沿用服務設定 | 不強制關閉，沿用服務設定 |

型號細項以 `ApiThinkingPolicy.cs` 的已知清單與 OpenRouter 回傳能力為準。OpenRouter 能力資料來自既有模型清單請求，保留一小時，不另外送出模型探測／付費請求；沒有能力資料時沿用服務設定。必要時按「重新獲取模型」更新能力資料。

自訂 OpenAI 若指向已知官方 OpenAI 相容端點，可依主機辨識思考協定。未知轉接站不會只憑模型名稱被塞入某個供應商的擴充欄位；亦不會替使用者換模型、網址、金鑰或驗證方式。Google 原生與 OpenAI 相容協定不同，此規則不會把自訂 OpenAI 自動改成 Google 原生接口。

## 審查用量與結果

已知會思考的審查請求，在原有回答上限之外預留 4096 Token，總上限最高 8192。預估用量先包含這份預留，再經過原有的呼叫數／Token 限制；超出時仍走既有的預算確認流程，沒有繞過用量限制。這是最大可用空間，不代表每次一定消耗該用量，也不保證每種模型都能在此上限內完成。

Policy Agent 設定頁的測試連線也使用審查用途與該組 API 的思考設定，不會套用一般翻譯的思考選項或額外翻譯要求。審查快取版本由 3 改為 4，並將思考選項納入審查身分，讓不同模式的判斷不被混用。明確的本機禁止規則、`review`／錯誤／預算不足時的保守處理都不變。

Google 回應若包含 `thought=true` 的部分，一般翻譯與審查都只解析最終回答。這是回應解析的處理，與服務端思考是否開啟分開。

## 官方參考（2026-10-21 核對）

- [MiMo OpenAI Chat Completions](https://mimo.mi.com/docs/zh-CN/api/chat/openai-api)
- [DeepSeek 思考模式](https://api-docs.deepseek.com/guides/thinking_mode/)
- [GLM 深度思考與不可關閉型號](https://docs.bigmodel.cn/cn/guide/capabilities/thinking)
- [百煉深度思考與模型差異](https://help.aliyun.com/zh/model-studio/deep-thinking/)
- [OpenAI 推理指南](https://developers.openai.com/api/docs/guides/reasoning)、[Chat Completions 參數](https://developers.openai.com/api/reference/resources/chat/subresources/completions/methods/create)、[GPT-5.6 Sol](https://developers.openai.com/api/docs/models/gpt-5.6-sol)、[GPT-5.4 Mini](https://developers.openai.com/api/docs/models/gpt-5.4-mini)
- [Grok 推理限制](https://docs.x.ai/developers/model-capabilities/text/reasoning)、[Grok 4.3](https://docs.x.ai/developers/models/grok-4.3)
- [Gemini 思考預算與程度](https://ai.google.dev/gemini-api/docs/generate-content/thinking)
- [OpenRouter 模型能力與統一思考參數](https://openrouter.ai/docs/guides/best-practices/reasoning-tokens)

## 驗證範圍

`tests/NativeHttpTransportSelfTest` 載入實際 1.7 模組 DLL，檢查模型／用途參數表、未知服務不加參數、OpenRouter 強制思考能力、預算先預留後送出、翻譯與審查的本機 HTTP 實際請求，以及 Google 思考內容不混入譯文。測試使用合成憑證與本機 HTTP server；不代表完成各正式供應商的金鑰實連或品質驗收。

`tests/ApiConfigurationSelfTest` 同時提供 net48 與 net10.0 測試宿主，三版共用的 210 項檢查包含思考模式、提示詞、模型清單操作及實際請求建構／回應解析。流程檢查僅在獨立測試程序中攔截傳輸邊界並回傳合成回應，不送出正式 API 請求。

目前本機只有 1.5 的完整 Managed 組件與 1.7 執行環境；1.6 使用 `Krafs.Rimworld.Ref 1.6.4871` 編譯，其 DLL 的純 API 邏輯在載入 1.5 相容依賴的 net48 測試宿主執行。這不代表完成真正 1.6 遊戲內的 UnityWebRequest 連線、UI 或存檔驗收。1.7 另有本機 HTTP 實送與 Scribe 存讀測試。
