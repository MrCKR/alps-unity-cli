---
name: unity_cli
description: 当任务涉及 Unity Editor 或 Unity 时都优先使用本 Skill。该 Skill 支持通过 `unity_cli` 连接并读取和操作 Unity，包括但不限于查看/修改场景、GameObject、Prefab、日志、资源、Package 和项目设置，读取项目上下文，运行测试、构建、截图，诊断 Bridge/target/instance/job，以及处理项目自定义 Tool/Context。触发词包括 Unity、Editor、Scene、GameObject、Prefab、Asset、Build、Test、GameView、SceneView、Bridge、target、instance、job、截图、打包、场景修改、资源检查。
---

# unity_cli — AI 入口卡片

`unity_cli` 是本机用于操作 Unity Editor 的 CLI。你可以用它发现运行中的 Unity 实例、选择目标 Editor、读取只读 Context、执行 Tool、管理扩展包、运行批处理和诊断问题。

不要把本 Skill 当完整文档。除顶层命令和基本流程外，具体工具、参数、输入输出 schema、风险等级，都通过 CLI 自发现。

## 顶层命令

只使用这些用户可见入口：

- `unity_cli init`：把 Bridge 包安装到 Unity 项目；如果项目已有 `.pi`、`.claude` 或 `.agents`，会自动安装本 Skill。
- `unity_cli extensions list`：查看 CLI 扩展包状态。
- `unity_cli extensions install <id>`：安装满足依赖的 Unity 侧扩展目录到 `Packages/com.unity_cli/Extensions/<id>/`。
- `unity_cli extensions remove <id>`：移除扩展包 Unity 侧目录中 manifest 管理的文件。
- `unity_cli extensions doctor`：诊断扩展包依赖和安装状态。
- `unity_cli target current|set|clear|alias`：查看或设置当前 Unity Editor 目标。
- `unity_cli instances list|prune`：查看或清理 Bridge 注册的 Unity Editor 实例。
- `unity_cli jobs status|logs|describe|cancel`：管理长任务。
- `unity_cli batch schema|run`：查看或执行批处理文件。
- `unity_cli doctor`：诊断 CLI 控制面、Bridge、目标和扩展状态；当 Bridge 报 `E_UNITY_COMPILING` 或所有命令失败时，优先看 `compile_status` 检查项——它会列出 Unity 当前编译错误（常见于 `Packages/com.unity_cli/Extensions/` 下扩展脚本不兼容），并指向具体错误，避免把扩展编译失败误判为 Bridge 不可达。
- `unity_cli tools group|describe|run|search`：发现和执行 Unity Tool。
- `unity_cli context list|describe|read`：发现和读取只读 Context。

Unity 能力域不要作为顶层命令使用。不要猜 `scene`、`asset`、`package`、`build`、`test`、`editor` 等顶层入口；这些能力走 `tools run <tool>` 或 `context read <context>`。

## 标准操作流程

首次使用本 CLI 时，先主动执行一次 `unity_cli tools group`，读取所有 group 的 `summary` 建立能力地图；当前对话或任务链里已经执行过时不要重复执行。

```powershell
unity_cli instances list
unity_cli target set <instanceId-or-alias>
unity_cli tools group
unity_cli tools group list <group>
unity_cli tools describe <tool>
unity_cli tools run <tool> --input '<json>'
```

如果只需要状态或上下文，优先走 Context：

```powershell
unity_cli context list
unity_cli context describe <context>
unity_cli context read <context> --input '<json>'
```

目标选择规则：

- 没有 ready 实例：提示用户打开 Unity Editor，并运行 `unity_cli init --project <path>` 安装 Bridge。
- 多个 ready 实例：不要猜，先 `unity_cli target set <id|alias>`。
- 不要手动读取 Bridge 注册文件或直接 ping 端点；用 `instances`、`target`、`doctor`。

## Tool 自发现

使用 group-first 发现方式，避免一次性展开过多工具。`tools group` 和 `tools group list <group>` 使用 `summary` 做短说明，`tools describe <tool>` 再返回完整 schema 和风险字段：

```powershell
unity_cli tools group
unity_cli tools group list <group>
unity_cli tools describe <tool>
unity_cli tools search <keyword>
```

固定字段含义：

- `source`：`built-in` 为 CLI 内置能力，`extension` 为扩展贡献能力，`project` 为目标 Unity 项目自定义能力。
- `destructiveHint`：为 `true` 时需要 `--yes`；通常表示持久化资产/项目文件修改或任意代码执行。
- `playModeSupport`：`supported`、`session-affecting`、`editor-only`、`runtime-only`、`unknown`。
- `supportStatus`：`live-bridge`、`bridge-job`、`batch-job`、`cli-only`、`planned`、`live-bridge-runtime`。
- `target`：工具或 Context 的作用目标，只能是 `editor`、`runtime` 或 `both`。

执行规则：

- 执行前先 `tools describe <tool>`，读取 `inputSchema`、`destructiveHint`、`supportsDryRun`、`requiresPackages`、`target`、`availabilityScope`、`playModeSupport`。
- 用 `--input`、`--input-file` 或 `--stdin` 传参；缺少 `inputSchema.required` 字段会在执行前返回 `E_SCHEMA_INVALID`。
- Bridge 工具在 Unity 主线程执行；Unity 正在 compile/import/build、Play Mode 切换或 domain reload 时，先用 `tools run editor.state --input '{"waitReady":true}'` 等待 `busy.isBusy=false`。
- Bridge listener 会在 domain reload 后自动重连；用 `editor.state` 的 `bridge` heartbeat/build stamp 判断恢复，不要手动读注册文件或 ping endpoint。
- `destructiveHint: true` 的工具先用 `--dry-run`（如支持），确认后加 `--yes`。
- Runtime 工具通常需要 Play Mode；遇到 `E_RUNTIME_NOT_READY`，按 `next_actions` 处理。
- `playModeSupport: "editor-only"` 的工具不要在 Play Mode 下运行；
- 长任务返回 job 时，用 `unity_cli jobs status <jobId>`、`unity_cli jobs logs <jobId>`、`unity_cli jobs cancel <jobId> --yes`。

## Context 自发现

Context 是只读入口，用于让 AI 先理解项目/Editor 状态。Context 不使用 `summary`，在 list 和 describe 中都用 `description` 说明用途和边界：

```powershell
unity_cli context list
unity_cli context describe <context>
unity_cli context read <context> --input '<json>'
```

规则：

- Context 不应修改 Unity 状态。
- 读取前先 `context describe <context>` 看输入输出 schema。
- 如果找不到 Context，按错误里的 `next_actions` 使用 `context list` 或 `--include-custom`。

## 自定义 Tool / Context 扩展

当 CLI 没有某种能力时，按以下优先级决策：

1. **一次性需求**：用 `tools run code.exec --yes` 动态执行 C# 代码解决。
2. **反复需要的只读信息**：创建自定义 Context（`[UnityCliContext]`），必须声明 `Description`，读取 `references/custom-contexts.md`。
3. **反复需要的操作能力**：创建自定义 Tool（`[UnityCliTool]`），必须声明 `Summary`，读取 `references/custom-tools.md`。

不要绕过 CLI 用其他方式操作 Unity——优先在 CLI 框架内解决。

**目录约定**：所有自定义 Tool 和 Context 的 C# 文件必须放在名为 `UnityCLI` 的目录下（如 `Assets/UnityCLI/`、`Assets/Editor/UnityCLI/`、`Assets/SomeModule/UnityCLI/`）。位置和数量不限，但目录名必须是 `UnityCLI`，便于统一发现和管理。

**扩展包编译约定**：CLI 扩展（`unity_cli extensions install`）的 C# 脚本会安装到 `Packages/com.unity_cli/Extensions/<id>/`，它们以 `partial class UnityCliBridge` 形式与主包同一程序集。因此扩展不自带独立 `.asmdef`，其可编译性依赖主包根部的 `Packages/com.unity_cli/UnityCli.Editor.asmdef`（作用域递归覆盖 `Extensions/`）。如果修改安装投影、移动 asmdef、或把扩展脚本放到包外，会破坏 partial class 并导致整个工程编译失败——此时 `unity_cli doctor` 的 `compile_status` 检查项会列出 Unity 编译错误并指向具体文件。

日常使用自定义能力时不需要读取引文，直接走发现链：

```powershell
unity_cli context read tools.custom
unity_cli tools group list <custom-tool-group> --include-custom
unity_cli tools describe <custom-tool> --include-custom
unity_cli tools run <custom-tool> --include-custom --input '<json>'

unity_cli context list --include-custom
unity_cli context describe <custom-context> --include-custom
unity_cli context read <custom-context> --include-custom --input '<json>'
```

其中 `<custom-tool-group>` 使用 `context read tools.custom` 返回的真实 `group`，不要假设存在名为 `custom` 的工具组。

## 错误与安全

CLI 输出是 JSON envelope。失败时重点看：

- `error.code`
- `error.message`
- `error.transient`
- `error.retry_after_ms`
- `error.next_actions`

处理规则：

- `transient: true`：等待 `retry_after_ms` 后重试，最多重试少量次数。
- `E_TARGET_AMBIGUOUS`：先 `unity_cli target set <id|alias>`。
- `E_RUNTIME_NOT_READY`：进入 Play Mode 或按 `next_actions` 处理。
- `E_TOOL_FINGERPRINT_MISMATCH`：通知用户；只有审计后才用 `--fingerprint-mode warn`。
- 执行任何会改项目/场景/资源的命令前，先确认目标、风险和输入。