using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace AutoTranslator_Core.Workflow
{
    public sealed partial class WorkflowBackend
    {
        public Task<bool> RunApiConnectionTestAsync(
            ApiKeyConfig configuration,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return RunExclusiveAsync(
                WorkflowTaskKind.ApiConnectionTest,
                "API connection test",
                async token =>
                {
                    using (TranslationUsageCoordinator.PushRequestContext(
                        null, "connection_test", null, 0, exempt: true, itemCount: 0))
                    {
                        WorkflowRawModelResponse response = await AutoTranslatorAPI.InvokeWorkflowJsonAsync(
                            "Return exactly this JSON object: {\"connected\":true}", token, configuration);
                        token.ThrowIfCancellationRequested();
                        return JObject.Parse(response.Content).Value<bool?>("connected") == true;
                    }
                },
                cancellationToken);
        }
    }
}
