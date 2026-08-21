# Unity Task Admission Pool Specification

本文件定义 `unity_cli` 面向 Editor 与 Runtime 的公开根调用准入池最终语义，是产品实现、契约更新和定向验证的 source of truth。它不属于当前安装产物；产品实现完成前，不得把本文语义写入当前用户文档或安装后的 `skills/unity_cli`。

## 1. 目标与非目标

### 1.1 目标

- 在多个 AI 会话并发调用同一 Unity 进程时，把可进入业务执行面的公开根调用数量维持在固定上限内。
- 在 CLI 发送业务请求前，由 Unity 原子决定是否放行，消除先读状态再发送造成的竞态。
- 把准入等待超时与放行后的业务执行超时分开；准入失败必须能证明业务 effect 未开始。
- 保留现有 mutation lease、completion proof、durable coordinator、session attestation 和 outcome-unknown 语义。
- Editor 与 Development Player Runtime 使用同一准入语义，并保留各自现有 transport 安全边界。

### 1.2 非目标

- 不替代 Editor 全局 mutation lease，也不允许多个 mutation 同时取得业务所有权。
- 不提供跨 Unity 进程、跨 Editor/Player session 或跨项目的全局池。
- 不提供严格 FIFO、公平队列、AI caller 身份或调用方配额。
- 不把 Registry、Health、项目文件或请求级持久文件用作实时任务队列。
- 不向公开 Tool descriptor 或业务成功 DTO 暴露 token、队列深度、listener 或内部池状态。

## 2. 术语与计数边界

### 2.1 Target session

一个池只属于一个已验证的 live Unity 进程 session。Editor session 与每个 Development Player Runtime session 分别拥有独立池；即使它们属于同一 canonical project，也不共享计数。

### 2.2 公开根调用

公开根调用是用户通过公开 CLI 发起、需要选定 live Unity target 并形成一个最终 JSON envelope 的一次调用。以下调用各计一个 root：

- `tools run <tool>` 的实际执行。
- `command <name>` 的实际执行。
- 作为独立公开命令发起并需要 live target 的 catalog、describe 或读取调用。
- `editor.state`、`editor.dialogs` 等公开观察调用。
- `--dry-run` 调用，只要它需要 live Unity target。

同一公开 root 内部产生的 catalog resolution、readiness、start、status、terminal、rebind 和 completion readback 都属于同一个 root，不得重复占槽。

`instances`、`target`、`doctor`、`init`、`extensions` 等 CLI 控制面不因本规范自动成为 Unity root；只有其内部明确进入 live Unity 业务执行时，才按对应业务 root 计数。

### 2.3 内部控制请求

只有以下内部请求绕过根任务池：

- transport health 与 identity attestation。
- admission acquire、consume、revoke 与内部清理。
- 公开业务调用为解析同一 live descriptor 发出的隐藏 catalog 请求。
- durable root 的内部 status、terminal recovery 和 exact cleanup。
- domain reload、listener generation 交接和 session shutdown 清理。

公开读取即使纯只读也不因只读属性绕过任务池。绕过请求不得产生业务 effect，不得调用任意 Tool handler，也不得成为公开成功结果。

### 2.4 Grant、Consumed root 与 Active

- `grant`：Unity 已原子预留一个槽，但 CLI 尚未提交首个业务请求。
- `consumed root`：首个业务请求已用一次性 token 消费 grant，该 root 已进入执行所有权。
- `active`：所有未失效 grant 与尚未达到可信 terminal 的 consumed root 数量之和。
- `gate`：`open` 或 `closed` 的高低水位状态。

同一个 root `requestId` 在同一 session 内只能对应一个 active identity。同 ID 不同业务指纹必须 fail closed；同 ID 同指纹的 attach/recovery 不增加计数。

## 3. 配置

Editor 项目级配置由现有 `UnityCliProjectSettings` 持有：

```text
TaskPoolMaxActive = 5
TaskPoolReopenAt = 3
```

约束为：

```text
TaskPoolMaxActive >= 2
1 <= TaskPoolReopenAt < TaskPoolMaxActive
```

配置只在 Editor 启动或下一次 domain reload 时读取。不得在已有请求执行中即时改变有效值，不得允许不同 CLI 调用携带各自池配置。

Development Player 的构建 sidecar 必须投影构建时生效的同一组配置。Runtime sidecar 使用严格 schema；新增字段时同步升级当前格式版本，Editor build injection 与 Player reader 只保留同一当前格式。已构建 Player 的值固定到该构建，配置变化后必须重新构建才会生效。

如果新配置的 `maxActive` 小于同 session 恢复出的 active 数量，不取消任何已接受 root；gate 保持 closed，直到 active 降到新的 `reopenAt` 或以下。

## 4. 高低水位状态机

初始状态为 `open`、`active=0`。所有 acquire、consume、terminal、revoke 和恢复操作必须在同一个 session-local lock 下原子更新。

### 4.1 Open

- `active < maxActive` 时可以发放 grant，并原子执行 `active += 1`。
- 发放后若 `active == maxActive`，gate 立即切换为 `closed`。
- `active` 未达到 `maxActive` 时保持 `open`。

### 4.2 Closed

- `active > reopenAt` 时拒绝新 grant。
- 从 `maxActive` 降到 `maxActive - 1` 不重新开放。
- 只有 `active <= reopenAt` 时才切换回 `open`。
- 默认配置下，active 从 5 降到 4 仍关闭，降到 3 时开放；随后最多原子发放两个 grant，到 5 再关闭。

Unity 不保存池外等待者，不建立无界 FIFO。未获 grant 的 CLI 在自身进程内等待并重新 acquire；因此系统只保证容量与原子性，不保证跨进程严格公平。

## 5. 原子准入协议

### 5.1 禁止 check-then-send

不得先读取 `available=true` 再发送业务请求。该模式允许多个 CLI 同时观察空位并一起越过上限。唯一合法入口是原子 acquire：成功响应本身已经占用一个槽。

### 5.2 Acquire route

Editor 与 Runtime listener 都提供内部 `POST /bridge/admission/acquire`。该 route：

- 使用与 `/health`、`/bridge/execute` 相同的 expected instance/session/target/listener attestation headers。
- 只访问 session-local 有界内存和 monotonic time，不进入 Editor/Player 主线程队列，不调用 Unity API。
- 不进入公开 Tool catalog，不产生公开 Tool envelope，不写 transaction log。
- 对一个 acquire 请求最多发放一个 grant。

内部请求至少包含：

```json
{
  "protocolVersion": 1,
  "requestId": "req_<uuid>",
  "instanceId": "unity-<sha256>",
  "target": "editor|runtime",
  "command": "<resolved-public-command>",
  "rootFingerprint": "<sha256>"
}
```

`rootFingerprint` 必须绑定已解析的 command、typed business input、dry-run 状态、selected instance、target 和当前 completion contract identity，但不得包含尚未创建的执行 deadline。不同业务内容不得复用同一 grant。

成功 grant 的内部响应至少包含一次性随机 token 和 grant 失效时间；未放行只返回 `granted:false` 与内部建议等待值，不创建 server-side waiter。

### 5.3 Token

- token 使用至少 256 bit CSPRNG 随机值。
- token 在公开输出、descriptor、日志、错误 details 和 transaction log 中均不得出现。
- 未消费 token 绑定当前 instance、session、listener generation、root requestId 和 root fingerprint。
- 未消费 token 默认 2000ms 失效；失效时原子移除并减少 active。
- token 只能消费一次。不同 root、fingerprint、session 或 generation 使用同一 token必须 fail closed，且不得执行 handler。

首个业务 `POST /bridge/execute` 通过私有 header 携带 token。token 不进入 immutable Bridge request body，因此不改变 request digest。Bridge 必须在 WorkQueue 入队前原子消费 token；消费成功后，所有权从 generation-bound grant 转为 session-bound consumed root。

同一 session 后续发生 listener generation 交接时，已消费 root 可以按现有 exact request/digest 规则 attach 或恢复；未消费旧 generation token 必须作废。

### 5.4 CLI 轮询

CLI 只轮询 acquire，不轮询普通 `available` 状态。一次未放行后，以 100ms 为基准加入每次独立随机抖动：

```text
sleepMs = random integer in [80, 120]
```

不得并行发出同一 root 的多个 acquire。每次响应完成后才能安排下一次；CLI 退出、target identity 失效或准入 deadline 到达时必须停止轮询。

100ms 轮询是内部实现，安装后的公开 Skill 只说明 CLI 会自行等待，不指导 AI 手工循环调用。

## 6. 双预算 timeout

设调用归一化后的 Tool timeout 为 `T`。同一个数值分别用于准入等待和放行后的执行，不能相互借用。

### 6.1 准入预算

```text
admissionDeadline = dispatchStartedAt + T
```

参数解析、live target 绑定、同一业务 root 的隐藏 catalog resolution、lifecycle/identity 检查和 acquire 轮询都消耗准入预算。schema、instance、blocker 或 identity 错误继续返回各自稳定错误；只有 target 可寻址但 gate 在 deadline 前始终未发放 grant 时，才返回公开 `E_UNITY_BUSY`。

准入超时必须满足：

```json
{
  "code": "E_UNITY_BUSY",
  "transient": true,
  "retryAfterMs": 100,
  "message": "The Unity task pool did not reopen before the admission deadline; no business request was sent.",
  "nextActions": [
    "Wait for active Unity CLI tasks to finish, then submit a new request."
  ]
}
```

该错误是 effect-before 的可信 terminal：没有业务 Bridge request 入队，没有 Tool handler 执行，调用方稍后提交新 root 不构成 outcome-unknown 重放。

### 6.2 执行预算

CLI 收到 grant 时只刷新一次：

```text
executionDeadline = grantObservedAt + T
```

首个业务 POST、compile/import/domain reload、listener rebind、内部 status、terminal recovery 和 completion proof 全部共享该 execution deadline。任何子请求、重绑、token 重取或 status 轮询都不得再次刷新。

如果 grant 后、首个业务 POST 前 token 因 generation 变化或 2000ms TTL 失效，CLI 只能在当前 execution deadline 的剩余预算内重新 acquire；重新获得 token不创建第三份预算。

首个 mutation POST 后继续遵守现有 fence：Editor session 改变时禁止向新 session 第二次 POST；终态前 timeout、断连或无法证明结果仍返回 `E_EXECUTION_TIMEOUT` 与 outcome unknown。

### 6.3 Host 预算

公开 Skill 和当前用户文档在产品完成后必须把命令 host 最小等待预算更新为：

```text
hostWaitMs >= 2 * T + 5000
```

CLI 在准入或执行预算结束后仍遵守现有 stdout 完成与最多 1 秒退出收敛规则。host 提前停止等待不证明 CLI、grant 或 Unity effect 已停止。

## 7. Root 生命周期与释放

### 7.1 状态

每个槽只能按以下方向流转：

```text
granted -> consumed -> terminal -> removed
granted -> expired|revoked -> removed
```

禁止 consumed root 回到 granted，禁止 terminal root 重新占槽。exact terminal recovery 只返回已有终态，不重新增加 active。

### 7.2 Terminal owner

每类公开 root 必须在实现时声明唯一 terminal owner：

- direct Bridge Tool：`BridgeWorkItem` completion owner。
- durable compile/build/refresh/package/test/navmesh/occlusion root：对应 coordinator。
- Runtime Tool：Player main-thread request owner。
- CLI 与 Unity 协同的 hybrid root：持有 durable root 的 coordinator；内部 status 不拥有槽。
- 纯 CLI 本地工作若未发送 Unity 业务请求：CLI 只能通过内部 revoke 释放未消费 grant。任何已经 consumed 的 Editor/Runtime root 都只能由对应 Unity terminal owner 或 exact cleanup 释放，CLI 不提供通用 complete 旁路。

不得由多个 owner 竞争释放同一槽。所有释放都必须匹配 session、root requestId、root fingerprint 和内部 token identity，并具有幂等性。

### 7.3 Deadline 与 outcome unknown

未消费 grant 到 TTL 必须释放，因为能证明业务请求未发送。

已消费 root 不能仅因为 CLI execution deadline 到达就无条件释放。如果 terminal owner 已返回可信 terminal 或证明 effect 未开始/已停止，可以释放；如果 Unity effect 仍可能运行或结果未知，槽继续占用，直到：

- terminal owner 发布可信 terminal；
- exact cleanup 证明不再有业务所有权；
- 所属 Unity process session 结束。

该规则避免 timeout 后立即放进新任务与仍运行的旧 effect 重叠。最坏情况下最多保留 `maxActive` 个 outcome-unknown root，状态仍有界；恢复需要内部 terminal/status 收敛或结束对应 Unity session。

### 7.4 CLI 断开

HTTP client 断开不释放已消费 root，也不取消 coordinator 已接受的 mutation。未被消费的 grant按 TTL 清理。CLI 退出后，Unity terminal owner继续完成、持久化终态并释放槽。

## 8. Session、reload 与机制状态

任务池属于机制状态，必须遵守：同一 Unity 进程启动内有效，进程重启后全部作废，数量永远不超过 `maxActive`，不得创建一请求一文件。

### 8.1 Editor

- 内存表最多保存 `maxActive` 条 grant/consumed identity。
- 需要跨 domain reload 保留的 gate mode 和 bounded identity 使用 `SessionState` 或现有固定 coordinator active state，并携带当前 `editorSessionId`。
- 未消费 grant绑定 listener generation，reload 前全部 revoke。
- 已消费 direct request在 reload 前按现有 cancellation/terminal规则收敛；不能恢复的请求不得伪装成功。
- 已消费 durable root由固定 active coordinator state恢复同一占用，不增加计数。
- gate 的 open/closed mode 在同一 Editor session 跨 reload 保留；恢复后按 active 与新配置执行边界收敛。closed 且 active 降到 `reopenAt` 时开放，open 且 active 达到 `maxActive` 时关闭，其余情况不擅自翻转。
- Editor进程退出时清除全部内存/SessionState机制状态；业务资产与已产生效果不回滚。

### 8.2 Runtime

- 每个 Development Player session 只使用进程内 bounded state。
- Player退出、listener shutdown 或 runtime session identity改变时全部 token失效。
- Runtime 不创建项目内请求状态文件。
- Runtime 构建 sidecar只提供池配置，不保存活跃任务。

## 9. 与现有并发机制的关系

### 9.1 Mutation lease

任务池只做 root admission 和负载边界。Editor `UnityCliSessionMutationLease` 继续保证全局单 mutation owner；即使池中同时存在多个 root，也只能有一个 mutation 取得 effect ownership。`E_UNITY_BUSY` mutation lease 竞争与准入 timeout 的 `E_UNITY_BUSY` 都必须保持结构化，但 message/nextActions 要能区分“未取得池 grant”和“池内另一个 mutation 已拥有 lease”。

### 9.2 Editor WorkQueue

只有已消费 grant 的首个业务请求才能进入 WorkQueue；同一 root 的内部 status/control按绕过规则执行。WorkQueue 每帧启动限制继续存在。任务池不能把 Unity API 执行移到 HTTP thread，也不能改变 read 与 mutation 的主线程执行约束。

公开 read roots 可以与其他 root交错，任务池不提供事务快照。公开 `editor.state` 也受池控制；gate closed 时它会等待准入。只有内部 health、admission 和 durable terminal/status可以继续推进恢复。

### 9.3 Runtime transport queue

Runtime 现有 128 项 request cache/transport queue和每帧最多处理16项继续保留，作为协议错误、内部请求或异常调用压力下的最后容量边界。新的5/3 root pool位于其上层，不得删除或扩大这两个限制来替代准入。

## 10. Fail-closed 与安全约束

- acquire 必须先完成完整 target attestation；任何 instance/session/target/listener mismatch 都不得发 grant。
- root fingerprint、requestId、token 或 session不匹配必须在 WorkQueue/handler前失败。
- acquire body、header 和 token必须有固定长度上限；未知字段按严格 schema失败。
- pool状态读取或更新发生异常时不得解释为空池或开放状态；返回内部不可用并由 CLI在原准入预算内处理。
- 配置非法时不得静默 clamp。Editor设置入口拒绝保存非法组合；Runtime sidecar非法时 Runtime Bridge fail closed不启动。
- admission不构成额外用户授权。公开 Tool调用仍是授权，token只是内部容量所有权。
- 不得把 token 当 auth secret、公开 capability 或可由用户传入的业务参数。

## 11. 错误投影

### 11.1 准入未放行

CLI在完整准入预算内持续内部轮询；单次 `granted:false` 不直接结束公开调用。deadline到达后统一投影 `E_UNITY_BUSY`，并保证没有业务请求发送。

### 11.2 Grant 后失败

- 首个业务 POST 前发生 token stale/reload：在当前执行预算内重新 acquire，不刷新预算。
- token mismatch 或内部状态不可证明：业务 handler不得执行；若能够证明首个业务 POST未被接受，可返回结构化内部/连接失败，否则返回 `E_EXECUTION_TIMEOUT`。
- 首个 mutation POST 后 transport丢失、session改变或 terminal未知：`E_EXECUTION_TIMEOUT`，不得改写为准入 busy。
- mutation lease竞争：沿用 `E_UNITY_BUSY`，但不得声称本 root尚未发送业务请求。

公开 `error.details` 只在机器分支确有需要时返回稳定原因枚举；不得返回 token、active root列表、PID、port、listener、endpoint或内部队列结构。

## 12. 日志与审计

- 每个100ms acquire probe不得写 transaction log，也不得产生一请求一文件。
- Bridge rolling log可以记录 bounded事件：`admission_granted`、`admission_consumed`、`admission_revoked`、`admission_terminal`，只含时间、root requestId、target、gate mode和有界计数，不含 token和业务 input。
- 连续拒绝 probe应聚合或采样，禁止每次轮询写日志导致增长与噪声。
- 实际业务 request/response继续由现有 session transaction log记录一次；内部 admission envelope不进入公开审计。
- CLI最终准入 timeout可以进入CLI诊断日志，但公开 stdout只输出一个标准 JSON envelope。
- 日志继续使用现有滚动上限与固定位置。

## 13. 契约与文档同步

产品实现完成后，以下 owner 必须在同一次变更中同步：

1. 新增严格 admission request/response schema与HTTP route契约。
2. `contracts/stable-session/direct-call-semantics.json` 改为准入预算加执行预算，并声明 effect-before busy保证。
3. Bridge request body继续使用当前 immutable schema；admission token只走私有 transport header，不进入request digest。
4. Editor ProjectSettings字段与Runtime build sidecar当前格式同步。
5. 稳定错误目录补充两种 `E_UNITY_BUSY` 的可区分语义。
6. `docs/tool-execution.md`、`docs/error-codes.md` 和相关 target/session文档更新当前行为。
7. 安装后的 `skills/unity_cli/SKILL.md` 与 `references/operation-protocol.md` 更新内部等待、双预算host等待和busy/timeout恢复规则。
8. generated contract、schema、README示例和测试只使用最终当前语义，不保留单预算双轨或旧 route fallback。

在产品实现、契约和验证未完成前，第6、7项不得提前修改为已支持任务池。

## 14. 公开 Skill 必须表达的最终规则

安装后的 `unity_cli` Skill只需要指导AI正确调用，不暴露token协议。实现完成后至少表达：

- CLI会在发送业务请求前自动等待Unity准入，AI不得自行每100ms轮询。
- `--timeout-ms T` 分别提供最多T的准入等待和放行后最多T的执行预算。
- host等待预算至少为 `2 * T + 5000ms`。
- 准入超时的 `E_UNITY_BUSY` 证明本次业务effect未开始，可以稍后提交新root。
- 放行后的 `E_EXECUTION_TIMEOUT` 仍表示outcome unknown，必须先观察业务状态再作新决策。
- CLI进程必须前台等待到stdout完整envelope和进程退出。

Skill不得要求AI管理token、读取pool计数、直接访问内部route或绕过准入。

## 15. 实现所有权

实现至少涉及以下现有边界，具体文件可在编码前按当前索引确认：

- TypeScript dispatcher/session supervisor：root identity、两份deadline、100ms抖动轮询、token header和公开错误投影。
- Editor HTTP listener：admission route、attestation、原子池、WorkQueue入队前消费。
- Editor completion/coordinator：root terminal释放、reload恢复和mutation lease关联。
- Unity ProjectSettings：`maxActive`、`reopenAt`校验与reload生效。
- Runtime build injection/sidecar：配置投影与严格格式。
- Runtime listener/main-thread executor：同一高低水位池和terminal释放。
- contracts/docs/public Skill/tests：当前契约同步与陌生AI恢复指导。

不得为任务池新增用户可见顶层命令、独立Tool、MCP接口、常驻CLI daemon或请求级持久文件。

## 16. 定向验证规范

实现全部收敛后才进入验证。只覆盖本次任务池增删改及直接影响面，但该影响面必须包含以下完整类别。

### 16.1 自动化与契约

- 配置边界：默认5/3、最小值、相等、反序、非法sidecar。
- 状态机：open到5关闭、5到4不开放、降到3开放、再发两个关闭。
- 原子竞争：至少50个并发acquire下active永不超过5，单token只发一次。
- 同root attach不重复计数；同ID不同fingerprint失败。
- 未消费token 2000ms失效并释放；stale generation不能消费。
- 轮询串行且每次delay位于80到120ms。
- 准入deadline返回 `E_UNITY_BUSY`，并断言业务POST次数为0。
- grant后获得完整T；重取token、status和rebind不再次刷新。
- consumed root只由匹配terminal owner释放，重复terminal幂等。
- 私有token不进入request body、digest、公开envelope或日志。
- Editor与Runtime contract投影一致，Runtime原128项边界仍存在。

### 16.2 Dev Unity真实矩阵

- Editor默认5/3下并发根调用达到5、4、3三个关键水位。
- 竞争者只在3时重新放行，开放后最多两个新root成功消费。
- mutation lease仍只允许一个mutation owner；读root按既定规则交错。
- 公开读取在gate closed时等待，内部status仍能推进terminal与释放。
- 准入timeout证明目标业务效果未发生。
- grant后执行timeout保持outcome unknown，不被误报为busy。
- grant未消费时domain reload使其失效；同session durable consumed root恢复一次占用。
- Editor重启清除机制状态，不影响业务资产。
- Development Player使用构建sidecar值执行同一矩阵；Player重启清空状态。
- 现场盘点确认无请求级文件、内存/SessionState最多5条、日志仍有界且位于规定根。

### 16.3 陌生AI门禁

只执行与本改动相关的门禁：

- capability/task：陌生AI能按公开Skill正常调用，不自行实现准入轮询，并给host足够等待预算。
- perturbation：多个独立AI会话竞争同一实例时，首次业务调用行为和busy/timeout判断正确。
- blocker仅在本改动实际改变blocker恢复路径时运行；否则不运行。

门禁在受影响自动化、契约和Dev Unity矩阵全部通过后才启动，不得用于边写边试。

## 17. 验收不变量

最终实现必须同时满足：

1. 任一 target session 的 active 永不超过配置 `maxActive`。
2. closed gate在 active大于 `reopenAt` 时绝不发grant。
3. 无有效一次性token的公开业务请求绝不进入handler。
4. 准入超时返回busy且业务POST为零。
5. grant只刷新一次执行deadline，后续内部恢复不刷新。
6. 首次mutation POST后的unknown outcome绝不降级为effect-before busy。
7. mutation lease、completion proof和durable terminal语义不被任务池替代。
8. 机制状态重启作废、数量有界、无请求级文件。
9. Editor与Runtime均遵守同一高低水位语义，Runtime transport边界仍保留。
10. 公开Skill只描述已实现行为，AI无需知道或操作token。
