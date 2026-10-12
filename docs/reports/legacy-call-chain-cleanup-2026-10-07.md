# 旧实现调用链清理与上传标记改造

日期：2026-10-07。状态：源代码已修改，仅静态审阅，未经编译或运行验证。

## 范围与方法

对整个源码目录建立函数、类型对象和同名调用关系清单，再从启动、四个界面页、后台任务、Harmony 补丁、连接检测及云端上传入口人工审阅旧链路。
清单是文本词法分析，非 Roslyn 语义分析；重载、同名外部成员、属性、接口调度和反射不能仅靠同名关系判定。删除依据是入口迁移与跨文件实际引用审阅，不是机械删除无调用计数的方法。

## 最终调用链

| 业务 | 入口与流向 |
|---|---|
| 新版 XML/DLL 工作流 | WorkflowMainTab → WorkflowBackend → AnalysisWorkflowService / AiReviewService / AiTranslationService → 新版模型传输 → InvokeWorkflowJsonAsync → 共用 HTTP 生命周期 |
| 手工编辑与参考字典 | WorkflowEditorTab / Window_ReferenceDictionary → WorkflowBackend → 数据库及托管输出服务 |
| 连接检测 | SettingsTab → RunConnectionTest → WorkflowBackend.RunApiConnectionTestAsync → 新版 JSON 请求封装 |
| 界面采集 | Harmony UI 补丁 → UIInterceptor.ObserveDisplayedText → DLL 运行观察库；不启动旧翻译队列、不替换显示文本 |
| 文件格式修复 | 设置页 → WorkflowBackend.RunTranslationFileRepairAsync → TranslationFileRepairService → 同步和内存刷新；不调用模型 |
| 上传 | 单个预览 / 两种批量入口 → 上传标记校验 → 数据库来源与本次实际文件统计 → 打包 → 原云端上传协议 |

## 删除与保留

已删除旧工作台及窗口、旧扫描编排、旧失败重试、旧批量翻译与提供方协议、自动术语提取/挖掘/学习、旧 Policy Agent 与影子扫描、旧 UI 缓存替换和后台队列、纯 AI 上传重建、旧上传来源过滤及旧翻译结果 JSON 缓存实现。

保留新版实际复用的语言规则、译文机械校验、HTTP 返回类型/超时/取消/并发恢复、XML 原文与继承读取、原生译文识别、文件来源记录、备份、云端下载与内存注入。共享校验、输出归属和本地文件修复已移入 Workflow/Output。历史设置读取字段保留兼容，旧自动功能没有执行入口。

旧源码、未提交成果与文档在清理前整体备份到 `E:\Documents\Codex交付\旧实现清理\2026-10-07-before`。没有重置 Git、删除用户历史数据文件、构建 DLL、部署、启动游戏或发起真实模型/上传请求。

## 上传规则

三个类别 ID 保持兼容。汉化组精翻权限逻辑不变；AI 和人工类别都是标记，完整上传符合既有文件选择规则的所选内容。每个 Mod、每个语种按本次实际上传文件中的非空译文条目计数。数据库明确记录的本地 AI 条目超过 50% 时禁止人工标记，恰好 50% 允许；来源不明及外部导入算入总数但不算 AI。

匹配来源同时核对数据库的当前条目/版本/文本哈希、输出文件绑定、条目键、实际译文。历史数据库中的默认 AI 来源没有 AI 请求信息时，使用明确的条目来源记录排除旧同步误判。文件同步默认来源改为未知；不按上传类别推断混合包的逐条来源。

预览页禁用不合格的人工标记，编辑后更新占比。编辑保存回各条目原文件，修复旧实现另写汇总文件造成重复键的问题。单个和批量实际发送前再次检查，拒绝时给出原因，不静默改类别、不重新翻译。

## 静态收尾

新增与删除后的方法引用差异没有发现未处理的项目方法调用。外部同名成员 Contains/Run/TryParse/Append 经人工归类不属于被删除的实现。类型差异仅剩 IL 数据流分析器中的 Evidence 属性同名命中，已经人工排除。保留的 HTTP 返回类型与语言规则已单独迁移。

本报告不能替代编译、Harmony 装载或真实业务验收。远端服务端未连接和修改，客户端上传占比、预览界面与游戏内效果均未经运行验证。

## 静态清单规模

| 清单 | 清理前 | 修改后 |
|---|---:|---:|
| 含声明源码文件 | 234 | 179 |
| 方法声明 | 2501 | 1758 |
| 类/结构/接口/枚举声明 | 585 | 419 |

## 清单中已删除或迁移的源码文件

- `AI/AutoTranslatorAPI.DeepL.cs`
- `AI/AutoTranslatorAPI.DeepSeek.cs`
- `AI/AutoTranslatorAPI.ResponseParsing.cs`
- `AI/AutoTranslatorAPI.StructuredProviders.cs`
- `AI/AutoTranslatorAPI.Terminology.cs`
- `AI/AutoTranslatorAPI.TranslationPolicy.cs`
- `AI/PolicyStructuredProviderAdapter.cs`
- `Maintenance/LegacyRepairer.cs`
- `Scanning/AutoTranslatorScanner.ScanOrchestration.cs`
- `Scanning/AutoTranslatorScanner.TranslationPolicyAgent.cs`
- `Scanning/AutoTranslatorScanner.TranslationPolicyShadow.cs`
- `Scanning/AutoTranslatorScanner.TranslationProcessing.cs`
- `Scanning/AutoTranslatorScanner.TranslationReuse.cs`
- `Scanning/AutoTranslatorScanner.Unresolved.cs`
- `Scanning/Policy/TranslationPolicyAgentBatchPlanner.cs`
- `Scanning/Policy/TranslationPolicyAgentBudget.cs`
- `Scanning/Policy/TranslationPolicyAgentModels.cs`
- `Scanning/Policy/TranslationPolicyAgentResolutionPlanner.cs`
- `Scanning/Policy/TranslationPolicyAgentResponseParser.cs`
- `Scanning/Policy/TranslationPolicyApplication.cs`
- `Scanning/Policy/TranslationPolicyEstimator.cs`
- `Scanning/Policy/TranslationPolicyNativeTargetFilter.cs`
- `Scanning/Policy/TranslationPolicyShadowEngine.cs`
- `Scanning/Policy/TranslationPolicySourceFingerprint.cs`
- `Scanning/TranslationPolicyAgentCache.cs`
- `Scanning/TranslationPolicyAgentCoordinator.cs`
- `Scanning/TranslationPolicyPreflightResultCache.cs`
- `Scanning/TranslationResultCache.cs`
- `Scanning/TranslationUnresolvedManager.cs`
- `Terminology/AlignedTranslationMiner.cs`
- `Terminology/TerminologyAgentResponseParser.cs`
- `Terminology/TerminologyApplicationValidator.cs`
- `Terminology/TerminologyCache.cs`
- `Terminology/TerminologyCandidateExtractor.cs`
- `Terminology/TerminologyModels.cs`
- `Terminology/TerminologyMorphology.cs`
- `Terminology/TerminologyPackageSelection.cs`
- `Terminology/TerminologyPromptContextBuilder.cs`
- `Terminology/TerminologyRuntime.cs`
- `Terminology/TerminologySessionStore.cs`
- `UI/Interception/UIDynamicNumberTemplate.cs`
- `UI/Interception/UIInterceptor.Cache.cs`
- `UI/Interception/UIInterceptor.Management.cs`
- `UI/Interception/UIInterceptor.Queue.cs`
- `UI/ModNameTranslationCache.cs`
- `UI/TargetedHardcoded/HardcodedUiAutomaticPipeline.cs`
- `UI/TargetedHardcoded/HardcodedUiPolicyBridge.cs`
- `UI/Windows/Window_HardcodedUiWorkbench.cs`
- `UI/Windows/Window_TerminologyReview.cs`
- `UI/Windows/Window_TerminologySettings.cs`
- `UI/Windows/Window_TranslationPolicyAgentBudget.cs`
- `UI/Windows/Window_UITranslationManager.cs`
- `UI/Windows/Window_UnresolvedTranslations.cs`
- `UI/Windows/Window_WorkbenchBatchReplace.cs`
- `UI/Workbench/TranslationGeneratedOutputOwnership.cs`
- `UI/Workbench/TranslationWorkbenchTab.BatchReplace.cs`
- `UI/Workbench/TranslationWorkbenchTab.DataLoading.cs`
- `UI/Workbench/TranslationWorkbenchTab.Dll.cs`
- `UI/Workbench/TranslationWorkbenchTab.EditingRenderer.cs`
- `UI/Workbench/TranslationWorkbenchTab.ManualXml.cs`
- `UI/Workbench/TranslationWorkbenchTab.ModSelectionRenderer.cs`
- `UI/Workbench/TranslationWorkbenchTab.Navigation.cs`
- `UI/Workbench/TranslationWorkbenchTab.Persistence.cs`
- `UI/Workbench/TranslationWorkbenchTab.RestoreBaselines.cs`
- `UI/Workbench/TranslationWorkbenchTab.Search.cs`
- `UI/WorkbenchTab.cs`
