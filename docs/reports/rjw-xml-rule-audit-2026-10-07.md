# RJW XML 文件规则审阅与参考字典

日期：2026-10-07。范围：本机下载的 `RJW-Fluid-EmpireV1.zip`、`RJW-Milk-VariationsV2.0.zip`，以及本机原版和官方 DLC 的语言文件。

## 文件扫描结果

用户本次明确授权运行扫描器分析本地文件，禁止启动游戏。独立文件入口直接编译当前源码中的 `TranslationPolicyXmlScanner`、`TranslationPolicyClassifier`、身份与分组工具、Def 继承解析器，调用与新版 XML 分析器相同的文件扫描函数和分类器，没有运行测试套件或游戏。

| 样本 | Def XML 文件 | 修改前规则候选 | 修改后规则候选 | 本次确认漏项 |
|---|---:|---:|---:|---|
| Fluid Empire V1 | 32 | 129 | 131 | 2 条 `ThingDef.stuffProps.stuffAdjective` |
| Milk Variations V2.0 | 35 | 346 | 346 | 未确认新增可翻译字段漏项 |

候选数字包含样本中的可选依赖内容；实际启用列表、全局已有译文、用户分类及翻译记录会影响最终 AI 队列。本次仅运行文件扫描规则层，没有运行工作台、数据库迁移、字典提示构造或运行时注入。

Fluid Empire 的两条确认漏项为：

- `ROBTRG_Curdwood.stuffProps.stuffAdjective`，原文 `Woodilk`。
- `ROBTRG_NectarResin.stuffProps.stuffAdjective`，原文 `VagBricks`。

该字段描述材料制品的名称修饰语。原版 `Core/Defs/ThingDefs_Items/Items_Resource_Stuff.xml` 自身使用 `stuffAdjective`（如 `golden`、`wooden`），属于通用玩家可见字段。已补 XML 扫描器、分类器和共享扫描字段表，分析器版本提升为 `xml-5`，使旧候选结果重新分析。

原始叶节点独立遍历后与候选按 Def 类型和条目路径对照，再人工检查未召回字段。剩余项以类型引用、资源路径、枚举、数值和布尔配置为主。Milk Variations 中 `foodType=AnimalProduct, Fluid` 是枚举配置，不作为漏翻修改。两个样本的继承警告分别为 16 与 36 条，涉及样本外部的原版或依赖父节点；本轮读取并核查样本自有文本，不能据此确认群友旧截图中的全部英文均已解决。

完整候选、修改前后清单、原始叶节点及比较材料保存在本机交付目录：

`E:\Documents\Codex交付\RJW规则审阅\2026-10-07`。

## 内置参考字典

从本机 `E:\Program Files\RimWorld\Data` 的英文 Def 标签与各内容包 `ChineseSimplified (简体中文).tar` 逐键对应，人工选取 65 条典型物品、装备、作物名称，覆盖 Core、Royalty、Ideology、Biotech、Anomaly、Odyssey。内置源码保存条目键和语言文件来源，交付目录的 `seed-evidence.json` 保存配对证据。本轮内置语种为简体中文，其他语种可由用户维护，不能借用简体中文词条给其他语种。

| 原词 | 建议译名 | 语境 |
|---|---|---|
| go-juice | 活力水 | 游戏药物，不是普通果汁 |
| devilstrand | 魔菇布 | 织物材料 |
| devilstrand | 恶魔菇 | 种植作物 |
| luciferium | 魔鬼素 | 游戏药物 |
| neutroamine | 中性胺 | 制药原料 |
| plasteel | 玻璃钢 | 游戏材料 |
| bioferrite | 活铁 | Anomaly 材料 |
| gravcore | 逆重核心 | Odyssey 物品 |

数据库第 18 版迁移新增参考字典表，按目标语种保存，支持共享范围和关联 Mod 的范围，同一个原词可以保存多个词性、语境和义项。初始化用 `INSERT OR IGNORE` 补内置条目；用户修改、复制或停用的记录不会被初始化覆写或恢复。停用保留记录以避免下次启动再次补入。

设置页新增“参考字典（内置与用户维护）”入口，支持查询、新增、复制、编辑、词性、语境、例句、Mod 范围和停用。所有维护由用户执行，不自动提词、学习、调用 AI 生成词库或修改旧译文。

AI 翻译每次运行读取启用词条，仅把本批原文中匹配且适用范围对应的词条提供给模型；同形异义词保留不同语境供模型选择。模型根据当前原文与语境、词性适配，不进行机械替换或额外词条使用汇报。参考块受现有提示 Token 预算和条目数量/字符预算约束，并计入拆批和试跑预算。字典变化不改变已有译文的原文哈希有效性，已经有效翻译的内容继续排除在 AI 队列之外。

数据库与界面、模型使用效果仅做静态审阅，尚待人工验收。
