---
name: unity_cli
description: 当任务涉及 Unity Editor 或 Unity 时都优先使用本 Skill。该 Skill 支持通过 `unity_cli` 连接并读取和操作 Unity，包括但不限于查看/修改场景、GameObject、Prefab、日志、资源、Package 和项目设置，读取项目上下文，运行测试、构建、截图，诊断 Bridge/target/instance/job，以及处理项目自定义 Tool/Context。触发词包括 Unity、Editor、Scene、GameObject、Prefab、Asset、Build、Test、GameView、SceneView、Bridge、target、instance、job、截图、打包、场景修改、资源检查。
---

# unity_cli — AI 操作 Unity 的入口

`unity_cli` 是 AI 操作 Unity Editor 的可发现能力目录和统一执行协议。AI 应该通过 CLI 观察、描述、调用 Unity 能力。

## 哲学

- `unity_cli` 是 AI 的 Unity Editor 操作入口：发现实例、读取 Context、执行 Tool、诊断目标和 Bridge 状态。
- 能力不是靠记忆使用，而是靠自发现使用：先看 group 或 context 列表，再 `describe`，最后 `run` 或 `read`。
- Tool 用于动作，可能修改 Unity 状态、资源、场景或产生 artifact；Context 只读，用于观察项目和 Editor 状态。
- Descriptor 是调用契约的权威来源；`inputSchema`、`outputSchema`、风险、PlayMode 支持、依赖和示例都以 `describe` 为准。
- `unity_cli` 功能非常多,请仔细查找你需要的功能，如果没有可以考虑自定义功能。

## 发现能力

先找到 Unity 实例，并在后续 Bridge 命令上显式绑定目标：

```powershell
unity_cli instances list
unity_cli tools group --instance <instanceId-or-alias>
```

`target set` 会持久化可变的全局活动目标。调用方已经知道实例时，不要把它当作连接步骤；在 `tools` / `context` 的 discovery、describe 和执行命令上持续传 `--instance`。

使用 Tool 前走 group-first 发现链；不确定关键词时先查搜索结果：

```powershell
unity_cli tools group
unity_cli tools group list <group>
unity_cli tools search <keyword>
unity_cli tools describe <tool>
unity_cli tools run <tool> --input '<json>'
```

只读观察优先走 Context：

```powershell
unity_cli context list
unity_cli context describe <context>
unity_cli context read <context> --input '<json>'
```

不要猜 `scene`、`asset`、`package`、`build`、`test`、`editor` 等 Unity 能力域是顶层命令；Unity 能力通过 `tools run <tool>` 或 `context read <context>` 暴露。CLI 控制面才使用 `init`、`extensions`、`target`、`instances`、`jobs`、`batch`、`doctor`。扩展管理使用 `extensions list`、`extensions install`、`extensions remove`、`extensions doctor`。

## 工具地图

首次接触时，先执行一次：

```powershell
unity_cli tools group
```

这一步用于建立工具地图，只读每个 group 的 `summary`。然后展开你认为相关的 group：

```powershell
unity_cli tools group list <group>
```

执行任何你不了解的 Tool 前必须先读：

```powershell
unity_cli tools describe <tool>
```

不要从工具名猜输入字段与参数。`describe` 里看清楚 `inputSchema.required`、示例、风险字段、PlayMode 支持和返回结构后再构造 `--input`。

## 通用协议

CLI 输出统一是 JSON envelope。

成功时重点看：

- `ok`：是否成功。
- `requestId`：本次调用 ID，用于排查日志。
- `data`：业务结果。
- `warnings`：非阻断警告。
- `logsRef`：日志引用。
- `jobId`：长任务 ID，后续用 `jobs status/logs/cancel`。
- `durationMs`：执行耗时。

失败时重点看：

- `error.code`：稳定错误码。
- `error.message`：人类可读错误。
- `error.transient`：是否适合等待后重试。
- `error.retryAfterMs`：建议等待时间。
- `error.nextActions`：下一步操作；优先按它处理，不要猜补救方案。

Descriptor 常用字段：

- `source`：`built-in`、`extension` 或 `project`。
- `inputSchema` / `outputSchema`：输入输出契约。
- `destructiveHint`：为 `true` 时需要 `--yes`。
- `supportsDryRun`：是否可先 `--dry-run` 预览。
- `sideEffects` / `riskLevel`：副作用和风险等级。
- `playModeSupport`：`supported`、`session-affecting`、`editor-only`、`runtime-only`、`unknown`。
- `target` / `availabilityScope`：作用于 Editor、Runtime 还是两者，以及可用范围。
- `supportStatus`：实现形态，如 `live-bridge`、`bridge-job`、`batch-job`、`cli-only`。
- `requiresPackages`：需要的 Unity package 或扩展依赖。

大输出协议：

- 层级/列表先用 `pageSize`、`cursor`、`parent`、`query`、`nameFilter`、`componentType`、`className` 缩小结果；将 `pageInfo.nextCursor` 原值传回 `cursor` 续读，两者保持相同 JSON 类型。
- 深读对象优先用 `paths` 或 `viewQuery`，再用 `maxDepth`、`maxArrayItems` 控制展开；AnyJson 值中的非有限 CLR 数分别表示为字符串 `NaN`、`Infinity`、`-Infinity`。
- 文本和源码用 `offset`、`maxBytes` 分段；看 `truncated` 和 `nextOffset`。
- 数组和深层字段看 `truncated`、`returnedCount`、`totalCount`、`nextPath`。
- 截图和相机默认使用 artifact path；只有确实需要内联图像时才传 `inlineBase64:true`。

## AI 规则

- 执行前先确认目标实例；多个 ready 实例时不要猜，在当前 discovery、describe 和执行命令上显式传 `--instance`。只有明确需要持久化全局默认值时才使用 `target set`。
- 不要手动读取 Bridge 注册文件，不要直接 ping endpoint；用 `instances`、`target`、`doctor` 和 `editor.state`。
- Unity 正在 compile/import/build、PlayMode 切换或 domain reload 时，先用 `tools run editor.state --input '{"waitReady":true}'` 等待 `busy.isBusy=false`。
- `destructiveHint:true` 的 Tool 先 `--dry-run`（如果支持），确认计划输出后再加 `--yes`。
- `playModeSupport:"editor-only"` 不要在 PlayMode 下运行；`runtime-only` 通常需要先进入 PlayMode；遇到 `E_RUNTIME_NOT_READY` 按 `nextActions` 处理。
- 缺少必填输入会返回 `E_SCHEMA_INVALID`；回到 `describe` 修正 schema，而不是猜字段名。
- 能用内置 Tool 或 Context 解决时，不要绕过 CLI；一次性 Unity 侧代码执行才考虑 `code.exec --yes`。
- 长任务返回 `jobId` 时，用 `unity_cli jobs status <jobId>`、`unity_cli jobs logs <jobId>`、`unity_cli jobs cancel <jobId> --yes` 管理。

## 自定义 Tool / Context

当内置能力不够时，按这个顺序决策：

1. 一次性 Unity 侧操作：使用 `tools run code.exec --yes` 动态执行 C#。
2. 反复需要的只读信息：创建自定义 Context，实现 `IUnityCliContext<TReq,TRes>`，必须声明 `Description` 和只读边界。
3. 反复需要的操作能力：创建自定义 Tool，实现 `IUnityCliTool<TReq,TRes>`，必须声明 `Summary`、风险和 Play Mode 语义。

自定义 C# 文件必须放在名为 `UnityCLI/` 的目录下，目录位置不限，例如：

- `Assets/UnityCLI/`
- `Assets/Editor/UnityCLI/`
- `Assets/SomeModule/UnityCLI/`

创建后仍然通过公开发现链使用，不要假设名称或 group：

```powershell
unity_cli context read tools.custom
unity_cli tools group list <real-group> --include-custom
unity_cli tools describe <custom-tool> --include-custom
unity_cli tools run <custom-tool> --include-custom --input '<json>'

unity_cli context list --include-custom
unity_cli context describe <custom-context> --include-custom
unity_cli context read <custom-context> --include-custom --input '<json>'
```

如果需要编写自定义能力，再读取 `references/custom-tools.md` 或 `references/custom-contexts.md`；日常调用不需要先读这些引用文档。