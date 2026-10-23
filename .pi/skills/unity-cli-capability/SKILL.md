---
name: unity-cli-capability
description: unitycli 的统一 Tool、Bridge、descriptor、schema、安全、Editor/PlayMode 与结构化输出硬规范。新增、修改或删除任何 Unity 能力、公开 metadata、summary/describe、错误 envelope、自定义 Tool 或 destructive 语义时必须先完整读取并遵守；Extension 能力也必须同时使用本 Skill。
compatibility: 适用于 alps-unity-cli 仓库；需要 read 和项目现有验证工具。
metadata:
  internal: true
---

# UnityCLI Capability

本 Skill 约束 `unitycli` 的公开能力设计和实现。开始新增、修改或删除前先确认根 `AGENTS.md` 已加载；涉及 Extension、框架 wait 或完成验证时，还必须分别读取对应项目 Skill。

## 统一Tool契约

所有Unity动作与观察能力都通过Tool暴露。公开调用语法只有同一catalog上的`tools run <tool> --input <json>`与`command <name> <business-args>`；`command`不得创建第二套descriptor、schema、dispatcher、安全、completion或envelope。读取型Tool依靠`destructiveHint: false`、空`sideEffects`和准确描述声明纯读取边界。

`command`必须满足：

- 省略name时只投影当前实例的同一live Tool catalog。
- native Tool使用完整精确名；official attribute Tool在`tools`中固定使用`UnityPipeline_<原名>`与`UnityPipeline`group，并可由attribute原名选择。
- native精确重名优先，attribute仍可由完整前缀名选择；禁止相似名称、case、snake/camel或语义映射。
- business argv只能按目标live `inputSchema`解析为typed input，随后复用统一dispatcher。
- 单个工具/同名冲突/provider 失败只隔离受影响名称：official attribute 原名重复时冲突双方都不可调用，native 占用保留前缀/group 只禁用该实现，其他工具与基础观察继续工作。
- official attribute method必须static且非泛型；只接受同步`void`/结果值或`Task`/`Task<T>`，拒绝`async void`和其他awaitable。`RuntimeOnly=true`只投影runtime-only。
- `[CliArg]`与`IStructuredCommandInput`必须派生封闭typed schema；`object`、Json.NET token或无法静态派生的值才显式使用`x-unityCliAnyJson`。未知字段、缺失required、递归DTO和重复投影名必须在effect前失败。
- official attribute Tool固定使用`official-attribute` profile、`arbitrary-method-invocation` side effect、`execute-user-code` permission和`supportsDryRun:false`；参数冻结后只跨一次effect barrier，`Task`超时不得声称取消底层method。

Tool必须满足：

- 任何可能修改 Unity 状态、项目文件、资源、Package、场景、GameObject、Component、PlayMode 状态或产生 artifact 的能力，都必须设计为 Tool。
- 完整内部 descriptor 必须具有 `name`、`summary`、`description`、`group`、`inputSchema`、`outputSchema`、风险、target、availabilityScope 与 PlayMode metadata，供 registry、fingerprint、结果校验和静态 contract 使用。公开 `tools describe` 只投影调用所需字段，不得返回 `outputSchema`、`group`、`permissions` 或内部路由/完成字段。
- 输出必须机器可读并返回稳定业务字段，不得只返回自然语言说明。
- 错误必须结构化，包含稳定的 `error.code`、`message`、`transient`、`retryAfterMs`、`nextActions`。

读取型 Tool 必须满足：

- 不得修改 Unity 状态、项目文件、资源、Package、场景、GameObject、Component。
- 不得触发 import、compile、domain reload、PlayMode 切换或任何副作用。
- 只要能力存在副作用，就必须准确声明 `sideEffects`，并按真实持久化风险设置 `destructiveHint`。

## 异步与框架 Wait

- Tool 接口默认异步：C# Tool 用 `ValueTask<T>`，TypeScript local Tool 用 `Promise<T>`；不得把 deferred 或 callback 形态作为特殊工具形态暴露给调用方。
- `wait` 是框架能力，不是 Tool 的私有执行机制：工具作者不得直接注册 `EditorApplication.update`、不得自行维护 callback 生命周期，只能通过框架 `ctx.Wait`（`NextFrame`、`Delay`、`UntilEditorReady`、`ForCallback`、`UntilStable`）或等价 wait service 申请等待。
- 整个 Unity 进程只允许一个中央 scheduler 持有 `EditorApplication.update`；timeout、cancel、Domain Reload、Play Mode exit 与 Editor quitting 时，所有 pending waits 必须由框架统一清理。
- 调用方只等待同一次 direct 调用的最终结果；不得重新发明 scheduler、准入轮询或 callback 清理机制。

## Editor 与 PlayMode

每个新增或修改的 Tool 都必须显式考虑 Editor 与 PlayMode：

- 只要 Unity 机制并非理论上无法实现，就必须尽量支持两种状态；不得为了省事标成 `editor-only`。
- 某状态确实不可用时，必须在 descriptor、文档、稳定错误码和测试中说明原因，并提供可执行的 `nextActions`。
- PlayMode 不可用不是设计失败，但不得硬凑实现或返回泛化错误。
- Runtime-only 行为必须标记为 `runtime-only`，并说明结果是否只影响当前 PlayMode 会话。
- 可能触发 import、compile、domain reload 或 PlayMode 状态变化的能力，必须标记为 `session-affecting`。

设计必须回答：

- Editor 下是否可用，成功输出是什么。
- PlayMode 下是否可用，成功输出是什么。
- 结果是否持久化到项目或场景。
- 不可用时返回什么稳定错误码。
- AI 能否根据 `nextActions` 明确恢复。

### Editor可操作性协议

- Registry v3只负责identity发现、lifecycle publication与listener generation交接；不得作为实时消息队列或主线程活性证明。
- Health v3必须同时返回严格transport identity与只读内存main-thread pulse；transport可达不能替代pulse，pulse不能证明具体业务effect。
- 公开`editor.state.status`固定归约为`ready|transitioning|blocked|unresponsive|unverifiable|offline|conflict`。`conflict`只允许用于至少两个verified live project claim；观察失败或权限不足必须是`unverifiable`。
- 实时观察失败时不得用 Registry 上一次 publication 的 PlayMode/compile/import、progress 或 lifecycle 回填当前状态：这些字段必须为 unknown，只保留本次独立观察到的 blocker。已验证进程但缺少实时响应证据时为`unresponsive`，不能断言主线程已经卡死；仅添加 `stale` 标记不代替该规则。
- `blocked`有两种精确 DTO，且都需要独立证据：独立Windows modal观察（`blocker.type="dialog"`，`source="windows-ui"`，`requiresUserAction=true`），或当前live pause publication证明的故意主线程暂停（`blocker.type="debug-pause"`，`source="unity-debug-pause"`，带`pauseId`/`threadId`/`mainThread`/`timeoutKind`/`remainingMs`/`resumeTool`/`infoTool`，`requiresUserAction=false`）。`editor.state`、`editor.dialogs`与`editor.dialog-click`使用exact frozen本地descriptor进入同一dispatcher，不得依赖Unity主线程或live catalog观察/恢复。其他Tool与catalog listing不得建立静态fallback。
- 暂停证据与主线程pulse互相独立：pause publication不改写main-thread pulse，也不读任何文件。pulse停止且有当前live pause publication证明主线程暂停时为`blocked`；只有pulse停止而没有该证据时仍为`unresponsive`，并不得用worker现场或旧publication推断因果。health在`mainThread.debugPause`单独发布该证据。
- 主线程暂停期间可用能力是冻结名单：`tools group/list/search`、`tools describe`、两个公开入口、`debug.pause-list/wait/inspect/evaluate/resume`、`debug.events-read`、`debug.thread-list`、`debug.hook-list/info`、`debug.session-list/info`、`debug.program-list/info`与命名了`pauseId`的`debug.value-read/write`在发起请求的线程上直接服务，并继续使用同一个live descriptor。命名了当前暂停现场所属`sessionId`的`debug.session-stop`是唯一的条件放宽：它复用同一session owner的stop入口与同一报告，关闭该owner的未来派发并发出resume/取消信号，需要主线程的清理条目先报告`cleanupPending`与精确原因、线程恢复后重复同一stop完成；未命名或命名其它session的stop仍在任何effect之前拒绝。没有发布catalog时如实报告不可用。其余业务mutation在任何effect之前返回`E_DEBUG_MAIN_THREAD_PAUSED`，携带实际`pauseId`与`debug.pause-list`/`debug.pause-resume`路由。解除路径必须在任何pause资源安装之前已发布，否则pause断点在effect之前被`E_DEBUG_CONTROL_PLANE_UNAVAILABLE`拒绝。
- Domain Reload只使用Registry lifecycle、新listener generation与该generation首个main-thread pulse交接；禁止固定时长reload marker或双轨判定。
- 同一次mutation只允许在`editorSessionId`不变时续接完全相同的request bytes；首次POST后session改变必须禁止第二次POST，并返回`E_EXECUTION_TIMEOUT`与outcome unknown。
- 公开live root使用两份有界预算：参数解析、target选择、health/PID/start/modal观察与准入轮询共用admission absolute deadline；grant后只创建一次完整execution absolute deadline，业务POST、compile/import/reload、rebind、status和terminal recovery共用它。token重取或内部恢复不得创建第三份预算。

## Descriptor 质量

功能能运行不等于接口合格。Descriptor 必须让陌生 AI 不读源码也能一次性判断、构造并安全调用。

每个 Tool descriptor 必须满足：

- 名称稳定、语义单一、与 group 一致。native Tool 名固定两段 `<group>.<tool>`，只允许一个点号；点前等于 group，tool 段内部细分只用 `-`。公开查找只有 group 与 tool 两层，不得用点号暗示不存在的子 group。官方 `UnityPipeline_<name>` 属性 Tool 保留外部作者原名，不受本规则约束。
- `summary` 能让 AI 在发现阶段选对 group 和能力。
- `description` 准确说明边界、默认行为、关键副作用和相邻能力差异。
- `inputSchema` 字段具体、必填项准确、字段名稳定。
- 内部 `outputSchema` 准确描述主要成功业务字段并参与 terminal 结果校验；公开 description 简述主要结果，不展开结果字段树。
- examples 可直接指导典型输入构造；公开形态只含 `description`、结构化 `input` 与按需 `execution`。
- 风险 metadata 准确：`destructiveHint`、`supportsDryRun`、`sideEffects`、`permissions`；其中 permissions 只用于内部策略，不进入公开 describe。
- 可用性 metadata 准确：`target`、`availabilityScope`、`playModeSupport`、`requiresPackages`；公开 describe 只返回 target、playModeSupport、非空 requiresPackages，并仅在不可用时返回 `available:false` 与原因。`handler`、support status 和 completion contract 只允许内部使用。
- 错误路径提供明确 `nextActions`，不能让 AI 猜。

### Summary 路由字段

`summary` 只负责发现阶段路由，不是营销文案，也不替代完整文档。

Group summary 必须：

- 覆盖当前公开 Tool 的主要能力类别，不能只描述历史子集或单个 happy path。
- 说明领域对象和主要动作，例如 `Camera operations: list/info, create, aim, lens settings, screenshots, and multiview capture.`
- 与相邻 group 保持清晰边界；易混 group 必须写明关键区别，如 `offline cache`、`live method call`、`schema generation`、`runtime component`、`asset editing`、`scene object`。
- 明确默认入口关系；alias、兼容或诊断入口不得表现得比主入口更推荐。
- 不得包含当前 group 不具备或属于其他 group 的能力。
- 避免 `manage`、`various`、`utilities`、`helpers` 等空泛词；使用时必须紧跟具体能力类别。
- 保持一句话，英文优先 12–24 词且最多 32 词；中文也必须是一句短句。

Tool summary 必须：

- 描述单一动作、作用对象和主要结果，让 AI 能在同 group 内选对 Tool。
- 与邻近 Tool 区分关键语义，如 source text / structured data、asset / scene object、offline cache / live reflection、summary read / deep read。
- 对影响路由的风险或执行模式给出短提示，如 `executes C#`、`writes asset files`、`dry-run supported`、`runtime-only`。
- 不列完整参数；参数、边界、错误路径和副作用属于 `description`、schema 和 examples。
- 不承诺未支持的状态、对象、持久化结果或副作用。
- 不写 `Run <tool>`、`Manage <domain>`、`Tool for <name>` 等同义反复。

`summary` 与 `description` 必须分工明确：前者负责发现路由；后者负责能力边界、推荐替代、alias 关系、PlayMode 差异、副作用、不可用原因和恢复指导。

修改 summary 后必须检查：

- `tools group` 是否让 AI 选对 group。
- `tools group list <group>` 是否让 AI 选对 Tool。
- summary 是否覆盖真实能力且没有写入不存在的能力。
- 易混 group / Tool 是否有明确边界和推荐主入口。
- legacy alias、兼容入口和危险入口是否没有抢占主入口地位。

### Describe 输出

`tools describe <tool>` 是单能力学习入口，分两层交付：默认集合负责调用决策，`--fields` 按需读取细节。

默认返回集合固定为：`name`、`description`、`params`、`hasNotes`、`destructiveHint`、`sideEffects`、`playModeSupport`、`supportsDryRun`、`target`、`available`、`requiresPackages`（仅不可用时附加 `unavailableReason`）。任何 Tool 不得缺项。

- `--fields <name,...>` 整体替换默认集合，未知字段名返回 `E_SCHEMA_INVALID` 并列出合法字段；`--detail full` 等价于全字段。
- `params` 是 schema 投影：顶层属性 `名字:类型`，必填加 `*`，`$ref` 取 `$defs` 名，嵌套不展开。
- `inputSchema`、`examples`、`notes` 只能通过 `--fields` 读取。

字段职责：

- `summary` 与 `group`：只在 group list/search 发现阶段路由，不重复进入 describe。
- `description`：调用阶段判断，包括边界、默认行为、相邻工具差异、副作用、PlayMode、安全确认、主要结果、输出控制和跨字段约束；预算见《Descriptor 预算与归属》。
- `params`：调用形状，让 AI 无需读取完整 schema 也能构造输入。
- `inputSchema`：按需读取的字段级输入契约；完整 `outputSchema` 留在内部 terminal 校验与 contract，不进入公开 describe。
- `notes`：机制细节、配额与恢复流程；默认不返回，只能经 `--fields notes` 读取，内容预算见《Descriptor 预算与归属》。
- `examples`：按需读取，返回 `description`、结构化 `input` 和按需 `execution`，不重复固定 CLI argv。
- 风险与状态字段：公开返回 destructiveHint、非空 sideEffects、supportsDryRun、playModeSupport、target、非空依赖，以及仅不可用时的 available:false/unavailableReason。

Describe 必须按需覆盖：

- 对象、动作范围和主要成功结果。
- 必填输入、默认行为、互斥字段、字段优先级、路径或目标解析顺序。
- 相邻工具选择，特别是 alias、兼容入口、fallback 和推荐主入口。
- 持久化、副作用、PlayMode、session-affecting 与 `--dry-run` 语义。
- 读取型 Tool 的只读边界，不得暗示刷新、修改或触发 Unity 状态变化。

Describe 不得包含：

- 内部开发记录、竞品来源、changelog、迁移说明或临时阶段说明。
- 与 schema、examples、风险字段、Bridge 行为或错误路径不一致的承诺。
- 完整参数表或完整调用样例等与 schema/examples 重复的内容。

修改 describe 后必须检查：

- 陌生 AI 能否仅凭 describe 一次调用成功。
- `tools search <k1|k2|...>` 是否会因 describe 文本误导到错误能力。
- discovery summary、schema、examples、风险字段、Bridge 行为、错误 envelope 和文档是否一致。

## Descriptor 预算与归属

`describe` 的输出直接占用调用方上下文，并且会随对话轮次重复发送。预算与归属是硬规范。

预算：

- 单 Tool `description` ≤ 300 token（约 1,200 字符）；超过 150 token 记录 WARN。
- 单 Tool `notes` 合计 ≤ 2,000 token。
- 默认 describe 输出 ≤ 1,000 token；单域 `tools group list` ≤ 1,000 token。

三层归属，判定问题是「删掉这句会不会导致调用失败或误读结果」：

- 会：写进 `description`，包括前置条件、互斥或必填约束、成功证明什么、独有失败码、副作用、PlayMode 差异。
- 不会但影响理解：写进 `notes`，包括配额数字、机制推导、线程或生命周期细节、失败后的完整恢复流程。
- 全局不变量（调用授权、等待、错误恢复、输出格式）：留在本 Skill 正文，不写进 descriptor。

约束：

- `description` 与 `notes` 只写结论，不写叙事推理；禁止 `so`、`because`、`which means` 串起的因果长句。
- 域内共用文本用 canonical 源码中的共享常量（`static readonly string[]`）引用，禁止在 descriptor 中复制正文。
- descriptor 内禁止出现 Skill 路径、reference 路径或其他非产品路径：Skill 是安装投影，CLI 不得依赖它。
- `inputSchema` 与 `examples` 不属于可持续压缩项：schema 是调用正确性契约，示例保持最小可用 1–2 条。

扫描与门禁：

- `node scripts/audit-descriptor-budget.mjs` 离线扫描全部 descriptor，输出 `description`、`notes`、超限清单与汇总。
- `npm run release:check` 聚合架构、契约、Issue 台账、版本同步、发布凭据与描述预算，输出 `{pass, blockers, warnings}`。
- 开发期预算结论只报 WARN，不阻断实现；发布前 blockers 必须为空，warnings 必须告知项目所有者。

## 自定义 C# 能力目录

项目自定义 `IUnityCliTool<TReq,TRes>` 文件必须位于名为 `UnityCLI` 的目录：

- 合法示例：`Assets/UnityCLI/`、`Assets/Editor/UnityCLI/`、`Assets/SomeModule/UnityCLI/`。
- 项目内可以存在多个 `UnityCLI` 目录，但目录名大小写必须完全一致。
- 此约定不适用于内置 Bridge `Packages/com.alpsckr.unitycli/` 和 Extension `Packages/com.alpsckr.unitycli/Extensions/`。

## 安全与破坏性操作

只有以下能力标记 `destructiveHint: true`：

- 对资产文件执行真实持久化创建、修改或删除，如 `.asset`、`.unity`、`.controller`、`.mat`、`.png`。
- 修改 `Packages/manifest.json` 或 `ProjectSettings/`。
- 写入持久化光照数据，如 lightmap、reflection probe bake。
- 执行任意代码，如 `debug.code-execute`、`editor.menu-execute`、`debug.method-invoke`。

以下能力不标记为 destructive：

- 只修改场景内存或会话状态，如 GameObject/Component 增删改、Console/Profiler 清空、选择与编辑器工具切换、Prefab unpack、场景卸载。
- 新建场景内存对象，如 GameObject、Canvas、UI 元素。

调用授权与 `--dry-run`：

- 调用公开 Tool 或 CLI 控制面即为授权。CLI 不得因 `destructiveHint`、`sideEffects`、任意代码或持久化效果而要求额外确认、预定义按钮列表或意图复核。
- `destructiveHint`、`sideEffects` 与 `supportsDryRun` 是准确、机器可读的描述信息，不得阻止执行。
- Tool 可以支持 `--dry-run` 并返回机器可读的 planned effects；调用方可选用它预览，不能以未使用 dry-run 拒绝实际调用。
- 不得用模糊描述隐藏副作用。

破坏性描述必须三层一致：

1. Bridge spec `destructiveHint`：`src/bridge/domains/*.ts`。
2. CLI tool metadata 与 bridge spec 的 `destructiveHint`：`src/tools/**`、`src/bridge/domains/*.ts`，由 `src/contracts/command-contract.ts` 做一致性校验。
3. C# descriptor / registry：`ToolDescriptor.DestructiveHint` 或运行时 descriptor。

任一层声明 destructive 时三层必须同步；该声明不构成授权门。CLI 路径和 C# `RegisterCommand` 的真实执行语义不得冲突。

## 临时文件与机制状态

能力（Tool、Bridge handler、Extension）产生的所有临时文件与机制状态必须遵守 AGENTS.md《临时文件与机制状态》三条铁律：

- 机制状态（互斥锁、进行中标记、会话标记）只对 Unity 本次启动有效，必须携带本次启动标识，重启作废。
- 临时文件数量与大小不得随调用次数和时间无限增长：状态文件一类一个固定路径、终态即删；产物（artifacts）保留最新 100 个；日志按大小滚动并保留最近 2 个。禁止一请求一文件。
- 临时文件位置：用户级 `%LOCALAPPDATA%\UnityCli\.temp\`；项目内 `Library\UnityCli\`。新增能力不得在其他位置写临时文件。
- 并发快照publication必须由单写者mutex串行，mutex内重读正式文件并按session/generation/publication sequence做语义CAS；写固定同目录`.tmp`，flush到磁盘后用原子replace/move发布。reader无锁且只读正式路径，在原absolute deadline内做有界重读；不得读取temp/backup或扫描“最新文件”。

## 输出与错误

所有用户可见命令必须输出统一 JSON envelope。

成功：

- `ok: true`
- `requestId`
- `data`
- 必要时包含 `warnings`
- `data` 只包含业务 Result DTO；内部日志、审计、proof、通用耗时和执行状态不得公开

失败：

- `ok: false`
- `requestId`
- `error.code`
- `error.message`
- `error.transient`
- `error.retryAfterMs`
- `error.nextActions`
- 仅在需要机器分支时包含业务 `error.details`

`error` 对象是闭集，不得重复 `requestId` 或附加日志、审计、endpoint、port、listener identity、native window handle、Windows command line。blocker 只能使用公开 DTO，并且只有两种精确形态：弹窗 blocker 使用 `type="dialog"`、`dialogId`、`title`、`message`、`buttons`、`requiresUserAction=true`、`source="windows-ui"` 与 `confidence="exact"`；故意主线程暂停使用 `type="debug-pause"`、`pauseId`、`threadId`、`mainThread`、`methodIdentity`、`targetKey`、`position`、`sessionId`、`timeoutKind`、`remainingMs`、`pausedForMs`、`resumeTool="debug.pause-resume"`、`infoTool="debug.pause-list"`、`requiresUserAction=false`、`source="unity-debug-pause"` 与 `confidence="exact"`。

超时或终态前断连统一使用`E_EXECUTION_TIMEOUT`。同一`editorSessionId`内可用完全相同的请求attach/recover durable terminal；首次mutation POST后session改变时必须禁止向新session重投。调用方先观察业务状态，再判断是否提交全新调用。审计只记录session级append日志。

禁止：

- 用裸字符串作为主要输出。
- 用泛化错误掩盖可恢复问题。
- 把机器需要解析的信息只写进自然语言。
- 返回依赖日志才能理解的失败结果。
