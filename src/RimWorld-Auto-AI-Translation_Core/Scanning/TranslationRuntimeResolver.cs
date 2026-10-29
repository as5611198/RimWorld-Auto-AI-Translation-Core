using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Verse;

namespace AutoTranslator_Core
{
    // Background validation must not enter Verse's mutable type caches or its
    // shared handle StringBuilder. Keep native spelling, priority and occurrence
    // semantics, using immutable reflection data and call-local text buffers.
    internal static class TranslationRuntimeResolver
    {
        private const BindingFlags Fields = BindingFlags.IgnoreCase | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly Regex FormatSymbols = new Regex("{.*?}");
        private sealed class HandleField { internal FieldInfo Field; internal int Priority; }
        private static readonly ConcurrentDictionary<Type, HandleField[]> handles = new ConcurrentDictionary<Type, HandleField[]>();
        private static readonly object typeGate = new object();
        private sealed class TypeIndex
        {
            internal readonly Dictionary<string, Type> Full = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
            internal readonly Dictionary<string, Type> Short = new Dictionary<string, Type>(StringComparer.Ordinal);
        }
        private static volatile TypeIndex types;
        internal static void Invalidate() { lock (typeGate) types = null; }

        internal static Type DefType(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            var index = types;
            if (index == null) lock (typeGate)
            {
                index = types;
                if (index == null)
                {
                    index = new TypeIndex();
                    var assemblies = new[] { typeof(Def).Assembly }.Concat(LoadedModManager.RunningModsListForReading
                        .ToArray().SelectMany(m => m.assemblies.loadedAssemblies.ToArray())).Distinct();
                    foreach (var assembly in assemblies)
                    {
                        Type[] entries;
                        try { entries = assembly.GetTypes(); }
                        catch (ReflectionTypeLoadException ex) { entries = ex.Types.Where(t => t != null).ToArray(); }
                        foreach (var type in entries)
                        {
                            if (type.FullName != null && !index.Full.ContainsKey(type.FullName)) index.Full.Add(type.FullName, type);
                            if (type.Namespace == null || GenTypes.IgnoredNamespaceNames.Contains(type.Namespace)) index.Short[type.Name] = type;
                        }
                    }
                    types = index;
                }
            }
            if (index.Short.TryGetValue(name, out var found) || index.Full.TryGetValue(name, out found))
                return typeof(Def).IsAssignableFrom(found) ? found : null;
            foreach (var ns in GenTypes.IgnoredNamespaceNames)
                if (index.Full.TryGetValue(ns + "." + name, out found)) return typeof(Def).IsAssignableFrom(found) ? found : null;
            return null;
        }

        private static HandleField[] HandleFields(Type type) => handles.GetOrAdd(type, t => t.GetFields(Fields)
            .Select(f => new { Field = f, Attribute = (TranslationHandleAttribute)f.GetCustomAttributes(typeof(TranslationHandleAttribute), false).FirstOrDefault() })
            .Where(f => f.Attribute != null).Select(f => new HandleField { Field = f.Field, Priority = f.Attribute.Priority }).ToArray());

        internal static int ElementIndex(object list, string handle, int occurrence)
        {
            if (list == null) throw new InvalidOperationException("Tried to get element by handle on null object.");
            var type = list.GetType();
            var count = type.GetProperty("Count");
            var item = type.GetProperty("Item");
            if (count == null || item == null) throw new InvalidOperationException("Translation handle requires a list.");
            int length = (int)count.GetValue(list, null);
            occurrence = Math.Max(0, occurrence);
            HandleField selected = null;
            for (int i = 0; i < length; i++)
            {
                var value = item.GetValue(list, new object[] { i });
                if (value == null) continue;
                foreach (var field in HandleFields(value.GetType()))
                {
                    var candidate = field.Field.GetValue(value);
                    if (candidate != null && Matches(candidate, handle) && (selected == null || field.Priority > selected.Priority)) selected = field;
                }
            }
            if (selected == null) throw new InvalidOperationException("None of the list elements have a handle named " + handle + ".");
            int seen = 0;
            for (int i = 0; i < length; i++)
            {
                var value = item.GetValue(list, new object[] { i });
                if (value == null) continue;
                foreach (var field in HandleFields(value.GetType()))
                {
                    if (field.Field.DeclaringType != selected.Field.DeclaringType || field.Field.Name != selected.Field.Name) continue;
                    var candidate = field.Field.GetValue(value);
                    if (candidate != null && Matches(candidate, handle) && seen++ == occurrence) return i;
                }
            }
            throw new InvalidOperationException("Handle occurrence is not present: " + handle + "[" + occurrence + "].");
        }

        private static bool Matches(object item, string handle)
        {
            handle = Normalize(handle);
            if (string.IsNullOrEmpty(handle)) return false;
            if (item is Type type) return Normalize(type.Name) == handle || Normalize(type.FullName) == handle || Normalize(type.ToString()) == handle;
            string value;
            // Handles must not execute arbitrary mod-defined ToString implementations.
            var valueType = item.GetType();
            if (!(item is string) && !(item is Def) && !valueType.IsEnum && !valueType.IsPrimitive && !(item is decimal))
                throw new InvalidOperationException("Unsupported translation handle value type.");
            try { value = item is Def def ? def.defName : item.ToString(); }
            catch (Exception ex) { throw new InvalidOperationException("Translation handle ToString failed.", ex); }
            return !string.IsNullOrEmpty(value) && Normalize(value) == handle;
        }

        private static string Normalize(string handle)
        {
            if (string.IsNullOrEmpty(handle)) return handle;
            handle = handle.Trim().Replace(' ', '_').Replace('\n', '_').Replace("\r", "").Replace('\t', '_').Replace(".", "").Replace("-", "");
            if (handle.IndexOf('{') >= 0) handle = FormatSymbols.Replace(handle, "");
            var text = new StringBuilder(handle.Length);
            foreach (char c in handle)
            {
                if ("qwertyuiopasdfghjklzxcvbnmQWERTYUIOPASDFGHJKLZXCVBNM1234567890-_".IndexOf(c) < 0) continue;
                if (c != '_' || text.Length == 0 || text[text.Length - 1] != '_') text.Append(c);
            }
            string result = text.ToString().Trim('_');
            return result.Length > 0 && result.All(char.IsDigit) ? "_" + result : result;
        }
    }
}
