# V4 Policy 云端判断加速：服务端、管理端与客户端完整实施方案

> 文档 ID：ATC-DES-002
> 当前状态：专项基准。客户端工作流入口、分析触发和本地状态职责服从 [`ATC-DES-003 五步独立翻译工作流`](./v4-five-stage-independent-translation-workflow.md)；云端查询或 AI 分类复核不得暗中重新执行 XML/DLL 分析。实施前参见 [`设计文档索引`](./README.md)。

> 面向没有本地项目上下文的服务端开发者。本文是当前协议基线；旧版“客户端自动上传完整记录”的方案已废弃。技术基线来自原作者提供的 `worker-v2.js`（2026-08-12，约 89 KB 的已打包 Cloudflare Worker）。

## 0. 已确认的云端技术基线

本方案不再按“任意后端框架”描述，必须接入现有 Cloudflare 架构：

- API 运行在 Cloudflare Worker，入口是默认导出的 `fetch(request, env, ctx)`。
- 结构化数据使用 D1，绑定名固定为 `env.DB`；Policy 数据不需要 R2。
- R2 绑定名为 `env.BUCKET`，当前只用于翻译包和附件，不能为了 Policy ID 索引额外写 R2。
- 路由目前集中写在 `fetch()` 的 `if/match` 分发中，未使用第三方 Router。
- 管理鉴权使用 `authorizeRequest()`、`authorizeToken()`、`ROLE_SCOPES`、`requireScope()`；凭据从 `X-Admin-Token` 或 `Authorization: Bearer` 读取。
- 错误由 `HttpStatusError`、`httpError()`、`normalizeError()` 统一转为 `json({ error: message }, status)`。
- 审计采用独立事件表和 `write*Event()`/`tryOptionalDbWrite()` 风格，记录匿名化 IP、User-Agent、操作者和 Metadata JSON。
- Worker 已有 `scheduled()` 和 `purgeSoftDeletedRecords()`，但它只应继续清理现有翻译包；Policy 软删除数据不得被这个定时任务物理删除。
- 公共 CORS 已允许 `GET, POST, PATCH, DELETE, OPTIONS`，并允许 `Content-Type, Authorization, X-Admin-Token`，本功能无需新增请求头。

已验证线上 `https://api.anln666-nas.xyz/api/v1/health` 与国内备用域健康检查正常；截至 2026-08-12，`/api/v1/policy-analysis/...` 仍返回 `404`，说明本功能尚未部署。

`worker-v2.js` 是构建产物，末尾引用的 `worker-v2.js.map` 未随文件提供；它可用于理解和紧急补丁，但正式开发应修改原始 Worker 工程后重新构建，不能长期把打包文件当唯一源码。

## 1. 背景和目标

RimWorld Mod 的 XML 扫描先由纯代码规则判断文本是否需要翻译。只有规则无法确定的候选项才交给 Policy Agent（LLM）判断。相同 Mod、相同源文件版本、相同规则版本的模糊候选项没有必要让每名玩家重复付费分析，因此需要云端复用“哪些稳定候选 ID 需要翻译”的判断结果。

这不是译文仓库。系统只保存不可逆的稳定候选 ID 和必要的 Mod 版本元数据，严禁上传 XML 原文、译文、提示词、API Key 或本地绝对路径。

本方案必须同时解决两个风险：

1. 第一名分析者可能判断错误，不能让一次错误上传不可撤销地污染所有用户。
2. 普通客户端不能拥有删除、覆盖或管理公共数据的权限。

因此接口分为两套：

- 普通用户接口：查询、提交增量证据；只能增加，不能删除或覆盖。
- 管理员接口：查看审计、软删除记录或单项、恢复上一修订；只在服务端管理界面调用。

## 2. 客户端已经实现的行为（服务端必须与之对接）

本地 V4 客户端当前实现如下：

- 云端判断总开关默认关闭，由用户主动开启。
- 翻译 Mod 列表逐项显示：全局关闭、尚未使用、已云端加速、本 Mod 已关闭、重新分析待上传、增量已上传。
- 用户可针对单个 Mod 关闭云端加速。下一次 Policy 分析会同时绕过云端记录和本地 Policy 决策缓存，确保真的重新调用 Agent，而不是复用旧判断。
- 关闭操作本身不会立即调用付费模型，只会选中该 Mod；用户仍需点击正常的 Policy 分析/翻译按钮。
- 云端查询命中不要求本地配置 Policy 模型；只有云端未命中、且本地也不能复用时才需要模型。
- 完整重新分析结束后，结果仅保存在本地“待上传”状态，不自动联网提交。
- 用户在 Mod 列表的管理窗口明确点击“上传新增判断项”后，才调用普通增量接口。
- 上传失败时保留本地待提交数据和同一个幂等 ID，稍后重试不会重新付费分析。
- 普通上传请求只有 `addAllowedCandidateIds`，没有删除列表、替换标志或完整记录覆盖能力。
- 客户端 schema v2 使用 `candidateDomain=xml|dll` 隔离两类候选；本地状态也按 `candidateDomain + packageId` 分开保存。
- schema v1 记录只允许作为 XML 只读兼容，绝不能用于 DLL。
- DLL 普通贡献在服务端 domain-aware 契约部署前由客户端硬性禁止；查询失败按普通缓存未命中降级。

本地状态文件位于翻译包目录的 `Cache/PolicyCloudAccelerationState.v1.json`。它只是 UI 状态与失败恢复记录，不是服务器权威数据。

## 3. 精确记录身份

schema v2 云端记录唯一键必须是：

```text
(candidateDomain, packageId, gameVersion, sourceFingerprint, policyVersion, promptVersion)
```

- `candidateDomain`：只允许 `xml` 或 `dll`；不得省略、猜测或跨域回退。
- `packageId`：统一去空格并转小写。
- `gameVersion`：RimWorld 分支，如 `1.6`。
- `sourceFingerprint`：域内稳定 SHA-256 指纹。XML 域覆盖当前有效 About、LoadFolders、Defs 和目标语言 XML；DLL 域覆盖 About、LoadFolders 以及本次已加载程序集的规范相对路径、SHA-256、MVID 和分析器版本。
- `policyVersion`：机械规则/判断协议版本。
- `promptVersion`：Policy Agent 提示与结构化输出协议版本。

任一字段不同均视为另一条源记录，绝不能跨版本或跨候选域模糊匹配。`modName` 仅供展示，不参与身份判断。XML Candidate ID 必须以 `tpc_` 开头；DLL Candidate ID 必须以 `hardcoded-ui:` 开头，服务端必须拒绝混域 ID。

迁移规则：既有 schema v1 数据只能迁移为 `candidateDomain=xml`。不得根据 Candidate ID 猜测并迁移为 DLL，也不得在同一记录内混装两种前缀。

“Mod 源版本保留数量”与“管理修订历史”是两个概念：

- 同一 `packageId + gameVersion` 可按既有产品约定保留最近 3 个不同 `sourceFingerprint`，便于玩家未同步到最新版时复用。
- 每个精确记录只保留当前修订和紧邻的上一个修订，用于管理员恢复；不保留无限历史快照。审计事件可长期保留，但不保存第三份数据快照。

## 4. 普通用户 API

### 4.1 查询公开加速集

```http
GET /api/v1/policy-analysis/{candidateDomain}/{packageId}/{gameVersion}/{sourceFingerprint}
  ?policyVersion={policyVersion}
  &promptVersion={promptVersion}
```

成功返回 `200`：

```json
{
  "schemaVersion": 2,
  "candidateDomain": "xml",
  "packageId": "author.mod",
  "modName": "Example Mod",
  "gameVersion": "1.6",
  "sourceFingerprint": "sha256...",
  "policyVersion": "1",
  "promptVersion": "4",
  "candidateCount": 120,
  "allowedCandidateIds": ["tpc_..."],
  "analyzedUtc": "2026-08-12T00:00:00Z",
  "complete": true,
  "retainLatestVersions": 3
}
```

要求：

- 只返回当前有效且未软删除的公开项。
- 路径域、响应 `candidateDomain` 和 Candidate ID 前缀必须一致；不一致按无效记录处理，不能由服务端自动改域。
- 待确认贡献不能混入 `allowedCandidateIds`。
- 无记录返回 `404`；软删除、身份不匹配、记录未完成也按 `404` 处理，避免客户端误用。
- 支持 `ETag` 和 `If-None-Match`，便于以后减少流量；第一版可以暂不实现，但 JSON 结构不能随意改变。

### 4.2 提交普通用户增量贡献

```http
POST /api/v1/policy-analysis/contributions
Content-Type: application/json
```

请求示例：

```json
{
  "schemaVersion": 2,
  "candidateDomain": "xml",
  "packageId": "author.mod",
  "modName": "Example Mod",
  "gameVersion": "1.6",
  "sourceFingerprint": "sha256...",
  "policyVersion": "1",
  "promptVersion": "4",
  "contributorId": "本机随机生成的匿名 GUID",
  "contributionId": "本次待提交数据的幂等 GUID",
  "candidateCount": 120,
  "addAllowedCandidateIds": ["tpc_..."],
  "analyzedUtc": "2026-08-12T00:00:00Z"
}
```

建议成功响应：

```json
{
  "accepted": true,
  "idempotentReplay": false,
  "pendingCount": 3,
  "promotedCount": 1,
  "recordRevision": 7
}
```

服务端约束：

- `(candidateDomain, contributorId, contributionId)` 建唯一索引；重复请求返回原结果，不能重复计票。
- 同一个 `contributorId` 对同一精确记录、同一候选 ID 最多算一份独立证据。
- 请求只能增加证据，不能包含 `delete`、`replace`、`allowedCandidateIds` 全量覆盖等字段；发现未知危险字段应返回 `400`。
- `candidateDomain` 必填且只允许 `xml|dll`；XML 贡献只接受 `tpc_`，DLL 贡献只接受 `hardcoded-ui:`。
- `addAllowedCandidateIds=[]` 是合法的完整分析贡献，表示该用户没有发现需翻译项；它可作为“已分析”证据保存，但不能删除公共项。
- 校验候选 ID 格式、长度、总数量、请求体大小、字符串长度和时间格式。
- `candidateCount` 对已存在精确记录必须一致；不一致返回 `409 identity_conflict`，不得静默合并。
- 普通接口不接受原文、译文或任意 XML；建议对请求 JSON 使用字段白名单反序列化。

## 5. 首次误判保护：贡献与公开数据分离

普通上传不能直接等同于公开真值，否则第一个错误的 `allow` 仍会立即污染所有用户。服务端应保存“贡献证据”，再根据发布策略提升为公开项。

推荐默认策略：

- 新候选 ID 第一次由普通用户提交：状态为 `pending`，不进入查询接口的公开集合。
- 至少 2 个不同 `contributorId` 对同一精确记录提交相同 ID：提升为 `active`。
- 管理员可人工将 `pending` 提升为 `active`，或软删除错误证据/公开项。
- 同一匿名贡献者重复安装或伪造身份无法完全防御，因此还需 IP/设备速率限制、异常贡献检测和管理员审计；匿名 ID 不是安全凭证。

如果产品希望首人结果即可加速，可将特定可信贡献者加入白名单，或把门槛做成服务端配置；不建议把全体用户的门槛降为 1。否则本文最初提出的错误结果问题并未真正解决。

## 6. D1 数据模型与迁移

表名沿用现有 D1 的 PascalCase 风格。必须以独立 migration SQL 建表，不能在普通 API 请求中临时执行 `CREATE TABLE`。

### `PolicyAnalysisRecords`

- `id`
- 六字段精确身份及唯一索引
- `mod_name`
- `candidate_count`
- `revision`（乐观并发号）
- `is_deleted`, `deleted_at`, `deleted_by`
- `created_at`, `updated_at`
- `PreviousSnapshotJson`：只保留变更前一版可恢复快照；下一次管理变更覆盖旧快照

### `PolicyAnalysisEntries`

- `id`, `record_id`, `candidate_id`
- `status`: `pending | active | deleted`
- `independent_support_count`
- `activated_at`, `deleted_at`, `deleted_by`
- `(record_id, candidate_id)` 唯一索引

### `PolicyAnalysisContributions` 与 `PolicyAnalysisContributionEntries`

- `id`, `record_id`, `contributor_id`, `contribution_id`
- `candidate_id`（空列表贡献可以另存 contribution header）
- `received_at`, `client_analyzed_at`
- 唯一索引防重放和重复计票

### `PolicyAnalysisEvents`

- 管理员、动作、目标记录/条目、原因、时间、请求追踪 ID
- 审计日志追加写，不允许从普通管理界面删除
- 审计可以长期保留；不得包含原文或译文

### 建议 migration 骨架

```sql
CREATE TABLE IF NOT EXISTS PolicyAnalysisRecords (
  RecordId TEXT PRIMARY KEY,
  CandidateDomain TEXT NOT NULL,
  PackageId TEXT NOT NULL,
  ModName TEXT NOT NULL DEFAULT '',
  GameVersion TEXT NOT NULL,
  SourceFingerprint TEXT NOT NULL,
  PolicyVersion TEXT NOT NULL,
  PromptVersion TEXT NOT NULL,
  CandidateCount INTEGER NOT NULL,
  Revision INTEGER NOT NULL DEFAULT 1,
  IsDeleted INTEGER NOT NULL DEFAULT 0,
  DeletedAt TEXT,
  DeletedBy TEXT,
  PreviousSnapshotJson TEXT,
  CreatedAt TEXT NOT NULL,
  UpdatedAt TEXT NOT NULL,
  CHECK (CandidateDomain IN ('xml', 'dll')),
  UNIQUE (CandidateDomain, PackageId, GameVersion, SourceFingerprint, PolicyVersion, PromptVersion)
);

CREATE TABLE IF NOT EXISTS PolicyAnalysisEntries (
  RecordId TEXT NOT NULL,
  CandidateId TEXT NOT NULL,
  Status TEXT NOT NULL DEFAULT 'pending',
  IndependentSupportCount INTEGER NOT NULL DEFAULT 0,
  ActivatedAt TEXT,
  DeletedAt TEXT,
  DeletedBy TEXT,
  PRIMARY KEY (RecordId, CandidateId),
  FOREIGN KEY (RecordId) REFERENCES PolicyAnalysisRecords(RecordId)
);

CREATE TABLE IF NOT EXISTS PolicyAnalysisContributions (
  ContributionRowId TEXT PRIMARY KEY,
  RecordId TEXT NOT NULL,
  ContributorIdHash TEXT NOT NULL,
  ContributionId TEXT NOT NULL,
  CandidateCount INTEGER NOT NULL,
  ClientAnalyzedAt TEXT,
  ReceivedAt TEXT NOT NULL,
  UNIQUE (RecordId, ContributorIdHash, ContributionId),
  FOREIGN KEY (RecordId) REFERENCES PolicyAnalysisRecords(RecordId)
);

CREATE TABLE IF NOT EXISTS PolicyAnalysisContributionEntries (
  ContributionRowId TEXT NOT NULL,
  RecordId TEXT NOT NULL,
  ContributorIdHash TEXT NOT NULL,
  CandidateId TEXT NOT NULL,
  PRIMARY KEY (ContributionRowId, CandidateId),
  UNIQUE (RecordId, ContributorIdHash, CandidateId)
);

CREATE TABLE IF NOT EXISTS PolicyAnalysisEvents (
  EventId TEXT PRIMARY KEY,
  RecordId TEXT,
  CandidateId TEXT,
  ActorCodeId TEXT,
  Action TEXT NOT NULL,
  MetadataJson TEXT NOT NULL DEFAULT '{}',
  IpHash TEXT,
  UserAgent TEXT NOT NULL DEFAULT '',
  CreatedAt TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS IX_PolicyAnalysisRecords_Package
  ON PolicyAnalysisRecords(CandidateDomain, PackageId, GameVersion, UpdatedAt DESC);
CREATE INDEX IF NOT EXISTS IX_PolicyAnalysisEntries_Status
  ON PolicyAnalysisEntries(RecordId, Status);
CREATE INDEX IF NOT EXISTS IX_PolicyAnalysisEvents_Record
  ON PolicyAnalysisEvents(RecordId, CreatedAt DESC);
```

服务端保存 `ContributorIdHash`，不要保存客户端匿名 GUID 原值。哈希方式可以复用现有 `sha256Hex(new TextEncoder().encode(...))`；如果原作者希望防止离线枚举，可拼接已有 `TOKEN_HASH_PEPPER` 或新增专用 pepper。

## 7. 管理员 API

管理员接口置于 `/api/v1/admin/policy-analysis`，复用现有 `authorizeRequest()` 鉴权。不得在游戏客户端内置管理员 Token，也不得新增另一套管理员账号系统。

在 `ROLE_SCOPES` 中新增：

```js
"policy_analysis:read"
"policy_analysis:moderate"
"policy_analysis:restore"
```

`master` 保留全部权限；建议给现有 `reviewer` 增加三项权限，其他角色默认不增加。每个管理路由分别调用：

```js
const auth = await authorizeRequest(request, env, "policy_analysis:read");
const auth = await authorizeRequest(request, env, "policy_analysis:moderate");
const auth = await authorizeRequest(request, env, "policy_analysis:restore");
```

### 查询与详情

```http
GET /api/v1/admin/policy-analysis?packageId=&gameVersion=&status=&page=
GET /api/v1/admin/policy-analysis/{recordId}
GET /api/v1/admin/policy-analysis/{recordId}/audit
```

详情需显示当前公开项、待确认项、软删除项、贡献者数量、当前修订号和是否存在可恢复的上一修订。

### 软删除整个 Mod 精确记录

```http
DELETE /api/v1/admin/policy-analysis/{recordId}
If-Match: "revision-7"
Content-Type: application/json

{ "reason": "错误的首次分析，准备重新分析" }
```

D1 批处理中执行：保存当前状态到唯一的 `PreviousSnapshotJson`，将记录及其对外可见项标记软删除，修订号加一，写审计。查询普通接口立即变为 `404`。

### 软删除单个候选项

```http
DELETE /api/v1/admin/policy-analysis/{recordId}/entries/{candidateId}
If-Match: "revision-7"

{ "reason": "该项实际不应翻译" }
```

同样先保存一份上一修订快照，再软删除目标项并写审计。普通用户增量接口以后再次收到该 ID 时，建议回到 `pending` 而不是立刻重新激活。

### 恢复上一修订

```http
POST /api/v1/admin/policy-analysis/{recordId}:restore-previous
If-Match: "revision-8"

{ "reason": "误删，恢复上一版" }
```

恢复操作交换“当前快照”和“上一快照”，因此管理员可撤销最近一次管理变更，但不能无限回滚。每次恢复仍须写审计。

### 管理员重新分析流程

1. 在云端界面软删除错误记录。
2. 管理员在游戏客户端对该 Mod 选择“关闭本 Mod 加速并重新分析”。
3. 完成 Agent 分析并人工检查。
4. 在客户端明确上传增量贡献。
5. 云端管理员将可信贡献提升，或由门槛自动提升。

服务端不需要也不应持有玩家的 LLM API Key，更不应自己重跑付费翻译任务。

## 8. 云端管理界面要求（独立前端工程）

原作者提供的 `worker-v2.js` 只包含 API，不包含管理页面 HTML/JS。以下功能必须在现有云端管理端前端仓库中实现，不能误以为修改 Worker 就会自动出现界面。

在现有云端服务的管理界面新增“Policy 判断加速”模块：

- 列表列：Mod 名、packageId、游戏版本、源指纹短码、规则/提示版本、候选总数、公开项数、待确认项数、贡献者数、状态、最后更新时间、修订号。
- 筛选：packageId、Mod 名、游戏版本、有效/软删除、存在待确认、异常贡献。
- 详情页：身份字段、公开/待确认/已删除条目分页、每项支持数、贡献时间线和管理审计。
- 操作：提升待确认项、软删除单项、软删除整条记录、恢复上一修订。
- 所有破坏性操作必须二次确认并填写原因；恢复按钮仅在存在上一快照时可用。
- UI 请求必须带当前 `revision` 或 `If-Match`，冲突返回 `409` 并要求刷新，防止两名管理员相互覆盖。
- 不提供“物理删除”按钮。数据库维护级硬删除必须走离线运维流程。

管理端继续使用现有 `X-Admin-Token`/Bearer 登录状态调用 Worker；不要把 Token 写入前端源码。前端发生 `401/403` 时回到现有登录流程，发生 `409` 时刷新记录并提示修订冲突。

## 9. 安全、限流和可观测性

- 普通 POST：按 IP、`contributorId`、精确记录三层限流；限制单次 ID 数和请求体大小。
- 管理 API：复用现有 Token 鉴权和细粒度 scope；查看要求 `policy_analysis:read`，删除/提升要求 `policy_analysis:moderate`，恢复要求 `policy_analysis:restore`。如果管理端另外使用 Cookie 会话，再对其写操作增加 CSRF 防护。
- 所有 ID 使用固定前缀和长度白名单，拒绝控制字符、超长 packageId 和异常 JSON 深度。
- 指标：查询命中率、404 率、贡献接受/拒绝数、待确认提升率、管理员删除率、恢复次数、身份冲突数、P95 延迟。
- 日志使用 request ID 串联，但不要记录请求中的完整候选 ID 数组；必要时只记录数量和集合摘要。
- 备份必须覆盖当前数据、上一快照与管理审计。

## 10. 状态与 D1 并发规则

```text
不存在 --普通贡献--> pending --达到门槛/管理员提升--> active
active --管理员软删除--> deleted --恢复上一修订--> active
record active --管理员软删除记录--> record deleted --恢复上一修订--> record active
```

普通贡献处理按现有 Worker 风格使用预编译语句和 `env.DB.batch()`：验证身份、写 contribution、写贡献项、按门槛提升 entry、增加 record revision。唯一索引是最终防重边界；遇到唯一约束冲突时应重新读取已有贡献并返回 `idempotentReplay=true`，不能把正常重试当作 `500`。

不要用“先 SELECT、后无约束 INSERT”的方式判断幂等，因为两个 Worker 请求可能并发通过 SELECT。计票也必须按 `COUNT(DISTINCT ContributorIdHash)` 从证据表计算或使用受唯一索引保护的增量逻辑。

## 11. 错误响应约定

为兼容当前 Worker，第一版错误响应沿用现有格式：

```json
{
  "error": "candidateCount does not match the existing record"
}
```

业务代码通过 `throw httpError(409, "...")` 进入现有统一错误出口。未来若全站统一增加 `errorCode`、`message`、`requestId`，应一次性升级所有接口，不能只让 Policy 接口使用另一套错误结构。

建议状态码：`400` 字段/格式错误，`401/403` 管理权限错误，`404` 无可用公开记录，`409` 身份或修订冲突，`413` 请求过大，`429` 限流，`500/503` 服务端故障。

客户端查询失败会回退到本地缓存或 Agent，不阻断任务；上传失败会保留待提交数据，不会重新调用 Agent。

## 12. 上线顺序与验收清单

1. 取得 Worker 原始工程、`wrangler.toml`、D1 migration 目录和管理端前端仓库；`worker-v2.js` 只作为行为参照。
2. 在测试 D1 执行 migration，验证外键、唯一索引和查询计划。
3. 在 Worker `fetch()` 中加入普通查询和贡献路由及对应 handler。
4. 实现幂等、双独立贡献者提升和 `PolicyAnalysisEvents`。
5. 加入管理员路由、权限 scopes、软删除单项/整记录与恢复上一修订。
6. 在管理端前端仓库增加 Policy 管理模块。
7. 部署预发布 Worker，联调真实 D1；确认请求中不存在原文和译文，R2 没有新增 Policy 对象。
8. 分别验证主域与国内备用域，再灰度启用客户端总开关。

必须通过的验收场景：

- 云端命中时客户端没有 Policy API Key 也能完成判断。
- 同一贡献重试 10 次只保存并计票一次。
- 第一个普通用户的错误 ID 不会立即出现在公开 GET 结果。
- 第二个独立用户确认后按配置提升。
- 普通接口无法删除、覆盖或降低集合。
- 用户对单个 Mod 关闭加速后，下一次确实绕过云端和本地缓存。
- 上传失败后重启游戏，本地待提交项仍存在且可用同一幂等 ID重试。
- 管理员软删除单项后普通 GET 不再返回该项。
- 管理员软删除整记录后普通 GET 返回 `404`。
- 恢复上一修订只恢复最近一次变更；更早快照不可恢复。
- 两名管理员并发操作时，旧修订请求得到 `409`，不会静默覆盖。

## 13. Worker 路由接入清单

在现有 `fetch()` 的公共接口区域加入：

```js
const policyMatch = path.match(/^\/api\/v1\/policy-analysis\/(xml|dll)\/([^/]+)\/([^/]+)\/([^/]+)$/);
if (policyMatch && method === "GET") {
  return await handleGetPolicyAnalysis(env, policyMatch, url);
}
if (path === "/api/v1/policy-analysis/contributions" && method === "POST") {
  return await handleCreatePolicyContribution(request, env);
}
```

在现有管理员路由区域加入：

```text
GET    /api/v1/admin/policy-analysis?candidateDomain=xml|dll
GET    /api/v1/admin/policy-analysis/{recordId}
GET    /api/v1/admin/policy-analysis/{recordId}/events
DELETE /api/v1/admin/policy-analysis/{recordId}
DELETE /api/v1/admin/policy-analysis/{recordId}/entries/{candidateId}
POST   /api/v1/admin/policy-analysis/{recordId}/entries/{candidateId}:promote
POST   /api/v1/admin/policy-analysis/{recordId}:restore-previous
```

新增函数建议保持现有命名风格：

- `handleGetPolicyAnalysis`
- `handleCreatePolicyContribution`
- `handleListAdminPolicyAnalysis`
- `handleGetAdminPolicyAnalysis`
- `handleDeletePolicyAnalysisRecord`
- `handleDeletePolicyAnalysisEntry`
- `handlePromotePolicyAnalysisEntry`
- `handleRestorePreviousPolicyAnalysis`
- `writePolicyAnalysisEvent`

所有响应复用现有 `json()`/`text()`，所有可预期业务错误使用 `httpError()`，审计写入可以复用 `tryOptionalDbWrite()`，但删除/恢复的核心状态更新失败时不能只记录 warning 后继续返回成功。

## 14. 部署拓扑待原作者确认

当前已知两个公开入口：

- `https://api.anln666-nas.xyz/api/v1`
- `https://cn-api.anln666-nas.xyz/api/v1`

仅凭打包文件无法判断它们是同一 Worker 的两个路由、两个 Worker 绑定同一个 D1，还是两套独立 D1。开发前必须由原作者确认：

1. 两个域名对应的 Worker 名称和 Cloudflare account。
2. `env.DB` 是否指向同一个 D1 database ID。
3. 如果是两个 D1，哪一个是写主库，贡献接口是否只写主域，以及如何同步到备用域。
4. migration 应对哪些数据库执行、执行顺序和回滚方式。

推荐只有一个权威写库；备用域可访问同一 D1，或只读复制后的公开集合。不能让两个独立 D1 同时接受贡献而没有合并机制，否则贡献门槛、软删除和修订号会分叉。

正式部署所需材料：Worker 原始源码、构建命令、`wrangler.toml`（可隐去 secret，但需保留 binding 名和资源 ID）、D1 migrations、两个域的部署关系、管理端前端仓库及最小权限 Cloudflare 成员/API Token。不要发送 `MASTER_SECRET` 或主账号密码。

## 15. 明确不在本功能范围内

- 不上传或分发翻译文本。
- 不评价译文质量，不覆盖人工汉化。
- 不在服务端调用 LLM。
- 不允许普通用户删除公共数据。
- 不把匿名 `contributorId` 当作真实身份或质量保证。
