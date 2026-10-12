using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using Verse;

namespace AutoTranslator_Core
{
    public static partial class AutoTranslatorScanner
    {
        private const int MaxStaticTranslationRefreshFieldsPerPump = 32;
        private const int MaxStaticTranslationRefreshAssembliesPerPump = 1;
        private static readonly object _staticTranslationRefreshLock = new object();
        private static Queue<StaticTranslationRefreshCandidate> _staticTranslationRefreshQueue;
        private static Queue<Assembly> _staticTranslationAssemblyScanQueue;
        private static readonly Dictionary<string, string> _staticTranslationTargetLookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> _staticTranslationTargetHints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> _staticTranslationSourceLookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> _staticTranslationSourceHints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static int _staticTranslationRefreshGeneration = 0;

        private struct StaticTranslationRefreshCandidate
        {
            public FieldInfo Field;
            public string Key;
        }

        private static void RequestStaticCachedTranslationRefresh()
        {
            lock (_pendingInjectLock)
            {
                _pendingStaticCachedTranslationRefresh = true;
            }
        }

        private static void RememberStaticTranslationTargetHint(string key, string value)
        {
            if (!IsUsableStaticTranslationTarget(key, value)) return;

            lock (_staticTranslationRefreshLock)
            {
                _staticTranslationTargetHints[key] = value;
            }
        }

        private static void RememberStaticTranslationSourceHint(string key, string value)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value)) return;
            if (!LooksLikeModTranslationKey(key)) return;
            if (LanguageDetector.LooksLikeTargetLanguage(value, AutoTranslatorMod.Settings.TargetLang)) return;

            lock (_staticTranslationRefreshLock)
            {
                _staticTranslationSourceHints[key] = value;
            }
        }

        private static void PumpStaticCachedTranslationRefresh()
        {
            bool shouldStart = false;
            lock (_pendingInjectLock)
            {
                if (_pendingStaticCachedTranslationRefresh)
                {
                    _pendingStaticCachedTranslationRefresh = false;
                    shouldStart = true;
                }
            }

            if (shouldStart)
            {
                BeginStaticCachedTranslationRefresh();
            }

            ProcessStaticCachedTranslationAssemblyScanQueue(MaxStaticTranslationRefreshAssembliesPerPump);
            ProcessStaticCachedTranslationRefreshQueue(MaxStaticTranslationRefreshFieldsPerPump);
        }

        private static void BeginStaticCachedTranslationRefresh()
        {
            LoadedLanguage activeLang = LanguageDatabase.activeLanguage;
            if (activeLang == null) return;

            Dictionary<string, string> targetLookup = BuildStaticTranslationTargetLookup(activeLang);
            if (targetLookup.Count == 0) return;

            Dictionary<string, string> sourceLookup;
            lock (_staticTranslationRefreshLock)
            {
                sourceLookup = new Dictionary<string, string>(_staticTranslationSourceHints, StringComparer.OrdinalIgnoreCase);
            }
            HashSet<string> keyPrefixes = BuildStaticTranslationKeyPrefixSet(targetLookup.Keys);

            Queue<Assembly> assemblyQueue = new Queue<Assembly>(
                AppDomain.CurrentDomain.GetAssemblies()
                    .Where(ShouldScanAssemblyForStaticTranslationRefresh)
                    .Where(a => MayAssemblyContainStaticTranslationKeys(a, keyPrefixes))
                    .OrderByDescending(IsHighPriorityStaticTranslationAssembly)
                    .ThenBy(a => a.GetName().Name, StringComparer.OrdinalIgnoreCase));

            lock (_staticTranslationRefreshLock)
            {
                _staticTranslationTargetLookup.Clear();
                foreach (var kvp in targetLookup)
                {
                    _staticTranslationTargetLookup[kvp.Key] = kvp.Value;
                }

                _staticTranslationSourceLookup.Clear();
                foreach (var kvp in sourceLookup)
                {
                    _staticTranslationSourceLookup[kvp.Key] = kvp.Value;
                }

                _staticTranslationAssemblyScanQueue = assemblyQueue;
                _staticTranslationRefreshQueue = new Queue<StaticTranslationRefreshCandidate>();
                _staticTranslationRefreshGeneration++;
            }
        }

        private static void ProcessStaticCachedTranslationAssemblyScanQueue(int maxAssemblies)
        {
            int processed = 0;
            while (processed < maxAssemblies)
            {
                Assembly assembly;
                Dictionary<string, string> targetLookup;
                lock (_staticTranslationRefreshLock)
                {
                    if (_staticTranslationAssemblyScanQueue == null || _staticTranslationAssemblyScanQueue.Count == 0)
                    {
                        _staticTranslationAssemblyScanQueue = null;
                        return;
                    }

                    assembly = _staticTranslationAssemblyScanQueue.Dequeue();
                    targetLookup = new Dictionary<string, string>(_staticTranslationTargetLookup, StringComparer.OrdinalIgnoreCase);
                }

                processed++;
                Queue<StaticTranslationRefreshCandidate> candidates = BuildStaticRefreshCandidatesForAssembly(assembly, targetLookup);
                if (candidates.Count == 0) continue;

                lock (_staticTranslationRefreshLock)
                {
                    if (_staticTranslationRefreshQueue == null)
                    {
                        _staticTranslationRefreshQueue = new Queue<StaticTranslationRefreshCandidate>();
                    }

                    while (candidates.Count > 0)
                    {
                        _staticTranslationRefreshQueue.Enqueue(candidates.Dequeue());
                    }
                }
            }
        }

        private static Queue<StaticTranslationRefreshCandidate> BuildStaticRefreshCandidatesForAssembly(
            Assembly assembly,
            Dictionary<string, string> targetLookup)
        {
            Queue<StaticTranslationRefreshCandidate> queue = new Queue<StaticTranslationRefreshCandidate>();
            if (assembly == null || targetLookup == null || targetLookup.Count == 0) return queue;

            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t != null).ToArray();
            }
            catch
            {
                return queue;
            }

            foreach (Type type in types)
            {
                if (type == null || !HasStaticConstructorOnStartup(type)) continue;

                FieldInfo[] fields;
                try
                {
                    fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                }
                catch
                {
                    continue;
                }

                foreach (FieldInfo field in fields)
                {
                    if (field == null || field.FieldType != typeof(string) || field.IsLiteral || field.IsInitOnly) continue;
                    if (!TryFindStaticTranslationKey(field, targetLookup, out string key)) continue;

                    queue.Enqueue(new StaticTranslationRefreshCandidate
                    {
                        Field = field,
                        Key = key
                    });
                }
            }

            return queue;
        }

        private static void ProcessStaticCachedTranslationRefreshQueue(int maxFields)
        {
            Queue<StaticTranslationRefreshCandidate> queue;
            Dictionary<string, string> targetLookup;
            Dictionary<string, string> sourceLookup;
            lock (_staticTranslationRefreshLock)
            {
                queue = _staticTranslationRefreshQueue;
                if (queue == null || queue.Count == 0) return;
                targetLookup = new Dictionary<string, string>(_staticTranslationTargetLookup, StringComparer.OrdinalIgnoreCase);
                sourceLookup = new Dictionary<string, string>(_staticTranslationSourceLookup, StringComparer.OrdinalIgnoreCase);
            }

            int processed = 0;
            int refreshed = 0;
            while (processed < maxFields)
            {
                StaticTranslationRefreshCandidate candidate;
                lock (_staticTranslationRefreshLock)
                {
                    if (_staticTranslationRefreshQueue == null || _staticTranslationRefreshQueue.Count == 0)
                    {
                        _staticTranslationRefreshQueue = null;
                        break;
                    }

                    candidate = _staticTranslationRefreshQueue.Dequeue();
                }

                processed++;
                if (TryRefreshStaticTranslationField(candidate, targetLookup, sourceLookup))
                {
                    refreshed++;
                }
            }

            if (refreshed > 0)
            {
                Log.Message($"[AutoTranslationCore] Static cached translations refreshed: {refreshed}");
            }
        }

        private static bool TryRefreshStaticTranslationField(
            StaticTranslationRefreshCandidate candidate,
            Dictionary<string, string> targetLookup,
            Dictionary<string, string> sourceLookup)
        {
            if (candidate.Field == null || string.IsNullOrEmpty(candidate.Key)) return false;
            if (targetLookup == null || !targetLookup.TryGetValue(candidate.Key, out string translatedValue) || string.IsNullOrWhiteSpace(translatedValue)) return false;

            if (string.Equals(translatedValue, candidate.Key, StringComparison.Ordinal)) return false;

            string currentValue;
            try
            {
                currentValue = candidate.Field.GetValue(null) as string;
            }
            catch
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(currentValue)) return false;
            if (string.Equals(currentValue, translatedValue, StringComparison.Ordinal)) return false;

            bool currentLooksLikeTarget = LanguageDetector.LooksLikeTargetLanguage(currentValue, AutoTranslatorMod.Settings.TargetLang);
            bool currentLooksLikeKey = string.Equals(currentValue.Trim(), candidate.Key, StringComparison.OrdinalIgnoreCase);
            bool hasSourceHint = false;
            bool currentMatchesSource = false;
            if (sourceLookup != null && sourceLookup.TryGetValue(candidate.Key, out string sourceValue) && !string.IsNullOrWhiteSpace(sourceValue))
            {
                hasSourceHint = true;
                string normalizedCurrent = NormalizeStaticTranslationSourceText(currentValue);
                string normalizedSource = NormalizeStaticTranslationSourceText(sourceValue);
                currentMatchesSource =
                    string.Equals(normalizedCurrent, normalizedSource, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(currentValue.Trim(), sourceValue.Trim(), StringComparison.OrdinalIgnoreCase);
            }

            if (currentLooksLikeTarget) return false;

            if (!currentMatchesSource && !currentLooksLikeKey && (hasSourceHint || !LooksLikeRefreshableStaticSource(currentValue)))
            {
                return false;
            }

            try
            {
                candidate.Field.SetValue(null, translatedValue);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static Dictionary<string, string> BuildStaticTranslationTargetLookup(LoadedLanguage activeLang)
        {
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            lock (_staticTranslationRefreshLock)
            {
                foreach (var kvp in _staticTranslationTargetHints)
                {
                    result[kvp.Key] = kvp.Value;
                }
            }

            if (result.Count > 0) return result;
            if (activeLang?.keyedReplacements == null) return result;

            foreach (var kvp in activeLang.keyedReplacements)
            {
                string key = kvp.Key;
                if (string.IsNullOrWhiteSpace(key)) continue;
                if (!LooksLikeModTranslationKey(key)) continue;
                if (result.ContainsKey(key)) continue;

                string value = null;
                try
                {
                    value = kvp.Value.value;
                }
                catch
                {
                    value = null;
                }

                if (IsUsableStaticTranslationTarget(key, value))
                {
                    result[key] = value;
                }
            }

            return result;
        }

        private static bool IsUsableStaticTranslationTarget(string key, string value)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value)) return false;
            if (string.Equals(value.Trim(), key, StringComparison.Ordinal)) return false;
            if (LanguageDetector.LooksLikeTargetLanguage(value, AutoTranslatorMod.Settings.TargetLang)) return true;

            switch (AutoTranslatorMod.Settings.TargetLang)
            {
                case TargetLanguage.Traditional:
                case TargetLanguage.Simplified:
                    return CountChars(value, IsStaticHanChar) > 0;

                case TargetLanguage.Japanese:
                    return CountChars(value, IsStaticHanChar) + CountChars(value, IsStaticKanaChar) > 0;

                case TargetLanguage.Korean:
                    return CountChars(value, IsStaticHangulChar) > 0;

                case TargetLanguage.Russian:
                case TargetLanguage.Ukrainian:
                    return CountChars(value, IsStaticCyrillicChar) > 0;

                case TargetLanguage.Thai:
                    return CountChars(value, IsStaticThaiChar) > 0;

                default:
                    return false;
            }
        }

        private static bool TryFindStaticTranslationKey(FieldInfo field, Dictionary<string, string> sourceLookup, out string key)
        {
            key = null;
            if (field == null || sourceLookup == null || sourceLookup.Count == 0) return false;

            string fieldName = field.Name;
            if (string.IsNullOrWhiteSpace(fieldName)) return false;

            foreach (string candidate in BuildStaticTranslationKeyCandidates(field))
            {
                if (sourceLookup.ContainsKey(candidate))
                {
                    key = candidate;
                    return true;
                }
            }

            return false;
        }

        private static IEnumerable<string> BuildStaticTranslationKeyCandidates(FieldInfo field)
        {
            string fieldName = field.Name;
            Type declaringType = field.DeclaringType;
            string typeName = declaringType?.Name ?? string.Empty;
            string ns = declaringType?.Namespace ?? string.Empty;

            if (!string.IsNullOrEmpty(fieldName))
            {
                yield return fieldName;
            }

            if (!string.IsNullOrEmpty(typeName) && !string.IsNullOrEmpty(fieldName))
            {
                yield return typeName + "_" + fieldName;
            }

            string compactNs = GetCompactNamespaceTail(ns);
            if (!string.IsNullOrEmpty(compactNs) && !string.IsNullOrEmpty(fieldName))
            {
                yield return compactNs + "_" + fieldName;
            }

            string prefix = GuessStaticTranslationKeyPrefix(ns, typeName);
            if (!string.IsNullOrEmpty(prefix) && !string.IsNullOrEmpty(fieldName))
            {
                yield return prefix + "_" + fieldName;

                if (fieldName.StartsWith("Label", StringComparison.Ordinal))
                {
                    yield return prefix + "_" + fieldName.Substring("Label".Length) + "Label";
                }

                if (!fieldName.EndsWith("Query", StringComparison.Ordinal))
                {
                    yield return prefix + "_" + fieldName + "Query";
                }

                if (fieldName.StartsWith("ToolTip", StringComparison.Ordinal))
                {
                    yield return prefix + "_Tooltip" + fieldName.Substring("ToolTip".Length);
                    yield return prefix + "_ToolTip" + fieldName.Substring("ToolTip".Length);
                }

                if (fieldName.StartsWith("ToolTipTicks", StringComparison.Ordinal))
                {
                    yield return prefix + "_ToolTip" + fieldName.Substring("ToolTipTicks".Length);
                }

                if (fieldName.StartsWith("Description", StringComparison.Ordinal))
                {
                    yield return prefix + "_Description" + fieldName.Substring("Description".Length);
                }

                if (fieldName.StartsWith("Settings", StringComparison.Ordinal))
                {
                    yield return prefix + "_Settings" + fieldName.Substring("Settings".Length);
                }
            }
        }

        private static bool ShouldScanAssemblyForStaticTranslationRefresh(Assembly assembly)
        {
            if (assembly == null || assembly.IsDynamic) return false;

            string name = assembly.GetName().Name ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name)) return false;

            if (name.Equals("Assembly-CSharp", StringComparison.OrdinalIgnoreCase)) return false;
            if (name.StartsWith("System", StringComparison.OrdinalIgnoreCase)) return false;
            if (name.StartsWith("UnityEngine", StringComparison.OrdinalIgnoreCase)) return false;
            if (name.StartsWith("Unity.", StringComparison.OrdinalIgnoreCase)) return false;
            if (name.StartsWith("Mono.", StringComparison.OrdinalIgnoreCase)) return false;
            if (name.StartsWith("mscorlib", StringComparison.OrdinalIgnoreCase)) return false;
            if (name.StartsWith("netstandard", StringComparison.OrdinalIgnoreCase)) return false;
            if (name.StartsWith("Newtonsoft.Json", StringComparison.OrdinalIgnoreCase)) return false;
            if (name.StartsWith("0Harmony", StringComparison.OrdinalIgnoreCase)) return false;
            if (name.IndexOf("Auto_AI_Translation", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (name.IndexOf("AutoTranslation", StringComparison.OrdinalIgnoreCase) >= 0) return false;

            return true;
        }

        private static bool IsHighPriorityStaticTranslationAssembly(Assembly assembly)
        {
            if (assembly == null) return false;

            string name = assembly.GetName().Name ?? string.Empty;
            if (name.IndexOf("Quarry", StringComparison.OrdinalIgnoreCase) >= 0) return true;

            return false;
        }

        private static HashSet<string> BuildStaticTranslationKeyPrefixSet(IEnumerable<string> keys)
        {
            HashSet<string> result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (keys == null) return result;

            foreach (string key in keys)
            {
                if (string.IsNullOrWhiteSpace(key)) continue;
                int index = key.IndexOf('_');
                if (index <= 0 || index > 12) continue;
                result.Add(key.Substring(0, index));
            }

            return result;
        }

        private static bool MayAssemblyContainStaticTranslationKeys(Assembly assembly, HashSet<string> keyPrefixes)
        {
            if (assembly == null) return false;
            if (IsHighPriorityStaticTranslationAssembly(assembly)) return true;
            if (keyPrefixes == null || keyPrefixes.Count == 0) return false;

            string name = assembly.GetName().Name ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name)) return false;

            foreach (string prefix in BuildAssemblyStaticTranslationPrefixCandidates(name))
            {
                if (keyPrefixes.Contains(prefix)) return true;
            }

            return false;
        }

        private static IEnumerable<string> BuildAssemblyStaticTranslationPrefixCandidates(string assemblyName)
        {
            if (string.IsNullOrWhiteSpace(assemblyName)) yield break;

            string compact = new string(assemblyName.Where(char.IsLetterOrDigit).ToArray());
            if (string.IsNullOrWhiteSpace(compact)) yield break;

            string guessed = GuessStaticTranslationKeyPrefix(string.Empty, compact);
            if (!string.IsNullOrWhiteSpace(guessed)) yield return guessed;

            string uppercase = new string(compact.Where(char.IsUpper).ToArray());
            if (uppercase.Length >= 2 && uppercase.Length <= 8) yield return uppercase;

            string letters = new string(compact.Where(char.IsLetter).ToArray()).ToUpperInvariant();
            if (letters.Length >= 2)
            {
                yield return letters.Substring(0, Math.Min(3, letters.Length));
                yield return letters.Substring(0, Math.Min(4, letters.Length));
            }
        }

        private static bool HasStaticConstructorOnStartup(Type type)
        {
            try
            {
                return type.GetCustomAttributes(typeof(StaticConstructorOnStartup), false).Length > 0;
            }
            catch
            {
                return false;
            }
        }

        private static string TryTranslateKeyToString(string key)
        {
            try
            {
                return key.Translate().ToString();
            }
            catch
            {
                return null;
            }
        }

        private static bool LooksLikeModTranslationKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return false;
            if (key.Length > 120) return false;
            if (key.IndexOf('_') < 0) return false;
            if (!char.IsLetter(key[0])) return false;
            return key.All(c => char.IsLetterOrDigit(c) || c == '_' || c == '-');
        }

        private static bool LooksLikeRefreshableStaticSource(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            if (text.Length > 160) return false;

            int letterCount = 0;
            int latinLikeCount = 0;
            foreach (char c in text)
            {
                if (!char.IsLetter(c)) continue;
                letterCount++;
                if (IsStaticLatinLikeChar(c)) latinLikeCount++;
            }

            return letterCount >= 3 && latinLikeCount * 100 / Math.Max(1, letterCount) >= 75;
        }

        private static int CountChars(string text, Func<char, bool> predicate)
        {
            if (string.IsNullOrEmpty(text) || predicate == null) return 0;

            int count = 0;
            foreach (char c in text)
            {
                if (predicate(c)) count++;
            }
            return count;
        }

        private static bool IsStaticHanChar(char c)
        {
            return (c >= '\u3400' && c <= '\u4DBF') ||
                   (c >= '\u4E00' && c <= '\u9FFF') ||
                   (c >= '\uF900' && c <= '\uFAFF');
        }

        private static bool IsStaticKanaChar(char c)
        {
            return (c >= '\u3040' && c <= '\u30FF') ||
                   (c >= '\u31F0' && c <= '\u31FF') ||
                   (c >= '\uFF66' && c <= '\uFF9F');
        }

        private static bool IsStaticHangulChar(char c)
        {
            return (c >= '\u1100' && c <= '\u11FF') ||
                   (c >= '\u3130' && c <= '\u318F') ||
                   (c >= '\uAC00' && c <= '\uD7AF');
        }

        private static bool IsStaticCyrillicChar(char c)
        {
            return (c >= '\u0400' && c <= '\u04FF') ||
                   (c >= '\u0500' && c <= '\u052F');
        }

        private static bool IsStaticLatinLikeChar(char c)
        {
            return (c >= 'A' && c <= 'Z') ||
                   (c >= 'a' && c <= 'z') ||
                   (c >= '\u00C0' && c <= '\u024F');
        }

        private static bool IsStaticThaiChar(char c)
        {
            return c >= '\u0E00' && c <= '\u0E7F';
        }

        private static string NormalizeStaticTranslationSourceText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;

            string normalized = text.Normalize(NormalizationForm.FormD);
            StringBuilder builder = new StringBuilder(normalized.Length);
            foreach (char c in normalized)
            {
                UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(c);
                if (category == UnicodeCategory.NonSpacingMark ||
                    category == UnicodeCategory.SpacingCombiningMark ||
                    category == UnicodeCategory.EnclosingMark)
                {
                    continue;
                }

                builder.Append(NormalizePseudoEnglishChar(c));
            }

            return builder
                .ToString()
                .Normalize(NormalizationForm.FormC)
                .Trim();
        }

        private static char NormalizePseudoEnglishChar(char c)
        {
            switch (c)
            {
                case 'ð':
                case 'Ð':
                    return 'd';
                case 'þ':
                case 'Þ':
                    return 't';
                case 'ł':
                case 'Ł':
                    return 'l';
                case 'ı':
                    return 'i';
                case 'ş':
                case 'Ş':
                    return 's';
                case 'ç':
                case 'Ç':
                    return 'c';
                case 'ý':
                case 'Ý':
                    return 'y';
                default:
                    return c;
            }
        }

        private static string GetCompactNamespaceTail(string ns)
        {
            if (string.IsNullOrWhiteSpace(ns)) return string.Empty;
            string[] parts = ns.Split('.');
            return parts.Length > 0 ? parts[parts.Length - 1] : ns;
        }

        private static string GuessStaticTranslationKeyPrefix(string ns, string typeName)
        {
            string compactNs = GetCompactNamespaceTail(ns);
            string candidate = !string.IsNullOrWhiteSpace(compactNs) ? compactNs : typeName;
            if (string.IsNullOrWhiteSpace(candidate)) return string.Empty;

            if (candidate.Equals("Quarry", StringComparison.OrdinalIgnoreCase)) return "QRY";

            string uppercase = new string(candidate.Where(char.IsUpper).ToArray());
            if (uppercase.Length >= 2 && uppercase.Length <= 6) return uppercase;

            string letters = new string(candidate.Where(char.IsLetter).ToArray()).ToUpperInvariant();
            if (letters.Length >= 3) return letters.Substring(0, Math.Min(4, letters.Length));
            return letters;
        }
    }
}
