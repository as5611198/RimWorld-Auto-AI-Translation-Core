using Newtonsoft.Json.Linq;
using System;
using System.Threading;
using System.Threading.Tasks;
using AutoTranslator_Core.TranslationPolicy;
using System.Linq;
using System.Collections.Generic;

namespace AutoTranslator_Core
{
    internal sealed class WorkflowRawModelResponse
    {
        public string Content = string.Empty;
        public long? InputTokens;
        public long? OutputTokens;
        public string FinishReason = string.Empty;
        public string ProviderName = string.Empty;
        public string ModelName = string.Empty;
        public int ConfiguredOutputTokenLimit;
        public int? KnownModelOutputTokenLimit;
        public int ActualOutputTokenLimit;
    }

    public static partial class AutoTranslatorAPI
    {
        private static long? ReadNullableTokenCount(JToken token)
        {
            if (token == null) return null;
            long value;
            return long.TryParse(token.ToString(), out value) && value >= 0L ? (long?)value : null;
        }
        internal static async Task<WorkflowRawModelResponse> InvokeWorkflowJsonAsync(
            string prompt,
            CancellationToken cancellationToken,
            ApiKeyConfig selectedConfiguration = null,
            TranslationTaskTier taskTier = TranslationTaskTier.Bulk,
            Workflow.AI.ModelInvocationRequest invocation = null)
        {
            ApiKeyConfig config = selectedConfiguration ?? GetNextWorkflowConfig(taskTier);
            if (config == null) throw new InvalidOperationException(
                taskTier == TranslationTaskTier.Bulk && AutoTranslatorMod.Settings?.ApiConfigs?.Any(IsConfigReady) == true
                    ? "大量翻译需要至少一个可用的 Bulk 配置；中量或小量配置不会替代大量翻译。"
                    : DescribeUnavailableWorkflowConfigurations());
            string configurationProblem = GetWorkflowConfigurationProblem(config);
            if (configurationProblem != null)
                throw new InvalidOperationException(configurationProblem);

            string apiKey = CleanInput(config.Key);
            string model = CleanInput(config.SelectedModel);
            int configuredOutputTokenLimit = Math.Max(1, config.AtcMaxOutputTokens);
            int? knownModelOutputTokenLimit = config.GetKnownOutputTokenLimit(model);
            int maximumOutputTokens = config.ResolveActualOutputTokenLimit(model);
            AutoTranslatorSettings.AddLog(
                "模型请求：提供方 " + config.Provider + "，模型 " + model +
                "，ATC 上限 " + configuredOutputTokenLimit +
                "，模型能力 " + (knownModelOutputTokenLimit.HasValue
                    ? knownModelOutputTokenLimit.Value.ToString()
                    : "未知") + "，实际请求上限 " + maximumOutputTokens);
            AutoTranslatorSettings.AddDebugLog(
                "workflow.model output_limit provider=" + config.Provider +
                " model=" + model + " configured_limit=" + configuredOutputTokenLimit +
                " known_model_limit=" + (knownModelOutputTokenLimit?.ToString() ?? "unknown") +
                " actual_limit=" + maximumOutputTokens);
            string baseUrl = GetBaseUrl(config).TrimEnd('/');
            string url;
            JObject payload;
            if (config.Provider == TranslatorProvider.Google)
            {
                url = baseUrl + "/models/" + model + ":generateContent?key=" + apiKey;
                payload = new JObject
                {
                    ["contents"] = new JArray
                    {
                        new JObject
                        {
                            ["parts"] = new JArray { new JObject { ["text"] = prompt ?? string.Empty } }
                        }
                    },
                    ["generationConfig"] = new JObject
                    {
                        ["maxOutputTokens"] = Math.Max(1, maximumOutputTokens),
                        ["responseMimeType"] = "application/json"
                    }
                };
            }
            else
            {
                url = baseUrl + "/chat/completions";
                payload = new JObject
                {
                    ["model"] = model,
                    ["messages"] = new JArray
                    {
                        new JObject { ["role"] = "system", ["content"] = "Return valid JSON only and follow the supplied schema exactly." },
                        new JObject { ["role"] = "user", ["content"] = prompt ?? string.Empty }
                    },
                    ["max_tokens"] = Math.Max(1, maximumOutputTokens)
                };
                StructuredTranslationMode structuredMode = StructuredTranslationProviderAdapter.ResolveMode(config);
                if (structuredMode != StructuredTranslationMode.PromptOnly)
                    payload["response_format"] = new JObject { ["type"] = "json_object" };
                if (config.Provider == TranslatorProvider.DeepSeek)
                {
                    // Workflow steps need the final JSON, not chain-of-thought. DeepSeek V4 enables
                    // thinking by default; a small structured-output budget can otherwise be spent
                    // entirely on reasoning and leave message.content empty.
                    payload["thinking"] = new JObject { ["type"] = "disabled" };
                    payload["max_tokens"] = Math.Max(1, maximumOutputTokens);
                }
                if (config.Provider == TranslatorProvider.OpenRouter)
                    payload["provider"] = new JObject { ["require_parameters"] = false };
            }

            int timeoutSeconds = WorkflowRequestTimeout.Resolve(
                AutoTranslatorMod.Settings != null ? AutoTranslatorMod.Settings.TimeoutSeconds : 120);
            AutoTranslatorSettings.AddLog(
                "模型请求参数：响应超时 " + timeoutSeconds + " 秒；超过该时间仍未收到完整响应将判定为超时");
            Func<bool> cancelled = () => cancellationToken.IsCancellationRequested ||
                                         AutoTranslatorSettings.IsCancellationRequested;
            string lastFinishReason = string.Empty;
            string tokenSampleCallId = Guid.NewGuid().ToString("N");
            int physicalAttempt = 0;
            const int maximumStructuredAttempts = 3;
            for (int structuredAttempt = 0; structuredAttempt < maximumStructuredAttempts; structuredAttempt++)
            {
                System.Diagnostics.Stopwatch requestTimer = System.Diagnostics.Stopwatch.StartNew();
                ATC_WebResponse response = await SendTranslationRequestWithConcurrencyRecoveryAsync(
                    async () =>
                    {
                        int attempt = ++physicalAttempt;
                        LogWorkflowTokenSample(prompt, payload, invocation, config, model,
                            tokenSampleCallId, attempt, structuredAttempt + 1, "input", null);
                        try
                        {
                            ATC_WebResponse attemptResponse = await SendJsonRequestAttemptAsync(
                                url, payload.ToString(Newtonsoft.Json.Formatting.None), apiKey,
                                config.Provider, timeoutSeconds, cancelled);
                            LogWorkflowTokenSample(prompt, payload, invocation, config, model,
                                tokenSampleCallId, attempt, structuredAttempt + 1, "response", attemptResponse);
                            return attemptResponse;
                        }
                        catch
                        {
                            LogWorkflowTokenSample(prompt, payload, invocation, config, model,
                                tokenSampleCallId, attempt, structuredAttempt + 1, "exception", null);
                            throw;
                        }
                    },
                    config,
                    cancelled);
                requestTimer.Stop();
                cancellationToken.ThrowIfCancellationRequested();
                if (response == null || !response.IsSuccess)
                {
                    if (response != null && response.FailureKind == TranslationRequestFailureKind.ResponseTimeout)
                    {
                        int effectiveTimeoutSeconds = response.TimeoutSeconds > 0
                            ? response.TimeoutSeconds
                            : timeoutSeconds;
                        throw new InvalidOperationException(
                            "模型请求超时：配置超时时间=" + effectiveTimeoutSeconds +
                            " 秒；本次调用累计等待=" + requestTimer.ElapsedMilliseconds +
                            " 毫秒；超时阶段=" +
                            (string.IsNullOrWhiteSpace(response.FailureStage)
                                ? "WaitingResponse"
                                : response.FailureStage) +
                            "；底层错误=" +
                            (string.IsNullOrWhiteSpace(response.ErrorText)
                                ? "Request timeout"
                                : response.ErrorText));
                    }

                    throw new InvalidOperationException(response?.ErrorText ?? "No model response was returned.");
                }

                JObject envelope = JObject.Parse(response.ResponseBody ?? string.Empty);
                WorkflowRawModelResponse result = ExtractWorkflowResponse(envelope, config.Provider);
                lastFinishReason = ReadWorkflowFinishReason(envelope, config.Provider);
                result.FinishReason = lastFinishReason;
                result.ProviderName = config.Provider.ToString();
                result.ModelName = model;
                result.ConfiguredOutputTokenLimit = configuredOutputTokenLimit;
                result.KnownModelOutputTokenLimit = knownModelOutputTokenLimit;
                result.ActualOutputTokenLimit = maximumOutputTokens;
                if (IsWorkflowLengthTermination(lastFinishReason)) return result;
                if (!string.IsNullOrWhiteSpace(result.Content)) return result;

                AutoTranslatorSettings.AddDebugLog(
                    "workflow.model empty-content provider=" + config.Provider +
                    " model=" + model + " finishReason=" + lastFinishReason +
                    " attempt=" + (structuredAttempt + 1));
                if (structuredAttempt < maximumStructuredAttempts - 1)
                    PrepareEmptyStructuredResponseRetry(
                        payload,
                        config.Provider,
                        removeResponseFormat: structuredAttempt == 1);
            }

            throw new InvalidOperationException(
                "The model returned an empty structured response" +
                (string.IsNullOrWhiteSpace(lastFinishReason)
                    ? "."
                    : " (finish reason: " + lastFinishReason + ")."));
        }

        private static void LogWorkflowTokenSample(
            string prompt, JObject payload, Workflow.AI.ModelInvocationRequest invocation,
            ApiKeyConfig config, string model, string callId, int attempt, int structuredAttempt,
            string sampleEvent, ATC_WebResponse response)
        {
            if (AutoTranslatorMod.Settings == null || AutoTranslatorMod.Settings.LogLevel < AtcLogLevel.Debug)
                return;

            string text = prompt ?? string.Empty;
            int? boundary = invocation?.FixedPromptCharacters;
            bool knownSplit = boundary.HasValue && boundary.Value >= 0 && boundary.Value <= text.Length;
            string fixedText = knownSplit ? text.Substring(0, boundary.Value) : string.Empty;
            string dynamicText = knownSplit ? text.Substring(boundary.Value) : string.Empty;
            string systemText = string.Join("\n", (payload["messages"] as JArray)?
                .OfType<JObject>()
                .Where(message => message["role"]?.ToString() == "system")
                .Select(message => message["content"]?.ToString() ?? string.Empty) ?? Enumerable.Empty<string>());
            long promptTokens = Workflow.AI.ApproximateTokenEstimator.Estimate(text);
            long systemTokens = Workflow.AI.ApproximateTokenEstimator.Estimate(systemText);
            long? actualInput = null;
            long? actualOutput = null;
            long? actualTotal = null;
            string responseModel = null;
            string usageStatus = sampleEvent == "input" ? "pending" : "unavailable";
            if (response != null && !string.IsNullOrWhiteSpace(response.ResponseBody))
            {
                try
                {
                    JObject envelope = JObject.Parse(response.ResponseBody);
                    actualInput = ReadNullableTokenCount(config.Provider == TranslatorProvider.Google
                        ? envelope["usageMetadata"]?["promptTokenCount"] : envelope["usage"]?["prompt_tokens"]);
                    actualOutput = ReadNullableTokenCount(config.Provider == TranslatorProvider.Google
                        ? envelope["usageMetadata"]?["candidatesTokenCount"] : envelope["usage"]?["completion_tokens"]);
                    actualTotal = ReadNullableTokenCount(config.Provider == TranslatorProvider.Google
                        ? envelope["usageMetadata"]?["totalTokenCount"] : envelope["usage"]?["total_tokens"]);
                    responseModel = (envelope["model"] ?? envelope["modelVersion"])?.ToString();
                    usageStatus = actualInput.HasValue && actualOutput.HasValue ? "reported" : "missing_fields";
                }
                catch (Newtonsoft.Json.JsonException) { usageStatus = "invalid_response_json"; }
            }

            // Numeric diagnostics only: no prompt, response text, URL or credentials.
            var sample = new JObject
            {
                ["schema"] = "workflow-token-sample-v1",
                ["event"] = sampleEvent,
                ["call_id"] = callId,
                ["attempt"] = attempt,
                ["structured_attempt"] = structuredAttempt,
                ["purpose"] = invocation?.RequestPurpose ?? "connection_or_direct_call",
                ["request_scope"] = invocation?.RequestScope,
                ["provider"] = config.Provider.ToString(),
                ["model"] = model,
                ["response_model"] = responseModel,
                ["estimator"] = Workflow.AI.ApproximateTokenEstimator.Version,
                ["split_known"] = knownSplit,
                ["item_count"] = invocation?.ItemCount,
                ["source_chars"] = invocation?.SourceCharacters,
                ["prompt_chars"] = text.Length,
                ["fixed_chars"] = knownSplit ? (int?)fixedText.Length : null,
                ["dynamic_chars"] = knownSplit ? (int?)dynamicText.Length : null,
                ["system_chars"] = systemText.Length,
                ["estimated_fixed_tokens"] = knownSplit ? (long?)Workflow.AI.ApproximateTokenEstimator.Estimate(fixedText) : null,
                ["estimated_dynamic_tokens"] = knownSplit ? (long?)Workflow.AI.ApproximateTokenEstimator.Estimate(dynamicText) : null,
                ["estimated_system_tokens"] = systemTokens,
                ["estimated_prompt_tokens"] = promptTokens,
                ["estimated_input_tokens"] = promptTokens + systemTokens,
                ["protocol_framing_estimated"] = false,
                ["estimated_output_tokens"] = invocation?.EstimatedOutputTokens,
                ["actual_input_tokens"] = actualInput,
                ["actual_output_tokens"] = actualOutput,
                ["actual_total_tokens"] = actualTotal,
                ["usage_status"] = usageStatus,
                ["response_success"] = response == null ? (bool?)null : response.IsSuccess,
                ["budget_denied"] = response == null ? (bool?)null : response.BudgetDenied
            };
            AutoTranslatorSettings.AddDebugLog("workflow.token_sample " + sample.ToString(Newtonsoft.Json.Formatting.None));
        }

        private static WorkflowRawModelResponse ExtractWorkflowResponse(
            JObject envelope,
            TranslatorProvider provider)
        {
            WorkflowRawModelResponse result = new WorkflowRawModelResponse();
            if (provider == TranslatorProvider.Google)
            {
                JArray parts = envelope["candidates"]?[0]?["content"]?["parts"] as JArray;
                result.Content = string.Concat(parts?
                    .OfType<JObject>()
                    .Where(part => part["thought"]?.Type != JTokenType.Boolean || !part["thought"].Value<bool>())
                    .Select(part => ReadWorkflowTextFragment(part["text"]))
                    .Where(text => text != null) ?? Enumerable.Empty<string>());
                result.InputTokens = ReadNullableTokenCount(envelope["usageMetadata"]?["promptTokenCount"]);
                result.OutputTokens = ReadNullableTokenCount(envelope["usageMetadata"]?["candidatesTokenCount"]);
            }
            else
            {
                result.Content = ExtractOpenAiCompatibleContent(envelope["choices"]?[0]?["message"]?["content"]);
                result.InputTokens = ReadNullableTokenCount(envelope["usage"]?["prompt_tokens"]);
                result.OutputTokens = ReadNullableTokenCount(envelope["usage"]?["completion_tokens"]);
            }
            return result;
        }

        private static string ExtractOpenAiCompatibleContent(JToken content)
        {
            if (content == null || content.Type == JTokenType.Null) return string.Empty;
            if (content.Type == JTokenType.String) return content.ToString();
            if (content is JArray blocks)
            {
                return string.Concat(blocks.OfType<JObject>()
                    .Select(ReadOpenAiTextBlock)
                    .Where(text => text != null));
            }
            return content.ToString();
        }

        private static string ReadOpenAiTextBlock(JObject block)
        {
            string type = block["type"]?.ToString() ?? string.Empty;
            if ((block["thought"]?.Type == JTokenType.Boolean && block["thought"].Value<bool>()) ||
                type.Equals("thinking", StringComparison.OrdinalIgnoreCase) ||
                type.Equals("reasoning", StringComparison.OrdinalIgnoreCase))
                return null;
            if (type.Length > 0 &&
                !type.Equals("text", StringComparison.OrdinalIgnoreCase) &&
                !type.Equals("output_text", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("模型返回了非文本响应片段，无法作为翻译 JSON 处理。");
            return ReadWorkflowTextFragment(block["text"] ?? block["content"]);
        }

        private static string ReadWorkflowTextFragment(JToken text)
        {
            if (text == null || text.Type == JTokenType.Null) return null;
            if (text.Type != JTokenType.String)
                throw new InvalidOperationException("模型响应的文本片段不是字符串，无法作为翻译 JSON 处理。");
            return text.Value<string>();
        }

        private static string ReadWorkflowFinishReason(JObject envelope, TranslatorProvider provider)
        {
            return provider == TranslatorProvider.Google
                ? envelope["candidates"]?[0]?["finishReason"]?.ToString() ?? string.Empty
                : envelope["choices"]?[0]?["finish_reason"]?.ToString() ?? string.Empty;
        }

        private static bool IsWorkflowLengthTermination(string finishReason)
        {
            string value = (finishReason ?? string.Empty).Trim()
                .Replace('-', '_').Replace(' ', '_').ToUpperInvariant();
            return value == "LENGTH" || value == "MAX_TOKENS" ||
                   value == "MAX_OUTPUT_TOKENS" || value == "TOKEN_LIMIT" ||
                   (value.Contains("TOKEN") &&
                    (value.Contains("MAX") || value.Contains("LIMIT")));
        }

        private static void PrepareEmptyStructuredResponseRetry(
            JObject payload,
            TranslatorProvider provider,
            bool removeResponseFormat)
        {
            if (provider == TranslatorProvider.Google)
            {
                return;
            }

            if (provider == TranslatorProvider.DeepSeek && removeResponseFormat)
            {
                // DeepSeek documents an occasional empty response in JSON Output mode.
                // Keep response_format for the first retry. Only the final attempt falls
                // back to the prompt-level JSON contract.
                payload.Remove("response_format");
            }
            JArray messages = payload["messages"] as JArray;
            JObject system = messages?.OfType<JObject>()
                .FirstOrDefault(message => string.Equals(message["role"]?.ToString(), "system", StringComparison.Ordinal));
            if (system != null)
                system["content"] = (system["content"]?.ToString() ?? string.Empty) +
                                    " Never return an empty response; emit the complete JSON object now.";
        }

        private static string GetWorkflowConfigurationProblem(ApiKeyConfig config)
        {
            if (config == null) return AutoTranslatorMod.WfText("配置不存在", "Configuration is missing");
            if (!config.Enabled) return AutoTranslatorMod.WfText("配置未启用", "Configuration is disabled");
            if (config.Provider == TranslatorProvider.DeepL)
                return AutoTranslatorMod.WfText("DeepL 不支持工作流 JSON 模型调用", "DeepL does not support workflow JSON model requests");
            if (string.IsNullOrEmpty(CleanInput(config.Key)))
                return AutoTranslatorMod.WfText("API Key 清理空白和无效字符后为空", "API key is empty after input normalization");
            if (string.IsNullOrEmpty(CleanInput(config.SelectedModel)))
                return AutoTranslatorMod.WfText("模型名称清理空白和无效字符后为空", "Model name is empty after input normalization");
            return null;
        }

        private static string DescribeUnavailableWorkflowConfigurations()
        {
            List<ApiKeyConfig> configs = AutoTranslatorMod.Settings?.ApiConfigs?.ToList() ?? new List<ApiKeyConfig>();
            string summary = AutoTranslatorMod.WfText("没有可用于翻译或复核的 API 配置。", "No API configuration is available for translation or review.");
            if (configs.Count == 0)
                return summary + AutoTranslatorMod.WfText("请先在设置中添加接口。", " Add an API configuration in settings.");
            return summary + " " + string.Join("; ", configs.Select((item, index) =>
                "API " + (index + 1) + ": " +
                (GetWorkflowConfigurationProblem(item) ?? AutoTranslatorMod.WfText(
                    "配置已变化，请重新尝试", "Configuration changed; try again"))));
        }

        private static ApiKeyConfig GetNextWorkflowConfig(TranslationTaskTier taskTier)
        {
            if (AutoTranslatorMod.Settings?.ApiConfigs == null) return null;
            var ready = AutoTranslatorMod.Settings.ApiConfigs
                .Where(IsConfigReady)
                .ToList();
            var eligible = TranslationTaskTierRouter.SelectEligible(ready, taskTier);
            if (eligible.Count == 0) return null;
            if (eligible.Count == 1) return eligible[0];
            int index = System.Threading.Interlocked.Increment(ref currentKeyIndex);
            return eligible[(index & int.MaxValue) % eligible.Count];
        }
    }
}
