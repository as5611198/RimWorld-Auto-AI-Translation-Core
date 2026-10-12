using System;
using System.IO;
using AutoTranslator_Core.Workflow;

namespace AutoTranslator_Core
{
    public static partial class AutoTranslatorScanner
    {
        internal static bool ShouldIncludeUploadFile(string sourceFolder, string file, string packageId)
        {
            if (IsWorkbenchManualExportPath(file)) return false;
            string name = Path.GetFileName(file).ToLowerInvariant();
            string id1 = (packageId ?? string.Empty).ToLowerInvariant();
            string id2 = id1.Replace('.', '_');
            return sourceFolder.IndexOf("Upload_Workspace", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.StartsWith(id1 + "_") || name.StartsWith(id1 + ".") ||
                   name.StartsWith(id2 + "_") || name.StartsWith(id2 + ".");
        }

        internal static bool TryPrepareUploadSourceFolder(
            string sourceFolder, string packageId, string languageFolder, string translationType,
            out string preparedSourceFolder, out UploadLabelValidationSummary summary)
        {
            // Categories are labels. Never filter out translations or generate replacements.
            preparedSourceFolder = sourceFolder;
            summary = new UploadLabelValidationSummary();
            if (string.Equals(translationType, "Official_Group", StringComparison.OrdinalIgnoreCase)) return true;
            if (!Directory.Exists(sourceFolder)) return false;
            if (!string.Equals(translationType, "Manual", StringComparison.OrdinalIgnoreCase)) return true;
            UploadClassificationSummary classification = WorkflowBackendRuntime.GetOrCreate()
                .EvaluateUploadClassification(sourceFolder, packageId, languageFolder);
            summary.IncludedEntries = (int)Math.Min(int.MaxValue, classification.TotalEntries);
            if (string.Equals(translationType, "Manual", StringComparison.OrdinalIgnoreCase) &&
                !classification.CanLabelManual)
            {
                summary.BlockReason = classification.ManualBlockedReason;
                AutoTranslatorSettings.AddWarningLog(packageId + ": " + summary.BlockReason);
                return false;
            }
            return true;
        }
    }
}
