# 工具执行（Tool Execution）

`tools run`是统一Tool dispatcher的结构化JSON入口。package内置、Extension、项目注册、Editor、Runtime和package-local Tool也可通过[`command`](command-execution.md)按同一live schema调用；二者不建立第二套catalog或执行路径。

## 基本用法

```powershell
# 通过 --input 传参（JSON 字符串）
unity_cli tools run camera.list --input '{}'

# 通过 --input-file 传参（JSON 文件）
unity_cli tools run camera.screenshot --input-file params.json

# 通过 --stdin 传参
echo '{"target":"Main Camera"}' | unity_cli tools run camera.screenshot --stdin
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
unity_cli tools run camera.screenshot --input '{"target":"Main Camera","output":"shot.png"}'

# 错误：缺少必需字段
unity_cli tools run camera.screenshot --input '{}'
# → E_SCHEMA_INVALID, nextActions 指向 tools describe
```

## Descriptor 示例

`tools describe <name>` 的每个示例把业务 `input` 与命令层 `execution` 分开。`input` 必须只包含 `inputSchema` 定义的 Request 字段；dry-run 与 timeout 会在示例 `argv` 中显示为 `--dry-run` 和 `--timeout-ms <n>`，不能放入 JSON input。`examples[].argv` 是参数 token 数组，不是可直接粘贴的 shell 字符串；PowerShell 内联 JSON 必须用单引号包裹，复杂输入应改用 `--input-file`。`tools describe <name>` 同样提供 `tools run <name> --input <json>` 示例。

## Bridge 执行约束

Bridge-backed Tool 由 Unity Editor 主线程执行。session supervisor 在调用的单一 absolute deadline 内透明吸收 compile、import、domain reload 和 listener restart，并在恢复后的 validated generation 上继续同一逻辑请求。调用方不需要轮询 listener 或手动重试 transport。

直接 Tool 只返回已证明的 terminal business success 或 terminal structured failure。同一次调用可在 session recovery 中续接完全相同的请求；调用返回后 CLI 不会自行启动新调用，也不提供请求级重放门。超时或终态前断连统一返回 `E_EXECUTION_TIMEOUT`，只表示没有可信终态；调用方可自行决定是否观察业务状态、提交新调用、重复调用或继续 mutation。要即时观察进度、freshness 和 blocker，可使用 `tools run editor.state --input '{}'`；它不会等待 ready 或触发 Unity 副作用。

### `editor.compile`

`editor.compile` 是唯一公开编译 Tool。一次调用在同一 root request 下等待 pre-existing import/compile、触发 owned compilation cycle、跨 domain reload 恢复、收集本次 `CompilationPipeline` diagnostics，并等待 Editor 与 listener 重新稳定。公开成功要求 `compile.completed=true`、`errorCount=0`、`editorState.status=ready`；warning 可作为 diagnostics 返回。compiler error 返回 `E_UNITY_COMPILE_FAILED`，不会用旧 Console 历史判断本次结果。

```powershell
unity_cli tools run editor.compile --input '{"refresh":true,"includeWarnings":true}' --timeout-ms 120000
```

accepted、queued、phase、run ID 和 listener 字段仅属于内部 coordinator，不会成为公开成功。稳定 Play Mode 调用返回 `E_UNITY_COMPILE_REQUIRES_EDIT_MODE`；dry-run 只返回 planned effects，不触发 refresh 或 compile。

## 实例 Tool 完整性

实例 registry 中的 Tool 在 discovery 时整体校验 descriptor 完整性和名称 ownership；malformed、重复或冲突目录返回 `E_TOOL_REGISTRY_UNHEALTHY`，不会静默形成部分 catalog。项目注册 Tool 在执行前继续经过内部完整性校验。正常 descriptor 与成功 envelope 只提供调用所需信息；校验不匹配时，结构化错误会提供恢复步骤。

## 风险元数据与可选预览

公开 Tool 与 CLI 控制面调用本身即授权。`destructiveHint`、`sideEffects` 和 `supportsDryRun` 只描述效果，不引入额外确认、预定义按钮列表或意图复核。

```powershell
# 可选：支持时预览 plannedEffects，不执行 mutation
unity_cli tools run asset.delete --input '{"path":"Assets/Temp.mat"}' --dry-run

# 公开调用即授权并执行
unity_cli tools run asset.delete --input '{"path":"Assets/Temp.mat"}'
```

不支持 `--dry-run` 的 Tool 使用该选项时返回 `E_DRY_RUN_UNSUPPORTED`；直接调用不受影响。

## 大响应治理

可能返回大量数据的工具默认应先返回小摘要，AI 调用时也应优先使用过滤、分页和精确读取，避免一次性展开场景、Prefab、序列化对象、Shader 源码或图片 base64。

通用规则：

- 列表/层级工具优先传 `pageSize`、`cursor`、`parent`、`query`、`componentType` 或 `className`。继续读取时将输出里的 `pageInfo.nextCursor` 原值传回 `cursor`；两者保持相同 JSON 类型。`hasMore`、`truncated` 或 `childrenTruncated` 表示还有更深或更多内容。
- 深度序列化工具（`object.get-data`、`asset.get-data`、`component.get`）优先传 `paths` 或 `viewQuery`，并保持 `maxDepth` 较低。数组、列表和字典受 `maxArrayItems` 限制；输出里的 `totalCount`、`returnedCount`、`truncated`、`nextPath` 用于判断是否需要继续按路径读取。CLR 的 `NaN`、正无穷和负无穷无法作为 JSON number，会在 AnyJson 值节点中分别表示为字符串 `"NaN"`、`"Infinity"`、`"-Infinity"`。
- 文本源码工具（如 `shader.read`）使用 `offset` 和 `maxBytes` 分段读取。输出 `truncated=true` 时，下一次把 `nextOffset` 作为新的 `offset`。
- 截图和相机工具默认返回 artifact 路径。只有确实需要内联图片时才传 `inlineBase64:true`；图片超过 Bridge 内联上限时会省略 `imageBase64` 并返回 warning，继续使用 artifact 路径。

示例：

```powershell
# 先读场景小页，再用 nextCursor 续页
unity_cli tools run scene.hierarchy --input '{"pageSize":50,"query":"Camera"}'

# 深读时只读需要的字段，并限制数组项
unity_cli tools run component.get --input '{"path":"Player","component":"Inventory","paths":["items"],"maxArrayItems":20}'

# Shader 源码分段读取
unity_cli tools run shader.read --input '{"path":"Assets/Shaders/Probe.shader","maxBytes":65536}'
```

## 验证策略

执行后应验证结果：

| 工具类型 | 验证方式 |
|----------|---------|
| 只读工具 | 检查输出数据是否符合预期 |
| 修改工具 | 重新调用只读工具确认变更 |
| Live Editor Tool | 读取能力自己的终态业务数据，必要时用读取型Tool验证 |
| 诊断工具 | 检查 doctor 或 preflight |

## 错误处理

| 错误码 | 原因 | 恢复方式 |
|--------|------|---------|
| `E_TOOL_NOT_FOUND_FOR_TARGET` | 工具在目标不可用 | 检查 requiresPackages 和 available |
| `E_RUNTIME_NOT_READY` | Runtime 工具但非 Play Mode | 先执行 `tools run editor.play`，transient 可重试 |
| `E_SCHEMA_INVALID` | 输入缺少 inputSchema.required 字段 | 查看 `tools describe` 的 inputSchema |
| `E_DRY_RUN_UNSUPPORTED` | 工具不支持 dry-run | 去除可选 `--dry-run` 后直接调用 |
| `E_INSTANCE_CONFLICT` | 同一 canonical project 存在多个 Unity Editor process/session claim | 关闭重复 Editor，只保留目标 session |
| `E_INSTANCE_NOT_RUNNING` | 选定项目没有可验证的运行中 Editor | 检查 `instances list` 并打开该项目 |
| `E_UNITY_BLOCKED` | effect 前已确认 Unity/UI blocker | 读取同一响应的 `details.blockers` 并处理对应窗口 |
| `E_EXECUTION_TIMEOUT` | 调用在终态前超时或断连 | 没有可信终态；是否观察、重试、重复调用或继续 mutation 由调用方决定 |
| `E_UNITY_TRANSITION_TIMEOUT` | lifecycle transition 未在 absolute deadline 内稳定 | 检查 `editor.state` 的 progress/blockers |

弹窗 blocker 在原错误的 `details.blockers` 中结构化返回。其 DTO 包含 `type`、`dialogId`、`title`、`message`、`buttons`、`requiresUserAction`、`source` 和 `confidence`。使用只读 `tools run editor.dialogs` 获取当前 dialogId、`observedAt`、modal 状态和按钮；无弹窗时返回空数组，Windows 读取失败返回 `E_DIALOG_OBSERVATION_FAILED`。再以 `tools run editor.dialog.click` 精确传入 dialogId 与规范化 button label。该 Tool 会重新验证选中 Unity 的 PID/processStartedAt、dialogId 和唯一 enabled button，并证明原 dialog 已消失或不再 modal；不匹配分别返回 stale、not-found 或 ambiguous 错误，无法证明点击结果返回 `E_DIALOG_CLICK_UNPROVEN`。任意当前可见 modal 的任意 enabled button 都可点击，不设预定义按钮列表、确认或人工处理门。

## Object 与 Component 修改

`object.modify` 和 `component.modify` 只接受非空 `pathPatches`。每一项必须是可由 Unity `SerializedObject.FindProperty` 定位并 fresh readback 的 direct `replace`；不提供 merge、add、remove、reflection member path 或 diff 旁路。支持的值类型以当前 `inputSchema` 和错误 `nextActions` 为准。

`path` 使用 Unity serialized property path，例如 `m_Name`、`m_LocalPosition.x` 或数组的 `Array.data[0]`。它不是 `/` 分隔的 C# member path。`asset.modify` 是独立的文本资产覆写 Tool，不使用 ObjectPatch。

```powershell
# 先读后改
unity_cli tools run component.get --input '{"path":"Player","component":"Rigidbody"}'
unity_cli tools run component.modify --input '{"path":"Player","component":"Rigidbody","pathPatches":[{"path":"m_Mass","op":"replace","value":10}]}'

unity_cli tools run object.modify --input '{"path":"Player","pathPatches":[{"path":"m_Name","op":"replace","value":"PlayerModified"}]}'
```

## reflection.method.call

`reflection.method.call` 不做调用范围限制（public/private/static/instance、任意 namespace/assembly、Editor/Runtime），且 `destructiveHint: true`。先用 `reflection.method.find` 获取签名；存在同名 overload 时，将选中签名的 `parameterType` 数组作为 `parameterTypes`。公开调用即授权：

```powershell
unity_cli tools run reflection.method.call --input '{
  "typeName":"UnityEngine.Transform",
  "methodName":"Translate",
  "parameterTypes":["UnityEngine.Vector3","UnityEngine.Space"],
  "args":[{"x":1,"y":0,"z":0},0],
  "instanceRef":{"instanceId":123}
}'
```

`--dry-run` 返回 `plannedEffects` 且不执行方法；它是可选预览。无法唯一选择 overload 时返回 `E_METHOD_AMBIGUOUS` 和候选签名。调用后只返回方法调用的业务结果和已声明 completion readback；内部审计不进入公开成功响应。

## asset.get-data 复用 serializer

`asset.get-data` 是 asset-friendly wrapper，底层复用 `object.get-data` 的 `ObjectSerializer`，不重复实现序列化。`object.get-data` 是任意 `UnityEngine.Object` 深序列化主入口，`asset.get-data` 提供按 assetPath 解析的便捷入口。
