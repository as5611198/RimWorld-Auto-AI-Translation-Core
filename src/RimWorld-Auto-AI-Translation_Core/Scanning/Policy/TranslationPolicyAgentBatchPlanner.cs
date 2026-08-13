using System;
using System.Collections.Generic;

namespace AutoTranslator_Core.TranslationPolicy
{
    public static class TranslationPolicyAgentBatchPlanner
    {
        public const int MaximumGroupsPerRequest = 20;

        public static List<List<T>> CreateBatches<T>(IEnumerable<T> items)
        {
            List<T> source = new List<T>();
            if (items != null)
            {
                foreach (T item in items) source.Add(item);
            }

            List<List<T>> batches = new List<List<T>>();
            for (int index = 0; index < source.Count; index += MaximumGroupsPerRequest)
            {
                int count = Math.Min(MaximumGroupsPerRequest, source.Count - index);
                batches.Add(source.GetRange(index, count));
            }

            return batches;
        }
    }
}
