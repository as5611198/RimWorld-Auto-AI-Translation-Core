using System;
namespace AutoTranslator_Core
{
    internal static class WorkflowRequestTimeout
    {
        public static int Resolve(int configuredSeconds)
        {
            return Math.Min(600, Math.Max(15, configuredSeconds > 0 ? configuredSeconds : 60));
        }
    }
}
