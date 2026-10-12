# Modern Dev Tools 翻译失败调查与调试记录改动

证据来自当前测试数据库、Player.log、AutoTranslation_Log.txt、部署 Mod 的 Source 目录与项目源码。数据库只读查询，不运行 Mod。源码改动未经编译、部署或运行验证。

## 结果可追溯性

- 本次运行日志：候选 756、成功 707、失败 49。34 条来自第 25 批编号异常，14 条因英文残留拒绝，1 条报受保护原文未变化。
- [15 条原文、最终被拒绝模型输出、完整条目 ID 与定位](./modern-dev-tools-rejections.md)。输出是数据库保存的单条补试结果，方括号数字是提示词替换标记，不代表最终游戏译文。
- [第 25 批 34 条失败原文和条目 ID](./modern-dev-tools-batch25.md)。本次普通日志仅记录编号异常，没有模型输出诊断文件，不能还原实际响应或原请求顺序。

## 校验逻辑

TranslationResultLanguagePolicy 使用正则清理标签、受保护片段和部分路径，再统计拉丁字母占全部字母的比例，并用正则和空格分词判断是否像英文。既不是大模型判断，也不是单一的“含英文就拒绝”。非拉丁目标语种下，拉丁字母占比至少 65% 且被视为句子时仍可能拒绝；有目标语种且比例低于 45% 才提前放行。没有可靠地区分专名、方法名和一般英语。

例如 Modern Dev Tools 的新内容中的拉丁字母 14、汉字 4，拉丁占比约 77%；报告给（GitHub）中的拉丁字母 6、汉字 3，占比约 66%。两者都能触发目前的误拒绝。

并非 15 条都误拒绝：Bad attributeRegex in 仍保留英文；AnalysisCompleted 那条译文漏掉受保护的 [Modern Dev Tools]，RestoreProtectedTokens 回退原文，后续校验才报“原文未变化”。实际原因是前缀漏失，错误描述不能理解为模型真的原样返回。

## 技术字符串进入候选的链路

三项完整原句分别是 harmonypatch:、aboutincompat:、wid:，不是更长自然语言句子的截断结果。

| 原句 | 原 Mod 实际用途 | DLL 结论 | AI 复核理由 |
|---|---|---|---|
| harmonypatch: | Module_HarmonyPatches.Diagnose 为 ErrorDiagnosis.Source 拼接类型和方法，形成诊断来源标识 | UNKNOWN_DYNAMIC_FLOW，待判定 | UI label 'harmonypatch:' displayed in diagnostics |
| aboutincompat: | Module_AboutIncompat.Diagnose 为 ErrorDiagnosis.Source 拼接不兼容组合键 | UNKNOWN_DYNAMIC_FLOW，待判定 | UI label prefix for AboutIncompat module |
| wid: | CommunityData.ParseReplacements 为字典键拼接 oldWorkshopId；读取时用同样前缀查找 | UNKNOWN_DYNAMIC_FLOW，待判定 | UI label key wid: with prefix, translated in-game |

ErrorContext.cs 的 Source 字段注释明确它是来源/忽略键。Window_ModernLog 通过 IgnoreIssue(d.Source) 使用它，标题、说明、修复文本另有字段。以上三项不应当作为译文替换目标。

当前 DLL 扫描收集可疑 ldstr 到 review_string_literal，静态数据流未证明其用途时给 Uncertain；复核提示词虽要求代码标识/查找键无需翻译，但本次模型错误判定为界面标签，AI 层的需要翻译结论使其进入翻译。

还发现两个 XML keywords 条目被 AI 认作界面关键词。原 Mod KnownIssueIndex 将这些值转小写后用于 textLower.Contains(kw) 的错误日志匹配，也需要作为技术匹配词处理：trymakepretoilreservations() returned false、more calls to beginscrollview。

## 本次源码修改

1. 调试级别的拒绝记录增加 candidateId，保留 modelTranslation、restoredTranslation、sanitizedTranslation 和具体校验理由；原文通过数据库 ID 查找，不再重复写入这条日志。
2. 批次失败的调试日志打印完整 responseContent，以及 itemIndex/candidateId/sourceFile/locator 对应表；保留已有模型响应诊断文件。复核、翻译与单条翻译补试的异常入口均接通。
3. 翻译编号异常明确指出重复编号或越界编号及允许范围。
4. Memory Drop 分片与最终 Def 应用耗时达到 1000 ms 才记警告；50–999 ms 留在调试记录。原有正常完成耗时摘要保留。

## 热重载耗时解释

AI 翻译任务结束后，已保存成功译文会自动刷新到游戏运行时，即使部分条目失败。XML 通过 Memory Drop 更新当前语言表和 Def，DLL 则请求重载补丁清单。

主线程分片是把可以分批的应用工作分散到多个游戏帧；最终调用游戏自身的前置注入、后置注入和规范化步骤分阶段执行，单阶段调用尚不能进一步拆开。本次 02:11:52 的三个阶段分别 62、58、150 ms，最终 270 ms；不是 API 请求时间，不是翻译失败。

待后续处理：技术候选识别和复核误判、专名/技术名词导致的校验误拒绝、保护前缀漏失的准确错误说明，以及部分失败终态文案。本次没有改变这些业务判断规则，也没有重新调用模型补造第 25 批结果。
