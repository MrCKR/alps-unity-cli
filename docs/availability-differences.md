# 可用性差异（Availability Differences）

## 核心原则

能力目录和可用性都是per-instance。`tools`读取一个已连接Unity实例的完整catalog；省略name的`command`只是该catalog的command-name投影。不同项目、Package、Extension、项目注册和Editor状态会产生不同descriptor集合与`available`状态。

完整显示不等于无条件执行。目录不按 source 或 Edit/Play 状态过滤，执行仍按 schema、Package、PlayMode、安全、deadline 和 completion contract fail closed。

## 外部控制面排除

catalog只包含必须由已打开Unity Editor或development Player提供的项目内自动化。auth、Unity Hub、Editor安装或打开、Cloud、License和proxy配置由系统或官方控制面工具负责；`unity_cli`不会把这些操作包装成Tool、相似名称映射或额外顶层命令。

## 实例状态

| 状态 | Discovery 与执行 |
|------|------------------|
| `ready` | 返回当前实例完整 Tool catalog；可执行状态由 descriptor 表达 |
| `transitioning` | supervisor 在同一 deadline 内尝试绑定恢复后的 generation；无法稳定时返回结构化 transition/timeout error |
| `blocked` | discovery 或执行返回 blocker error；`editor.state` / `editor.dialogs` 提供只读观察 |
| `offline` | 不返回 capability catalog；返回 `E_INSTANCE_NOT_RUNNING` |
| `conflict` | 不猜测 session；返回 `E_INSTANCE_CONFLICT` |

没有 selector 且 CWD 不在 Unity 项目中时返回 `E_INSTANCE_REQUIRED`。静态 snapshot 不作为公开 fallback。

## Package 与 Extension

依赖特定 Unity Package 的能力仍出现在实例目录中。descriptor 使用以下字段表达当前可执行性：

```json
{
  "name": "probuilder.create-shape",
  "source": "extension",
  "requiresPackages": ["com.unity.probuilder"],
  "available": false,
  "unavailableReason": "E_EXTENSION_DEPENDENCY_MISSING: ..."
}
```

`source` 只用于说明和审计，不影响 visibility、describe、execution 或公开路由。安装 Package/Extension 后重新运行原 discovery 命令。

## 项目注册能力

项目通过 `IUnityCliTool<TReq,TRes>` 注册的能力，与 package 内置和 Extension 能力使用相同入口：

```powershell
unity_cli tools group
unity_cli tools group list <group>
unity_cli tools describe <tool>
unity_cli tools run <tool> --input '<json>'
```

不同实例可以注册不同名称和分组；不需要额外来源参数，也不存在来源专用能力入口。official attribute Tool固定使用`UnityPipeline_<原名>`和`UnityPipeline`group，可由`command <原名>`选择。

## EditMode 与 PlayMode

Editor 与 Runtime 能力在两种模式中都保持可发现。`available`、`unavailableReason`、`target`、`availabilityScope` 和 `playModeSupport` 表达当前状态：

| 当前模式 | `target: editor` | `target: runtime` |
|---------|------------------|-------------------|
| EditMode | 通常可执行 | 显示但通常 `available:false`；执行返回 `E_RUNTIME_NOT_READY` |
| PlayMode | 依据 `playModeSupport` 执行或拒绝 | 可执行；结果通常限于当前 PlayMode session |

进入 PlayMode 的标准步骤：

1. `unity_cli tools run editor.play --input '{}'`
2. `unity_cli tools run editor.state --input '{}'`
3. 重新 `tools describe <runtime-tool>` 或直接按 `nextActions` 调用

Editor-only Tool 在 PlayMode 中同样保持可见，但执行继续返回准确错误。

## Unity 版本差异

Unity API版本门禁不从major版本猜测。Tool仍在当前实例catalog中，`available:false`与`unavailableReason`说明当前编译API surface。

`screenshot.editor-element`只在Unity6000.7+可执行，因为它唯一使用该版本公开的UI Toolkit `VisualElement.CaptureToRenderTexture`。更早版本仍可discover/describe，但actual与framework dry-run都在render、artifact write和effect barrier之前返回`E_EDITOR_ELEMENT_CAPTURE_UNAVAILABLE`。

`editor.autotick.set`会在当前Editor没有可调用的`EditorApplication.SignalTick`时返回`E_EDITOR_AUTOTICK_UNAVAILABLE`，不会注册替代update owner或持久化fallback。

## Domain Reload

Domain reload 期间，registry 将实例归约为 `transitioning`，listener generation 可以变化。session supervisor 在同一 direct call 与 absolute deadline 内重新验证 generation；终态前断连或 deadline 耗尽返回 `E_EXECUTION_TIMEOUT`。该错误只表示本次调用没有可信终态；调用方可观察状态、重试、重复调用或继续 mutation，并自行决定后续动作。`nextActions` 仅为建议。

## AI Agent 处理方式

1. 显式传 `--instance <id|projectPath|alias>`，或从目标 Unity 项目 CWD 调用。
2. 先 discovery，再 describe；不要从 source 或工具名推断参数。
3. 对 `available:false` 读取 `unavailableReason`、`requiresPackages` 和 `nextActions`。
4. Package、Extension、项目代码、domain reload 或 PlayMode 变化后重新 discovery。
5. 即时状态使用 `tools run editor.state --input '{}'`。

## 交叉引用

- [Target 与 Instance](./target-and-instance.md)
- [工具发现](./tool-discovery.md)
- [错误码](./error-codes.md)
