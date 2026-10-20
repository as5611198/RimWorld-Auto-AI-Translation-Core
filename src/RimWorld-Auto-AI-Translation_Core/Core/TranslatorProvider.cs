using HarmonyLib;
using Newtonsoft.Json;
using RimWorld;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using Verse;
using static AutoTranslator_Core.DeleteTranslationWindow;
// 這個檔案定義可用的翻譯供應商列舉。
// EN: This file defines the supported translation providers.

namespace AutoTranslator_Core
{
    // 這個列舉定義 翻譯器供應商 可使用的固定選項。
    // EN: This enum defines the available translator provider options.
    public enum TranslatorProvider { Google, OpenAI, DeepSeek, Grok, GLM, Alibaba, OpenRouter, DeepL, Custom_OpenAI, MiMo }
}
