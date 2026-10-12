# Token 估算与接口用量调试样本

2026-10-09。用户授权添加逐次调用统计，为以后拟合提供数据。本次只添加源码及静态审阅，未编译、未调用模型、未运行项目、未部署。

## 如何取得样本

设置日志等级为 Debug 后进行正常 AI 复核/翻译。现有持久化 ATC 日志中检索 `workflow.token_sample`，后面是单行 JSON。每次尝试分别写入 input 和 response（抛出异常时为 exception）；拟合只取 response 行中 usage_status=reported 的样本，避免把输入和响应两行重复计数。

同一次工作流模型调用使用同一个 call_id；attempt 区分并发限制重试/空响应重试，structured_attempt 区分空响应纠正轮数。单条译文补试是独立调用，purpose=ai_translation_retry，可与普通首轮分开统计。request_scope 与现有批次日志关联。

连接测试/直接调用没有可靠的业务提示词切分，split_known=false，固定/动态字段为 null；不能将其当作固定开销为零的复核或翻译样本。预算拒绝、异常及接口未报告用量也不得作为零用量样本参与拟合。

## 字段口径

| 字段 | 含义 |
|---|---|
| schema / estimator | 日志结构版本 / 当前粗估算法版本 |
| provider / model / response_model | 配置厂商、请求模型名、接口报告模型名（未报告则 null） |
| purpose / request_scope / item_count | 阶段、批次关联、条目数 |
| source_chars | 条目原文总长度 |
| fixed_chars / estimated_fixed_tokens | 通用任务指令和输出契约的长度/粗估 Token |
| dynamic_chars / estimated_dynamic_tokens | 本批条目、定位、语境、历史分类、文件表、参考字典及对应 JSON 包装的长度/粗估 Token |
| system_chars / estimated_system_tokens | 真实发送的额外 system 指令长度/粗估 Token；空响应重试附加的 system 指令也重新统计 |
| prompt_chars / estimated_prompt_tokens | 实际完整业务提示词长度/粗估 Token |
| estimated_input_tokens | 完整业务提示词估算 + system 指令估算，未加 30% 预算余量 |
| estimated_output_tokens | 当前业务层对输出的预测，未加预算余量；不是实际输出上限 |
| actual_input_tokens / actual_output_tokens / actual_total_tokens | 接口直接报告的用量，缺失为 null；不使用本地回退估算填充 |
| usage_status / response_success / budget_denied | 用量是否报告、返回是否成功、是否被本地预算拦截 |
| protocol_framing_estimated | 当前固定 false：消息角色边界等厂商协议额外 Token 未单独估算 |

字符数沿用项目 string.Length，即 UTF-16 长度。固定与动态边界由提示词构造器明确提供，不通过搜索文字猜边界。翻译的动态部分包括按本批挑选的参考字典；复核的动态部分包含文件表和条目 JSON。接口 system 部分另列，Google 当前请求没有这段 system 指令。

按不同片段分别调用现有估算器时，舍入与分词边界可能导致片段 Token 相加不等于完整业务提示词的估算；因此同时保留 estimated_prompt_tokens，不能强行补差到动态字段。缓存不抵扣，actual_input_tokens 使用接口总输入字段。

## 静态审阅

- AI 复核、批量翻译、单条补试都传入准确的固定前缀边界；原用于分批的提示词函数保留同样的文本构造路径。
- 日志写在每次发送尝试及返回处，早于正文提取与业务 JSON 校验；业务拒收结果也不丢失返回用量。
- 当前重试结构下，每次 sendAttempt 均有独立 attempt 编号；未发送成功不假定零用量。
- Info/Error 等级不执行样本切分、额外估算或 JSON 统计解析；Debug 样本仅输出统计与模型/阶段标识，不输出正文、请求 payload、URL 或密钥。
- 未修改估算规则、30% 预算余量、输出上限、实际提示词或分批阈值；未添加自动拟合或自动调参。

本记录描述的是源码接线与静态判断，尚无这次日志格式的真实运行样本。
