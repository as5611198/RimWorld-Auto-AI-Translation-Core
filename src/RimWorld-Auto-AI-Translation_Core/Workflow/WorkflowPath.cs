using System;
using System.IO;

namespace AutoTranslator_Core.Workflow
{
    internal static class WorkflowPath
    {
        public static StringComparer Comparer { get; } =
            Path.DirectorySeparatorChar == '\\'
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;

        public static StringComparison Comparison { get; } =
            Path.DirectorySeparatorChar == '\\'
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
    }
}
