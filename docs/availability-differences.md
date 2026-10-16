# 可用性差异（Availability Differences）

## 核心原则

能力目录和可用性都是per-instance。`tools`读取一个已连接Unity实例的完整catalog；省略name的`command`只是该catalog的command-name投影。不同项目、Package、Extension、项目注册和Editor状态会产生不同descriptor集合与`available`状态。

完整显示不等于无条件执行。目录不按 source 或 Edit/Play 状态过滤，执行仍按 schema、Package、PlayMode、安全、deadline 和 completion contract fail closed。

## 外部控制面排除

catalog只包含必须由已打开Unity Editor或development Player提供的项目内自动化。auth、Unity Hub、Editor安装或打开、Cloud、License和proxy配置由系统或官方控制面工具负责；`unitycli`不会把这些操作包装成Tool、相似名称映射或额外顶层命令。

## 实例状态

| 状态 | Discovery 与执行 |
|------|------------------|
| `ready` | 返回当前实例完整Tool catalog；session/listener与近期主线程pulse均已证明 |
| `transitioning` | supervisor在同一deadline内等待lifecycle与listener generation交接 |
| `blocked` | 普通catalog/Tool返回blocker error；exact `editor.state` / `editor.dialogs` / `editor.dialog.click`保留无业务准入的本地观察与恢复路径。`editor.state` 在两种独立证据下返回该状态：独立观察到的 Windows modal（`blocker.type="dialog"`），或当前 live pause publication 证明的故意主线程暂停（`blocker.type="debug-pause"`） |
| `unresponsive` | transport存在但主线程不可响应；返回`E_UNITY_UNRESPONSIVE` |
| `unverifiable` | ownership证据不完整；返回`E_INSTANCE_UNVERIFIABLE`，不得误报duplicate |
| `offline` | 不返回capability catalog；返回`E_INSTANCE_NOT_RUNNING` |
| `conflict` | 至少两个verified live claim；返回`E_INSTANCE_CONFLICT` |

没有 selector 且 CWD 不在 Unity 项目中时返回 `E_INSTANCE_REQUIRED`。静态 snapshot 不作为公开 fallback。只有三个exact Editor观察名称、六个域专用durable status名称及`tools describe`对它们的精确学习入口可使用冻结本地descriptor；status仍执行一次选定实例上的SafeStatus读取，相邻名称、alias、group、handler和其他offline Tool仍按Live catalog与普通准入失败。dialogs只在Windows可用。

主线程暂停期间的可达能力是冻结名单，而不是回退清单：`tools group/list/search`、`tools describe`、两个公开入口、`debug.pause.list/wait`、`debug.pause.inspect/evaluate/resume`、`debug.events.read`、`debug.thread.list`、`debug.hook.list/info`、`debug.session.list/info`、`debug.program.list/info`、`internal.editor.state.snapshot` 与命名了 `pauseId` 的 `debug.value.read/write` 仍在发起请求的线程上直接服务，并继续使用同一个 live catalog descriptor；没有发布 catalog 时如实报告不可用，不回退到静态清单。其他业务 mutation 在任何 effect 之前返回 `E_DEBUG_MAIN_THREAD_PAUSED`。

健康探针在 `mainThread.debugPause` 上单独发布当前主线程暂停；它不修改主线程 pulse。因此“transport 可达”“pulse 新鲜”和“主线程被故意暂停”不能互相替代：pulse 停止且有该 publication 时状态为 `blocked`，只有 pulse 停止而没有该 publication 时仍为 `unresponsive`，绝不用旧文件或 worker 现场推断因果。

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

`source` 只用于说明和审计，不影响 visibility、describe、execution 或公开路由。Extension dependency gate 只接受 generated exact owner 与 compiled assembly identity 的闭合证明；不从名称前缀冒充 Extension。安装 Package/Extension 后重新运行原 discovery 命令。

## 项目注册能力

项目通过 `IUnityCliTool<TReq,TRes>` 注册的能力，与 package 内置和 Extension 能力使用相同入口：

```powershell
unitycli tools group
unitycli tools group list <group>
unitycli tools describe <tool>
unitycli tools run <tool> --input '<json>'
```

不同实例可以注册不同名称和分组；不需要额外来源参数，也不存在来源专用能力入口。official attribute Tool固定使用`UnityPipeline_<原名>`和`UnityPipeline`group，可由`command <原名>`选择。

## EditMode 与 PlayMode

Editor 与 Runtime 能力在两种模式中都保持可发现。`available`、`unavailableReason`、`target`、`availabilityScope` 和 `playModeSupport` 表达当前状态：

| 当前模式 | `target: editor` | `target: runtime` |
|---------|------------------|-------------------|
| EditMode | 通常可执行 | 显示但通常 `available:false`；执行返回 `E_RUNTIME_NOT_READY` |
| PlayMode | 依据 `playModeSupport` 执行或拒绝 | 可执行；结果通常限于当前 PlayMode session |

进入 PlayMode 的标准步骤：

1. `unitycli tools run editor.play-mode.start --input '{}'`
2. `unitycli tools run editor.state --input '{}'`
3. 重新 `tools describe <player-runtime 工具名>` 或直接按 `nextActions` 调用

Editor-only Tool 在 PlayMode 中同样保持可见，但执行继续返回准确错误。

## Unity 版本差异

Unity API版本门禁不从major版本猜测。Tool仍在当前实例catalog中，`available:false`与`unavailableReason`说明当前编译API surface。

`screenshot.editor-element`只在Unity6000.7+可执行，因为它唯一使用该版本公开的UI Toolkit `VisualElement.CaptureToRenderTexture`。更早版本仍可discover/describe，但actual与framework dry-run都在render、artifact write和effect barrier之前返回`E_EDITOR_ELEMENT_CAPTURE_UNAVAILABLE`。

`editor.autotick.set`会在当前Editor没有可调用的`EditorApplication.SignalTick`时返回`E_EDITOR_AUTOTICK_UNAVAILABLE`，不会注册替代update owner或持久化fallback。

## Domain Reload

Domain reload期间，Registry v3 lifecycle将实例归约为`transitioning`，listener generation随后交接；Health v3中该generation的首个Unity主线程pulse才允许归约为`ready`。CLI在原命令截止时间内等待首次记录或新ready generation，不读取固定时长marker，也不要求AI轮询。catalog discovery 会把 exact editor session、listener ID和generation绑定到整次顶层调用及root admission；首次业务POST前若发生变化，返回`E_TOOL_CATALOG_CHANGED`，不发送旧业务请求。session supervisor只在同一`editorSessionId`内续接完全相同的请求；首次mutation POST后若session改变，不向新session重投，并返回`E_EXECUTION_TIMEOUT`与outcome unknown。调用方先读取业务状态，再决定是否提交全新请求。

## AI Agent 处理方式

1. 显式传 `--instance <name|alias|projectPath>`，或从目标 Unity 项目 CWD 调用。
2. 先 discovery，再 describe；不要从 source 或工具名推断参数。
3. 对 `available:false` 读取 `unavailableReason`、`requiresPackages` 和 `nextActions`。
4. Package、Extension、项目代码、domain reload 或 PlayMode 变化后重新 discovery。
5. 即时状态使用 `tools run editor.state --input '{}'`。

## 交叉引用

- [Target 与 Instance](./target-and-instance.md)
- [工具发现](./tool-discovery.md)
- [错误码](./error-codes.md)
