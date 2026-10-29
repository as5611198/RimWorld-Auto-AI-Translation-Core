using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace AutoTranslator_Core
{
    public sealed class DefTranslationPathResult
    {
        public string Reason;
        public string Path;
        public string ResolvedPath;
        public string Evidence;
        public bool Allowed => Reason == "valid";
        public bool Temporary => Reason == "not_ready" || Reason == "missing_def" ||
            Reason == "missing_def_type" || Reason == "null_reference" || Reason == "validation_unavailable" || Reason == "config_validation_unavailable";
    }

    // Read-only traversal. In particular, never invoke an unknown dictionary indexer.
    // The adapter supplies the selected game's handle resolver and TKey/Def aliases.
    public static class DefTranslationPathValidator
    {
        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase;
        private static readonly ConcurrentDictionary<Type, FieldInfo[]> FieldsByType = new ConcurrentDictionary<Type, FieldInfo[]>();
        private static readonly ConcurrentDictionary<FieldInfo, IList<CustomAttributeData>> Attributes = new ConcurrentDictionary<FieldInfo, IList<CustomAttributeData>>();

        public static DefTranslationPathResult Validate(object root, string path, Func<object, string, int, int> handleIndex)
        {
            return ValidateTarget(root, path, handleIndex, false);
        }

        public static DefTranslationPathResult ValidateTarget(object root, string path, Func<object, string, int, int> handleIndex, bool fullList)
        {
            var result = new DefTranslationPathResult { Path = path, ResolvedPath = path, Reason = "valid", Evidence = "runtime fields" };
            string[] parts = (path ?? "").Split('.');
            Func<string, DefTranslationPathResult> fail = reason => { result.Reason = reason; return result; };
            if (parts.Length < 2 || parts.Any(string.IsNullOrWhiteSpace)) return fail("invalid_path");
            if (root == null) return fail("missing_def");
            object current = root;
            bool countCanChange = false;
            Type targetType = fullList ? typeof(System.Collections.Generic.List<string>) : typeof(string);
            var canonical = (string[])parts.Clone();
            for (int i = 1; i < parts.Length; i++)
            {
                if (current == null) return fail("null_reference");
                Type type = current.GetType();
                string part = parts[i];
                int index;
                bool numeric = int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out index);
                FieldInfo field = numeric ? null : FindField(type, part);
                bool last = i == parts.Length - 1;
                if (field != null)
                {
                    if (field.IsStatic || field.IsInitOnly || Has(field, "NoTranslateAttribute") || Has(field, "UnsavedAttribute"))
                        return fail("untranslatable_field");
                    canonical[i] = field.Name;
                    parts[i] = field.Name;
                    countCanChange |= Has(field, "TranslationCanChangeCountAttribute");
                    if (last)
                    {
                        if (field.FieldType != targetType) return fail("non_text_field");
                        if (fullList && !Has(field, "TranslationCanChangeCountAttribute")) return fail("count_change_forbidden");
                    }
                    else
                    {
                        // Strings, primitives and Def references are never containers for nested text.
                        if (field.FieldType == typeof(string) || field.FieldType.IsPrimitive || field.FieldType.IsEnum || IsDef(field.FieldType))
                            return fail("non_text_field");
                        current = field.GetValue(current);
                        if (current != null && IsDef(current.GetType())) return fail("non_text_field");
                    }
                }
                else
                {
                    if (!type.IsGenericType || type.GetGenericTypeDefinition() != typeof(List<>))
                        return fail(type.GetProperty("Item") != null || numeric ? "unsupported_container" : "missing_field");
                    PropertyInfo count = type.GetProperty("Count");
                    var items = type.GetProperties().Where(p => p.Name == "Item").ToArray();
                    if (count == null && items.Length == 0) return fail(numeric ? "unsupported_container" : "missing_field");
                    if (count == null || count.PropertyType != typeof(int) || count.GetIndexParameters().Length != 0 ||
                        items.Length != 1 || items[0].GetIndexParameters().Length != 1 ||
                        items[0].GetIndexParameters()[0].ParameterType != typeof(int)) return fail("unsupported_container");
                    PropertyInfo item = items[0];
                    if (!numeric)
                    {
                        string handle = part;
                        int occurrence = -1;
                        int dash = part.IndexOf('-');
                        if (dash >= 0)
                        {
                            if (!int.TryParse(part.Substring(dash + 1), out occurrence) || occurrence < 0)
                                return fail("missing_handle");
                            handle = part.Substring(0, dash);
                        }
                        if (handleIndex == null) return fail("missing_handle");
                        try { index = handleIndex(current, handle, occurrence); }
                        catch (InvalidOperationException ex) { result.Evidence = ex.Message; return fail("missing_handle"); }
                    }
                    int length = (int)count.GetValue(current, null);
                    if (index < 0 || index >= length) return fail("index_out_of_bounds");
                    canonical[i] = index.ToString(CultureInfo.InvariantCulture);
                    if (last)
                    {
                        if (item.PropertyType != targetType) return fail("non_text_field");
                        if (fullList && !countCanChange) return fail("count_change_forbidden");
                        if (!item.CanWrite) return fail("untranslatable_field");
                    }
                    else
                    {
                        current = item.GetValue(current, new object[] { index });
                        if (current != null && IsDef(current.GetType())) return fail("non_text_field");
                    }
                }
            }
            result.ResolvedPath = string.Join(".", canonical);
            result.Path = string.Join(".", parts);
            return result;
        }

        private static bool Has(FieldInfo field, string name) => Attributes.GetOrAdd(field, f => CustomAttributeData.GetCustomAttributes(f))
            .Any(a => a.AttributeType.FullName == "Verse." + name);

        // Keep this read-only validator usable by the standalone XML tests too.
        private static bool IsDef(Type type)
        {
            for (; type != null; type = type.BaseType)
                if (type.FullName == "Verse.Def") return true;
            return false;
        }

        // Only read a path that has just passed structural validation. Numeric
        // canonical indexes prevent a second, ambiguous handle resolution.
        public static string ReadValidatedText(object root, string resolvedPath)
            => ReadValidatedValue(root, resolvedPath) as string;

        internal static object ReadValidatedValue(object root, string resolvedPath)
        {
            object current = root;
            foreach (string part in resolvedPath.Split('.').Skip(1))
            {
                if (current == null) return null;
                var field = FindField(current.GetType(), part);
                if (field != null) current = field.GetValue(current);
                else
                {
                    if (current is System.Collections.IDictionary || !int.TryParse(part, out int index)) return null;
                    var item = current.GetType().GetProperties().Single(p => p.Name == "Item" &&
                        p.GetIndexParameters().Length == 1 && p.GetIndexParameters()[0].ParameterType == typeof(int));
                    current = item.GetValue(current, new object[] { index });
                }
            }
            return current;
        }

        private static FieldInfo FindField(Type type, string name)
        {
            var fields = FieldsByType.GetOrAdd(type, t => t.GetFields(Fields));
            var field = fields.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
            if (field != null) return field;
            // LoadAlias is explicit equivalence evidence; write the actual field name.
            return fields.FirstOrDefault(f => Attributes.GetOrAdd(f, v => CustomAttributeData.GetCustomAttributes(v)).Any(a =>
                a.AttributeType.FullName == "Verse.LoadAliasAttribute" && a.ConstructorArguments.Count == 1 &&
                string.Equals(a.ConstructorArguments[0].Value as string, name, StringComparison.Ordinal)));
        }
    }
}
