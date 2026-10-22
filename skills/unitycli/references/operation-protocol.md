# UnityCLI 操作协议

只在日常入口不足以判断实例、descriptor、输出、分页、availability、弹窗或长操作语义时读取本文件。

## 实例与目录

`--instance <name|alias|projectPath>`是通用显式实例选择器；`command --project-path <path>`是同一canonical identity选择语法。显式空值必须失败；业务`input.instance`不是selector。未传selector时只从当前Unity项目CWD解析；无法确定实例即稳定失败，`--project`和Tool input都不能选实例。

`target alias` 只维护 canonical project identity alias。`source` 只供说明和审计，不影响显示、describe、owner 路由或执行，也不进入搜索结果。连接实例的 Tool 目录是权威目录；离线静态 catalog 不能替代它。PowerShell中执行多token搜索时使用`tools search '<k1|k2|...>'`；单引号必须包住完整关键词，避免`|`被解释为shell管道。

`tools group list`、`tools search`和省略name的`command`列表按名称稳定分页，默认20项、最大100项。存在`pageInfo.nextCursor`时，将其原值传回同一查询的`--cursor`；不要重新解释或递增cursor。stdout默认是单行紧凑JSON，`--pretty`只增加缩进。

`instances list`逐个验证当前正式实例记录。旧格式、损坏或不可读文件不参与路由，也不阻断其他合法实例；存在时结果包含`invalidRecordCount`。当前项目由Unity在ready时自动重建。需要清理历史记录时调用`instances prune --instance <name|alias|projectPath>`并检查`removed`、`remaining`、`deferred`、`scanned`和`truncated`；不得读取、兼容解析、转换、移动或手工删除内部文件。

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

`--timeout-ms T`先提供一份最多`T`的准入预算。CLI在其中等待首次ready记录、当前编译/导入/重载和任务池；证明Unity未运行时立即返回。准入deadline的`E_UNITY_BUSY`明确证明业务Bridge请求未发送、effect未开始，可以稍后提交新root。获得grant后再创建一份完整`T`的execution预算；token重取、status、rebind、reload、terminal recovery和blocker诊断都只能消费该预算，不能刷新第三份预算。blocker补充最多把现有deadline缩短到10秒；没有剩余时间或没有proof时保持主错误不变。

`E_EXECUTION_TIMEOUT`、放行后的transport丢失、进程中断或没有可信terminal envelope时，只能确定本次调用没有返回可信终态，不能据此判断mutation是否发生。先读取对象、场景、Package、build/test状态或artifact manifest；只有形成新的业务决策后才提交新请求，不把outcome unknown自动重放。同session的exact terminal只保留有界的最近一批完整信封，域专用终态账本按同一规则只保留原调用execution deadline内的identity；两类记录到期后自行裁剪。所以重投一个已被挤出的identity得到`E_EXECUTION_TIMEOUT`与`details.replay="blocked"`且永不重放effect，而重投历史本身满额时准入前返回`E_UNITY_BUSY`（`details.reason="terminal-replay-history-capacity"`或`"navmesh-terminal-ledger-capacity"`），等`retryAfterMs`后即可重提，不需要重启Editor。

调用公开CLI的命令host必须等待进程退出；传入`--timeout-ms T`时至少等待`2 * T + 5000`毫秒，不得后台分离。CLI只输出一个terminal envelope，并在stdout完成且已注册的local owner都settle后退出，或在唯一的1000ms宽限期到达时退出；local settlement cleanup只处理仍未消费的lease，永不settle的本地Promise不能延长该宽限。CLI timeout不会强停Unity中已开始的effect，host提前退出也不能作为停止证明。

compile、import、domain reload和listener generation交接由session supervisor在同一execution absolute deadline内处理。同一次调用仅在`editorSessionId`未改变时，才能用完全相同的request bytes续接durable terminal；首次mutation POST后若session改变，CLI禁止向新session再次POST并返回`E_EXECUTION_TIMEOUT`。

## 运行时调试

运行时调试的九步闭环与证据判断见 [运行时调试闭环](debugging.md)；本节只固定跨能力的协议事实。

`debug.session.*` 是本次 Editor 会话的资源 owner。定义只在同一 `editorSessionId` 内有效：Domain Reload 后恢复的定义不带任何运行中的资源，Play exit 与 Editor 重启使其失效。调查结束用 `debug.session.stop` 并读取清理报告；只有已停止的会话才能被删除，未完成的清理返回 `E_DEBUG_CLEANUP_PENDING`。recipe 是同一会话的可重新加载配置：导入只建立未激活的会话，不执行其中的启动或清理内容。

主线程被暂停时，可达能力是冻结名单：暂停自身的读取与解除、事件读取、线程读取、hook/program/session 的只读项，以及命名了当前 `pauseId` 的字段读取与写入，都在发起请求的线程上直接服务并继续使用同一 live descriptor；其他 mutation 在任何 effect 之前返回 `E_DEBUG_MAIN_THREAD_PAUSED`，并指向暂停的读取与解除入口。暂停必须显式选择有界或无界，没有默认值。解除暂停是解除等待，不是抢占：同步代码仍会运行完，无法证明已解除时必须报告 pending 或 unknown，不得声称线程已被强制恢复。

`debug.*.wait` 由中央 scheduler 跨帧投递，不阻塞 Unity 主线程；一次调用处于等待期间不阻止另一次调用停止或替换同一资源。等待到达 deadline 只表示该次调用没有可信终态。

事件流是跨定义共享的有界环形缓冲。分页游标绑定当时的 publication，失效时返回过期错误，必须重新读取而不是推断；溢出、采样与截断进入覆盖事实。窗口结论在 `verdict`（`passed`/`failed`/`indeterminate`），不在 `ok`；`debug.run.compare` 只在调用方声明的来源、位置、对齐键、值路径与时钟上比较，不默认使用全局 sequence、对象地址或墙上时间。

`debug.console.read` 是一次有界、不可续读的 Unity Console 快照；`debug.console.clear` 是独立的状态修改。编译产物预算由本域的编译型能力共用，删除定义不会回收已加载程序集，也不会自动触发 reload。

## 大输出

- 列表/层级：优先 `query`、`nameFilter`、`componentType`、`className`、`parent`、`pageSize`；把 `pageInfo.nextCursor` 原值作为下一次 `cursor`。
- 深对象：优先 `paths` 或 `viewQuery`，再限制 `maxDepth`、`maxArrayItems`。
- 文本/源码：使用 `offset`、`maxBytes`，检查 `truncated` 与 `nextOffset`。
- 数组/深字段：检查 `returnedCount`、`totalCount`、`nextPath`。
- 截图、相机和大结果默认使用 artifact path；仅确需内联时使用 `inlineBase64:true`。
- AnyJson 的非有限 CLR 数以字符串 `NaN`、`Infinity`、`-Infinity` 表示。

## 弹窗与长操作

`editor.state`公开状态固定为`ready|transitioning|blocked|unresponsive|unverifiable|offline|conflict`。它与`editor.dialogs`、`editor.dialog.click`是仅有的三个exact观察名称：使用冻结本地descriptor进入统一dispatcher，不依赖可能被阻断的Live catalog或普通业务admission；alias、group、handler和其他offline Tool不继承该路径。`tools describe`可精确学习这三个descriptor。三者仍执行schema、availability、completion和结果校验，共用从dispatch开始的一份absolute deadline；`editor.state`只可发出一次private snapshot观察，dialogs仅Windows可用。`conflict`只表示至少两个verified live claim；身份读取失败使用`unverifiable`，transport存在但主线程不推进使用`unresponsive`。原错误包含`details.blockers`时，先`tools run editor.dialogs`。点击前使用当前返回的dialogId和规范化button label；只能选择当前可见且enabled的按钮。点击会保留最小pre-effect预算，deadline先到会关闭首次effect barrier；点击后必须证明弹窗消失或状态改变。

`build.run`、`test.run`、`editor.refresh`、`debug.code.execute` 和需要刷新 Editor 的 Package Tool 在一次调用内等待最终业务结果。超时只表示该次调用没有可信终态，后续动作由调用方决定。`debug.code.execute`的同步C#一旦在Unity主线程开始，timeout不能中止它；死循环、阻塞等待或长期Unity API调用可继续占用Editor。调用方应先判断这类代码是否确有必要；决定执行后应把timeout视为outcome unknown并在重试前观察业务状态。
