using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;

namespace AutoTranslator_Core.Workflow.AI
{
    public sealed class ModelTokenUsage
    {
        public long InputTokens { get; set; }
        public long OutputTokens { get; set; }
        public long TotalTokens => InputTokens + OutputTokens;
        public bool IsEstimated { get; set; }
    }

    public sealed class ModelInvocationRequest
    {
        public string Prompt { get; set; } = string.Empty;
        // Prefix boundary supplied by the prompt builder; null means no reliable split.
        public int? FixedPromptCharacters { get; set; }
        // Estimation is report-only. It must never determine a real provider request limit.
        public long EstimatedOutputTokens { get; set; }
        // Zero means the selected API/model configuration resolves the real request limit.
        public int ActualOutputTokenLimit { get; set; }
        public bool IsSimulation { get; set; } = false;
        public string ModelRoute { get; set; } = string.Empty;
        public string PackageId { get; set; } = string.Empty;
        public string RequestPurpose { get; set; } = string.Empty;
        public string RequestScope { get; set; } = string.Empty;
        public long SourceCharacters { get; set; }
        public int ItemCount { get; set; }
    }

    public sealed class ModelInvocationResult
    {
        public string Content { get; set; } = string.Empty;
        public ModelTokenUsage Usage { get; set; } = new ModelTokenUsage();
        public string FinishReason { get; set; } = string.Empty;
        public string ProviderName { get; set; } = string.Empty;
        public string ModelName { get; set; } = string.Empty;
        public int ActualOutputTokenLimit { get; set; }
        public int ConfiguredOutputTokenLimit { get; set; }
        public int? KnownModelOutputTokenLimit { get; set; }

        public bool IsOutputTruncated
        {
            get
            {
                string value = NormalizeFinishReason(FinishReason);
                return value == "LENGTH" || value == "MAX_TOKENS" ||
                       value == "MAX_OUTPUT_TOKENS" || value == "TOKEN_LIMIT" ||
                       (value.Contains("TOKEN") &&
                        (value.Contains("MAX") || value.Contains("LIMIT")));
            }
        }

        public string NormalizedFinishReason => NormalizeFinishReason(FinishReason);

        private static string NormalizeFinishReason(string finishReason)
        {
            return (finishReason ?? string.Empty).Trim().Replace('-', '_').Replace(' ', '_').ToUpperInvariant();
        }

        public bool IsNormalCompletion
        {
            get
            {
                string value = (FinishReason ?? string.Empty).Trim();
                return value.Equals("stop", StringComparison.OrdinalIgnoreCase) ||
                       value.Equals("end_turn", StringComparison.OrdinalIgnoreCase) ||
                       value.Equals("completed", StringComparison.OrdinalIgnoreCase) ||
                       value.Equals("simulation", StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    public interface ILanguageModelTransport
    {
        Task<ModelInvocationResult> InvokeAsync(ModelInvocationRequest request, CancellationToken cancellationToken);
    }

    public sealed class LanguageModelGateway
    {
        private readonly ILanguageModelTransport _transport;

        public LanguageModelGateway(ILanguageModelTransport transport)
        {
            _transport = transport;
        }

        public Task<ModelInvocationResult> InvokeAsync(
            ModelInvocationRequest request,
            CancellationToken cancellationToken)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (!request.IsSimulation)
            {
                if (_transport == null) throw new InvalidOperationException("No language model transport is configured.");
                AutoTranslatorSettings.AddDebugLog(
                    "workflow.model invoke mode=actual promptChars=" + request.Prompt.Length +
                    " outputLimitSource=api-model-config");
                return _transport.InvokeAsync(request, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            AutoTranslatorSettings.AddDebugLog(
                "workflow.model invoke mode=simulation promptChars=" + request.Prompt.Length +
                " estimatedOutputTokens=" + request.EstimatedOutputTokens);
            return Task.FromResult(new ModelInvocationResult
            {
                Content = string.Empty,
                FinishReason = "simulation",
                ActualOutputTokenLimit = 0,
                Usage = new ModelTokenUsage
                {
                    InputTokens = ApproximateTokenEstimator.Estimate(request.Prompt),
                    OutputTokens = Math.Max(0, request.EstimatedOutputTokens),
                    IsEstimated = true
                }
            });
        }
    }

    public static class ApproximateTokenEstimator
    {
        public const double BudgetProtectionRatio = 1.3d;
        public const string Version = "fallback-cjk3-word1-other4-v1";
        private const int LongOpaqueRunThreshold = 24;
        private static readonly Regex OpaqueIdentifierPattern = new Regex(
            @"(?<![A-Za-z0-9])(?:[A-Fa-f0-9]{8}(?:-[A-Fa-f0-9]{4}){3}-[A-Fa-f0-9]{12}|[A-Fa-f0-9]{32,}|[A-Za-z0-9+/]{32,}={0,2})(?![A-Za-z0-9])",
            RegexOptions.CultureInvariant);

        public static long Estimate(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            long opaqueTokens = 0;
            StringBuilder normalized = new StringBuilder(text);
            foreach (Match match in OpaqueIdentifierPattern.Matches(text))
            {
                opaqueTokens += DivideRoundUp(match.Length, 4);
                for (int index = match.Index; index < match.Index + match.Length; index++) normalized[index] = ' ';
            }
            text = normalized.ToString();
            long cjkCharacters = 0;
            long words = 0;
            long otherCharacters = 0;
            StringBuilder run = new StringBuilder();

            Action flushRun = () =>
            {
                if (run.Length == 0) return;
                string value = run.ToString();
                if (IsOpaqueRun(value)) otherCharacters += value.Length;
                else words += CountWordParts(value);
                run.Clear();
            };

            for (int i = 0; i < text.Length; i++)
            {
                char current = text[i];
                if (IsCjk(current))
                {
                    flushRun();
                    cjkCharacters++;
                }
                else if (IsAsciiLetterOrDigit(current))
                {
                    run.Append(current);
                }
                else
                {
                    flushRun();
                    if (!char.IsWhiteSpace(current) && current != '_') otherCharacters++;
                }
            }
            flushRun();
            return opaqueTokens + DivideRoundUp(cjkCharacters, 3) + words + DivideRoundUp(otherCharacters, 4);
        }

        public static long WithBudgetProtection(long baseTokens)
        {
            return (long)Math.Ceiling(Math.Max(0, baseTokens) * BudgetProtectionRatio);
        }

        private static long CountWordParts(string run)
        {
            if (string.IsNullOrEmpty(run)) return 0;
            long count = 1;
            for (int i = 1; i < run.Length; i++)
            {
                char previous = run[i - 1];
                char current = run[i];
                bool letterDigitBoundary = char.IsDigit(previous) != char.IsDigit(current);
                bool lowerToUpper = char.IsLower(previous) && char.IsUpper(current);
                bool acronymToWord = i + 1 < run.Length && char.IsUpper(previous) && char.IsUpper(current) && char.IsLower(run[i + 1]);
                if (letterDigitBoundary || lowerToUpper || acronymToWord) count++;
            }
            return count;
        }

        private static bool IsOpaqueRun(string value)
        {
            if (value.Length >= LongOpaqueRunThreshold) return true;
            int digits = 0;
            for (int i = 0; i < value.Length; i++) if (char.IsDigit(value[i])) digits++;
            return value.Length >= 12 && digits * 2 >= value.Length;
        }

        private static bool IsAsciiLetterOrDigit(char value)
        {
            return (value >= 'a' && value <= 'z') || (value >= 'A' && value <= 'Z') ||
                   (value >= '0' && value <= '9');
        }

        private static bool IsCjk(char value)
        {
            return (value >= '\u3400' && value <= '\u4dbf') ||
                   (value >= '\u4e00' && value <= '\u9fff') ||
                   (value >= '\uf900' && value <= '\ufaff') ||
                   (value >= '\u3040' && value <= '\u30ff') ||
                   (value >= '\uac00' && value <= '\ud7af');
        }

        private static long DivideRoundUp(long value, long divisor)
        {
            return value <= 0 ? 0 : (value + divisor - 1) / divisor;
        }
    }
}
