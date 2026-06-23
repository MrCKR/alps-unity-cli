---
name: unity_cli
description: 当用户任务涉及 Unity Editor 或 Unity 项目时优先使用本 Skill。该 Skill 支持通过 `unity_cli` 连接并操作 Unity，包括查看/修改场景、GameObject、Prefab、资源、Package 和项目设置，读取项目上下文，运行测试、构建、截图，诊断 Bridge/target/instance/job，以及处理项目自定义 Tool/Context。触发词包括 Unity、Editor、Scene、GameObject、Prefab、Asset、Build、Test、GameView、SceneView、Bridge、target、instance、job、截图、打包、场景修改、资源检查。
---

# unity_cli — AI 入口卡片

`unity_cli` 是本机用于操作 Unity Editor 的 CLI。你可以用它发现运行中的 Unity 实例、选择目标 Editor、读取只读 Context、执行 Tool、管理扩展包、运行批处理和诊断问题。

不要把本 Skill 当完整文档。除顶层命令和基本流程外，具体工具、参数、输入输出 schema、风险等级，都通过 CLI 自发现。

## 顶层命令

只使用这些用户可见入口：

- `unity_cli init`：把 Bridge 包安装到 Unity 项目；如果项目已有 `.pi`、`.claude` 或 `.agent`，会自动安装本 Skill。
- `unity_cli extensions list`：查看 CLI 扩展包状态。
- `unity_cli extensions install <id>`：安装满足依赖的扩展包 Unity 侧 bundle。
- `unity_cli extensions remove <id>`：移除扩展包 Unity 侧 bundle。
- `unity_cli extensions doctor`：诊断扩展包依赖和安装状态。
- `unity_cli target current|set|clear|alias`：查看或设置当前 Unity Editor 目标。
- `unity_cli instances list|prune`：查看或清理 Bridge 注册的 Unity Editor 实例。
- `unity_cli jobs status|logs|describe|cancel`：管理长任务。
- `unity_cli batch schema|run`：查看或执行批处理文件。
- `unity_cli doctor`：诊断 CLI 控制面、Bridge、目标和扩展状态。
- `unity_cli tools group|describe|run|search`：发现和执行 Unity Tool。
- `unity_cli context list|describe|read`：发现和读取只读 Context。

Unity 能力域不要作为顶层命令使用。不要猜 `scene`、`asset`、`package`、`build`、`test`、`editor` 等顶层入口；这些能力走 `tools run <tool>` 或 `context read <context>`。

## 标准操作流程

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

使用 group-first 发现方式，避免一次性展开过多工具：

```powershell
unity_cli tools group
unity_cli tools group list <group>
unity_cli tools describe <tool>
unity_cli tools search <keyword>
```

执行规则：

- 执行前先 `tools describe <tool>`，读取 `inputSchema`、`readOnlyHint`、`destructiveHint`、`requiresPackages`、`target`、`availabilityScope`。
- 用 `--input`、`--input-file` 或 `--stdin` 传参。
- `destructiveHint: true` 的工具先用 `--dry-run`（如支持），确认后加 `--yes`。
- Runtime 工具通常需要 Play Mode；遇到 `E_RUNTIME_NOT_READY`，按 `next_actions` 处理。
- 长任务返回 job 时，用 `unity_cli jobs status <jobId>`、`unity_cli jobs logs <jobId>`、`unity_cli jobs cancel <jobId> --yes`。

## Context 自发现

Context 是只读入口，用于让 AI 先理解项目/Editor 状态：

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

只在要要创建、修改、审计或排查项目自定义能力时读取引文：

- 需要让 AI 执行动作、修改项目、生成资源、运行项目逻辑：读取 `references/custom-tools.md`，使用 `[UnityCliTool]`。
- 需要给 AI 增加只读项目知识、状态摘要、团队约定或资源索引：读取 `references/custom-contexts.md`，使用 `[UnityCliContext]`。

日常使用自定义能力时不需要读取引文，直接走发现链：

```powershell
unity_cli tools group list custom --include-custom
unity_cli tools describe <custom-tool>
unity_cli tools run <custom-tool> --input '<json>'

unity_cli context list --include-custom
unity_cli context describe <custom-context>
unity_cli context read <custom-context> --input '<json>'
```

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