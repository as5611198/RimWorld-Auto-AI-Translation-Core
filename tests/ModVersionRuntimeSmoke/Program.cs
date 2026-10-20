using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml;

internal static partial class Program
{
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static int passed;
    private static string packRoot;
    public static bool PackPathPrefix(ref string __result) { __result = packRoot; return false; }
    private static void Check(bool value, string name)
    {
        if (!value) throw new Exception(name);
        passed++; Console.WriteLine("PASS " + name);
    }

    private static int Main(string[] args)
    {
        if (args.Length != 4) { Console.Error.WriteLine("Usage: smoke <mod.dll> <game assembly directory> <harmony.dll> <1.5|1.6|1.7>"); return 2; }
        string modPath = Path.GetFullPath(args[0]);
        string gameDirectory = Path.GetFullPath(args[1]);
        string harmonyPath = Path.GetFullPath(args[2]);
        string version = args[3];
        string temporary = Path.Combine(Path.GetTempPath(), "ATC-VersionSmoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        AppDomain.CurrentDomain.AssemblyResolve += (_, request) => {
            string name = new AssemblyName(request.Name).Name + ".dll";
            foreach (string dir in new[] { Path.GetDirectoryName(modPath), Path.GetDirectoryName(harmonyPath), gameDirectory })
            {
                string path = Path.Combine(dir, name);
                if (File.Exists(path)) return Assembly.LoadFrom(path);
            }
            return null;
        };
        try
        {
            Console.WriteLine("Version=" + version + "; CLR=" + Environment.Version + "; CoreLib=" + typeof(object).Assembly.Location);
            Assembly harmony = Assembly.LoadFrom(harmonyPath);
            if (version != "1.7")
            {
                foreach (string path in Directory.GetFiles(gameDirectory, "Unity*.dll")) Assembly.LoadFrom(path);
            }
            Assembly.LoadFrom(Path.Combine(gameDirectory, "Assembly-CSharp.dll"));
            Assembly mod = Assembly.LoadFrom(modPath);
            Type[] types = mod.GetTypes();
            Check(types.Length > 500, "all product types load (" + types.Length + ")");
            Type scanner = mod.GetType("AutoTranslator_Core.AutoTranslatorScanner", true);
            Check((string)scanner.GetProperty("CurrentRimWorldVersion", Static).GetValue(null, null) == version, "compiled game version");
            Type settings = mod.GetType("AutoTranslator_Core.AutoTranslatorSettings", true);
            mod.GetType("AutoTranslator_Core.AutoTranslatorMod", true).GetField("Settings", Static).SetValue(null, Activator.CreateInstance(settings));
            var resolveDefs = scanner.GetMethod("GetAllEffectiveDefsPaths", Static, null, new[] { typeof(string), typeof(string) }, null);
            var resolveLanguages = scanner.GetMethod("GetAllEffectiveLangPaths", Static, null, new[] { typeof(string), typeof(string) }, null);
            foreach (string v in new[] { "1.5", "1.6", "1.7" })
            {
                string root = Path.Combine(temporary, v);
                Directory.CreateDirectory(Path.Combine(root, "Defs"));
                Directory.CreateDirectory(Path.Combine(root, "Languages", "English", "Keyed"));
            }
            File.WriteAllText(Path.Combine(temporary, "LoadFolders.xml"), "<loadFolders><v1.5><li>1.5</li></v1.5><v1.6><li>1.6</li></v1.6><v1.7><li>1.7</li></v1.7></loadFolders>");
            string[] defs = ((IEnumerable)resolveDefs.Invoke(null, new object[] { "test.version.defs", temporary })).Cast<string>().ToArray();
            string[] languages = ((IEnumerable)resolveLanguages.Invoke(null, new object[] { "test.version.languages", temporary })).Cast<string>().ToArray();
            Check(defs.Length == 1 && Path.GetFullPath(defs[0]) == Path.Combine(temporary, version, "Defs"), "Defs use exact game branch");
            Check(languages.Length == 1 && Path.GetFullPath(languages[0]) == Path.Combine(temporary, version, "Languages"), "Languages use exact game branch");

            Type export = mod.GetType("AutoTranslator_Core.ExportManager", true);
            export.GetMethod("WriteLoadFoldersXml", Static).Invoke(null, new object[] { temporary });
            var xml = new XmlDocument();xml.Load(Path.Combine(temporary, "LoadFolders.xml"));
            Check(xml.SelectSingleNode("/loadFolders/v" + version + "/li")?.InnerText == version, "export manifest uses current version");
            Type infoType = mod.GetType("AutoTranslator_Core.ExportableModInfo", true);
            object info = Activator.CreateInstance(infoType);
            foreach (string field in new[] { "ModName", "PackageId", "Author" })
            {
                FieldInfo valueField = infoType.GetField(field);
                if (valueField != null) valueField.SetValue(info, "test.fixture");
                PropertyInfo property = infoType.GetProperty(field);
                if (property != null && property.CanWrite) property.SetValue(info, "test.fixture", null);
            }
            Type templates = mod.GetType("AutoTranslator_Core.ExportTemplates", true);
            string about = (string)templates.GetMethod("GetAboutXml", Static).Invoke(null, new[] { info });
            xml.LoadXml(about);
            Check(xml.SelectSingleNode("/ModMetaData/supportedVersions/li")?.InnerText == version, "export metadata uses current version");

            bool isWaylith = mod.GetType("AutoTranslator_Core.Patch_GUI_Label_String") != null;
            Check(isWaylith == (version == "1.7"), "Waylith-only patch is isolated from legacy builds");
            if (version == "1.7")
            {
                Type harmonyType = harmony.GetType("HarmonyLib.Harmony", true);
                object patcher = Activator.CreateInstance(harmonyType, new object[] { "atc.product-smoke" });
                harmonyType.GetMethod("PatchAll", new[] { typeof(Assembly) }).Invoke(patcher, new object[] { mod });
                var patched = ((IEnumerable)harmonyType.GetMethod("GetPatchedMethods").Invoke(patcher, null)).Cast<MethodBase>().ToArray();
                Check(patched.Length >= 8, "all product Harmony annotations mount (" + patched.Length + ")");
                Check(patched.Any(m => m.DeclaringType.FullName == "UnityEngine.GUI" && m.Name == "Label" && m.GetParameters()[1].ParameterType == typeof(string)), "direct string GUI patch mounted");
                Type harmonyMethodType = harmony.GetType("HarmonyLib.HarmonyMethod", true);
                object prefix = Activator.CreateInstance(harmonyMethodType, new object[] { typeof(Program).GetMethod(nameof(PackPathPrefix)) });
                MethodInfo patch = harmonyType.GetMethods().Single(m => m.Name == "Patch" && m.GetParameters().Length == 5);
                patch.Invoke(patcher, new object[] { scanner.GetMethod("GetLocalPackPath", Static), prefix, null, null, null });
                packRoot = Path.Combine(temporary, "generated-pack");Directory.CreateDirectory(Path.Combine(packRoot, "About"));
                string packAbout = Path.Combine(packRoot, "About", "About.xml");
                File.WriteAllText(packAbout, "<ModMetaData><name>Keep this name</name><packageId>AITranslation.Pack</packageId><supportedVersions><li>1.6</li></supportedVersions></ModMetaData>");
                File.WriteAllText(Path.Combine(packRoot, "translation.xml"), "preserve");
                scanner.GetMethod("EnsurePackSkeleton", Static).Invoke(null, null);
                xml.Load(packAbout);
                Check(xml.SelectNodes("/ModMetaData/supportedVersions/li").Count == 3 && xml.SelectSingleNode("/ModMetaData/name").InnerText == "Keep this name" && File.ReadAllText(Path.Combine(packRoot, "translation.xml")) == "preserve", "existing generated pack gains versions without losing metadata or translations");
                scanner.GetMethod("EnsurePackSkeleton", Static).Invoke(null, null);
                xml.Load(packAbout);Check(xml.SelectNodes("/ModMetaData/supportedVersions/li").Count == 3, "pack version migration is idempotent");
                TestWorkbenchBatch(mod, temporary);
                harmonyType.GetMethod("UnpatchAll").Invoke(patcher, new object[] { "atc.product-smoke" });
            }
            Console.WriteLine("PASS: " + passed + " product smoke checks for " + version);return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            if (error is ReflectionTypeLoadException load) foreach (var inner in load.LoaderExceptions) Console.Error.WriteLine(inner);
            return 1;
        }
        finally { Directory.Delete(temporary, recursive: true); }
    }
}
