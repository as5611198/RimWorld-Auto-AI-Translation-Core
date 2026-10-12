using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace AutoTranslator_Core.Workflow.Persistence
{
    internal static class SQLiteNativeBootstrapper
    {
        private const int RtldNow = 2;
        private const int LinuxRtldGlobal = 0x100;
        private const int MacRtldGlobal = 0x8;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryW(string fileName);

        [DllImport("libdl.so.2", EntryPoint = "dlopen")]
        private static extern IntPtr LinuxDlopen(string fileName, int flags);

        [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "dlopen")]
        private static extern IntPtr MacDlopen(string fileName, int flags);

        public static Assembly PrepareAndLoadProvider(string coreModRoot, string generatedPackRoot)
        {
            string runtimeId = GetRuntimeId();
            string nativeFileName = GetNativeFileName(runtimeId);
            string sourceDirectory = Path.Combine(coreModRoot, "1.6", "Native", "sqlite", runtimeId);
            string sourceNativePath = Path.Combine(sourceDirectory, nativeFileName);
            string sourceHashPath = sourceNativePath + ".sha256";
            string providerPath = Path.Combine(coreModRoot, "1.6", "Assemblies", "System.Data.SQLite.dll");

            if (!File.Exists(providerPath))
                throw new FileNotFoundException("SQLite managed provider is not included in the mod package.", providerPath);
            if (!File.Exists(sourceNativePath) || !File.Exists(sourceHashPath))
                throw new FileNotFoundException("SQLite native runtime is not included for " + runtimeId + ".", sourceNativePath);

            string expectedHash = File.ReadAllText(sourceHashPath).Trim().ToLowerInvariant();
            if (expectedHash.Length != 64 || !IsHex(expectedHash))
                throw new InvalidDataException("SQLite native runtime checksum manifest is invalid: " + sourceHashPath);
            VerifyHash(sourceNativePath, expectedHash);

            string destinationDirectory = Path.Combine(generatedPackRoot, "Runtime", "SQLite", "3.53.4", runtimeId);
            Directory.CreateDirectory(destinationDirectory);
            string destinationNativePath = Path.Combine(destinationDirectory, nativeFileName);
            if (!File.Exists(destinationNativePath) || !HashMatches(destinationNativePath, expectedHash))
            {
                string temporaryPath = destinationNativePath + ".tmp." + Guid.NewGuid().ToString("N");
                File.Copy(sourceNativePath, temporaryPath, true);
                VerifyHash(temporaryPath, expectedHash);
                if (File.Exists(destinationNativePath)) File.Delete(destinationNativePath);
                File.Move(temporaryPath, destinationNativePath);
            }

            PreloadNative(destinationNativePath, runtimeId);
            return Assembly.LoadFrom(providerPath);
        }

        private static string GetRuntimeId()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return Environment.Is64BitProcess ? "win-x64" : ThrowUnsupported("win-x86");
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return Environment.Is64BitProcess ? "linux-x64" : ThrowUnsupported("linux-x86");
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64";
            return ThrowUnsupported(RuntimeInformation.OSDescription);
        }

        private static string ThrowUnsupported(string platform)
        {
            throw new PlatformNotSupportedException("Unsupported SQLite runtime: " + platform);
        }

        private static string GetNativeFileName(string runtimeId)
        {
            if (runtimeId.StartsWith("win-", StringComparison.Ordinal)) return "e_sqlite3.dll";
            if (runtimeId.StartsWith("linux-", StringComparison.Ordinal)) return "libe_sqlite3.so";
            return "libe_sqlite3.dylib";
        }

        private static void PreloadNative(string path, string runtimeId)
        {
            IntPtr handle;
            if (runtimeId.StartsWith("win-", StringComparison.Ordinal)) handle = LoadLibraryW(path);
            else if (runtimeId.StartsWith("linux-", StringComparison.Ordinal))
                handle = LinuxDlopen(path, RtldNow | LinuxRtldGlobal);
            else
                handle = MacDlopen(path, RtldNow | MacRtldGlobal);
            if (handle == IntPtr.Zero) throw new DllNotFoundException("Unable to preload SQLite native runtime: " + path);
        }

        private static bool HashMatches(string path, string expectedHash)
        {
            try { return string.Equals(ComputeHash(path), expectedHash, StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        private static void VerifyHash(string path, string expectedHash)
        {
            string actualHash = ComputeHash(path);
            if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("SQLite native runtime checksum mismatch: " + path);
        }

        private static string ComputeHash(string path)
        {
            using (FileStream stream = File.OpenRead(path))
            using (SHA256 algorithm = SHA256.Create())
            {
                byte[] bytes = algorithm.ComputeHash(stream);
                return BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        private static bool IsHex(string value)
        {
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            }
            return true;
        }
    }
}
