# 工作流入口与业务链静态复审

日期：2026-10-08。范围：打开设置、首次 Mod 名录同步、XML/DLL 分析、AI 复核与翻译、保存、热重载及取消。依据为当前源码、已确认设计、用户本次人工操作反馈及对应日志；没有运行项目代码、自动测试或游戏操作。不是全仓所有功能的验收结论。

## 已确认问题与修复

1. **数据库准备阻塞 UI。** 首次弹窗在构造函数中启动任务，主线程捕获 Mod 列表可以同步完成，随后公共 RunExclusiveAsync 在首次 await 前初始化数据库。本次实际日志升级阶段用时约 17 秒。弹窗改为 PostOpen 启动，两个公共任务重载统一先取得租约，再将初始化、任务登记、业务执行和收尾放在后台。不能只把内部扫描循环放到后台。
2. **数据库初始化/任务登记失败没有正确终态。** 原实现初始化在租约前、任务登记在 try 外；部分异常退出后不能明确发布失败。现在纳入共同异常处理。失败结果写入再次失败时记录警告、保留原始异常，并在 finally 发布内存终态；取消和部分结果仍保留原有语义。
3. **空原生译文 XML 中断整批。** Wolfein Race 的 EclipseTrigger.xml 为 0 字节，数据库记录 Root element is missing。空白内容跳过；目录发现也移入按 Mod 的异常处理。失败 Mod 标记 Failed、保留原因、继续其他 Mod；Failed 不作为已有原生译文，不命中扫描复用。
4. **准备单个 Mod 文件失败会阻止其他 Mod。** BuildTargetsAsync 原来任一异常直接退出；分析入口现在收集准备失败，继续构建其他目标和分析，最后汇总报失败。取消不被吞掉。没有把失败批次报为成功。
5. **XML 部分失败导致 DLL 阶段完全不执行。** 组合分析对两个阶段分别处理异常，保留完成结果、继续另一阶段，最后汇总失败。真正的取消立即退出。
6. **一键翻译在候选分析失败后仍进入 AI。** 原实现把分析异常放入列表后继续调用复核/翻译，可能沿用旧候选。现在真正的候选分析失败中止本次一键 AI 阶段并保留已完成结果。原生译文读取失败已在收集层处理，仍遵循用户确认的继续后续分析/AI 规则。
7. **UI 采集被错误地与 DLL 文件分析互斥。** 用户指出两者职责不同，本身不冲突。删除保存配置和执行分析的旧互斥限制，不自动关闭采集。每个 DLL 分析目标固定一份采集键快照，缓存指纹、实际分类和结果保存均使用同一快照；期间新增采集记录下一次分析使用。原有独立采集开关、时限、译文应用设置保持不变。

## 逐阶段复核记录

| 阶段 | 本次核对的调用链与业务条件 | 静态结论 |
|---|---|---|
| 打开与列表 | DoSettingsWindowContents → OpenOnce/PostOpen → SynchronizeModCatalogAsync；UpdateWorkflowUiReadModel 通过 Task.Run 读摘要 | 修正主线程初始化；摘要读取无 Wait/Result 同步等待未完成任务 |
| 独占与取消 | RunExclusiveAsync 两个重载 → WorkflowTaskCoordinator；初始化、任务登记、终态、租约释放 | 租约在提交后台任务前取得；异常保留失败状态；安全边界检查取消 |
| 原生译文 | CaptureInstalledModsAsync → 目录/版本 → XML → NativeTranslationScans → GetNativeTranslations | 空文件跳过，失败不复用；按目标语种、Loaded 来源和完成状态过滤 |
| 分析 | BuildTargetsAsync → PrepareTargets → XML/DLL RunLane → SaveAnalysisResult/SaveAnalysisFailure | 文件准备和阶段失败隔离；完成结果保留。UI 采集可继续，DLL 分析对每个目标固定证据快照 |
| 复核/翻译筛选 | CountAiCandidates/GetAiCandidatePage、GetCurrentAnalyzerCandidateSql、GetTranslationCurrentSql、GetTranslationReadySql | 校验当前分析版本/语种、分类、原文哈希；已有有效译文排除；读取失败和待恢复写入不作正常可翻候选 |
| 模型调用 | AiWorkflowServices → LanguageModelGateway → AutoTranslatorApiModelTransport → InvokeWorkflowJsonAsync | 保持普通 JSON；模拟分支不调用真实模型；Gemini 拼接非 thought 正文，其他接口读取 message.content；未恢复工具调用实验 |
| 响应与落盘 | ParseIndexedOutputItems → 译文校验 → PendingFileOperations → WriteBatch → CompletePendingTranslations | 逐编号检查返回数量；写文件与完成数据库分阶段；失败保留待恢复记录；人工保存记录 Manual 来源 |
| 热重载 | RebuildManagedTranslationOutputs → ForceReloadAllAsync → Memory Drop 与 DLL 重载请求 | 重建数据库当前已保存译文，不调用 AI；Unity 修改仍由主线程调度。DLL“已请求重载”不等于所有补丁已生效，需人工核对 |
| 取消网络任务 | AiBatchNetworkCoordinator 排队/提交取消检查及 finally 等待已发请求 | 停止不提前释放独占租约；未把尚在返回的请求当成已结束 |

## 采集与分析的业务澄清

此前提出“自动结束采集或提示手动关闭”的选项不成立：它把旧实现限制当成了业务要求。用户已澄清分析读取 XML/DLL，采集来自 UI。按职责移除文件分析的限制，并固定证据快照；本次没有修改采集与运行时译文注入之间的行为。

## 交付状态

本轮源码修复尚未编译部署，用户已关闭原测试游戏。本次静态审阅不能证明编译通过或业务验收通过。此前修复和其他未提交成果均保留。
