# unity_cli 操作协议

只在日常入口不足以判断实例、descriptor、输出、分页、availability、弹窗或长操作语义时读取本文件。

## 实例与目录

`--instance <full-id|canonical-project-path|alias>`是通用显式实例选择器；`command --project-path <path>`是同一canonical identity选择语法。显式空值必须失败；业务`input.instance`不是selector。未传selector时只从当前Unity项目CWD解析；无法确定实例即稳定失败，`--project`和Tool input都不能选实例。

`target alias` 只维护 canonical project identity alias。`source` 只供说明和审计，不影响显示、describe、owner 路由或执行。连接实例的 Tool 目录是权威目录；离线静态 catalog 不能替代它。

## Descriptor

Tool descriptor 常用字段：

- `inputSchema` / `outputSchema`：当前输入输出契约；不要从名称猜字段。
- `available` / `unavailableReason`：当前实例上的可执行状态。
- `destructiveHint`、`sideEffects`、`supportsDryRun`：效果和可选预览，不是确认门。
- `playModeSupport`：`supported`、`session-affecting`、`editor-only`、`runtime-only` 或 `unknown`。
- `target` / `availabilityScope`：Editor、Runtime 或两者及可用范围。
- `requiresPackages`：所需 Unity Package 或 Extension。
- `examples[].argv`：JSON token array；可按当前 schema 调整，不要把它当字符串命令。

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

## Timeout 与调用方决策

`E_EXECUTION_TIMEOUT`、transport 丢失、进程中断或没有可信 terminal envelope 时，只能确定本次调用没有返回可信终态，不能据此判断 mutation 是否发生。调用方可以读取对象、场景、Package、build/test 状态或 artifact manifest，也可以直接决定是否重试、重复调用或继续其他 mutation；Skill 不替调用方设置限制。

compile、import、domain reload 和 listener restart 由 session supervisor 在同一 absolute deadline 内处理；同一次调用可能续接完全相同的请求以取得 terminal。该调用返回后 CLI 不会自行启动新的用户调用，也不限制调用方主动提交后续调用。

## 大输出

- 列表/层级：优先 `query`、`nameFilter`、`componentType`、`className`、`parent`、`pageSize`；把 `pageInfo.nextCursor` 原值作为下一次 `cursor`。
- 深对象：优先 `paths` 或 `viewQuery`，再限制 `maxDepth`、`maxArrayItems`。
- 文本/源码：使用 `offset`、`maxBytes`，检查 `truncated` 与 `nextOffset`。
- 数组/深字段：检查 `returnedCount`、`totalCount`、`nextPath`。
- 截图、相机和大结果默认使用 artifact path；仅确需内联时使用 `inlineBase64:true`。
- AnyJson 的非有限 CLR 数以字符串 `NaN`、`Infinity`、`-Infinity` 表示。

## 弹窗与长操作

原错误包含 `details.blockers` 时，先 `tools run editor.dialogs`。点击前使用当前返回的 dialogId 和规范化 button label；只能选择当前可见且 enabled 的按钮。点击后必须证明弹窗消失或状态改变。

`build.run`、`test.run`、`editor.refresh`、`code.exec` 和需要刷新 Editor 的 Package Tool 在一次调用内等待最终业务结果。超时只表示该次调用没有可信终态，后续动作由调用方决定。`code.exec`的同步C#一旦在Unity主线程开始，timeout不能中止它；死循环、阻塞等待或长期Unity API调用可继续占用Editor。调用方应先判断这类代码是否确有必要；决定执行后应把timeout视为outcome unknown并在重试前观察业务状态。

## 扩展与安装

Extension 控制面使用：

```powershell
unity_cli extensions list --project <path>
unity_cli extensions install <id> --project <path>
unity_cli extensions remove <id> --project <path>
unity_cli extensions doctor --project <path>
```

Skill/runtime bundle 安装使用 `init`；`init --check` 只读校验。Unity project 文件投影完成后，如任务要求 Editor 效果，仍需在已连接实例中完成 import/compile 和业务 readback。
