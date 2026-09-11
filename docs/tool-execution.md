# 工具执行（Tool Execution）

`tools run`是统一Tool dispatcher的结构化JSON入口。package内置、Extension、项目注册、Editor、Runtime和package-local Tool也可通过[`command`](command-execution.md)按同一live schema调用；二者不建立第二套catalog或执行路径。

## 基本用法

```powershell
# 通过 --input 传参（JSON 字符串）
unitycli tools run camera.list --input '{}'

# 通过 --input-file 传参（JSON 文件）
unitycli tools run screenshot.from-camera --input-file params.json

# 通过 --stdin 传参
echo '{"target":"Main Camera"}' | unitycli tools run screenshot.from-camera --stdin
```

## 执行路由

`tools run` 先绑定一个已连接 Unity 实例并从其完整 catalog 解析名称，再根据公开 descriptor 的 `target` 和内部 owner 执行。package-local CLI Tool 可在 CLI 进程内产生业务结果，但同样不能绕过实例绑定和统一 discovery。Unity Tool 只连接用户已打开并由 `--instance` 或 CWD 唯一选中的 Editor，在同一次 direct 调用中返回已证明的终态业务结果。内部 listener 和 transport 由 CLI 管理，不是用户可调用接口。

### Target 路由

| target 值 | 路由行为 |
|-----------|---------|
| `editor` | 直接 Bridge 执行 |
| `runtime` | 检查 Play Mode 状态，非 Play Mode 返回 `E_RUNTIME_NOT_READY` |
| `both` | 默认走 editor 通道；`--runtime` 标志强制走 runtime 通道 |

## 输入校验

`tools run` 会用 `inputSchema.required` 做执行前必填字段校验。TS 工具的 schema 来自 `defineTool`，C# 工具的 schema 来自 Request DTO 和 `[UnityCliRequired]`。校验失败返回 `E_SCHEMA_INVALID`，`nextActions` 指向 `tools describe <tool>`。

```powershell
# 正确：提供 inputSchema 要求的字段
unitycli tools run screenshot.from-camera --input '{"target":"Main Camera","output":"shot.png"}'

# 错误：缺少必需字段
unitycli tools run screenshot.from-camera --input '{}'
# → E_SCHEMA_INVALID, nextActions 指向 tools describe
```

## Descriptor 示例

`tools describe <name>` 的每个示例把业务 `input` 与命令层 `execution` 分开。`input` 只包含 `inputSchema` 定义的 Request 字段；dry-run 与 timeout 分别表示为 `execution.dryRun` 和 `execution.timeoutMs`，不能混入业务 JSON。实际调用仍使用固定入口 `tools run <name> --input <json>`，并把 execution 控制映射为 `--dry-run`、`--timeout-ms <n>`；PowerShell内联JSON与`tools search`的pipe-separated关键词都使用单引号，例如`tools search 'camera|screenshot'`，复杂输入优先使用 `--input-file`。

## 调用进程预算

外层命令host必须等待`unitycli`进程退出，不能在CLI仍运行时把调用报告为完成。`--timeout-ms T`分别提供最多`T`的Unity任务池准入等待，以及放行后最多`T`的业务执行；因此host等待预算至少为`2 * T + 5000`毫秒。未显式传timeout时，先从`tools describe`的example execution或Tool边界选择`T`。CLI在任一内部deadline到达后输出一个完整JSON envelope；stdout完成且已注册的local owner都settle后退出，或在唯一的1000ms宽限期到达时退出。CLI timeout只结束调用方等待，不会强停Unity中已经开始的effect；host提前退出同样不能作为effect停止证明。

## Unity 任务池准入

每个运行中的Editor或Development Player session拥有独立任务池，默认最多5个active root；达到5后关闭准入，降到4仍保持关闭，只有降到3才重新开放。CLI在发送业务请求前自动等待和轮询，调用方不得手工按100毫秒循环，也不需要管理内部准入identity。

准入等待超过`T`时返回`E_UNITY_BUSY`、`transient:true`和`retryAfterMs:100`。该错误明确证明本次业务Bridge请求未发送、effect未开始，可以稍后提交一个新root。放行后的timeout、断连或终态不明仍返回`E_EXECUTION_TIMEOUT`，表示outcome unknown，必须先观察业务状态。

统一catalog在Editor和Runtime同时在线时，会在每个参与公开结果的target session分别准入；任一在线target未获准时不返回跨池的部分catalog。exact `editor.state`、`editor.dialogs`、`editor.dialog.click`和六个域专用durable status Tool是固定例外：它们使用冻结的本地descriptor完成相同contract、availability、schema、结果与envelope校验。三个Editor观察Tool不取得业务admission；域status取得独立只读root admission后向选定实例发送一次域绑定SafeStatus读取，但不取得mutation lease；其他offline Tool、相邻名称、group或handler不会获得该例外。

## Bridge 执行约束

Bridge-backed Tool由Unity Editor主线程执行。业务请求发送前，session supervisor会在准入deadline内等待首次ready记录、compile、import、domain reload和listener generation交接；只有能证明目标Unity正在运行时才等待，证明未运行时立即返回。准入放行后只创建一次execution absolute deadline，token重取、status、rebind和terminal recovery不得刷新预算。读取可在新generation重新执行完整读取；mutation只有在`editorSessionId`未改变时才可用完全相同的request bytes续接durable terminal。

首次mutation POST后若Editor session改变，CLI禁止向新session再次POST，并返回`E_EXECUTION_TIMEOUT`、`outcome:unknown`。其他超时或终态前断连同样表示没有可信终态；调用方应先用业务读取或`editor.state`收集证据，再决定是否提交全新请求。`asset.refresh.status`、`build.player.status`、`editor.play-mode.status`、`package.install.status`、`package.remove.status`和`test.run.status`可在同一Editor session内仅凭原root `requestId`读取active phase或exact terminal；digest只由Bridge内部校验，status不获取mutation lease、不刷新deadline且不重放effect。`editor.state`使用frozen本地descriptor绕过被阻断的Live catalog和strict ordinary selector，再读取Registry/Health v3、内存主线程pulse、Bridge和Windows modal证据；它唯一可发出一次无admission的private `internal.editor.state.snapshot` observeNow POST。两个dialog recovery Tool与域status Tool保持只读观察lane，其他Tool不建立静态fallback。

一次调用的selector、Bridge、Windows观察和错误blocker补充都消费适用的同一absolute deadline；10秒诊断上限只能缩短它，剩余时间为零时不再观察，主错误保持不变。本地Tool的公开deadline先到后只返回一个固定`E_EXECUTION_TIMEOUT` envelope；迟到Promise不能改变terminal或重放effect，且首次effect barrier已关闭。每次settlement的精确清理只处理未消费lease，并与stdout共用现有最多1秒退出宽限；Unity已消费root仍由WorkItem、coordinator或recovery owner收敛。

### `editor.compile`

`editor.compile` 是唯一公开编译 Tool。一次调用在同一 root request 下等待 pre-existing import/compile、刷新 Asset Database，并由 Unity 判断导入的脚本变化是否需要编译。Tool 只观察刷新后出现的新 `CompilationPipeline` cycle，不额外调用 `RequestScriptCompilation()`；有编译时跨 domain reload 恢复并收集本次 diagnostics，没有编译时返回 `compile.status="unchanged"`、`compilationTriggered=false` 和 `noOpReason="no-script-changes"`。公开成功要求 `compile.completed=true`、`errorCount=0`、`editorState.status=ready`；warning 可作为 diagnostics 返回。compiler error 返回 `E_UNITY_COMPILE_FAILED`，不会用旧 Console 历史判断本次结果。

```powershell
unitycli tools run editor.compile --input '{"includeWarnings":true}' --timeout-ms 120000
```

accepted、queued、phase、run ID 和 listener 字段仅属于内部 coordinator，不会成为公开成功。稳定 Play Mode 调用返回 `E_UNITY_COMPILE_REQUIRES_EDIT_MODE`；dry-run 只返回 planned effects，不触发 refresh 或 compile。

### `editor.refresh`

`editor.refresh` 不递归读取 `Assets`、不计算文件byte hash，也不逐文件调用dependency hash。它在同一 execution deadline/cancel 下读取 Unity-owned global artifact dependency/processed generation，调用一次 `AssetDatabase.Refresh`，观察 import/compile/reload，并在Editor稳定后返回 before/after generation。`changed=false`、`importObserved=false` 是明确成功，不会伪造文件总数或内容指纹；Play Mode 在effect前返回 `E_TOOL_PLAYMODE_UNAVAILABLE`。

### Memory snapshots

`profiler.memory-snapshot.take` 只写 `Library/UnityCli/memory-snapshots/<name>.snap`。`path` 必须是简单 `.snap` 文件名，单文件上限4 GiB；catalog保留最新100个且总量不超过8 GiB。capture先写同根pending文件，callback成功后串行publication；timeout、cancel、reload、write fault和late callback共享唯一终态并清理orphan。`profiler.memory-snapshot.list`只分页读取该有界目录，不接受任意搜索路径。

## 实例 Tool 完整性

实例 registry 中的 Tool 在 discovery 时整体校验 descriptor 完整性和名称 ownership；malformed、重复或冲突目录返回 `E_TOOL_REGISTRY_UNHEALTHY`，不会静默形成部分 catalog。项目注册 Tool 在执行前继续经过内部完整性校验。正常 descriptor 与成功 envelope 只提供调用所需信息；校验不匹配时，结构化错误会提供恢复步骤。

## 风险元数据与可选预览

公开 Tool 与 CLI 控制面调用本身即授权。`destructiveHint`、`sideEffects` 和 `supportsDryRun` 只描述效果，不引入额外确认、预定义按钮列表或意图复核。

```powershell
# 可选：支持时预览 plannedEffects，不执行 mutation
unitycli tools run asset.delete --input '{"path":"Assets/Temp.mat"}' --dry-run

# 公开调用即授权并执行
unitycli tools run asset.delete --input '{"path":"Assets/Temp.mat"}'
```

不支持 `--dry-run` 的 Tool 使用该选项时返回 `E_DRY_RUN_UNSUPPORTED`；直接调用不受影响。

## 大响应治理

可能返回大量数据的工具默认应先返回小摘要，AI 调用时也应优先使用过滤、分页和精确读取，避免一次性展开场景、Prefab、序列化对象、Shader 源码或图片 base64。

通用规则：

- discovery 的 `tools group list`、`tools search` 和省略name的`command`列表默认每页20项、最大100项；继续读取时将`pageInfo.nextCursor`原值传回CLI `--cursor`。
- `console.read`、`scene.hierarchy`、`game-object.find`、`profiler.hierarchy`、`profiler.frame-debugger.events`、`profiler.memory-snapshot.list`、`runtime-reflection.type.schema` 和 `runtime-reflection.method.find` 使用 opaque string `nextCursor`。游标绑定原始位置、selector 与 Unity/assembly generation；Frame Debugger 游标还绑定 Editor listener、CLI enable/disable generation、事件数与捕获首尾签名。游标必须原样传回，改变 selector、generation 或捕获后返回 `E_CONTINUATION_STALE`。`truncated=true` 表示还有原始位置；不能把当前页数量当作总数。
- 深度序列化工具（`serialized-object.read`、`asset.get-data`、`component.get`）优先传 `paths` 或 `viewQuery`。`maxDepth` 为 `0..16`，`maxArrayItems` 为 `1..1000`，`paths` 最多128条并保持输入顺序；cycle/depth 在展开前裁决，所有路径共享同一预算，非 `IList` enumerable 不会预 materialize。`truncated` 与 `nextPath` 表示被截断子树。
- 上述 Unity 主线程读取默认共用 `10000` scanned records、`4096` nodes、`2048` getters、`4096` reflection calls、`250ms` monotonic main-thread work 和 `1 MiB` serialized UTF-8。`budget` 可收紧或提高到公开上限 `100000`、`20000`、`10000`、`20000`、`2000ms`、`8 MiB`；零、负数或超限值在读取/effect前返回 `E_SCHEMA_INVALID` 或 `E_REQUEST_BUDGET_INVALID`。
- 文本源码工具（如 `shader.read`）使用 `offset` 和 `maxBytes` 分段读取。输出 `truncated=true` 时，下一次把 `nextOffset` 作为新的 `offset`。
- 截图/相机捕获的单图边长最多8192且最多16777216像素，capture count最多36。完整计划必须在首个effect前满足默认33554432 total pixels、128 MiB peak temporary memory、64 MiB artifact bytes；公开上限为67108864、256 MiB、256 MiB。`orbit`逐图释放，`surround`逐图写入contact sheet后释放，不同时保留所有tile。内联图片仍限512 KiB。

示例：

```powershell
# 先读场景小页，再用 nextCursor 续页
unitycli tools run scene.hierarchy --input '{"pageSize":50,"query":"Camera"}'

# 深读时只读需要的字段，并限制数组项
unitycli tools run component.get --input '{"path":"Player","component":"Inventory","paths":["items"],"maxArrayItems":20}'

# Shader 源码分段读取
unitycli tools run shader.read --input '{"path":"Assets/Shaders/Probe.shader","maxBytes":65536}'
```

## 验证策略

执行后应验证结果：

| 工具类型 | 验证方式 |
|----------|---------|
| 只读工具 | 检查输出数据是否符合预期 |
| 修改工具 | 重新调用只读工具确认变更 |
| Live Editor Tool | 读取能力自己的终态业务数据，必要时用读取型Tool验证 |
| 诊断控制面 | 检查 `doctor` 的独立 Registry/Health、pulse、business、catalog、compile 与 Extension checks |

## 错误处理

| 错误码 | 原因 | 恢复方式 |
|--------|------|---------|
| `E_TOOL_NOT_FOUND_FOR_TARGET` | 名称不属于当前实例完整 Tool 目录 | 运行 `tools search <k1|k2|...>` 发现相关 Tool，再用 `tools describe` 检查 requiresPackages 和 available |
| `E_RUNTIME_NOT_READY` | Runtime 工具但非 Play Mode | 先执行 `tools run editor.play-mode.start`，transient 可重试 |
| `E_SCHEMA_INVALID` | 输入缺少 inputSchema.required 字段 | 查看 `tools describe` 的 inputSchema |
| `E_DRY_RUN_UNSUPPORTED` | 工具不支持 dry-run | 去除可选 `--dry-run` 后直接调用 |
| `E_INSTANCE_CONFLICT` | 至少两个verified live process claim属于同一canonical project | 关闭重复Editor，只保留目标session |
| `E_INSTANCE_UNVERIFIABLE` | PID/start、project claim或状态文件证据无法证明 | 重新观察；不得把未知mutation自动重投 |
| `E_INSTANCE_NOT_RUNNING` | 选定项目已证明没有运行中Editor | 检查`instances list`并打开该项目 |
| `E_UNITY_UNRESPONSIVE` | transport存在，但近期Unity主线程pulse或Bridge响应不可证明 | 读取`editor.state`和`editor.dialogs` |
| `E_UNITY_BLOCKED` | effect前已确认Unity/UI blocker | 读取同一响应的`details.blockers`并处理对应窗口 |
| `E_EXECUTION_TIMEOUT` | 调用在终态前超时、断连或触发cross-session mutation fence | 没有可信终态；先观察业务状态，再作全新提交决策 |
| `E_UNITY_TRANSITION_TIMEOUT` | lifecycle transition 未在 absolute deadline 内稳定 | 检查 `editor.state` 的 progress/blockers |

弹窗 blocker 在原错误的 `details.blockers` 中结构化返回。其 DTO 包含 `type`、`dialogId`、`title`、`message`、`buttons`、`requiresUserAction`、`source` 和 `confidence`。使用只读 `tools run editor.dialogs` 获取当前 dialogId、`observedAt`、modal 状态和按钮；无弹窗时返回空数组，Windows 读取失败返回 `E_DIALOG_OBSERVATION_FAILED`。再以 `tools run editor.dialog.click` 精确传入 dialogId 与规范化 button label。该 Tool 会重新验证选中 Unity 的 PID/processStartedAt、dialogId 和唯一 enabled button，并证明原 dialog 已消失或不再 modal；不匹配分别返回 stale、not-found 或 ambiguous 错误，无法证明点击结果返回 `E_DIALOG_CLICK_UNPROVEN`。任意当前可见 modal 的任意 enabled button 都可点击，不设预定义按钮列表、确认或人工处理门。

## Object 与 Component 修改

`serialized-object.patch` 和 `component.modify` 只接受非空 `pathPatches`。每一项必须是可由 Unity `SerializedObject.FindProperty` 定位并 fresh readback 的 direct `replace`；不提供 merge、add、remove、reflection member path 或 diff 旁路。支持的值类型以当前 `inputSchema` 和错误 `nextActions` 为准。

`path` 使用 Unity serialized property path，例如 `m_Name`、`m_LocalPosition.x` 或数组的 `Array.data[0]`。它不是 `/` 分隔的 C# member path。`asset.write-text` 是独立的文本资产覆写 Tool，不使用 ObjectPatch。

```powershell
# 先读后改
unitycli tools run component.get --input '{"path":"Player","component":"Rigidbody"}'
unitycli tools run component.modify --input '{"path":"Player","component":"Rigidbody","pathPatches":[{"path":"m_Mass","op":"replace","value":10}]}'

unitycli tools run serialized-object.patch --input '{"path":"Player","pathPatches":[{"path":"m_Name","op":"replace","value":"PlayerModified"}]}'
```

## runtime-reflection.method.invoke

`runtime-reflection.method.invoke` 不做调用范围限制（public/private/static/instance、任意 namespace/assembly、Editor/Runtime），且 `destructiveHint: true`。先用 `runtime-reflection.method.find` 获取签名；存在同名 overload 时，将选中签名的 `parameterType` 数组作为 `parameterTypes`。公开调用即授权：

```powershell
unitycli tools run runtime-reflection.method.invoke --input '{
  "typeName":"UnityEngine.Transform",
  "methodName":"Translate",
  "parameterTypes":["UnityEngine.Vector3","UnityEngine.Space"],
  "args":[{"x":1,"y":0,"z":0},0],
  "instanceRef":{"instanceId":123}
}'
```

`--dry-run` 返回 `plannedEffects` 且不执行方法；它是可选预览。无法唯一选择 overload 时返回 `E_METHOD_AMBIGUOUS` 和候选签名。调用后只返回方法调用的业务结果和已声明 completion readback；内部审计不进入公开成功响应。

## asset.get-data 复用 serializer

`asset.get-data` 是 asset-friendly wrapper，底层复用 `serialized-object.read` 的 `ObjectSerializer`，不重复实现序列化。`serialized-object.read` 是任意 `UnityEngine.Object` 深序列化主入口，`asset.get-data` 提供按 assetPath 解析的便捷入口。
