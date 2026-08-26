# UnityCLI 操作协议

只在日常入口不足以判断实例、descriptor、输出、分页、availability、弹窗或长操作语义时读取本文件。

## 实例与目录

`--instance <full-id|canonical-project-path|alias>`是通用显式实例选择器；`command --project-path <path>`是同一canonical identity选择语法。显式空值必须失败；业务`input.instance`不是selector。未传selector时只从当前Unity项目CWD解析；无法确定实例即稳定失败，`--project`和Tool input都不能选实例。

`target alias` 只维护 canonical project identity alias。`source` 只供说明和审计，不影响显示、describe、owner 路由或执行。连接实例的 Tool 目录是权威目录；离线静态 catalog 不能替代它。PowerShell中执行多token搜索时使用`tools search '<k1|k2|...>'`；单引号必须包住完整关键词，避免`|`被解释为shell管道。

`instances list`逐个验证当前Registry v3正式文件。旧格式、损坏或不可读文件不参与路由，也不阻断其他合法实例；存在时结果包含`invalidRecordCount`，应使用`doctor`读取计数告警。不得读取、兼容解析、转换、移动或删除内部文件。显式选择对应坏instanceId仍严格失败。

## Descriptor

Tool descriptor 常用字段：

- `inputSchema`：当前输入契约；不要从名称猜字段。结果 schema 由内部 contract 校验，不在公开 describe 展开。
- `available:false` / `unavailableReason`：仅当前实例不可执行时出现；缺失表示当前可用。
- `destructiveHint`、`sideEffects`、`supportsDryRun`：效果和可选预览，不是确认门。
- `playModeSupport`：`supported`、`session-affecting`、`editor-only`、`runtime-only` 或 `unknown`。
- `target`：Editor、Runtime 或两者。
- `requiresPackages`：非空 Unity Package 或 Extension 依赖。
- `examples[].input` / `examples[].execution`：结构化业务输入和按需 dry-run/timeout 控制。

读取型Tool descriptor必须声明纯只读边界、schema、target、availability和PlayMode语义，不得触发import、compile、domain reload、PlayMode变化或文件写入。`command`只按同一`inputSchema`构造typed input，不改变descriptor语义。

## JSON envelope

成功：

- `ok:true`
- `requestId`
- `data`：最终业务结果

失败重点：

- `error.code`：稳定错误码
- `error.message`
- `error.transient` / `retryAfterMs`
- `error.nextActions`
- `error.details.blockers`

`nextActions`、`transient` 和 `retryAfterMs` 都是恢复建议，不构成权限、确认或重放门。`E_SCHEMA_INVALID` 可回到 `describe` 修正输入；`E_RUNTIME_NOT_READY`、Editor/PlayMode availability 错误可结合 descriptor 和当前状态处理。

## Timeout、任务池与调用方决策

每个live Editor或Development Player session拥有独立任务池，默认达到5个active root时关闭准入，降到4仍关闭，降到3才重开。CLI在业务请求前自动等待；AI不手工轮询、不读取active count，也不管理内部token。统一catalog在Editor和Runtime同时在线时，会分别经过两个session的准入后才返回完整结果。

`--timeout-ms T`先提供一份最多`T`的准入预算。准入deadline的`E_UNITY_BUSY`明确证明业务Bridge请求未发送、effect未开始，可以稍后提交新root。获得grant后再创建一份完整`T`的execution预算；token重取、status、rebind、reload和terminal recovery都只能消费该预算，不能刷新第三份预算。

`E_EXECUTION_TIMEOUT`、放行后的transport丢失、进程中断或没有可信terminal envelope时，只能确定本次调用没有返回可信终态，不能据此判断mutation是否发生。先读取对象、场景、Package、build/test状态或artifact manifest；只有形成新的业务决策后才提交新请求，不把outcome unknown自动重放。

调用公开CLI的命令host必须等待进程退出；传入`--timeout-ms T`时至少等待`2 * T + 5000`毫秒，不得后台分离。CLI在stdout写完或最多1秒后退出。CLI timeout不会强停Unity中已开始的effect，host提前退出也不能作为停止证明。

compile、import、domain reload和listener generation交接由session supervisor在同一execution absolute deadline内处理。同一次调用仅在`editorSessionId`未改变时，才能用完全相同的request bytes续接durable terminal；首次mutation POST后若session改变，CLI禁止向新session再次POST并返回`E_EXECUTION_TIMEOUT`。

## 大输出

- 列表/层级：优先 `query`、`nameFilter`、`componentType`、`className`、`parent`、`pageSize`；把 `pageInfo.nextCursor` 原值作为下一次 `cursor`。
- 深对象：优先 `paths` 或 `viewQuery`，再限制 `maxDepth`、`maxArrayItems`。
- 文本/源码：使用 `offset`、`maxBytes`，检查 `truncated` 与 `nextOffset`。
- 数组/深字段：检查 `returnedCount`、`totalCount`、`nextPath`。
- 截图、相机和大结果默认使用 artifact path；仅确需内联时使用 `inlineBase64:true`。
- AnyJson 的非有限 CLR 数以字符串 `NaN`、`Infinity`、`-Infinity` 表示。

## 弹窗与长操作

`editor.state`公开状态固定为`ready|transitioning|blocked|unresponsive|unverifiable|offline|conflict`。它与`editor.dialogs`、`editor.dialog.click`使用exact本地descriptor进入统一dispatcher，不依赖可能被阻断的Live catalog；其他Tool不使用静态fallback。`conflict`只表示至少两个verified live claim；身份读取失败使用`unverifiable`，transport存在但主线程不推进使用`unresponsive`。原错误包含`details.blockers`时，先`tools run editor.dialogs`。点击前使用当前返回的dialogId和规范化button label；只能选择当前可见且enabled的按钮。点击后必须证明弹窗消失或状态改变。

`build.run`、`test.run`、`editor.refresh`、`code.exec` 和需要刷新 Editor 的 Package Tool 在一次调用内等待最终业务结果。超时只表示该次调用没有可信终态，后续动作由调用方决定。`code.exec`的同步C#一旦在Unity主线程开始，timeout不能中止它；死循环、阻塞等待或长期Unity API调用可继续占用Editor。调用方应先判断这类代码是否确有必要；决定执行后应把timeout视为outcome unknown并在重试前观察业务状态。

## 扩展与安装

Extension 控制面使用：

```powershell
unitycli extensions list --project <path>
unitycli extensions install <id> --project <path>
unitycli extensions remove <id> --project <path>
unitycli extensions doctor --project <path>
```

Skill/runtime bundle 安装使用 `init`；`init --check` 只读校验。Unity project 文件投影完成后，如任务要求 Editor 效果，仍需在已连接实例中完成 import/compile 和业务 readback。
