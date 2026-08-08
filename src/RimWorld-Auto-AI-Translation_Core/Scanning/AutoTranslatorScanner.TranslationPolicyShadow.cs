using AutoTranslator_Core.TranslationPolicy;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using RimWorld;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Verse;

namespace AutoTranslator_Core
{
    public static partial class AutoTranslatorScanner
    {
        private const int TranslationPolicyShadowMaxErrorSamples = 100;
        private const int TranslationPolicyShadowMaxErrorTextLength = 500;

        private sealed class TranslationPolicyShadowModSnapshot
        {
            public string PackageId;
            public string ModName;
            public string RootDirectory;
            public List<string> EffectiveLanguageRoots = new List<string>();
            public List<string> KeyedSourceDirectories = new List<string>();
            public List<string> DefInjectedSourceDirectories = new List<string>();
            public List<string> RawDefsDirectories = new List<string>();
        }

        private sealed class TranslationPolicyShadowUiState
        {
            public bool IsRunning;
            public bool IsCancellationRequested;
            public bool IsSkipCurrentRequested;
            public string CurrentTaskName;
            public float CurrentProgress;
            public string SubTaskName;
            public float SubProgress;
        }

        private enum TranslationPolicyShadowControl
        {
            Continue,
            SkipCurrent,
            Cancel
        }

        private sealed class TranslationPolicyShadowSelectedModReport
        {
            [JsonProperty("packageId")]
            public string PackageId;

            [JsonProperty("modName")]
            public string ModName;
        }

        private sealed class TranslationPolicyShadowScanErrorReport
        {
            [JsonProperty("source")]
            public string Source;

            [JsonProperty("exceptionType")]
            public string ExceptionType;

            [JsonProperty("message")]
            public string Message;
        }

        private sealed class TranslationPolicyShadowAuditReport
        {
            [JsonProperty("reportVersion")]
            public int ReportVersion = 1;

            [JsonProperty("generatedUtc")]
            public string GeneratedUtc;

            [JsonProperty("targetLanguage")]
            public string TargetLanguage;

            [JsonProperty("selectedMods")]
            public List<TranslationPolicyShadowSelectedModReport> SelectedMods;

            [JsonProperty("scannedXmlCount")]
            public int ScannedXmlCount;

            [JsonProperty("scanErrors")]
            public int ScanErrors;

            [JsonProperty("scanErrorSamples")]
            public List<TranslationPolicyShadowScanErrorReport> ScanErrorSamples;

            [JsonProperty("scanErrorSamplesTruncated")]
            public bool ScanErrorSamplesTruncated;

            [JsonProperty("elapsedMilliseconds")]
            public long ElapsedMilliseconds;

            [JsonProperty("actualAiApiCalls")]
            public int ActualAiApiCalls;

            [JsonProperty("actualConsumedTokens")]
            public long ActualConsumedTokens;

            [JsonProperty("translationWrites")]
            public int TranslationWrites;

            [JsonProperty("runtimeInjections")]
            public int RuntimeInjections;

            [JsonProperty("policyResult")]
            public TranslationPolicyShadowResult PolicyResult;
        }

        public static void StartTranslationPolicyShadowRun(List<ModMetaData> mods)
        {
            ATC_Dispatcher.RunOnMainThread(() => StartTranslationPolicyShadowRunOnMainThread(mods));
        }

        private static void StartTranslationPolicyShadowRunOnMainThread(List<ModMetaData> mods)
        {
            if (AutoTranslatorMod.Settings == null || AutoTranslatorSettings.IsRunning) return;

            List<ModMetaData> selectedMods = (mods ?? new List<ModMetaData>())
                .Where(mod => mod != null)
                .GroupBy(
                    mod => (mod.PackageId ?? string.Empty) + "\n" + (mod.RootDir != null ? mod.RootDir.FullName : string.Empty),
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(mod => mod.PackageId ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(mod => mod.RootDir != null ? mod.RootDir.FullName : string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (selectedMods.Count == 0) return;

            TargetLanguage targetLanguage = AutoTranslatorMod.Settings.TargetLang;
            DateTime generatedUtc = DateTime.UtcNow;
            List<TranslationPolicyShadowModSnapshot> snapshots = selectedMods
                .Select(CreateTranslationPolicyShadowSnapshot)
                .ToList();
            string reportFileName = "translation-policy-shadow-" +
                generatedUtc.ToString("yyyyMMdd'T'HHmmssfffffff'Z'", CultureInfo.InvariantCulture) +
                ".json";
            string reportPath = Path.Combine(
                GetLocalPackPath(),
                "Reports",
                "TranslationPolicyShadow",
                reportFileName);

            TranslationPolicyShadowUiState previousState = CaptureTranslationPolicyShadowUiState();
            AutoTranslatorSettings.IsRunning = true;
            AutoTranslatorSettings.IsCancellationRequested = false;
            AutoTranslatorSettings.IsSkipCurrentRequested = false;
            AutoTranslatorMod.Settings.CurrentTaskName = "ATC_PolicyShadowRun_Task".Translate().ToString();
            AutoTranslatorMod.Settings.CurrentProgress = 0f;
            AutoTranslatorMod.Settings.SubTaskName = string.Empty;
            AutoTranslatorMod.Settings.SubProgress = 0f;
            AutoTranslatorSettings.AddLog("ATC_PolicyShadowRun_Start".Translate(snapshots.Count));

            Task.Run(() => ExecuteTranslationPolicyShadowRun(
                snapshots,
                targetLanguage,
                generatedUtc,
                reportPath,
                previousState));
        }

        private static TranslationPolicyShadowModSnapshot CreateTranslationPolicyShadowSnapshot(ModMetaData mod)
        {
            return new TranslationPolicyShadowModSnapshot
            {
                PackageId = mod.PackageId ?? string.Empty,
                ModName = mod.Name ?? string.Empty,
                RootDirectory = mod.RootDir != null ? mod.RootDir.FullName : string.Empty
            };
        }

        private static TranslationPolicyShadowControl DiscoverTranslationPolicyShadowPaths(
            TranslationPolicyShadowModSnapshot snapshot,
            TargetLanguage targetLanguage)
        {
            TranslationPolicyShadowControl control = GetTranslationPolicyShadowControl();
            if (control != TranslationPolicyShadowControl.Continue) return control;

            snapshot.EffectiveLanguageRoots = NormalizeTranslationPolicyShadowPaths(
                GetAllEffectiveLangPaths(snapshot.PackageId, snapshot.RootDirectory));
            control = GetTranslationPolicyShadowControl();
            if (control != TranslationPolicyShadowControl.Continue) return control;

            snapshot.RawDefsDirectories = NormalizeTranslationPolicyShadowPaths(
                GetAllEffectiveDefsPaths(snapshot.PackageId, snapshot.RootDirectory));
            control = GetTranslationPolicyShadowControl();
            if (control != TranslationPolicyShadowControl.Continue) return control;

            foreach (string languageRoot in snapshot.EffectiveLanguageRoots)
            {
                snapshot.KeyedSourceDirectories.AddRange(
                    GetTranslatableLanguageBucketPaths(languageRoot, targetLanguage, "Keyed", false));
                control = GetTranslationPolicyShadowControl();
                if (control != TranslationPolicyShadowControl.Continue) return control;

                snapshot.DefInjectedSourceDirectories.AddRange(
                    GetTranslatableLanguageBucketPaths(languageRoot, targetLanguage, "DefInjected", false));
                control = GetTranslationPolicyShadowControl();
                if (control != TranslationPolicyShadowControl.Continue) return control;
            }

            snapshot.KeyedSourceDirectories = NormalizeTranslationPolicyShadowPaths(snapshot.KeyedSourceDirectories);
            snapshot.DefInjectedSourceDirectories = NormalizeTranslationPolicyShadowPaths(snapshot.DefInjectedSourceDirectories);
            return GetTranslationPolicyShadowControl();
        }

        private static List<string> NormalizeTranslationPolicyShadowPaths(IEnumerable<string> paths)
        {
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<string> result = new List<string>();
            foreach (string path in paths ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(path)) continue;
                string fullPath = Path.GetFullPath(path);
                if (seen.Add(fullPath)) result.Add(fullPath);
            }

            return result;
        }

        private static TranslationPolicyShadowUiState CaptureTranslationPolicyShadowUiState()
        {
            return new TranslationPolicyShadowUiState
            {
                IsRunning = AutoTranslatorSettings.IsRunning,
                IsCancellationRequested = AutoTranslatorSettings.IsCancellationRequested,
                IsSkipCurrentRequested = AutoTranslatorSettings.IsSkipCurrentRequested,
                CurrentTaskName = AutoTranslatorMod.Settings.CurrentTaskName,
                CurrentProgress = AutoTranslatorMod.Settings.CurrentProgress,
                SubTaskName = AutoTranslatorMod.Settings.SubTaskName,
                SubProgress = AutoTranslatorMod.Settings.SubProgress
            };
        }

        private static void ExecuteTranslationPolicyShadowRun(
            List<TranslationPolicyShadowModSnapshot> snapshots,
            TargetLanguage targetLanguage,
            DateTime generatedUtc,
            string reportPath,
            TranslationPolicyShadowUiState previousState)
        {
            Stopwatch timer = Stopwatch.StartNew();
            TranslationPolicyShadowResult result = null;
            Exception failure = null;
            int scannedXmlCount = 0;
            int scanErrorCount = 0;
            List<TranslationPolicyShadowScanErrorReport> scanErrorSamples =
                new List<TranslationPolicyShadowScanErrorReport>();
            bool cancelled = false;
            bool reportWritten = false;

            try
            {
                TranslationPolicyShadowOptions options = new TranslationPolicyShadowOptions
                {
                    MaxSamplesPerGroup = 5
                };
                TranslationPolicyShadowSession session = CreateTranslationPolicyShadowSession(options);

                for (int index = 0; index < snapshots.Count; index++)
                {
                    TranslationPolicyShadowModSnapshot snapshot = snapshots[index];
                    PostTranslationPolicyShadowProgress(
                        snapshot.ModName,
                        (float)index / snapshots.Count,
                        0f);

                    TranslationPolicyShadowControl control = DiscoverTranslationPolicyShadowPaths(
                        snapshot,
                        targetLanguage);
                    HashSet<string> acceptedTargetKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    List<TranslationPolicyCandidate> acceptedCandidates = new List<TranslationPolicyCandidate>();

                    if (control == TranslationPolicyShadowControl.Continue)
                    {
                        control = ScanTranslationPolicyShadowDirectories(
                            snapshot,
                            snapshot.KeyedSourceDirectories,
                            TranslationPolicyBucket.Keyed,
                            acceptedTargetKeys,
                            acceptedCandidates,
                            ref scannedXmlCount,
                            ref scanErrorCount,
                            scanErrorSamples);
                    }
                    if (control == TranslationPolicyShadowControl.Continue)
                    {
                        PostTranslationPolicyShadowProgress(
                            snapshot.ModName,
                            (float)index / snapshots.Count,
                            1f / 3f);
                        control = ScanTranslationPolicyShadowDirectories(
                            snapshot,
                            snapshot.DefInjectedSourceDirectories,
                            TranslationPolicyBucket.DefInjected,
                            acceptedTargetKeys,
                            acceptedCandidates,
                            ref scannedXmlCount,
                            ref scanErrorCount,
                            scanErrorSamples);
                    }
                    if (control == TranslationPolicyShadowControl.Continue)
                    {
                        PostTranslationPolicyShadowProgress(
                            snapshot.ModName,
                            (float)index / snapshots.Count,
                            2f / 3f);
                        control = ScanTranslationPolicyShadowDirectories(
                            snapshot,
                            snapshot.RawDefsDirectories,
                            null,
                            acceptedTargetKeys,
                            acceptedCandidates,
                            ref scannedXmlCount,
                            ref scanErrorCount,
                            scanErrorSamples);
                    }

                    if (control == TranslationPolicyShadowControl.Continue)
                    {
                        foreach (TranslationPolicyCandidate candidate in acceptedCandidates)
                        {
                            session.AddCandidate(candidate);
                        }
                        control = GetTranslationPolicyShadowControl();
                    }

                    if (control == TranslationPolicyShadowControl.Cancel)
                    {
                        cancelled = true;
                        break;
                    }

                    if (control == TranslationPolicyShadowControl.SkipCurrent)
                    {
                        AutoTranslatorSettings.IsSkipCurrentRequested = false;
                    }

                    int completedMods = index + 1;
                    PostTranslationPolicyShadowProgress(
                        snapshot.ModName,
                        (float)completedMods / snapshots.Count,
                        1f);
                }

                if (!cancelled && !AutoTranslatorSettings.IsCancellationRequested)
                {
                    result = CompleteTranslationPolicyShadowSession(session);
                    if (!AutoTranslatorSettings.IsCancellationRequested)
                    {
                        WriteTranslationPolicyShadowReport(
                            reportPath,
                            snapshots,
                            targetLanguage,
                            generatedUtc,
                            scannedXmlCount,
                            scanErrorCount,
                            scanErrorSamples,
                            timer.Elapsed,
                            result);
                        reportWritten = true;
                    }
                    else
                    {
                        cancelled = true;
                    }
                }
                else
                {
                    cancelled = true;
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                timer.Stop();
                cancelled = !reportWritten && (cancelled || AutoTranslatorSettings.IsCancellationRequested);
                PostTranslationPolicyShadowCompletion(
                    previousState,
                    snapshots.Count,
                    reportPath,
                    result,
                    timer.Elapsed,
                    scanErrorCount,
                    cancelled,
                    failure);
            }
        }

        private static TranslationPolicyShadowSession CreateTranslationPolicyShadowSession(
            TranslationPolicyShadowOptions options)
        {
            return new TranslationPolicyShadowSession(options);
        }

        private static TranslationPolicyShadowResult CompleteTranslationPolicyShadowSession(
            TranslationPolicyShadowSession session)
        {
            return session.Complete();
        }

        private static TranslationPolicyShadowControl GetTranslationPolicyShadowControl()
        {
            if (AutoTranslatorSettings.IsCancellationRequested)
                return TranslationPolicyShadowControl.Cancel;
            if (AutoTranslatorSettings.IsSkipCurrentRequested)
                return TranslationPolicyShadowControl.SkipCurrent;
            return TranslationPolicyShadowControl.Continue;
        }

        private static void PostTranslationPolicyShadowProgress(
            string modName,
            float currentProgress,
            float subProgress)
        {
            ATC_Dispatcher.RunOnMainThread(() =>
            {
                if (AutoTranslatorMod.Settings == null) return;
                AutoTranslatorMod.Settings.CurrentProgress = currentProgress;
                AutoTranslatorMod.Settings.SubProgress = subProgress;
                AutoTranslatorMod.Settings.SubTaskName = modName ?? string.Empty;
            });
        }

        private static TranslationPolicyShadowControl ScanTranslationPolicyShadowDirectories(
            TranslationPolicyShadowModSnapshot snapshot,
            IEnumerable<string> directories,
            TranslationPolicyBucket? bucket,
            HashSet<string> acceptedTargetKeys,
            List<TranslationPolicyCandidate> acceptedCandidates,
            ref int scannedXmlCount,
            ref int scanErrorCount,
            List<TranslationPolicyShadowScanErrorReport> scanErrorSamples)
        {
            TranslationPolicyShadowControl control;
            List<string> files = EnumerateTranslationPolicyShadowXmlFiles(
                snapshot,
                directories,
                ref scanErrorCount,
                scanErrorSamples,
                out control);
            if (control != TranslationPolicyShadowControl.Continue) return control;

            foreach (string file in files)
            {
                control = GetTranslationPolicyShadowControl();
                if (control != TranslationPolicyShadowControl.Continue) return control;

                List<TranslationPolicyCandidate> candidates;
                try
                {
                    string xml = File.ReadAllText(file);
                    TranslationPolicySourceContext context = new TranslationPolicySourceContext
                    {
                        PackageId = snapshot.PackageId,
                        ModName = snapshot.ModName,
                        SourceFile = GetTranslationPolicyShadowRelativePath(snapshot.RootDirectory, file),
                        DeclaringAssembly = string.Empty,
                        SchemaFingerprint = string.Empty
                    };

                    if (bucket == TranslationPolicyBucket.Keyed)
                    {
                        candidates = TranslationPolicyXmlScanner.ScanKeyedXml(xml, context);
                    }
                    else if (bucket == TranslationPolicyBucket.DefInjected)
                    {
                        string defType = GetTranslationPolicyShadowDefType(directories, file);
                        candidates = TranslationPolicyXmlScanner.ScanDefInjectedXml(xml, defType, context);
                    }
                    else
                    {
                        candidates = TranslationPolicyXmlScanner.ScanDefsXml(xml, context);
                    }
                    scannedXmlCount++;
                }
                catch (Exception ex)
                {
                    scanErrorCount++;
                    AddTranslationPolicyShadowScanError(
                        scanErrorSamples,
                        GetTranslationPolicyShadowRelativePath(snapshot.RootDirectory, file),
                        ex);
                    continue;
                }

                foreach (TranslationPolicyCandidate candidate in candidates)
                {
                    control = GetTranslationPolicyShadowControl();
                    if (control != TranslationPolicyShadowControl.Continue) return control;

                    string targetKey = CreateTranslationPolicyShadowTargetKey(candidate);
                    if (!acceptedTargetKeys.Add(targetKey)) continue;
                    acceptedCandidates.Add(candidate);
                }
            }

            return TranslationPolicyShadowControl.Continue;
        }

        private static List<string> EnumerateTranslationPolicyShadowXmlFiles(
            TranslationPolicyShadowModSnapshot snapshot,
            IEnumerable<string> directories,
            ref int scanErrorCount,
            List<TranslationPolicyShadowScanErrorReport> scanErrorSamples,
            out TranslationPolicyShadowControl control)
        {
            HashSet<string> files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<string> orderedFiles = new List<string>();
            control = GetTranslationPolicyShadowControl();
            if (control != TranslationPolicyShadowControl.Continue) return orderedFiles;

            foreach (string directory in (directories ?? Enumerable.Empty<string>())
                .Where(path => !string.IsNullOrWhiteSpace(path)))
            {
                control = GetTranslationPolicyShadowControl();
                if (control != TranslationPolicyShadowControl.Continue) return orderedFiles;
                if (!Directory.Exists(directory)) continue;

                List<string> directoryFiles = new List<string>();
                try
                {
                    foreach (string file in Directory.EnumerateFiles(directory, "*.xml", SearchOption.AllDirectories))
                    {
                        control = GetTranslationPolicyShadowControl();
                        if (control != TranslationPolicyShadowControl.Continue) return orderedFiles;
                        directoryFiles.Add(Path.GetFullPath(file));
                    }
                }
                catch (Exception ex)
                {
                    scanErrorCount++;
                    AddTranslationPolicyShadowScanError(
                        scanErrorSamples,
                        "directory:" + GetTranslationPolicyShadowRelativePath(snapshot.RootDirectory, directory),
                        ex);
                    directoryFiles.Clear();
                }

                directoryFiles.Sort(StringComparer.OrdinalIgnoreCase);
                foreach (string file in directoryFiles)
                {
                    control = GetTranslationPolicyShadowControl();
                    if (control != TranslationPolicyShadowControl.Continue) return orderedFiles;
                    if (files.Add(file)) orderedFiles.Add(file);
                }
            }

            control = TranslationPolicyShadowControl.Continue;
            return orderedFiles;
        }

        private static void AddTranslationPolicyShadowScanError(
            List<TranslationPolicyShadowScanErrorReport> samples,
            string source,
            Exception exception)
        {
            if (samples == null || samples.Count >= TranslationPolicyShadowMaxErrorSamples) return;

            Exception safeException = exception != null ? exception.GetBaseException() : null;
            samples.Add(new TranslationPolicyShadowScanErrorReport
            {
                Source = TruncateTranslationPolicyShadowErrorText(source),
                ExceptionType = safeException != null
                    ? TruncateTranslationPolicyShadowErrorText(safeException.GetType().FullName)
                    : "UnknownException",
                Message = GetTranslationPolicyShadowSafeErrorMessage(safeException)
            });
        }

        private static string GetTranslationPolicyShadowSafeErrorMessage(Exception exception)
        {
            if (exception == null) return "Unknown scan error.";

            string message = (exception.Message ?? string.Empty)
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Trim();
            if (ContainsTranslationPolicyShadowAbsolutePath(message))
            {
                return "The input path could not be read.";
            }

            return TruncateTranslationPolicyShadowErrorText(message);
        }

        private static bool ContainsTranslationPolicyShadowAbsolutePath(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            if (value.IndexOf(@"\\", StringComparison.Ordinal) >= 0) return true;

            for (int index = 0; index + 2 < value.Length; index++)
            {
                if (char.IsLetter(value[index]) &&
                    value[index + 1] == ':' &&
                    (value[index + 2] == '\\' || value[index + 2] == '/'))
                {
                    return true;
                }
            }

            return false;
        }

        private static string TruncateTranslationPolicyShadowErrorText(string value)
        {
            string safeValue = value ?? string.Empty;
            return safeValue.Length <= TranslationPolicyShadowMaxErrorTextLength
                ? safeValue
                : safeValue.Substring(0, TranslationPolicyShadowMaxErrorTextLength);
        }

        private static string GetTranslationPolicyShadowDefType(IEnumerable<string> roots, string file)
        {
            string fullFile = Path.GetFullPath(file);
            foreach (string root in (roots ?? Enumerable.Empty<string>())
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .OrderByDescending(path => path.Length))
            {
                string fullRoot = Path.GetFullPath(root)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string prefix = fullRoot + Path.DirectorySeparatorChar;
                if (!fullFile.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

                string relative = fullFile.Substring(prefix.Length);
                int separator = relative.IndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar });
                return separator > 0 ? relative.Substring(0, separator) : "General";
            }

            return "General";
        }

        private static string GetTranslationPolicyShadowRelativePath(string modRoot, string file)
        {
            string fullFile = Path.GetFullPath(file);
            if (!string.IsNullOrWhiteSpace(modRoot))
            {
                string fullRoot = Path.GetFullPath(modRoot)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string prefix = fullRoot + Path.DirectorySeparatorChar;
                if (fullFile.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return fullFile.Substring(prefix.Length).Replace('\\', '/');
                }
            }

            return Path.GetFileName(fullFile).Replace('\\', '/');
        }

        private static string CreateTranslationPolicyShadowTargetKey(TranslationPolicyCandidate candidate)
        {
            return string.Join(
                "|",
                ((int)candidate.Bucket).ToString(CultureInfo.InvariantCulture),
                (candidate.DefType ?? string.Empty).Trim().ToLowerInvariant(),
                (candidate.KeyOrPath ?? string.Empty).Trim().Replace('\\', '/').ToLowerInvariant());
        }

        private static void WriteTranslationPolicyShadowReport(
            string reportPath,
            List<TranslationPolicyShadowModSnapshot> snapshots,
            TargetLanguage targetLanguage,
            DateTime generatedUtc,
            int scannedXmlCount,
            int scanErrorCount,
            List<TranslationPolicyShadowScanErrorReport> scanErrorSamples,
            TimeSpan elapsed,
            TranslationPolicyShadowResult result)
        {
            if (result == null) throw new InvalidDataException("Translation policy shadow result is empty.");

            string fullReportPath = Path.GetFullPath(reportPath);
            string directory = Path.GetDirectoryName(fullReportPath);
            if (string.IsNullOrWhiteSpace(directory))
                throw new InvalidDataException("Translation policy shadow report directory is empty.");

            TranslationPolicyShadowAuditReport report = new TranslationPolicyShadowAuditReport
            {
                GeneratedUtc = generatedUtc.ToString("o", CultureInfo.InvariantCulture),
                TargetLanguage = targetLanguage.ToString(),
                SelectedMods = snapshots
                    .Select(snapshot => new TranslationPolicyShadowSelectedModReport
                    {
                        PackageId = snapshot.PackageId,
                        ModName = snapshot.ModName
                    })
                    .ToList(),
                ScannedXmlCount = scannedXmlCount,
                ScanErrors = scanErrorCount,
                ScanErrorSamples = (scanErrorSamples ?? new List<TranslationPolicyShadowScanErrorReport>())
                    .Take(TranslationPolicyShadowMaxErrorSamples)
                    .ToList(),
                ScanErrorSamplesTruncated = scanErrorCount >
                    (scanErrorSamples != null ? scanErrorSamples.Count : 0),
                ElapsedMilliseconds = Math.Max(0L, (long)elapsed.TotalMilliseconds),
                ActualAiApiCalls = 0,
                ActualConsumedTokens = 0L,
                TranslationWrites = 0,
                RuntimeInjections = 0,
                PolicyResult = result
            };

            Directory.CreateDirectory(directory);
            string temporaryPath = fullReportPath + ".tmp";
            JsonSerializer serializer = JsonSerializer.Create(new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Include,
                Converters = new List<JsonConverter> { new StringEnumConverter() }
            });

            try
            {
                using (StreamWriter writer = new StreamWriter(temporaryPath, false, new UTF8Encoding(false)))
                using (JsonTextWriter jsonWriter = new JsonTextWriter(writer) { Formatting = Formatting.Indented })
                {
                    serializer.Serialize(jsonWriter, report);
                }

                if (File.Exists(fullReportPath))
                    throw new IOException("Translation policy shadow report already exists: " + fullReportPath);
                File.Move(temporaryPath, fullReportPath);
            }
            catch
            {
                try
                {
                    if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                }
                catch
                {
                }

                throw;
            }
        }

        private static void PostTranslationPolicyShadowCompletion(
            TranslationPolicyShadowUiState previousState,
            int modCount,
            string reportPath,
            TranslationPolicyShadowResult result,
            TimeSpan elapsed,
            int scanErrorCount,
            bool cancelled,
            Exception failure)
        {
            ATC_Dispatcher.RunOnMainThread(() =>
            {
                RestoreTranslationPolicyShadowUiState(previousState);

                if (cancelled)
                {
                    AutoTranslatorSettings.AddLog("ATC_CancelRequested".Translate());
                    return;
                }

                if (failure != null || result == null)
                {
                    string message = "ATC_Log_TaskError".Translate(failure != null ? failure.Message : "No result").ToString();
                    AutoTranslatorSettings.AddErrorLog(message);
                    Find.WindowStack.Add(new Dialog_MessageBox(
                        message,
                        null,
                        null,
                        null,
                        null,
                        "ATC_PolicyShadowRun_Title".Translate()));
                    return;
                }

                if (scanErrorCount > 0)
                {
                    AutoTranslatorSettings.AddLog(
                        "[TranslationPolicyShadow] Warning: skipped unreadable XML inputs: " +
                        scanErrorCount.ToString(CultureInfo.InvariantCulture));
                }

                TranslationPolicySummary summary = result.Summary ?? new TranslationPolicySummary();
                TranslationPolicyTokenEstimate estimate = result.Estimate ?? new TranslationPolicyTokenEstimate();
                string elapsedText = elapsed.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture) + "s";
                string summaryText = "ATC_PolicyShadowRun_Summary".Translate(
                    modCount,
                    summary.TotalCandidates,
                    summary.HardAllowCount,
                    summary.HardDenyCount,
                    summary.AmbiguousCount,
                    summary.AmbiguousGroupCount,
                    estimate.EstimatedRequestCount,
                    estimate.EstimatedInputTokens,
                    estimate.EstimatedOutputTokens,
                    estimate.EstimatedTotalTokens,
                    elapsedText,
                    reportPath).ToString();

                AutoTranslatorSettings.AddLog(summaryText);
                Find.WindowStack.Add(new Dialog_MessageBox(
                    summaryText,
                    null,
                    null,
                    null,
                    null,
                    "ATC_PolicyShadowRun_Title".Translate()));
            });
        }

        private static void RestoreTranslationPolicyShadowUiState(TranslationPolicyShadowUiState state)
        {
            if (state == null) return;

            AutoTranslatorSettings.IsRunning = state.IsRunning;
            AutoTranslatorSettings.IsCancellationRequested = state.IsCancellationRequested;
            AutoTranslatorSettings.IsSkipCurrentRequested = state.IsSkipCurrentRequested;
            if (AutoTranslatorMod.Settings == null) return;

            AutoTranslatorMod.Settings.CurrentTaskName = state.CurrentTaskName;
            AutoTranslatorMod.Settings.CurrentProgress = state.CurrentProgress;
            AutoTranslatorMod.Settings.SubTaskName = state.SubTaskName;
            AutoTranslatorMod.Settings.SubProgress = state.SubProgress;
        }
    }
}
