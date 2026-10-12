using System;
using System.Threading;
using System.Threading.Tasks;

namespace AutoTranslator_Core.Workflow
{
    public sealed partial class WorkflowBackend
    {
        public Task<TranslationFileRepairService.RepairSummary> RunTranslationFileRepairAsync(
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return RunExclusiveAsync(
                WorkflowTaskKind.TranslationFileMaintenance,
                "Translation file format repair",
                async token =>
                {
                    TranslationFileRepairService.RepairSummary result = await Task.Run(
                        () => TranslationFileRepairService.RepairCurrentLanguagePack(requestMemoryDrop: false), token);
                    token.ThrowIfCancellationRequested();
                    await Task.Run(() => _synchronizer.Synchronize(token), token);
                    if (!await AutoTranslatorScanner.RequestMemoryDropAsync())
                        throw new InvalidOperationException("Translation memory reload failed.");
                    token.ThrowIfCancellationRequested();
                    return result;
                },
                cancellationToken);
        }
    }
}
