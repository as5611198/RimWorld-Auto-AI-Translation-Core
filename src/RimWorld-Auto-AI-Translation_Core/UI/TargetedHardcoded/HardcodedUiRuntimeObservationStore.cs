using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using RimWorld;
using Verse;
using AutoTranslator_Core.Workflow;

namespace AutoTranslator_Core.TargetedHardcodedUi
{
    public sealed class HardcodedUiRuntimeObservation
    {
        // Missing versions belong to the legacy pipeline, never implicitly to the current one.
        [JsonProperty("pipelineVersion")] public string PipelineVersion { get; set; } = string.Empty;
        [JsonProperty("packageId")] public string PackageId { get; set; } = string.Empty;
        [JsonProperty("assemblyRelativePath")] public string AssemblyRelativePath { get; set; } = string.Empty;
        [JsonProperty("assemblyMvid")] public string AssemblyMvid { get; set; } = string.Empty;
        [JsonProperty("declaringType")] public string DeclaringType { get; set; } = string.Empty;
        [JsonProperty("methodSignature")] public string MethodSignature { get; set; } = string.Empty;
        [JsonProperty("methodMetadataToken")] public int MethodMetadataToken { get; set; }
        [JsonProperty("sourceText")] public string SourceText { get; set; } = string.Empty;
        [JsonProperty("firstObservedUtc")] public DateTime FirstObservedUtc { get; set; }
        [JsonProperty("lastObservedUtc")] public DateTime LastObservedUtc { get; set; }
        [JsonProperty("observationCount")] public int ObservationCount { get; set; }
    }

    public static class HardcodedUiRuntimeObservationStore
    {
        private const int SchemaVersion = 1;
        private static readonly object Sync = new object();
        private static readonly object PersistenceSync = new object();
        private const int MaxStackInspectionsPerTextPerSession = 16;
        private static readonly Dictionary<string, int> TextOccurrenceCounts =
            new Dictionary<string, int>(StringComparer.Ordinal);
        private static Dictionary<string, HardcodedUiRuntimeObservation> _observations;
        private static int _currentObservationCount;
        private static bool _dirty;
        private static long _changeVersion;

        private sealed class ObservationDocument
        {
            [JsonProperty("schemaVersion")] public int Version { get; set; } = SchemaVersion;
            [JsonProperty("observations")] public List<HardcodedUiRuntimeObservation> Observations { get; set; } = new List<HardcodedUiRuntimeObservation>();
        }

        private static string FilePath => Path.Combine(
            AutoTranslatorScanner.GetLocalPackPath(), "HardcodedUiRuntimeObservations.v1.json");

        public static void BeginSession()
        {
            lock (Sync) TextOccurrenceCounts.Clear();
        }

        public static bool ShouldInspectStack(string sourceText)
        {
            if (string.IsNullOrWhiteSpace(sourceText)) return false;
            lock (Sync)
            {
                TextOccurrenceCounts.TryGetValue(sourceText, out int previous);
                int occurrence = previous < int.MaxValue ? previous + 1 : int.MaxValue;
                TextOccurrenceCounts[sourceText] = occurrence;
                int lastSample = 1 << (MaxStackInspectionsPerTextPerSession - 1);
                return occurrence <= lastSample && (occurrence & (occurrence - 1)) == 0;
            }
        }

        public static void Record(string packageId, string assemblyRelativePath, MethodBase method, string sourceText)
        {
            if (method == null || string.IsNullOrWhiteSpace(packageId) || string.IsNullOrWhiteSpace(sourceText)) return;
            EnsureLoaded();
            string mvid;
            int token;
            try { mvid = method.Module.ModuleVersionId.ToString("D"); } catch { mvid = string.Empty; }
            try { token = method.MetadataToken; } catch { token = 0; }
            var observation = new HardcodedUiRuntimeObservation
            {
                PipelineVersion = WorkflowIdentity.UiObservationPipelineVersion,
                PackageId = packageId.Trim().ToLowerInvariant(),
                AssemblyRelativePath = HardcodedUiMethodIdentity.NormalizeRelativePath(assemblyRelativePath),
                AssemblyMvid = mvid,
                DeclaringType = method.DeclaringType?.FullName ?? method.DeclaringType?.Name ?? string.Empty,
                MethodSignature = HardcodedUiMethodIdentity.GetMethodSignature(method),
                MethodMetadataToken = token,
                SourceText = sourceText
            };
            string key = CreateStorageKey(observation);
            DateTime now = DateTime.UtcNow;
            lock (Sync)
            {
                if (_observations.TryGetValue(key, out HardcodedUiRuntimeObservation existing))
                {
                    existing.LastObservedUtc = now;
                    existing.ObservationCount = Math.Max(1, existing.ObservationCount) + 1;
                }
                else
                {
                    observation.FirstObservedUtc = now;
                    observation.LastObservedUtc = now;
                    observation.ObservationCount = 1;
                    _observations[key] = observation;
                    _currentObservationCount++;
                }
                _dirty = true;
                _changeVersion++;
            }
        }

        public static bool HasObservation(HardcodedUiPatchEntry entry)
        {
            if (entry == null) return false;
            EnsureLoaded();
            lock (Sync) return _observations.ContainsKey(
                WorkflowIdentity.UiObservationPipelineVersion + "\n" + CreateKey(entry));
        }

        public static string CreateCandidateMatchKey(HardcodedUiPatchEntry entry)
        {
            return entry == null ? string.Empty : CreateKey(entry);
        }

        public static string GetFingerprint(string packageId)
        {
            return GetSnapshotFingerprint(GetObservationKeys(packageId));
        }

        internal static string GetSnapshotFingerprint(IEnumerable<string> keys)
        {
            string material = WorkflowIdentity.UiObservationPipelineVersion + "\n" +
                string.Join("\n", keys.OrderBy(key => key, StringComparer.Ordinal));
            return HardcodedUiMethodIdentity.ComputeSha256(material);
        }

        public static int GetObservationCount()
        {
            EnsureLoaded();
            lock (Sync) return _currentObservationCount;
        }

        internal static HashSet<string> GetObservationKeys(string packageId)
        {
            EnsureLoaded();
            string normalizedPackage = (packageId ?? string.Empty).Trim().ToLowerInvariant();
            lock (Sync)
            {
                return new HashSet<string>(_observations.Values
                    .Where(item => IsCurrentPipeline(item) && string.Equals(item.PackageId, normalizedPackage, StringComparison.OrdinalIgnoreCase))
                    .Select(CreateKey), StringComparer.Ordinal);
            }
        }

        public static void Flush()
        {
            lock (PersistenceSync)
            {
                EnsureLoaded();
                ObservationDocument document;
                long snapshotVersion;
                lock (Sync)
                {
                    if (!_dirty) return;
                    snapshotVersion = _changeVersion;
                    document = new ObservationDocument
                    {
                        Observations = _observations.Values
                            .OrderBy(item => item.PackageId, StringComparer.Ordinal)
                            .ThenBy(item => item.AssemblyRelativePath, StringComparer.Ordinal)
                            .ThenBy(item => item.MethodSignature, StringComparer.Ordinal)
                            .ThenBy(item => item.SourceText, StringComparer.Ordinal)
                            .Select(item => new HardcodedUiRuntimeObservation
                            {
                                PipelineVersion = item.PipelineVersion,
                                PackageId = item.PackageId,
                                AssemblyRelativePath = item.AssemblyRelativePath,
                                AssemblyMvid = item.AssemblyMvid,
                                DeclaringType = item.DeclaringType,
                                MethodSignature = item.MethodSignature,
                                MethodMetadataToken = item.MethodMetadataToken,
                                SourceText = item.SourceText,
                                FirstObservedUtc = item.FirstObservedUtc,
                                LastObservedUtc = item.LastObservedUtc,
                                ObservationCount = item.ObservationCount
                            })
                            .ToList()
                    };
                }
                try
                {
                    string path = FilePath;
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    string temporary = path + ".tmp";
                    File.WriteAllText(temporary, JsonConvert.SerializeObject(document, Formatting.Indented));
                    if (File.Exists(path)) File.Replace(temporary, path, null);
                    else File.Move(temporary, path);
                    lock (Sync)
                    {
                        if (_changeVersion == snapshotVersion) _dirty = false;
                    }
                }
                catch (Exception ex)
                {
                    AutoTranslatorSettings.AddWarningLog("UI 采集证据保存失败：" + ex.Message);
                }
            }
        }

        private static void EnsureLoaded()
        {
            lock (Sync)
            {
                if (_observations != null) return;
                _observations = new Dictionary<string, HardcodedUiRuntimeObservation>(StringComparer.Ordinal);
                try
                {
                    if (!File.Exists(FilePath)) return;
                    ObservationDocument document = JsonConvert.DeserializeObject<ObservationDocument>(File.ReadAllText(FilePath));
                    foreach (HardcodedUiRuntimeObservation item in document?.Observations ?? new List<HardcodedUiRuntimeObservation>())
                    {
                        if (item == null || string.IsNullOrWhiteSpace(item.PackageId) || string.IsNullOrWhiteSpace(item.SourceText)) continue;
                        _observations[CreateStorageKey(item)] = item;
                    }
                    _currentObservationCount = _observations.Values.Count(IsCurrentPipeline);
                    int outdatedCount = _observations.Count - _currentObservationCount;
                    if (outdatedCount > 0)
                        AutoTranslatorSettings.AddWarningLog(AutoTranslatorMod.WfText(
                            "UI 采集流水线已更新，旧版本证据不参与当前归类，请重新采集。保留的旧记录数：",
                            "UI observation pipeline updated; old evidence is excluded from classification. Recapture required. Retained old records: ") + outdatedCount);
                }
                catch (Exception ex)
                {
                    AutoTranslatorSettings.AddWarningLog("UI 采集证据读取失败：" + ex.Message);
                }
            }
        }

        private static bool IsCurrentPipeline(HardcodedUiRuntimeObservation item)
        {
            return string.Equals(item.PipelineVersion, WorkflowIdentity.UiObservationPipelineVersion, StringComparison.Ordinal);
        }

        private static string CreateStorageKey(HardcodedUiRuntimeObservation item)
        {
            return (item.PipelineVersion ?? string.Empty) + "\n" + CreateKey(item);
        }

        private static string CreateKey(HardcodedUiRuntimeObservation item)
        {
            return CreateKey(item.PackageId, item.AssemblyRelativePath, item.AssemblyMvid,
                item.DeclaringType, item.MethodSignature, item.MethodMetadataToken, item.SourceText);
        }

        private static string CreateKey(HardcodedUiPatchEntry entry)
        {
            return CreateKey(entry.PackageId, entry.AssemblyRelativePath, entry.AssemblyMvid,
                entry.DeclaringType, entry.MethodSignature, entry.MethodMetadataToken, entry.Literal);
        }

        private static string CreateKey(string packageId, string assemblyRelativePath, string assemblyMvid,
            string declaringType, string methodSignature, int methodMetadataToken, string sourceText)
        {
            return (packageId ?? string.Empty).Trim().ToLowerInvariant() + "|" +
                   HardcodedUiMethodIdentity.NormalizeRelativePath(assemblyRelativePath).ToLowerInvariant() + "|" +
                   (assemblyMvid ?? string.Empty).Trim().ToLowerInvariant() + "|" +
                   (declaringType ?? string.Empty) + "|" + (methodSignature ?? string.Empty) + "|" +
                   methodMetadataToken + "|" + (sourceText ?? string.Empty);
        }
    }

    public static class HardcodedUiCaptureSession
    {
        private static readonly object SessionSync = new object();
        private static bool _wasEnabled;
        private static long _deadlineUtcTicks;
        private static int _durationMinutes = -1;

        public static void NotifySettingsChanged(bool resetCountdown)
        {
            lock (SessionSync)
            {
                AutoTranslatorSettings settings = AutoTranslatorMod.Settings;
                bool enabled = settings != null && settings.EnableUIInterceptor;
                if (enabled && (resetCountdown || !_wasEnabled)) Start(settings.UIInterceptorAutoDisableMinutes);
                if (!enabled && _wasEnabled) EndCapture();
                _wasEnabled = enabled;
            }
        }

        public static void Update()
        {
            lock (SessionSync)
            {
                AutoTranslatorSettings settings = AutoTranslatorMod.Settings;
                bool enabled = settings != null && settings.EnableUIInterceptor;
                if (!enabled && _wasEnabled)
                {
                    _wasEnabled = false;
                    EndCapture();
                    return;
                }
                if (enabled && (!_wasEnabled || _durationMinutes != settings.UIInterceptorAutoDisableMinutes))
                    Start(settings.UIInterceptorAutoDisableMinutes);
                _wasEnabled = enabled;
                if (!enabled || _deadlineUtcTicks <= 0L || DateTime.UtcNow.Ticks < _deadlineUtcTicks) return;
                settings.EnableUIInterceptor = false;
                _wasEnabled = false;
                EndCapture();
                LoadedModManager.GetMod<AutoTranslatorMod>()?.WriteSettings();
                Messages.Message(AutoTranslatorMod.WfText(
                        "UI 采集已按时结束并保存证据；请重新运行 DLL 分析以应用采集结果。",
                        "UI capture ended on schedule and saved its evidence; rerun DLL analysis to apply it."),
                    MessageTypeDefOf.PositiveEvent, false);
            }
        }

        public static string GetDurationLabel()
        {
            int minutes = AutoTranslatorMod.Settings?.UIInterceptorAutoDisableMinutes ?? 5;
            return minutes == 0 ? AutoTranslatorMod.WfText("不设时限", "No time limit")
                : minutes + AutoTranslatorMod.WfText(" 分钟", " min");
        }

        private static void Start(int durationMinutes)
        {
            _durationMinutes = durationMinutes;
            _deadlineUtcTicks = durationMinutes == 0 ? 0L : DateTime.UtcNow.AddMinutes(durationMinutes).Ticks;
            HardcodedUiRuntimeObservationStore.BeginSession();
            UIInterceptor.ResetProbeCaches();
            AutoTranslatorSettings.AddLog("UI 采集已开启；流水线版本=" + WorkflowIdentity.UiObservationPipelineVersion +
                "；采集时限=" + GetDurationLabel());
        }

        private static void EndCapture()
        {
            _deadlineUtcTicks = 0L;
            HardcodedUiRuntimeObservationStore.Flush();
            AutoTranslatorSettings.AddLog("UI 采集已结束；已保存观察证据=" +
                HardcodedUiRuntimeObservationStore.GetObservationCount());
        }
    }
}
