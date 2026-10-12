using System;
using System.Threading;
using System.Threading.Tasks;

namespace AutoTranslator_Core.Workflow.AI
{
    public sealed class AutoTranslatorApiModelTransport : ILanguageModelTransport
    {
        public async Task<ModelInvocationResult> InvokeAsync(
            ModelInvocationRequest request,
            CancellationToken cancellationToken)
        {
            WorkflowRawModelResponse response;
            using (TranslationUsageCoordinator.PushRequestContext(
                request.PackageId,
                request.RequestPurpose,
                request.RequestScope,
                request.SourceCharacters,
                exempt: request.IsSimulation,
                itemCount: request.ItemCount))
            {
                response = await AutoTranslatorAPI.InvokeWorkflowJsonAsync(
                    request.Prompt, cancellationToken, taskTier:
                    request.RequestPurpose == "ai_review" ? TranslationTaskTier.Standard :
                    request.RequestPurpose == "ai_translation_retry" ? TranslationTaskTier.Precision :
                    TranslationTaskTier.Bulk, invocation: request);
            }
            return new ModelInvocationResult
            {
                Content = response.Content,
                FinishReason = response.FinishReason,
                ProviderName = response.ProviderName,
                ModelName = response.ModelName,
                ActualOutputTokenLimit = response.ActualOutputTokenLimit,
                ConfiguredOutputTokenLimit = response.ConfiguredOutputTokenLimit,
                KnownModelOutputTokenLimit = response.KnownModelOutputTokenLimit,
                Usage = new ModelTokenUsage
                {
                    InputTokens = response.InputTokens ?? ApproximateTokenEstimator.Estimate(request.Prompt),
                    OutputTokens = response.OutputTokens ?? ApproximateTokenEstimator.Estimate(response.Content),
                    IsEstimated = !response.InputTokens.HasValue || !response.OutputTokens.HasValue
                }
            };
        }
    }
}
