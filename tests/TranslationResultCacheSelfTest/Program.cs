using AutoTranslator_Core;
using System;
using System.Collections.Generic;
using System.IO;

namespace TranslationResultCacheSelfTest
{
    internal static class Program
    {
        private static int Main()
        {
            string root = Path.Combine(Path.GetTempPath(), "ATC_TranslationResultCacheSelfTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string path = Path.Combine(root, "cache.json");
                TranslationResultCache cache = new TranslationResultCache(path);
                cache.PutRange("author.mod", "Simplified", new[]
                {
                    new KeyValuePair<string, string>("Steel", "钢铁"),
                    new KeyValuePair<string, string>("Component", "零部件")
                });

                Assert(cache.TryGet("AUTHOR.MOD", "simplified", "Steel", out string steel), "case-normalized cache miss");
                Assert(steel == "钢铁", "wrong cached translation");
                Assert(!cache.TryGet("other.mod", "simplified", "Steel", out _), "cross-mod cache leak");
                Assert(!cache.TryGet("author.mod", "Traditional", "Steel", out _), "cross-language cache leak");

                cache.PutRange("author.mod", "Simplified", new[]
                {
                    new KeyValuePair<string, string>("Steel", "金属")
                });
                Assert(cache.TryGet("author.mod", "Simplified", "Steel", out steel) && steel == "钢铁",
                    "first accepted translation must win for consistency");

                TranslationResultCache reloaded = new TranslationResultCache(path);
                Assert(reloaded.TryGet("author.mod", "Simplified", "Component", out string component), "persisted cache miss");
                Assert(component == "零部件", "persisted translation mismatch");
                Assert(File.Exists(path), "cache file was not written");
                Console.WriteLine("PASS: 8 translation result cache assertions");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("FAIL: " + ex);
                return 1;
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
