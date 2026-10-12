using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text;

namespace AutoTranslator_Core.TargetedHardcodedUi
{
    internal static class HardcodedUiMethodIdentity
    {
        // The manifest signature intentionally describes the callable method
        // only.  Runtime patch tables need a stronger key because two loaded
        // assemblies can expose the same type and method signature.
        public static string GetRuntimeMethodIdentity(MethodBase method)
        {
            if (method == null) return string.Empty;

            string mvid = string.Empty;
            string moduleName = string.Empty;
            string location = string.Empty;
            string assemblyName = string.Empty;
            string metadataToken = string.Empty;
            string methodHandle = string.Empty;
            try
            {
                Module module = method.Module;
                if (module != null)
                {
                    mvid = module.ModuleVersionId.ToString("D").ToLowerInvariant();
                    moduleName = module.Name ?? string.Empty;
                }
            }
            catch { }

            try
            {
                Assembly assembly = method.DeclaringType != null
                    ? method.DeclaringType.Assembly
                    : null;
                if (assembly != null)
                {
                    assemblyName = assembly.FullName ?? string.Empty;
                    location = assembly.Location ?? string.Empty;
                }
            }
            catch { }

            try { metadataToken = method.MetadataToken.ToString("x8", CultureInfo.InvariantCulture); }
            catch { metadataToken = string.Empty; }
            try { methodHandle = method.MethodHandle.Value.ToInt64().ToString("x", CultureInfo.InvariantCulture); }
            catch { methodHandle = string.Empty; }

            return "runtime-method:" +
                mvid + "|" +
                moduleName + "|" +
                NormalizeIdentityPath(location) + "|" +
                assemblyName + "|" +
                metadataToken + "|" +
                methodHandle + "|" +
                GetMethodSignature(method);
        }

        // This key is persisted only in the in-memory patch manager. It binds
        // a group to every immutable assembly/manifest identity field, so a
        // same signature in another mod cannot be merged accidentally.
        public static string CreateMethodTargetIdentity(
            string packageId,
            string assemblyRelativePath,
            string assemblySha256,
            string assemblyMvid,
            string methodSignature,
            int methodMetadataToken = 0,
            string methodIlFingerprint = null)
        {
            string material = (packageId ?? string.Empty).Trim().ToLowerInvariant() + "|" +
                NormalizeRelativePath(assemblyRelativePath).ToLowerInvariant() + "|" +
                (assemblySha256 ?? string.Empty).Trim().ToLowerInvariant() + "|" +
                (assemblyMvid ?? string.Empty).Trim().ToLowerInvariant() + "|" +
                (methodSignature ?? string.Empty) + "|" +
                methodMetadataToken.ToString(CultureInfo.InvariantCulture) + "|" +
                (methodIlFingerprint ?? string.Empty).Trim().ToLowerInvariant();
            return "hardcoded-target:" + ComputeSha256(material);
        }

        public static bool IsDeterministicEntryId(
            string entryId,
            string packageId,
            string assemblyRelativePath,
            string methodSignature,
            int literalOrdinal,
            string literal)
        {
            return !string.IsNullOrWhiteSpace(entryId) &&
                string.Equals(
                    entryId,
                    CreateEntryId(packageId, assemblyRelativePath, methodSignature, literalOrdinal, literal),
                    StringComparison.Ordinal);
        }

        public static bool TryFindDuplicateEntryId(
            IEnumerable<string> entryIds,
            out string duplicateEntryId)
        {
            duplicateEntryId = string.Empty;
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string entryId in entryIds ?? new string[0])
            {
                if (string.IsNullOrWhiteSpace(entryId)) continue;
                if (seen.Add(entryId)) continue;
                duplicateEntryId = entryId;
                return true;
            }
            return false;
        }

        public static string GetMethodSignature(MethodBase method)
        {
            if (method == null) return string.Empty;

            StringBuilder builder = new StringBuilder();
            builder.Append(GetTypeName(method.DeclaringType));
            builder.Append("::");
            builder.Append(method.Name ?? string.Empty);
            if (method.IsGenericMethod)
            {
                builder.Append('`').Append(method.GetGenericArguments().Length.ToString(CultureInfo.InvariantCulture));
            }
            builder.Append('(');

            ParameterInfo[] parameters = method.GetParameters();
            for (int i = 0; i < parameters.Length; i++)
            {
                if (i > 0) builder.Append(',');
                builder.Append(GetTypeName(parameters[i].ParameterType));
            }

            builder.Append(")->");
            MethodInfo methodInfo = method as MethodInfo;
            builder.Append(GetTypeName(methodInfo != null ? methodInfo.ReturnType : typeof(void)));
            return builder.ToString();
        }

        public static string GetTypeName(Type type)
        {
            if (type == null) return string.Empty;
            if (type.IsByRef) return GetTypeName(type.GetElementType()) + "&";
            if (type.IsPointer) return GetTypeName(type.GetElementType()) + "*";
            if (type.IsArray)
            {
                return GetTypeName(type.GetElementType()) + "[" + new string(',', Math.Max(0, type.GetArrayRank() - 1)) + "]";
            }

            if (type.IsGenericType)
            {
                string genericName = type.GetGenericTypeDefinition().FullName ?? type.Name;
                int tick = genericName.IndexOf('`');
                if (tick >= 0) genericName = genericName.Substring(0, tick);
                Type[] arguments = type.GetGenericArguments();
                StringBuilder genericBuilder = new StringBuilder(genericName);
                genericBuilder.Append('<');
                for (int i = 0; i < arguments.Length; i++)
                {
                    if (i > 0) genericBuilder.Append(',');
                    genericBuilder.Append(GetTypeName(arguments[i]));
                }
                genericBuilder.Append('>');
                return genericBuilder.ToString();
            }

            return type.FullName ?? type.Name ?? string.Empty;
        }

        public static string CreateEntryId(
            string packageId,
            string assemblyRelativePath,
            string methodSignature,
            int literalOrdinal,
            string literal)
        {
            string material = (packageId ?? string.Empty).Trim().ToLowerInvariant() + "|" +
                NormalizeRelativePath(assemblyRelativePath) + "|" +
                (methodSignature ?? string.Empty) + "|" +
                literalOrdinal.ToString(CultureInfo.InvariantCulture) + "|" +
                (literal ?? string.Empty);
            return "hardcoded-ui:" + ComputeSha256(material).Substring(0, 32).ToLowerInvariant();
        }

        public static string NormalizeRelativePath(string path)
        {
            return (path ?? string.Empty).Replace('\\', '/').Trim().TrimStart('/');
        }

        private static string NormalizeIdentityPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;
            try
            {
                return Path.GetFullPath(path).Replace('\\', '/').ToLowerInvariant();
            }
            catch
            {
                return path.Replace('\\', '/').ToLowerInvariant();
            }
        }

        public static string ComputeSha256(string text)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? string.Empty));
                return ToHex(bytes);
            }
        }

        public static string ComputeFileSha256(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return string.Empty;
            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                return ToHex(sha.ComputeHash(stream));
            }
        }

        public static string ComputeMethodIlFingerprint(MethodBase method)
        {
            if (method == null || method.GetMethodBody() == null) return string.Empty;

            try
            {
                // Fingerprint the raw method body instead of asking Harmony to decode it
                // a second time. Some valid methods (notably closures and branch-heavy UI
                // methods) can be decoded once for discovery but fail on a repeated
                // PatchProcessor.ReadMethodBody call. The manifest already pins the exact
                // assembly hash and MVID, so raw IL bytes are the most conservative and
                // deterministic identity for this method inside that assembly.
                MethodBody methodBody = method.GetMethodBody();
                byte[] il = methodBody?.GetILAsByteArray();
                if (il == null) return string.Empty;
                StringBuilder builder = new StringBuilder();
                builder.Append("il:").Append(Convert.ToBase64String(il)).Append('\n');
                return ComputeSha256(builder.ToString());
            }
            catch
            {
                return string.Empty;
            }
        }

        private static void AppendOperand(StringBuilder builder, object operand)
        {
            if (operand == null) return;

            string text = operand as string;
            if (text != null)
            {
                builder.Append("s:").Append(text);
                return;
            }

            MethodBase method = operand as MethodBase;
            if (method != null)
            {
                builder.Append("m:").Append(GetMethodSignature(method));
                return;
            }

            FieldInfo field = operand as FieldInfo;
            if (field != null)
            {
                builder.Append("f:").Append(GetTypeName(field.DeclaringType)).Append("::")
                    .Append(field.Name).Append(':').Append(GetTypeName(field.FieldType));
                return;
            }

            Type type = operand as Type;
            if (type != null)
            {
                builder.Append("t:").Append(GetTypeName(type));
                return;
            }

            Array array = operand as Array;
            if (array != null)
            {
                builder.Append("a[");
                foreach (object item in array)
                {
                    AppendOperand(builder, item);
                    builder.Append(';');
                }
                builder.Append(']');
                return;
            }

            IFormattable formattable = operand as IFormattable;
            builder.Append(operand.GetType().FullName ?? operand.GetType().Name)
                .Append(':')
                .Append(formattable != null
                    ? formattable.ToString(null, CultureInfo.InvariantCulture)
                    : operand.ToString());
        }

        private static string ToHex(byte[] bytes)
        {
            StringBuilder builder = new StringBuilder((bytes != null ? bytes.Length : 0) * 2);
            if (bytes != null)
            {
                foreach (byte value in bytes) builder.Append(value.ToString("x2", CultureInfo.InvariantCulture));
            }
            return builder.ToString();
        }
    }
}
