using Verse;

namespace AutoTranslator_Core
{
    /// <summary>
    /// 保留旧窗口曾提供的共享 Mod 扫描判断。
    /// 删除翻译窗口本身已经废弃，但现有代码仍通过静态导入使用此方法。
    /// </summary>
    public static class DeleteTranslationWindow
    {
        public static bool IsCodeOnlyMod(ModMetaData mod)
        {
            return !AutoTranslatorScanner.HasScannableTranslationSources(mod);
        }
    }
}
