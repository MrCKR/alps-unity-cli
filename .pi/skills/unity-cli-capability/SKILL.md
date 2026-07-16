---
name: unity-cli-capability
description: unity_cli 的 Tool、Context、Bridge、descriptor、schema、安全、Editor/PlayMode 与结构化输出硬规范。新增、修改或删除任何 Unity 能力、公开 metadata、summary/describe、错误 envelope、自定义 Tool/Context 或 destructive 语义时必须先完整读取并遵守；Extension 能力也必须同时使用本 Skill。
compatibility: 适用于 alps-unity-cli 仓库；需要 read 和项目现有验证工具。
---

# Unity CLI Capability

本 Skill 约束 `unity_cli` 的公开能力设计和实现。开始新增、修改或删除前先确认根 `AGENTS.md` 已加载；涉及 Extension、框架 wait 或完成验证时，还必须分别读取对应项目 Skill。

## Tool 与 Context 契约

Tool 用于动作，Context 用于观察。

Tool 必须满足：

- 任何可能修改 Unity 状态、项目文件、资源、Package、场景、GameObject、Component、PlayMode 状态或产生 artifact 的能力，都必须设计为 Tool。
- descriptor 必须暴露 AI 调用所需的 `name`、`summary`、`description`、`group`、`inputSchema`、`outputSchema`、`destructiveHint`、`supportStatus`、`target`、`availabilityScope`、`playModeSupport`；内部路由字段不得混入公开 descriptor。
- 输出必须机器可读并返回稳定业务字段，不得只返回自然语言说明。
- 错误必须结构化，包含稳定的 `error.code`、`message`、`transient`、`retryAfterMs`、`nextActions`。

Context 必须满足：

- Context 必须只读。
- 不得修改 Unity 状态、项目文件、资源、Package、场景、GameObject、Component。
- 不得触发 import、compile、domain reload、PlayMode 切换或任何副作用。
- 只要能力存在副作用，即使主要用途是读取，也必须设计为 Tool。

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

## Descriptor 质量

功能能运行不等于接口合格。Descriptor 必须让陌生 AI 不读源码也能一次性判断、构造并安全调用。

每个 Tool / Context descriptor 必须满足：

- 名称稳定、语义单一、与 group 一致。
- `summary` 能让 AI 在发现阶段选对 group 和能力。
- `description` 准确说明边界、默认行为、关键副作用和相邻能力差异。
- `inputSchema` 字段具体、必填项准确、字段名稳定。
- `outputSchema` 描述主要成功业务字段。
- examples 可直接指导典型输入构造。
- 风险字段准确：`destructiveHint`、`supportsDryRun`、`sideEffects`、`riskLevel`、`permissions`。
- 执行状态字段准确：`handler`、`supportStatus`、`target`、`availabilityScope`、`playModeSupport`、`requiresPackages`。
- 错误路径提供明确 `nextActions`，不能让 AI 猜。

### Summary 路由字段

`summary` 只负责发现阶段路由，不是营销文案，也不替代完整文档。

Group / Context group summary 必须：

- 覆盖当前公开 Tool / Context 的主要能力类别，不能只描述历史子集或单个 happy path。
- 说明领域对象和主要动作，例如 `Camera operations: list/info, create, aim, lens settings, screenshots, and multiview capture.`
- 与相邻 group 保持清晰边界；易混 group 必须写明关键区别，如 `offline cache`、`live method call`、`schema generation`、`runtime component`、`asset editing`、`scene object`。
- 明确默认入口关系；alias、兼容或诊断入口不得表现得比主入口更推荐。
- 不得包含当前 group 不具备或属于其他 group 的能力。
- 避免 `manage`、`various`、`utilities`、`helpers` 等空泛词；使用时必须紧跟具体能力类别。
- 保持一句话，英文优先 12–24 词且最多 32 词；中文也必须是一句短句。

Tool summary 必须：

- 描述单一动作、作用对象和主要结果，让 AI 能在同 group 内选对 Tool。
- 与邻近 Tool 区分关键语义，如 source text / structured data、asset / scene object、offline cache / live reflection、summary read / deep read。
- 对影响路由的风险或执行模式给出短提示，如 `executes C#`、`writes asset files`、`requires --yes`、`dry-run supported`、`runtime-only`。
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

`tools describe <tool>` / `context describe <context>` 是单能力学习入口，必须自足。

字段职责：

- `summary`：发现阶段路由。
- `description`：调用阶段判断，包括边界、默认行为、相邻工具差异、副作用、PlayMode、安全确认、输出控制和跨字段约束。
- `inputSchema` / `outputSchema`：字段级输入输出契约。
- `examples`：可直接照抄的典型调用。
- 风险与状态字段：机器可读的风险、支持状态、目标、可用范围和依赖。

Describe 必须按需覆盖：

- 对象、动作范围和主要成功结果。
- 必填输入、默认行为、互斥字段、字段优先级、路径或目标解析顺序。
- 相邻工具选择，特别是 alias、兼容入口、fallback 和推荐主入口。
- 持久化、副作用、PlayMode、session-affecting、`--yes`、`--dry-run` 语义。
- 大输出的分页、截断、continuation、artifact path、`inlineBase64` 等控制。
- Context 的只读边界，不得暗示刷新、修改或触发 Unity 状态变化。

Describe 不得包含：

- 内部开发记录、竞品来源、changelog、迁移说明或临时阶段说明。
- 与 schema、examples、风险字段、Bridge 行为或错误路径不一致的承诺。
- 完整参数表或完整调用样例等与 schema/examples 重复的内容。

修改 describe 后必须检查：

- 陌生 AI 能否仅凭 describe 一次调用成功。
- `tools search <keyword>` 是否会因 describe 文本误导到错误能力。
- discovery summary、schema、examples、风险字段、Bridge 行为、错误 envelope 和文档是否一致。

## 自定义 C# 能力目录

项目自定义 `IUnityCliTool<TReq,TRes>` 和 `IUnityCliContext<TReq,TRes>` 文件必须位于名为 `UnityCLI` 的目录：

- 合法示例：`Assets/UnityCLI/`、`Assets/Editor/UnityCLI/`、`Assets/SomeModule/UnityCLI/`。
- 项目内可以存在多个 `UnityCLI` 目录，但目录名大小写必须完全一致。
- 此约定不适用于内置 Bridge `Packages/com.unity_cli/` 和 Extension `Packages/com.unity_cli/Extensions/`。

## 安全与破坏性操作

只有以下能力标记 `destructiveHint: true`：

- 对资产文件执行真实持久化创建、修改或删除，如 `.asset`、`.unity`、`.controller`、`.mat`、`.png`。
- 修改 `Packages/manifest.json` 或 `ProjectSettings/`。
- 写入持久化光照数据，如 lightmap、reflection probe bake。
- 执行任意代码，如 `code.exec`、`editor.menu`、`reflection.method.call`。

以下能力不标记为 destructive：

- 只修改场景内存或会话状态，如 GameObject/Component 增删改、Console/Profiler 清空、选择与编辑器工具切换、Prefab unpack、场景卸载。
- 新建场景内存对象，如 GameObject、Canvas、UI 元素。

`--yes` 与 `--dry-run`：

- `destructiveHint: true` 的 Tool 必须要求 `--yes`。
- destructive Tool 鼓励支持 `--dry-run`，并返回机器可读的 planned effects。
- 非 destructive Tool 也可以支持 `--dry-run` 预览内存修改，这与 `destructiveHint` 无关。
- 不得用模糊描述隐藏副作用。

破坏性声明必须三层一致：

1. Bridge spec `destructiveHint`：`src/bridge/domains/*.ts`。
2. CLI 安全策略：`src/security-policy.ts`。
3. C# descriptor / registry：`ToolDescriptor.DestructiveHint` 或运行时 descriptor。

任一层声明 destructive，业务 Tool 就必须要求 `--yes`；修改时三层必须同步。CLI 路径和 C# `RegisterCommand` 的真实执行语义不得冲突。

## 输出与错误

所有用户可见命令必须输出统一 JSON envelope。

成功：

- `ok: true`
- `requestId`
- `data`
- 必要时包含 `warnings`、`logsRef`、`jobId`、`durationMs`

失败：

- `ok: false`
- `requestId`
- `error.code`
- `error.message`
- `error.transient`
- `error.retryAfterMs`
- `error.nextActions`

禁止：

- 用裸字符串作为主要输出。
- 用泛化错误掩盖可恢复问题。
- 把机器需要解析的信息只写进自然语言。
- 返回依赖日志才能理解的失败结果。
