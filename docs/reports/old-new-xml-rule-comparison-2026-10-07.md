# 新旧 XML 收集规则静态对照

> 2026-10-08 补充：用户已确认未知字段的自然语言宽召回，只进入待复核，不直接判为需要翻译。`xml-3.01` 实施及静态审阅见 [XML 未知文本宽召回](./xml-unknown-text-recall-2026-10-08.md)；下文的“尚待确认”保留为当时状态。

日期：2026-10-07。性质：静态代码审阅及经用户授权的确认漏项修复，未运行扫描器、项目、编译或测试。以下差异记录修复前状态，实施状态见末节。

## 对照范围

- 旧版：Git `622d6af`（3.0）以及后续旧扫描器 `7c24701`、`d587640` 的 Def 提取规则；当前保留的 `AutoTranslatorScanner.DefExtraction.cs` 与共享字段表用于核对后续能力。
- 新版：当前工作区 `TranslationPolicyXmlScanner.cs`、`TranslationPolicyClassifier.cs` 和 `XmlWorkflowAnalyzer` 实际调用链。
- 本文比较 Def XML、英语 Keyed/DefInjected 的收集及 XML 分类；不是 DLL IL 规则、运行时注入或完整版本行为对照。

新版收集器声明沿用 V3 候选规则，但用户定义的“旧版”还包括 V3 之后、界面和逻辑改造之前的版本。只对照 V3 会遗漏这些后续扩充。

## 结论

旧版后续扩充并未全部迁入新版。新版分类器已经认识许多扩充字段，但收集器会先过滤它们；未进入候选库的条目不会因后续 AI 复核而恢复。

对照 `d587640` 共享字段表，40 个旧版精确字段不在新版收集器精确字段表中。其中 20 个也不符合新版字段名后缀规则；另外 20 个虽然符合后缀，仍可能因单词被视为引用标识而遗漏。数字是字段规则差异数，不是某个 Mod 的实际漏翻条数。

## 确认的规则差异

### 1. 20 个明确字段缺少收集入口

这些字段不在新版精确表中，也不符合新版 label/description/string/text/message/name/desc 后缀：

- 阵营复数：`pawnsPlural`。
- 身世、头衔：`title`、`titleShort`、`titleFemale`、`titleShortFemale`、`titleMale`、`titleShortMale`、`subtitle`。
- 称谓/主题：`theme`、`member`。
- 特殊成功提示：`successMessageNoNegativeThought`。
- 提示与说明：`tooltip`、`explanation`、`caption`、`extraTooltip`、`disabledReason`、`settingsTooltip`。
- 名称修饰与状态行：`labelShortAdj`、`baseInspectLine`、`inspectLine`。

本机官方文件佐证字段确为业务文本，例如 Royalty `Defs/BackstoryDefs/Shuffled/ImperialCommon_Child.xml` 第 6 行 `title=serving boy`、第 8 行 `titleShort=house boy`，Anomaly `Defs/FactionDefs/Factions_Player.xml` 第 10 行 `pawnsPlural=colonists`，Odyssey `Defs/MentalStateDefs/MentalStates_Special.xml` 第 12 行 `baseInspectLine=Mental state: Fleeing in terror`。这些官方样本用于说明字段语义；不表示新版会去翻译官方内容包。

### 2. 20 个字段仅保留了后缀，丢失精确字段豁免

`ideoName`、`successMessage`、`failureMessage`、`failMessage`、`warningMessage`、`fuelLabel`、`fuelGizmoLabel`、`permanentLabel`、`destroyedOutLabel`、`customLetterLabel`、`customLetterText`、`confirmationDialogText`、`invalidTargetMessage`、`cannotUseMessage`、`targetingLabel`、`targetLabel`、`gizmoLabel`、`gizmoDescription`、`settingsLabel`、`settingsDescription`。

多词文本通常仍可通过新版后缀规则，但无空格、由拉丁字母等组成的单词可能先被 `V3LooksLikeDefReferenceValue` 拦住。旧版后续扫描器对明确文本字段没有这个同样的拦截。因此不能把“后缀支持”视为完整迁移。

### 3. 明确文本列表漏迁

旧版对 `thoughtStageDescriptions` 和其他以 `stageDescriptions` 结尾的文本列表有专门入口；新版列表入口主要依赖父字段名或包含 rule 的字段名。

本机 Biotech `Defs/PreceptDefs/Precepts_Xenotype.xml` 第 33–45 行包含 `Preferred xenotype`、`Disliked xenotype`、`All preferred xenotypes` 等列表条目。静态追踪新版时，叶字段为 li，父字段 thoughtStageDescriptions 不满足后缀，也不含 rule；这批 Def 原文不会由该收集入口生成候选。分类器虽然支持 `.thoughtStageDescriptions.`，此时已没有机会使用。

旧版对 `.rulesStrings.` 也有已知路径豁免，新版主要通过父字段含 rule 的逻辑召回。一般完整语法规则仍能进入候选，不能将所有 rulesStrings 统一列为漏扫；需区分字面词、可翻译语句与不可翻译语法引用。

### 4. 特定路径和扩展字段名没有全部迁移

- 旧版 `.resource.name` 是明确文本路径。新版普通多词 name 可能进入候选，但单词可被当作引用；而 name 并非分类器精确文本字段，进入后也可能是待判定。
- 旧版支持 title、theme、member、tooltip、caption、prompt、hint、reason、header、subheader、option、setting、category、button、toggle、tab 等字段名后缀，以及字段名中含 message。新版收集器只保留较早的七类后缀。
- 对应字段不存在精确表豁免时，旧版与新版还都可能受值过滤影响。因此这里是“规则缺失”，不能据此宣称所有这些字段都曾被旧版收集。
- category/setting 等名称可能表示配置结构，宽泛后缀不宜直接作为新版必翻规则照搬。需要字段路径或实际样本确定业务语义。

### 5. 宽召回能力与分类层存在差异

旧版后续实现对未知字段中的自然语言句子有兜底；启用旧 Policy 模式时还会扩大非保护路径叶节点召回。新版在字段收集阶段没有等价宽召回入口。

这一差异不能直接解决为“全部进入必翻”：旧宽召回可能包含枚举、配置或引用。若要保留，合理方向是进入待判定候选，再由分类裁决，而不是强制翻译。

新版还新增/保留了明确排除结构字段、布尔值、数字、受保护 Def 类型和纯语法引用等分类。这些是分类策略差异，不能仅因旧版召回过就认定应恢复翻译。

## 已保留的能力与边界

- 对照 V3 原始精确文本字段表，没有发现旧精确字段被从新版表中删除；主要缺口来自 V3 之后的旧版扩充。
- jobString、customSummary、summary 等已知路径仍保留。
- Keyed/DefInjected 语言文件直接读取条目，通常无需依赖上述 Def 字段规则；因此源 Mod 自带英语语言文件时可能掩盖 Def 收集缺口。源 Mod 只在 Def 中写文本时缺口才直接暴露。
- 新版 Def 继承解析已接入；不能把未迁移的字段规则与继承能力混为一谈。
- 新版三处 Def 叶节点入口（流式、XElement、XmlNode）均使用同一套窄规则，后续修复须同步，不能只修单个入口。
- 上一轮补充的 stuffAdjective 在旧精确规则中不存在；但旧 Policy 宽召回模式可能把它作为普通叶节点收集。此前“旧版会漏”的说法应限定为常规字段收集，不能扩展成旧版所有模式均无法读取。

## 对照后提出的处理顺序

1. 先补回已明确属于文本的精确字段、明确文本列表及 resource.name 路径；保留新版结构引用与语法保护。
2. 再核对通用后缀与未知句子兜底的范围。明确文本字段可以直接裁定；语义不明字段只进入待判定，避免误翻配置。
3. 将收集层与分类层使用的已知字段/路径集中维护，避免分类器认识、收集器不认识的两份名单漂移。
4. 提升 XML 分析规则版本，使已经扫描过的 Mod 能重新生成遗漏候选；不覆盖已有有效译文。

代码依据：旧 DefExtraction 第 64、85、201、216、283–290 行；新版 XmlScanner 第 18、474–490、591–599、637–650、666–730 行；分类器字段表及 Classify；WorkflowAnalyzers.ReadFile 的候选收集→分类→入库调用顺序。

## 授权修复实施状态

用户在对照后明确要求“修复吧”。本轮实施明确文本语义的恢复：

- 收集器删除独立的 V3 精确字段副本，直接调用分类器共享的已知文本字段规则。40 个旧扩充字段全部由同一名单识别，同时保留分类器中已有的明确文本字段；三个 Def 叶节点入口同步。
- 共用已知文本路径，恢复 resource.name、rulesStrings、thoughtStageDescriptions 及其他 stageDescriptions 文本列表。资源名称可通过分类进入需翻译状态，不能只收集后留为未知。
- 明确字段与列表中的普通单词不再因引用外观被前置丢弃。数字、布尔、资源路径、结构引用、受保护类型和纯语法引用仍由既有分类保护裁决。
- XML 分析器版本升为 xml-6，并更新分析指纹说明。下一次执行 XML 分析时淘汰旧分析结果；本轮没有自动执行扫描，亦没有修改已有译文记录或译文覆盖规则。
- 通用宽后缀及未知句子兜底仍未实施，等待另行确认其召回范围，不能将它们描述为已经修复。

本轮仅静态审阅调用链及源代码差异，未编译、部署或进行运行验证。

后续用户明确要求验证，已执行独立 XML 规则验证，199 项定向检查失败 0 项，并完成两份真实 Mod 条目/分类前后对照。详细结果与未验证范围见 [XML 规则修复验证](./xml-rule-verification-2026-10-07.md)。上述“仅静态”描述指最初修复轮次。
