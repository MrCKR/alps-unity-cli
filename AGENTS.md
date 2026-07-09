# AGENTS.md

本文件是 `alps-unity-cli` 项目的 AI 开发硬规范，作用范围为整个仓库。除非项目所有者明确允许，任何 AI Agent 都必须遵守本文件。

## 项目定位

`unity_cli` 是面向 AI 使用的 Windows-first Unity Editor 自动化 CLI，不是 MCP server。

核心目标不是维护给人记忆的传统命令手册，而是提供可发现、可描述、可审计、可验证的工具目录，让陌生 AI 能通过一次自发现安全调用 Unity 能力。

### 设计哲学

本 CLI 的核心用户是 AI，不是人。所有设计决策的第一标准是：陌生 AI 看一眼 `describe` 就能一次调用成功，不需要读源码、不需要人教、不需要试错。

这个 CLI 是 AI 的「Unity 手」，不是人的工具。AI 通过 `describe` 学习工具用法（像读 API 文档），通过 `tools run` 执行操作（像调 API），通过 `context read` 观察状态（像读数据库）。安全策略只保护真正不可逆的持久化操作，不阻挡日常的 Unity 编辑操作。每个工具的 describe 必须让 AI 一次看懂、一次调对——做不到就是接口设计失败。

### 核心原则

1. **职责单一，不聚合**：不把多种能力塞进一个 action 路由器（如 `manage_material` 一个工具做 create/set/assign/color）。拆成 `material.info/create/assign/set` 独立工具——AI 从工具名就能判断该不该用，不用先理解 action 枚举。

2. **只吸收 AI 做不到的**：AI 能直接读写工程文件（如 `.cs` 脚本），不需要 CLI 包装。AI 不能直接操作 Unity Editor 内存/资产/场景/物理/渲染，这些保留。分界线是「Unity 侧独特价值」。

3. **不做 AI 已经能做的事**：install-unity/create-project/open/close Editor——AI 用命令行自己能做，不需要 CLI 包装。

4. **减少上下文消耗是硬指标**：返回量可控（分页/筛选/深度/namePattern）；`describe` 自足（不用先读 README）；大对象默认摘要，按需展开；减少轮询往返（如 `--wait-ready`）。

5. **安全策略最小化**：只拦「真实持久化文件操作」和「任意代码执行」。场景内存操作不拦——AI 不应该被 `--yes` 阻挡去做 Unity Editor 里的日常操作。

### 架构分层

```
AI
 ↓
tools describe <tool>        ← 唯一的学习入口
tools run <tool> --input     ← 唯一的执行入口
 ↓
CLI (TypeScript)
 ├── bridge spec → 结构化 --input 字段透传
 ├── security-policy → --yes 门禁（只拦持久化）
 ├── metadata → describe 输出来源
 └── handler routing → bridge / offline / hybrid / job
 ↓
Unity Bridge (C#)
 ├── RegisterCommand → 工具注册 + destructive 标记
 ├── SuccessJson → 统一 envelope（业务字段不丢）
 └── 各域 Commands → 实际 Unity API 调用
 ↓
Unity Editor
```

### 明确不做的东西

- MCP 协议 / server / transport / resource / prompt
- 桌面 GUI / setup wizard
- 工具开关 / 配置持久化
- 聚合 action 路由器（manage_*）
- 人类导向的命令手册（README → CLI 交互式）
- 兼容层 / 遗留语义

## 顶层 CLI 形态冻结

用户可见顶层命令只允许以下两类。

能力入口：

- `tools`
- `context`

CLI 控制面：

- `init`
- `extensions`
- `target`
- `instances`
- `jobs`
- `batch`
- `doctor`

硬规则：

- 不得新增任何用户可见顶层命令，除非项目所有者明确批准。
- Unity 能力域不得作为顶层命令出现，例如 `scene`、`asset`、`camera`、`go`、`prefab`、`package`、`build`、`test`、`editor`、`profiler` 等。
- 新增或修改 Unity 能力时，默认通过 `tools run <tool>` 或 `context read <context>` 暴露。
- 内部 Bridge command、job worker、registry 可以存在，但不得改变用户可见 CLI 形态。
- README、Skill、docs、tests、contract 和源码必须使用当前 CLI 形态；不得保留旧顶层命令语义或兼容性说明。

## Tool 与 Context 边界

Tool 用于动作，Context 用于观察。

Tool 规则：

- 任何可能修改 Unity 状态、项目文件、资源、Package、场景、GameObject、Component、PlayMode 状态或产生 artifact 的能力，必须是 Tool。
- Tool 必须通过 descriptor 暴露 AI 调用所需信息，包括 `name`、`summary`、`description`、`group`、`inputSchema`、`outputSchema`、`destructiveHint`、`supportStatus`、`target`、`availabilityScope`、`playModeSupport`；内部路由字段不得混入公开 descriptor。
- Tool 输出必须机器可读，返回稳定字段；禁止只返回自然语言说明。
- Tool 的错误必须结构化，包含稳定 `error.code`、`message`、`transient`、`retryAfterMs`、`nextActions`。

Context 规则：

- Context 必须只读。
- Context 不得修改 Unity 状态、项目文件、资源、Package、场景、GameObject、Component。
- Context 不得触发 import、compile、domain reload、PlayMode 切换或任何副作用。
- 如果能力存在副作用，即使主要用途是“读取”，也必须设计为 Tool。

## Editor / PlayMode 双态要求

每个新增或修改的 Tool 都必须显式考虑 Editor 与 PlayMode 两种状态，因为本 CLI 面向 AI，AI 可能在 PlayMode 中使用能力。

硬规则：

- 只要不是 Unity 机制上理论无法实现，就必须考虑并尽量支持 Editor 与 PlayMode 两种使用场景。
- 不得为了省事把能力标成 `editor-only`。
- 如果某状态无法支持，必须在 descriptor、文档、错误码和测试中说明原因。
- PlayMode 不可用不是失败；如果 Unity 机制确实不支持，必须返回稳定错误码和 `nextActions`，而不是硬凑实现。
- Runtime-only 行为必须明确标记为 `runtime-only`，并说明结果是否只影响当前 PlayMode 会话。
- 可能触发 import、compile、domain reload、PlayMode 状态变化的能力，必须标记为 `session-affecting`。

每个 Tool 的设计必须回答：

- Editor 下是否可用？成功输出是什么？
- PlayMode 下是否可用？成功输出是什么？
- 结果是否持久化到项目或场景？
- 不可用时返回什么错误码？
- AI 看到 `nextActions` 后能否知道下一步怎么做？

## Descriptor 质量门槛

功能完成不等于代码能跑。Descriptor 必须足够清楚，让陌生 AI 不读源码也能一次性正确调用。

每个 Tool / Context descriptor 必须满足：

- 名称稳定、语义单一、与 group 一致。
- `summary` 能作为 AI 自发现阶段的路由字段，让陌生 AI 在 `tools group` / `tools group list <group>` 中选对 group 和 tool。
- `description` 说明能力边界，不夸大、不遗漏关键副作用。
- `inputSchema` 足够具体，字段名稳定，必填项准确。
- `outputSchema` 描述主要成功输出字段。
- 示例能直接指导 AI 构造调用参数。
- 风险字段准确，包括 `destructiveHint`、`supportsDryRun`、`sideEffects`、`riskLevel`、`permissions`。
- 执行状态字段准确，包括 `handler`、`supportStatus`、`target`、`availabilityScope`、`playModeSupport`、`requiresPackages`。
- 错误路径有明确 `nextActions`，不能让 AI 猜。

### Summary 路由字段规范

`summary` 是 AI 自发现阶段的路由字段，不是营销文案，也不是完整文档。`tools group`、`tools group list <group>`、`context list` 和扩展发现输出中的 summary 必须让陌生 AI 在不读源码、不读 README 的情况下选对能力入口。

Group / Context group summary 必须：

- 覆盖该 group 当前公开 Tool / Context 的主要能力类别，不能只描述历史子集或单个 happy path。
- 说明领域对象和主要动作，例如 `Camera operations: list/info, create, aim, lens settings, screenshots, and multiview capture.`
- 与相邻 group 保持清晰边界；易混 group 必须在 summary 中写明区别，例如 `offline cache`、`live method call`、`schema generation`、`runtime component`、`asset editing`、`scene object`。
- 明确默认入口关系；如果某 group 只是别名、兼容入口或诊断入口，不得在 summary 中表现得比主入口更推荐。
- 不得包含该 group 不具备的能力，不得把其他 group 的能力写进本 group summary。
- 避免空泛词，如 `manage`、`various`、`utilities`、`helpers`；如需使用，后面必须列出具体能力类别。
- 控制为一句话，优先 12-24 个英文词，最多 32 个英文词；中文也必须是一句短句。

Tool summary 必须：

- 描述单一工具动作，包含作用对象和主要结果，让 AI 能在同 group 内选对 Tool。
- 在有邻近 Tool 时说明差异，例如 `source text` vs `structured data`、`asset` vs `scene object`、`offline cache` vs `live Editor reflection`、`summary read` vs `deep read`。
- 对影响路由的风险或执行模式给出短提示，例如 `executes C#`、`writes asset files`、`requires --yes`、`dry-run supported`、`runtime-only`。
- 不列完整参数，不替代 `inputSchema`；参数细节、边界、错误路径和副作用写进 `description`、schema、examples。
- 不承诺工具不支持的状态、对象、持久化结果或副作用。
- 不写同义反复，例如 `Run <tool>`、`Manage <domain>`、`Tool for <name>`。

`summary` 与 `description` 的分工必须清晰：`summary` 只负责发现阶段路由；`description` 负责解释边界、推荐替代工具、别名关系、PlayMode 差异、副作用、不可用原因和下一步指导。

修改或新增 summary 时必须检查：

- `tools group` 输出能否让 AI 选对 group。
- `tools group list <group>` 输出能否让 AI 选对 Tool。
- summary 是否覆盖当前真实能力，且没有写入不存在的能力。
- 易混 group / Tool 是否有明确边界和推荐主入口。
- legacy alias、兼容入口、危险入口是否没有抢占主入口地位。

### Describe 输出规范

`tools describe <tool>` / `context describe <context>` 是 AI 的单工具学习入口。describe 输出必须自足，让陌生 AI 不读源码、不读 README，也能判断是否该调用、如何构造输入、预期输出是什么、有什么风险和不可用条件。

Describe descriptor 字段分工必须清晰：

- `summary` 负责发现阶段路由。
- `description` 负责调用阶段判断：能力边界、默认行为、相邻工具差异、副作用、PlayMode 语义、安全确认、输出控制和跨字段约束。
- `inputSchema` / `outputSchema` 负责字段级输入输出说明。
- `examples` 负责可直接照抄的典型调用。
- 风险与状态字段负责机器可读判断，包括 `destructiveHint`、`supportsDryRun`、`sideEffects`、`playModeSupport`、`supportStatus`、`target`、`availabilityScope`、`requiresPackages`。

Describe 输出必须按需覆盖：

- 操作或读取的对象、动作范围、主要成功结果。
- 必填输入、默认行为、互斥字段、字段优先级、路径/目标解析顺序。
- 相邻工具选择，特别是 alias、兼容入口、fallback 工具和推荐主入口。
- 持久化、副作用、PlayMode/session-affecting、`--yes`、`--dry-run` 语义。
- 大输出工具的分页、截断、continuation、artifact path、`inlineBase64` 等输出控制。
- Context 的只读边界，不得暗示会刷新、修改或触发 Unity 状态变化。

Describe 输出不得包含：

- 内部开发记录、竞品来源说明、changelog、迁移说明或临时阶段说明。
- 与 schema、examples、风险字段、Bridge 实际行为或错误路径不一致的承诺。
- 让字段互相替代的重复内容，例如在 `description` 里写完整参数表或完整调用样例。

新增或修改 describe 相关字段时必须检查：

- `tools describe <tool>` / `context describe <context>` 是否足够让陌生 AI 一次调用成功。
- `tools search <keyword>` 是否会因为 describe 文本误导到错误工具。
- describe 输出是否与 discovery summary、schema、examples、风险字段、Bridge 行为、错误 envelope 和文档一致。

## Extension 形态与文件结构

扩展包必须沿用以下 extension 形态。

标准结构：

```text
extensions/<id>/
  extension.json
  bridge/domains/<domain>.ts
  docs/<id>.md
  unitypkg/Editor/<Domain>/<tool>.cs
```

Unity 项目安装形态：

```text
Packages/com.unity_cli/Extensions/<id>/
```

硬规则：

- 扩展不得创建 `Packages/com.unity_cli.<id>` 这类独立 Unity package
- 扩展能力必须在 manifest 中声明 Unity package 依赖、tools、contexts、csharpEntries，并通过 unityInstall.include 声明需要投影的 unitypkg/docs 文件。
- Core 不得对可选 Unity package 建立编译期依赖。
- 扩展可用性必须根据目标项目安装的 Unity package 判断。
- 扩展工具不可用时，必须返回明确缺失包和下一步操作。
- **扩展 tool 的完整 descriptor/schema 运行时从 bridge 获取；TS 不承担 extension tool 级 descriptor 真相。**
- extension manifest 只负责 extension 级离线信息（如 `providesTools`、`requiresUnityPackages`、title/description），不负责 tool 级 schema/describe。
- 新增扩展能力必须同步更新 manifest、Bridge spec、Unity C#、docs、contract、tests；如需离线 extension 名单/依赖信息，只更新 manifest/registry-data。

### 自定义 Tool / Context 目录约定

项目自定义工具（`IUnityCliTool<TReq,TRes>`）和自定义 Context（`IUnityCliContext<TReq,TRes>`）的 C# 文件必须放在名为 `UnityCLI` 的目录下：

- 合法路径示例：`Assets/UnityCLI/`、`Assets/Editor/UnityCLI/`、`Assets/SomeModule/UnityCLI/`
- 目录位置不限，项目内可存在多个 `UnityCLI` 目录
- 目录名必须是 `UnityCLI`（大小写敏感），便于统一发现和管理
- 这不适用于 CLI 内置 Bridge 包（`Packages/com.unity_cli/`）和扩展（`Packages/com.unity_cli/Extensions/`）

## 安全与破坏性操作

### 安全策略判定标准

只有满足以下条件的工具才标记 `destructiveHint: true`：

- 对资产文件做**真实持久化操作**（修改、创建、删除 `.asset`/`.unity`/`.controller`/`.mat`/`.png` 等文件）。
- 改变项目 `Packages/manifest.json` 或 `ProjectSettings/`。
- 写入持久化光照数据（lightmap、reflection probe bake）。
- **可执行任意代码**（`code.exec`、`editor.menu`、`reflection.method.call`）——潜在任何副作用。

**不标记 `destructiveHint: true`** 的工具：

- 只改场景内存 / 会话状态（GameObject 增删改、组件增删改、Console 清空、Profiler 清空、选择切换、编辑器工具切换、Prefab unpack、场景卸载等）——这些操作不保存则不持久。
- 新建场景内存对象（GameObject、Canvas、UI 元素等）——不破坏已有资源。

### `--yes` 与 `--dry-run` 规则

- `destructiveHint: true` 的 Tool 必须要求 `--yes` 才能执行。
- `destructiveHint: true` 的 Tool 鼓励支持 `--dry-run` 预览，返回可机器读取的 planned effects。
- `destructiveHint: false` 的 Tool 也可以支持 `--dry-run`（如 `component.modify`、`object.modify`），用于预览修改影响——这不依赖 `destructiveHint`。
- 不得通过模糊描述隐藏副作用。
- 破坏性 Tool 的 CLI 路径（`security-policy.ts`）与 Bridge 路径（C# `RegisterCommand` 第 3 参数）都必须保持安全语义一致。

### 三层一致性

破坏性声明必须在三层保持一致：

1. **Bridge spec `destructiveHint`**（`src/bridge/domains/*.ts`）：CLI/AI 路由和 `--yes` 判定的显式来源。
2. **`security-policy.ts`**（`src/security-policy.ts`）：CLI 侧显式安全策略列表。
3. **C# descriptor / registry 层的 `DestructiveHint`**（`ToolDescriptor.DestructiveHint` / 运行时注册后的 descriptor）：Unity 侧真实执行语义来源。

对业务工具而言，三层中任一层声明 `destructive=true`，该工具就需要 `--yes`。修改时三层必须同步。

## 异步执行与 Wait

本项目的 Tool / Context 执行接口默认具备异步能力。调用 CLI 的人/AI 不应感知同步 / 异步差异；调用者只负责发命令并取得最终结果。

### 接口层

- C# Tool / Context 应使用语言原生异步接口（推荐 `ValueTask<T>`）
- TS local tool / context 应使用 `Promise<T>`
- 不再把 deferred 作为“特殊工具形态”暴露给调用者或工具作者

### 框架层 wait

`wait` 是框架能力，不是工具能力。

硬规则：
- 工具作者**不得**直接注册 `EditorApplication.update`
- 工具作者**不得**自行维护 callback 生命周期
- 工具作者只能通过框架提供的 `ctx.Wait`（或等价 wait 服务）申请等待
- 框架必须只有一个中央 scheduler 持有 `EditorApplication.update`
- timeout / cancel / domain reload / Play Mode exit / Unity quitting 时，所有 pending waits 必须由框架统一清理

### CLI 层 wait 语义分层

必须显式区分两层 wait：

1. **命令层 wait（保留）**
   - `--wait`
   - `--wait-timeout-ms`
   - `waitReady`
   它们表达的是 job/batch/编排的交付模式，不是工具内部执行模型。

2. **框架层 wait（统一）**
   - `ctx.Wait.NextFrame(...)`
   - `ctx.Wait.Delay(...)`
   - `ctx.Wait.UntilEditorReady()`
   - `ctx.Wait.ForCallback(...)`
   它们表达的是工具内部跨帧/回调执行方式。

禁止把两层 wait 混淆实现。

## 输出与错误规范

所有用户可见命令必须输出统一 JSON envelope。

成功输出：

- `ok: true`
- `requestId`
- `data`
- 必要时包含 `warnings`、`logsRef`、`jobId`、`durationMs`

失败输出：

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

## 契约与测试

新增或修改能力时，必须维护契约一致性。

必须检查：

- Tool metadata 与 Bridge registry 一致。
- `contracts/command-contract.json` 与源码一致。
- C# handler 注册与 TypeScript metadata 一致。
- JSON Schema 与实际输入输出一致。
- README、docs、Skill 中的命令形态一致。
- 相关测试覆盖成功路径、错误路径、Editor/PlayMode 差异、extension availability。
- 凡新增或修改 Tool / Context / Extension 能力，必须建立真实调用测试矩阵；单元测试、契约测试、源码断言只能作为补充，不能替代 Unity 实例上的真实调用。
- 真实调用矩阵必须覆盖每个受影响能力的每类公开用法：默认调用、主要可选参数、筛选/分页/精读/续读等新增能力、边界值、错误路径、权限/`--yes`/`--dry-run` 行为，以及 Editor/PlayMode 差异。组合爆炸时可以按等价类压缩，但不得跳过任何公开行为类别。
- 正向用例必须验证输出 envelope 的关键业务字段，不只检查 `ok:true`；反向用例必须验证稳定 `error.code`、`message`、`transient`、`retryAfterMs`、`nextActions`。
- 每个真实调用证据必须记录完整命令、输入 JSON、输出 envelope、前置状态、测试夹具、目标 Unity instance/endpoint 和是否产生持久化副作用。
- **当 discovery / describe / offline visibility 的产品语义被明确变更时，tests / docs / skills / descriptor governance 必须整体切到新语义。**

不得为了让测试通过而降低 descriptor 质量或放宽契约。

## Dev 沙盒项目

`./Dev` 是本项目的 Unity 沙盒环境。

规则：

- 可以在 `./Dev` 中安装 Bridge、安装扩展、运行 Unity、创建/修改/删除测试资源。
- 可以用 `./Dev` 验证破坏性操作、PlayMode 行为、package 依赖和 artifact 输出。
- 不要把 `./Dev` 中的临时实验结果当成产品代码提交，除非任务明确要求。
- 如果需要验证 Unity 实际行为，优先使用 `./Dev`，不要只依赖单元测试或静态推断。
- 真实 Unity 调用前，必须确认 `./Dev/UnityProject` 已安装当前源码对应的 Bridge、扩展和测试夹具；不得用旧安装结果冒充当前实现验证。
- 如果本轮修改了 `unitypkg/`、扩展 `unitypkg/`、Bridge C# handler、Unity 安装投影逻辑或 Dev 测试夹具，必须先通过当前 CLI 的 init/install 流程同步到 `./Dev/UnityProject`。
- 如果本轮修改了影响 init/install 的 TypeScript CLI 逻辑，必须先完成必要 build，再同步到 `./Dev/UnityProject`。
- 同步后必须等待 Unity import、compile、domain reload 完成，并用 `editor.state` 或等价只读检查确认 Unity 不处于 busy 状态；否则真实调用证据无效。
- Dev 烟测不得只抽样一个 happy path；凡本轮影响 Unity 侧行为，必须在 `./Dev` 上真实调用所有受影响 Tool / Context / Extension 能力，并按契约与测试章节的矩阵要求覆盖正向、反向、边界、Editor/PlayMode 差异和持久化安全行为。
- 破坏性持久化能力的真实调用必须优先使用 `--dry-run` 验证计划输出；需要真实写入/删除时，只能在 `./Dev` 专用夹具路径执行，并必须记录清理或保留原因。
- 如果某个矩阵项因 Unity 机制、缺少 package、环境故障或安全风险无法真实调用，必须记录精确 blocker、不可替代原因、已覆盖的邻近证据和解锁条件；不得把未调用项写成通过。

## 子代理门禁

每个新增或修改的功能完成后，必须开启一个干净子代理进行 AI 可用性验证。

强制标准：

- 子代理模型应该显式使用"LMD/deepseek-v4-pro"
- 子代理不能继承实现细节或开发过程上下文。
- 子代理必须像陌生 AI 一样，从 CLI 自发现开始。
- Tool 必须经过 `tools group` / `tools group list <group>` / `tools describe <tool>` 后，真实执行 `tools run <tool>`；不得只读取 descriptor 或只调用无关工具。
- Context 必须经过 `context list` / `context describe <context>` 后，真实执行 `context read <context>`；不得只读取 description。
- Extension 必须能理解安装状态、依赖提示、可用性，然后成功发现、describe、真实调用其贡献能力。
- 子代理必须覆盖所有本轮新增或修改的 Tool / Context / Extension 能力。每个受影响能力至少包含默认正向调用、一个代表性可选用法正向调用、一个反向/错误路径调用；新增分页、筛选、精读、续读、PlayMode、dry-run 或 destructive 行为时，还必须覆盖对应行为类别。组合爆炸时可以按等价类压缩，但必须说明未枚举组合为什么等价。
- 子代理只能学习一次 descriptor；如果还需要额外人工解释，说明接口设计失败。
- 主 Agent 给子代理的提示词只能用自然语言说明任务目标、需要验证的能力名称、项目/实例/夹具信息和安全边界；最多只能提示“先查看 describe/工具说明再调用”，不得写出具体发现命令链、最终 `tools run` / `context read` 命令、完整输入 JSON、预期成功输出、源码路径、实现细节或人工补充参数解释。
- 子代理需要使用什么参数、如何构造输入、何时先查哪个能力，必须由公开 describe 信息提供；如果还需要主 Agent 在提示词里补充使用细节，说明 descriptor 设计失败。
- 如果子代理提示词已经把具体发现链、最终命令或输入 JSON 写好让子代理照抄，该次子代理门禁无效，功能必须保持未完成并用新的干净子代理重跑。
- 除 AI 系统性故障、环境异常、Unity 自身不可恢复状态外，子代理失败必须回改 descriptor、schema、示例、错误提示、PlayMode 语义或功能设计。

门禁证据必须记录：

- 子代理使用的发现链。
- 子代理读取的 descriptor 或 context description。
- 每个受影响能力的测试矩阵，标明正向、反向、边界、Editor/PlayMode、dry-run/destructive、分页/筛选/精读/续读等覆盖项。
- 每次真实调用的最终执行命令。
- 每次真实调用的输入 JSON。
- 每次真实调用的输出 envelope。
- 子代理是否收到过被禁止的具体发现链、最终命令、输入 JSON、预期输出、源码路径、实现细节或人工参数解释。
- 如失败，记录失败原因与回改内容。

功能只有同时满足以下条件才算完成：

- 代码实现完成。
- 单元测试或相关自动化测试通过。
- 契约校验通过。
- `./Dev` 沙盒验证通过，除非该功能不依赖 Unity 实例。
- 干净子代理门禁通过。

## 命名与兼容性

本项目仍处于开发中，不承担旧版本兼容义务，但当前规范一旦确认就必须保持一致。

规则：

- Tool 名、Context 名、group、字段名、错误码一旦公开，就视为契约。
- 只保留当前命令形态与当前语义，不得提交并存的双轨实现。
- 任何命名变更都必须同步更新 metadata、docs、Skill、schema、contract、tests。

## AI 开发方式

AI Agent 修改本项目时必须遵守：

- 先理解现有设计，再做外科手术式修改。
- 不做超出需求的抽象、兼容层或新框架。
- 不顺手重构无关代码。
- 新增普通方法要有简要中文注释；关键方法、复杂逻辑、状态流转、跨层入口要说明时机、约束、输入输出或副作用。
- 代码标识符保持英文，注释使用中文。
- 修改后必须说明验证方式；如果无法完成某个门禁，必须明确原因。

### 维护洁癖

项目所有者对代码与规范的洁净度要求很高，必须遵守以下维护规则：

- **文档、注释、descriptor、示例只描述当前代码现状**，不得写历史形态、迁移过程、阶段性说明、兼容背景或“以前如何如何”的叙述，除非该文件本身就是日志/审计/迁移记录。
- **废弃功能必须删除干净**：代码、注释、示例、测试、文档、配置、路由、空壳文件、无效 helper 都不得残留。
- **不允许阶段性代码、阶段性注释、阶段性文档**进入主线。临时桥接、过渡路径、"后续再删"、"先保留"、"兼容一下" 这类中间态不得作为最终提交物存在。
- **实现必须一次性满足当前确认的需求**。如果需求已讨论定稿，提交物就应是该需求的完整最终形态，而不是半完成的阶段产物。
- 如果某个需求当前做不到最终形态，应明确停下并报告 blocker；不要把过渡实现伪装成完成态。

核心判断标准：

> 功能不是给人读 README 后用的，而是给陌生 AI 通过一次自发现就能安全调用的；做不到就是接口设计失败。
