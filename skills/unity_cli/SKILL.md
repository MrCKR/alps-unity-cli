---
name: unity_cli
description: 当任务涉及 Unity Editor 或 Unity 时都优先使用本 Skill。该 Skill 支持通过 `unity_cli` 连接并读取和操作已打开的 Unity Editor，包括但不限于查看/修改场景、GameObject、Prefab、日志、资源、Package 和项目设置，读取项目上下文，运行测试、构建、截图，诊断 Bridge、target 和 instance，以及处理项目自定义 Tool/Context。触发词包括 Unity、Editor、Scene、GameObject、Prefab、Asset、Build、Test、GameView、SceneView、Bridge、target、instance、截图、打包、场景修改、资源检查。
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

`target` 只管理 canonical project identity alias，不保存全局活动目标。在 `tools` / `context` 的 discovery、describe 和执行命令上持续传 `--instance <full-id|canonical-project-path|alias>`；未传时只从当前 Unity 项目 CWD 解析，不能依赖单实例 fallback。

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

不要猜 `scene`、`asset`、`package`、`build`、`test`、`editor` 等 Unity 能力域是顶层命令；Unity 能力通过 `tools run <tool>` 或 `context read <context>` 暴露。CLI 控制面只使用 `init`、`extensions`、`target`、`instances`、`doctor`。扩展管理使用 `extensions list`、`extensions install`、`extensions remove`、`extensions doctor`。

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

不要从工具名猜输入字段与参数。`describe` 里看清楚 `inputSchema.required`、示例、副作用、PlayMode 支持和返回结构后再构造 `--input`。

## 通用协议

CLI 输出统一是 JSON envelope。

成功 envelope 只包含：

- `ok`：是否成功。
- `requestId`：本次调用 ID。
- `data`：最终业务结果。

失败时重点看：

- `error.code`：稳定错误码。
- `error.message`：人类可读错误。
- `error.transient`：是否适合等待后重试。
- `error.retryAfterMs`：建议等待时间。
- `error.nextActions`：下一步操作；优先按它处理，不要猜补救方案。

Descriptor 常用字段：

- `source`：`built-in`、`extension` 或 `project`。
- `inputSchema` / `outputSchema`：输入输出契约。
- `destructiveHint`：可能不可逆效果的描述信息，不构成执行门。
- `supportsDryRun`：是否支持可选的 `--dry-run` 预览。
- `sideEffects`：副作用说明。
- `playModeSupport`：`supported`、`session-affecting`、`editor-only`、`runtime-only`、`unknown`。
- `target` / `availabilityScope`：作用于 Editor、Runtime 还是两者，以及可用范围。
- `requiresPackages`：需要的 Unity package 或扩展依赖。

大输出协议：

- 层级/列表先用 `pageSize`、`cursor`、`parent`、`query`、`nameFilter`、`componentType`、`className` 缩小结果；将 `pageInfo.nextCursor` 原值传回 `cursor` 续读，两者保持相同 JSON 类型。
- 深读对象优先用 `paths` 或 `viewQuery`，再用 `maxDepth`、`maxArrayItems` 控制展开；AnyJson 值中的非有限 CLR 数分别表示为字符串 `NaN`、`Infinity`、`-Infinity`。
- 文本和源码用 `offset`、`maxBytes` 分段；看 `truncated` 和 `nextOffset`。
- 数组和深层字段看 `truncated`、`returnedCount`、`totalCount`、`nextPath`。
- 截图和相机默认使用 artifact path；只有确实需要内联图像时才传 `inlineBase64:true`。

## AI 规则

- 执行前先确认 canonical project identity；在当前 discovery、describe 和执行命令上显式传完整 `--instance`，或从目标 Unity 项目 CWD 调用。
- 不要读取 Bridge 注册文件、listener、端口或 URL，也不要直接 ping transport；用 `instances`、`doctor` 和 `context read editor.state`。
- compile/import/domain reload/listener restart 由 session supervisor 在同一 absolute deadline 内透明处理；需要即时状态时读 `context read editor.state --input '{}'`，不要轮询或手工重放 mutation。
- 公开 Tool 与 CLI 控制面调用本身即授权。`destructiveHint`、`sideEffects` 和 `supportsDryRun` 描述效果与可选预览，不能阻止执行；需要预览时才使用 `--dry-run`。
- `playModeSupport:"editor-only"` 不要在 PlayMode 下运行；`runtime-only` 通常需要先进入 PlayMode；遇到 `E_RUNTIME_NOT_READY` 按 `nextActions` 处理。
- 缺少必填输入会返回 `E_SCHEMA_INVALID`；回到 `describe` 修正 schema，而不是猜字段名。
- 弹窗 blocker 出现在原错误的 `details.blockers` 时，先读只读 Context `editor.dialogs`，再用 `editor.dialog.click` 传入返回的 dialogId 和规范化 button label；任意当前可见 modal 的任意 enabled button 都可操作。
- 能用内置 Tool 或 Context 解决时，不要绕过 CLI；一次性 Unity 侧代码执行才考虑 `code.exec`。
- 所有需要 Unity 执行的 Tool 都通过已打开的 Editor direct-final 返回结果；`build.run`、`test.run`、`editor.refresh`、`code.exec` 和需要刷新 Editor 的 Package Tool 会在同一次调用中等待最终业务结果。

## 自定义 Tool / Context

当内置能力不够时，按这个顺序决策：

1. 一次性 Unity 侧操作：使用 `tools run code.exec` 动态执行 C#。
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
unity_cli tools run <custom-tool> --input '<json>' --instance <id|projectPath|alias>

unity_cli context list --include-custom
unity_cli context describe <custom-context> --include-custom
unity_cli context read <custom-context> --include-custom --input '<json>'
```

如果需要编写自定义能力，再读取 `references/custom-tools.md` 或 `references/custom-contexts.md`；日常调用不需要先读这些引用文档。