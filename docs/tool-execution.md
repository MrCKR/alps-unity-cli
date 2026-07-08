# 工具执行（Tool Execution）

`tools run` 是统一执行入口，所有工具（内置和项目自定义）都通过此命令执行。

## 基本用法

```powershell
# 通过 --input 传参（JSON 字符串）
unity_cli tools run camera.list --input '{}'

# 通过 --input-file 传参（JSON 文件）
unity_cli tools run camera.screenshot --input-file params.json

# 通过 --stdin 传参
echo '{"target":"Main Camera"}' | unity_cli tools run camera.screenshot --stdin
```

## 执行通道

`tools run` 根据 descriptor 的 `handler` 和 `target` 自动路由。TS 工具由 `defineTool.execute` 返回业务数据；C# 工具由 `IUnityCliTool<TReq,TRes>.Execute(Request, ctx)` 返回 Result DTO；CLI/Bridge 负责把结果包装成统一 JSON envelope。

| Handler | 通道 | 说明 |
|---------|------|------|
| `bridge` | `POST /bridge/execute` | 实时编辑器命令 |
| `bridge-runtime` | Bridge + Play Mode guard | 运行时工具，需 Play Mode |
| `hybrid` | Bridge 或 job | 支持 `--job` 标志切换为 job 模式 |
| `job` | Unity batchmode | 测试、构建等长时间操作 |
| `offline` | 本地 CLI | 包管理、文档、反射等 |

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

## Bridge 执行约束

Bridge-backed tools 由 Unity Editor 侧队列调度到 Unity 主线程执行。Unity 正在 compile/import/build、切换 Play Mode 或 domain reload 时，工具可能返回 busy/transient 错误；先读 `tools run editor.state --input '{"waitReady":true}'`，等 `busy.isBusy=false` 后再重试。

Bridge listener 会在 domain reload 后重新注册并恢复 heartbeat。`editor.state` 输出里的 `bridge` build stamp 与 heartbeat 是 AI 判断自动重连是否恢复的公开信号；不要直接读内部注册文件或手动 ping endpoint 替代这个检查。

## Fingerprint 保护

项目自定义工具有 handler 代码 + descriptor 的 SHA256 指纹。执行前会校验指纹：

- 指纹匹配 → 正常执行
- 指纹不匹配 → 默认返回 `E_TOOL_FINGERPRINT_MISMATCH`（阻止执行）
- `--fingerprint-mode warn` → 警告但继续执行
- `UNITY_CLI_PROJECT_TOOL_FINGERPRINT_MODE=warn` → 环境变量覆盖

```powershell
# 默认 block 模式
unity_cli tools run my.custom-tool --input '{}'

# 警告模式（仅用于审计后的有意修改）
unity_cli tools run my.custom-tool --input '{}' --fingerprint-mode warn
```

## 破坏性工具确认

`destructiveHint: true` 的工具需要额外确认：

```powershell
# 1. 先 dry-run 预览（如工具支持）
unity_cli tools run asset.delete --input '{"path":"Assets/Temp.mat"}' --dry-run

# 2. 查看 plannedEffects
# 输出中会包含 plannedEffects 字段，说明将要执行的操作

# 3. 确认执行
unity_cli tools run asset.delete --input '{"path":"Assets/Temp.mat"}' --yes
```

不支持 `--dry-run` 的破坏性工具，使用 `--dry-run` 时返回 `E_DRY_RUN_UNSUPPORTED`。

## Batch 输入

`batch run` 的 step 可用 `input` 字段传结构化 JSON，CLI 会自动追加为 `--input <json>`。这比在 `args` 里手写 `--input` 字符串更适合 Windows 路径和嵌套对象；如果 step 的 `args` 已经包含 `--input`、`--input-file` 或 `--stdin`，显式输入来源优先生效。对 `tools run` step，`job:true` 会追加 `--job`，用于 descriptor 标记 `bridge-job` 的工具。

```json
{
  "steps": [
    {
      "id": "capture",
      "command": ["tools", "run", "profiler.capture"],
      "job": true,
      "input": { "path": "Artifacts/profile.json", "format": "json", "overwrite": true }
    }
  ]
}
```

## Job 模式

`handler: "hybrid"` 且声明 job 支持的工具可用 `--job` 标志切换为 batchmode/job 执行。`--job` 不需要 live Bridge endpoint，但需要可用 Unity 可执行文件和未被 Editor 打开的项目；项目已打开时返回 `UNITY_BUSY`，不会先部署 Bridge 包。未声明 job 支持的 bridge/hybrid 工具会返回 `E_TOOL_JOB_UNSUPPORTED`。

```powershell
# 实时 Bridge 执行（默认）
unity_cli tools run profiler.capture --input '{"path":"profile.json"}'

# Job 模式
unity_cli tools run profiler.capture --input '{"path":"profile.json"}' --job --project . --unity "C:\Program Files\Unity\Unity-2022.3.61f1\Editor\Unity.exe"

# 等待并取回 Bridge envelope 摘要
unity_cli tools run profiler.capture --input '{"path":"profile.json"}' --job --wait --project . --unity "C:\Program Files\Unity\Unity-2022.3.61f1\Editor\Unity.exe"

# 查看任务状态
unity_cli jobs status <jobId>
```

## 大响应治理

可能返回大量数据的工具默认应先返回小摘要，AI 调用时也应优先使用过滤、分页和精确读取，避免一次性展开场景、Prefab、序列化对象、Shader 源码或图片 base64。

通用规则：

- 列表/层级工具优先传 `pageSize`、`cursor`、`parent`、`query`、`componentType` 或 `className`。继续读取时使用输出里的 `pageInfo.nextCursor`；`hasMore`、`truncated` 或 `childrenTruncated` 表示还有更深或更多内容。
- 深度序列化工具（`object.get-data`、`asset.get-data`、`component.get`）优先传 `paths` 或 `viewQuery`，并保持 `maxDepth` 较低。数组、列表和字典受 `maxArrayItems` 限制；输出里的 `totalCount`、`returnedCount`、`truncated`、`nextPath` 用于判断是否需要继续按路径读取。
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
| Job 工具 | 检查 job status + logs |
| 诊断工具 | 检查 doctor 或 preflight |

## 错误处理

| 错误码 | 原因 | 恢复方式 |
|--------|------|---------|
| `E_TOOL_NOT_FOUND_FOR_TARGET` | 工具在目标不可用 | 检查 requiresPackages 和 available |
| `E_RUNTIME_NOT_READY` | Runtime 工具但非 Play Mode | 先执行 `tools run editor.play`，transient 可重试 |
| `E_SCHEMA_INVALID` | 输入缺少 inputSchema.required 字段 | 查看 `tools describe` 的 inputSchema |
| `E_TOOL_FINGERPRINT_MISMATCH` | handler 代码变更 | 审计后 `--fingerprint-mode warn` |
| `E_DRY_RUN_UNSUPPORTED` | 工具不支持 dry-run | 直接使用 `--yes` 确认 |
| `E_DESTRUCTIVE_CONFIRMATION_REQUIRED` | 破坏性工具未确认 | 添加 `--yes` 标志 |
| `E_TARGET_AMBIGUOUS` | 多个实例可用 | 显式指定 `--target` |
| `E_TARGET_NOT_FOUND` | 无可用实例 | 检查 `instances list` |

## 深度修改与 patch 语义

`object.modify` / `component.modify` / `asset.modify` 共享同一套 patch 引擎（底层 `ObjectPatch`），支持三个修改面，执行顺序对齐 IvanMurzak ReflectorNet：`jsonPatch` → `pathPatches` → `objectDiff`/`componentDiff`。

- `pathPatches`：`[{"path":"...","value":...}]`，路径语法 `fieldName`、`nested/field`、`arrayField/[i]`、`dictField/[key]`，同时兼容 `.` 分隔。
- `jsonPatch`：JSON Merge Patch (RFC 7396) 对象。
- `objectDiff`/`componentDiff`：SerializedMember 风格的完整 diff（等价 jsonPatch merge 语义）。

```powershell
# pathPatches 示例
unity_cli tools run component.modify --input '{"path":"Player","component":"Transform","pathPatches":[{"path":"localPosition","value":{"x":1,"y":2,"z":3}}]}'

# jsonPatch 示例
unity_cli tools run object.modify --input '{"instanceId":123,"jsonPatch":{"m_Color":{"r":1,"g":0,"b":0}}}'

# 先读后改（推荐）
unity_cli tools run component.get --input '{"path":"Player","component":"Rigidbody"}'
unity_cli tools run component.modify --input '{"path":"Player","component":"Rigidbody","jsonPatch":{"mass":10}}'
```

## reflection.method.call 安全确认流

`reflection.method.call` 不做调用范围限制（public/private/static/instance、任意 namespace/assembly、Editor/Runtime），分类为 `riskLevel: critical`、`destructiveHint: true`。必须传 `security` 对象：

```powershell
unity_cli tools run reflection.method.call --input '{
  "typeName":"UnityEngine.Transform",
  "methodName":"Translate",
  "args":[1,0,0],
  "instanceRef":{"instanceId":123},
  "security":{"confirmed":true,"destructiveFingerprint":"<sha256>"}
}'
```

未提供确认时返回 preview，包含 `fingerprint` 与 `plannedEffects`。确认后返回 target method preview、参数、结果序列化、异常（如有）与结构化 audit。

## asset.get-data 复用 serializer

`asset.get-data` 是 asset-friendly wrapper，底层复用 `object.get-data` 的 `ObjectSerializer`，不重复实现序列化。`object.get-data` 是任意 `UnityEngine.Object` 深序列化主入口，`asset.get-data` 提供按 assetPath 解析的便捷入口。
