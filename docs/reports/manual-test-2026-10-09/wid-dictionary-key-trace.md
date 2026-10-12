# wid: 未命中字典键规则的静态调查

2026-10-09。只读实际 DLL 元数据、现有日志和 SQLite，未执行 Mod、扫描器或游戏，未修改业务源码。使用独立 Python 元数据读取工具解析二进制，不加载执行目标程序集。

## 实际输入与分析记录

- 实际 Mod：E:/Program Files/RimWorld/Mods/WS_3771602203/Assemblies/ModernDevTools.dll。
- SHA-256：55F31B4ED2CBB346D6E298ECFBC7767ABFCEF3DAA686243D776D4CB7696374A1，与候选 context 中的 assemblySha256 一致。
- 测试版 ATC DLL SHA-256：F8B71AC32E6ECEB2DCCF6D89C12D9BDF9EDA9B06209A6E3FEE0D59A7AD86BC1A。
- 日志北京时间 02:04:36 开始 DLL 分析、02:04:37 进入静态分析、02:04:47 完成，候选 957。
- AnalysisRuns 记录 dll-3.06、957 条；旧 dll-3.02 是 8 月历史记录。候选最终 DLL 理由 UNKNOWN_DYNAMIC_FLOW。
- 本次 Workflow 扫描传 persistLegacyDecisionState=false，使用临时 baseline 再由 Cecil 结果覆盖；旧 HardcodedUiAnalysis.v1.json 中无该 Mod 记录。不是该旧决策文件把最新分类替换回去。

## 实际 DLL 指令证据

ParseReplacements 方法的相关位置：

| 方法体偏移（解析器含方法头偏移） | 操作 |
|---|---|
| 0x1ca | 加载字典局部变量 |
| 0x1cb | 加载字符串 wid: |
| 0x1d0 | 加载 ID 局部变量 |
| 0x1d2 | 调用 System.String.Concat(string,string) |
| 0x1d7 | 加载 Replacement 值 |
| 0x1d9 | 调用 Dictionary<string,Replacement>.set_Item(TKey,TValue) |

set_Item MemberRef 签名字节为 20 02 01 13 00 13 01：实例方法、两个参数、返回 void、参数分别为类型泛型参数 0 和 1。声明类型 TypeSpec 为 15 12 55 02 0e 12 80 a0，实际构造类型为 Dictionary，两个类型参数，其中第一个是 string。即元数据调用参数使用 TKey，并非直接的 System.String。

## 确定的代码缺陷

HardcodedUiIlDataflowAnalyzer.ExecuteCall 在调用 TryGetNonUiArgumentReason 之前要求 IsStringLike(call.Parameters[index].ParameterType)。IsStringLike 只接受 System.String、Verse.TaggedString、UnityEngine.GUIContent，没有把泛型参数代入声明类型的实际泛型实参。

所以 set_Item 的第一个参数在元数据中是 TKey，前置检查返回 false，根本没有进入已有的“字典第一个参数 → NON_UI_LOOKUP_KEY”规则。

Concat 的来源传播已经存在，字典 set_Item 的识别也已经存在；确定遗漏是调用点的泛型参数类型解析。仅添加 wid: 黑名单没有必要，也不能修复其他泛型字典键的同类问题。

## 后续修改边界

在调用点解析参数的实际类型：声明类型泛型参数对应 GenericInstanceType 的实参，方法泛型参数对应 GenericInstanceMethod 的实参；无法解析时保留未知，不将所有泛型参数一律当 string。再调用既有参数位置规则。

修复后需重新分析，因为当前 dll-3.06 的已有分类缓存不会自行变成新结果。此次只完成原因调查，不宣称重新扫描已得到正确分类，也不把这一原因套用到另外两个诊断来源字段。
