using System;

namespace AutoTranslator_Core.Workflow
{
    public static class ClassificationFlagsCodec
    {
        private const int BitsPerLayer = 2;
        private const byte LayerMask = 0x03;

        public static CandidateClassification Get(byte flags, ClassificationLayer layer)
        {
            int shift = (int)layer * BitsPerLayer;
            return (CandidateClassification)((flags >> shift) & LayerMask);
        }

        public static byte Set(byte flags, ClassificationLayer layer, CandidateClassification value)
        {
            if ((byte)value > LayerMask) throw new ArgumentOutOfRangeException(nameof(value));
            int shift = (int)layer * BitsPerLayer;
            int cleared = flags & ~(LayerMask << shift);
            return (byte)(cleared | ((byte)value << shift));
        }

        public static byte Clear(byte flags, ClassificationLayer layer)
        {
            return Set(flags, layer, CandidateClassification.NotAnalyzed);
        }

        public static byte ClearFrom(byte flags, ClassificationLayer firstLayerToClear)
        {
            int shift = (int)firstLayerToClear * BitsPerLayer;
            int keepMask = shift == 0 ? 0 : (1 << shift) - 1;
            return (byte)(flags & keepMask);
        }

        public static CandidateClassification GetEffective(byte flags)
        {
            for (int layer = (int)ClassificationLayer.Manual; layer >= (int)ClassificationLayer.Xml; layer--)
            {
                CandidateClassification value = Get(flags, (ClassificationLayer)layer);
                if (value != CandidateClassification.NotAnalyzed) return value;
            }
            // 00000000 means that no source has produced a classification.  It is
            // exposed to workflow consumers as "undetermined" while the null
            // effective layer still preserves the distinction from an explicit
            // source-level Undetermined value.
            return CandidateClassification.Undetermined;
        }

        public static CandidateClassification GetEffective(CandidateRecord candidate)
        {
            return GetEffective(GetEffectiveFlags(candidate));
        }

        public static ClassificationLayer? GetEffectiveLayer(byte flags)
        {
            for (int layer = (int)ClassificationLayer.Manual; layer >= (int)ClassificationLayer.Xml; layer--)
            {
                ClassificationLayer typedLayer = (ClassificationLayer)layer;
                if (Get(flags, typedLayer) != CandidateClassification.NotAnalyzed) return typedLayer;
            }
            return null;
        }

        public static ClassificationLayer? GetEffectiveLayer(CandidateRecord candidate)
        {
            return GetEffectiveLayer(GetEffectiveFlags(candidate));
        }

        private static byte GetEffectiveFlags(CandidateRecord candidate)
        {
            if (candidate == null) return 0;
            byte flags = candidate.ClassificationFlags;
            if (!string.Equals(
                    candidate.XmlAnalyzerVersion,
                    WorkflowIdentity.XmlAnalyzerVersion,
                    StringComparison.Ordinal))
                flags = Clear(flags, ClassificationLayer.Xml);
            if (!string.Equals(
                    candidate.DllAnalyzerVersion,
                    WorkflowIdentity.DllAnalyzerVersion,
                    StringComparison.Ordinal))
                flags = Clear(flags, ClassificationLayer.Dll);
            if (!WorkflowIdentity.IsAiReviewCurrent(candidate))
                flags = Clear(flags, ClassificationLayer.AiReview);
            return flags;
        }
    }
}
