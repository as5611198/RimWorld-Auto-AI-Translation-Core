# Modern Dev Tools 翻译拒绝明细

来源：当前测试数据库只读查询。记录最终拒绝输出，未保存的整批模型响应不能由数据库还原。

## 1 · atc1_0087979af7758de82726b52d632a31c49dd2f0efdfe9d3ce94768627ce3788ff

- 来源：`Assemblies/ModernDevTools.dll`
- 定位：`assembly=Assemblies/ModernDevTools;type=ModernDevTools.DebugTree;method=ModernDevTools.DebugTree::ResolveRoots()->HarmonyLib.AccessTools+FieldRef<System.Collections.Generic.Dictionary<RimWorld.DebugTabMenuDef,LudeonTK.DebugActionNode>>;callSite=1`
- 拒绝理由：The result still appears to contain untranslated English.

原文：

[Modern Dev Tools] could not bind Dialog_Debug.roots: 

模型返回（重试后最终记录，尚未还原提示词占位符）：

[0] 无法绑定 Dialog_Debug.roots：

## 2 · atc1_235779f46e8fca2dc1b4f8765e9d2a7c792aedfe6e8b28cd6fc15153e23b84f9

- 来源：`Assemblies/ModernDevTools.dll`
- 定位：`assembly=Assemblies/ModernDevTools;type=ModernDevTools.LogState;method=ModernDevTools.LogState::ResolveCanAutoOpen()->HarmonyLib.AccessTools+FieldRef<System.Boolean>;callSite=1`
- 拒绝理由：The result still appears to contain untranslated English.

原文：

[Modern Dev Tools] Could not bind EditWindow_Log.canAutoOpen: 

模型返回（重试后最终记录，尚未还原提示词占位符）：

[0] 无法绑定 EditWindow_Log.canAutoOpen: 

## 3 · atc1_301030f6610ca4ecd1fb4c7c347fb9051826223d018a9ea5b3ae089bd3642e0f

- 来源：`Assemblies/ModernDevTools.dll`
- 定位：`assembly=Assemblies/ModernDevTools;type=ModernDevTools.CommunityData;method=ModernDevTools.CommunityData::ParseReplacements(System.String)->System.Void;callSite=7`
- 拒绝理由：The result still appears to contain untranslated English.

原文：

wid:

模型返回（重试后最终记录，尚未还原提示词占位符）：

wid:

## 4 · atc1_3351efbce1d43ee004cef723f716fe3c0ff557e5113fa603e0fbf1cfa11f25b3

- 来源：`Assemblies/ModernDevTools.dll`
- 定位：`assembly=Assemblies/ModernDevTools;type=ModernDevTools.Module_AboutIncompat;method=ModernDevTools.Module_AboutIncompat::Diagnose(ModernDevTools.ErrorContext)->System.Void;callSite=3`
- 拒绝理由：The result still appears to contain untranslated English.

原文：

aboutincompat:

模型返回（重试后最终记录，尚未还原提示词占位符）：

aboutincompat:

## 5 · atc1_46c47f458655e69ade7abef3400dfb28588b8225821c71882b2afa8700e4bad5

- 来源：`Assemblies/ModernDevTools.dll`
- 定位：`assembly=Assemblies/ModernDevTools;type=ModernDevTools.KnownIssueIndex;method=ModernDevTools.KnownIssueIndex::CompileOne(ModernDevTools.KnownIssueDef,System.String)->System.Text.RegularExpressions.Regex;callSite=0`
- 拒绝理由：The result still appears to contain untranslated English.

原文：

[Modern Dev Tools] Bad attributeRegex in 

模型返回（重试后最终记录，尚未还原提示词占位符）：

[0] Bad attributeRegex in 

## 6 · atc1_4fe3fa9032bf4b1f91626d3a437cb30bd43bfc979ce646e12c5dfb4467238919

- 来源：`Assemblies/ModernDevTools.dll`
- 定位：`assembly=Assemblies/ModernDevTools;type=ModernDevTools.LogTimestamps;method=ModernDevTools.LogTimestamps::Probe()->System.Void;callSite=1`
- 拒绝理由：The result still appears to contain untranslated English.

原文：

[Modern Dev Tools] could not read Verse.LogMessage.timestamp (

模型返回（重试后最终记录，尚未还原提示词占位符）：

[0] 无法读取 Verse.LogMessage.timestamp (

## 7 · atc1_977cf6b754369e1948cc9d967cf23be1052fd701fde7917c331d29c567d29c62

- 来源：`Assemblies/ModernDevTools.dll`
- 定位：`assembly=Assemblies/ModernDevTools;type=ModernDevTools.ModernDevToolsAPI;method=ModernDevTools.ModernDevToolsAPI::NotifyAnalysisCompleted(ModernDevTools.LogAnalysis)->System.Void;callSite=0`
- 拒绝理由：The provider returned the unchanged protected-token source text.

原文：

[Modern Dev Tools] an AnalysisCompleted handler threw: 

模型返回（重试后最终记录，尚未还原提示词占位符）：

分析完成处理程序抛出异常：

## 8 · atc1_9b33610b49445583d5cb8803ec56414f2224252dcaedda3f28fe0221d9ed17a7

- 来源：`Assemblies/ModernDevTools.dll`
- 定位：`assembly=Assemblies/ModernDevTools;type=ModernDevTools.Module_HarmonyPatches;method=ModernDevTools.Module_HarmonyPatches::Diagnose(ModernDevTools.ErrorContext)->System.Void;callSite=7`
- 拒绝理由：The result still appears to contain untranslated English.

原文：

harmonypatch:

模型返回（重试后最终记录，尚未还原提示词占位符）：

harmonypatch:

## 9 · atc1_a63bed5f6efc208269d62c30bddf3ddea5255b26d5b07bc607ca80e936ec525c

- 来源：`Assemblies/ModernDevTools.dll`
- 定位：`assembly=Assemblies/ModernDevTools;type=ModernDevTools.DebugActions_Testing;method=ModernDevTools.DebugActions_Testing::ThrowInner(System.Int32)->System.Void;callSite=0`
- 拒绝理由：The result still appears to contain untranslated English.

原文：

[Modern Dev Tools test] simulated failure from DebugActions_Testing.ThrowInner (this is intentional).

模型返回（重试后最终记录，尚未还原提示词占位符）：

[0] 由 DebugActions_Testing.ThrowInner 模拟的故障（此为有意为之）。

## 10 · atc1_b9bc46591b6d18084acb063ad3da4f9ff1770f7c46d99870d3fedf63f6f38e87

- 来源：`Assemblies/ModernDevTools.dll`
- 定位：`assembly=Assemblies/ModernDevTools;type=ModernDevTools.DebugTree;method=ModernDevTools.DebugTree::RootOf(RimWorld.DebugTabMenuDef)->LudeonTK.DebugActionNode;callSite=0`
- 拒绝理由：The result still appears to contain untranslated English.

原文：

[Modern Dev Tools] RootOf failed: 

模型返回（重试后最终记录，尚未还原提示词占位符）：

[0] RootOf 失败：

## 11 · atc1_50c764952c2baf5f88810726d91c51e99a4a0968a9474a4f930f253907fe2314

- 来源：`Defs/KnownIssueDefs.xml`
- 定位：`defType=ModernDevTools.KnownIssueDef;defName=MDT_Reservation;fieldPath=keywords.2`
- 拒绝理由：The result still appears to contain untranslated English.

原文：

trymakepretoilreservations() returned false

模型返回（重试后最终记录，尚未还原提示词占位符）：

trymakepretoilreservations() 返回了假

## 12 · atc1_2244d276064757a165c1388fe93d7e4709e21fda959536a8fc261a6429b810fb

- 来源：`Defs/KnownIssueDefs.xml`
- 定位：`defType=ModernDevTools.KnownIssueDef;defName=MDT_GuiState;fieldPath=keywords.1`
- 拒绝理由：The result still appears to contain untranslated English.

原文：

more calls to beginscrollview

模型返回（重试后最终记录，尚未还原提示词占位符）：

更多对 beginscrollview 的调用

## 13 · atc1_2a13ee10fa859511d31083a9bac1e542e659e6a74b2bc2e8e1b25249bf94d45f

- 来源：`Languages/English/Keyed/ModernDevTools.xml`
- 定位：`key=MDT_ReportToModGithub`
- 拒绝理由：The result still appears to contain untranslated English.

原文：

Report to {0} (GitHub)

模型返回（重试后最终记录，尚未还原提示词占位符）：

报告给 [0]（GitHub）

## 14 · atc1_6c4b3c64fa7887fa6b0552ccc16477ce1e1c91fd5738c8d5de8c6785de2d7050

- 来源：`Languages/English/Keyed/ModernDevTools.xml`
- 定位：`key=MDT_UpdateNotesTitle`
- 拒绝理由：The result still appears to contain untranslated English.

原文：

What's new in Modern Dev Tools

模型返回（重试后最终记录，尚未还原提示词占位符）：

Modern Dev Tools 的新内容

## 15 · atc1_09c83ef50b3bd21d5c5c7f86550ac0314ece71e5e13f8c02ef7708c4d8c455cd

- 来源：`Languages/English/Keyed/ModernDevTools.xml`
- 定位：`key=MDT_ExtendHint`
- 拒绝理由：The result still appears to contain untranslated English.

原文：

Add your own analysis: ship a ModernDevTools.ErrorModuleDef or a ModernDevTools.KnownIssueDef in XML, or call ModernDevToolsAPI.RegisterModule in C# with your own ErrorModule subclass. Full guide: Documentation/RootCauseAnalysis.md.

模型返回（重试后最终记录，尚未还原提示词占位符）：

添加您自己的分析：在 XML 中发布 ModernDevTools.ErrorModuleDef 或 ModernDevTools.KnownIssueDef，或在 C# 中使用您自己的 ErrorModule 子类调用 ModernDevToolsAPI.RegisterModule。完整指南：Documentation/RootCauseAnalysis.md。
