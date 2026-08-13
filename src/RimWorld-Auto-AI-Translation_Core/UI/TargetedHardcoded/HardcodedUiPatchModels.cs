using Newtonsoft.Json;
using System.Collections.Generic;

namespace AutoTranslator_Core.TargetedHardcodedUi
{
    // This manifest is an explicit allow-list. Entries may be approved by the
    // static-analysis/Agent chain or by a user override; runtime still validates
    // every immutable assembly and method identity before applying a patch.
    public sealed class HardcodedUiPatchManifest
    {
        public HardcodedUiPatchManifest()
        {
            ManifestVersion = 1;
            Entries = new List<HardcodedUiPatchEntry>();
        }

        [JsonProperty("manifestVersion")]
        public int ManifestVersion { get; set; }

        [JsonProperty("approved")]
        public bool Approved { get; set; }

        [JsonProperty("entries")]
        public List<HardcodedUiPatchEntry> Entries { get; set; }
    }

    public sealed class HardcodedUiPatchEntry
    {
        public HardcodedUiPatchEntry()
        {
            EntryId = string.Empty;
            PackageId = string.Empty;
            AssemblyRelativePath = string.Empty;
            AssemblySha256 = string.Empty;
            AssemblyMvid = string.Empty;
            DeclaringType = string.Empty;
            MethodName = string.Empty;
            MethodSignature = string.Empty;
            MethodMetadataToken = 0;
            MethodIlFingerprint = string.Empty;
            Literal = string.Empty;
            CallDeclaringType = string.Empty;
            CallMethodName = string.Empty;
            CallSignature = string.Empty;
            DiscoveryKind = string.Empty;
            Translations = new Dictionary<string, string>();
        }

        [JsonProperty("entryId")]
        public string EntryId { get; set; }

        [JsonProperty("enabled")]
        public bool Enabled { get; set; } = true;

        [JsonProperty("packageId")]
        public string PackageId { get; set; }

        [JsonProperty("assemblyRelativePath")]
        public string AssemblyRelativePath { get; set; }

        [JsonProperty("assemblySha256")]
        public string AssemblySha256 { get; set; }

        [JsonProperty("assemblyMvid")]
        public string AssemblyMvid { get; set; }

        [JsonProperty("declaringType")]
        public string DeclaringType { get; set; }

        [JsonProperty("methodName")]
        public string MethodName { get; set; }

        [JsonProperty("methodSignature")]
        public string MethodSignature { get; set; }

        [JsonProperty("methodMetadataToken")]
        public int MethodMetadataToken { get; set; }

        [JsonProperty("methodIlFingerprint")]
        public string MethodIlFingerprint { get; set; }

        [JsonProperty("literal")]
        public string Literal { get; set; }

        [JsonProperty("literalOrdinal")]
        public int LiteralOrdinal { get; set; }

        [JsonProperty("callDeclaringType")]
        public string CallDeclaringType { get; set; }

        [JsonProperty("callMethodName")]
        public string CallMethodName { get; set; }

        [JsonProperty("callSignature")]
        public string CallSignature { get; set; }

        [JsonProperty("discoveryKind")]
        public string DiscoveryKind { get; set; }

        [JsonProperty("translations")]
        public Dictionary<string, string> Translations { get; set; }
    }
}
