# 错误码（Error Codes）

alps-unity-cli 使用结构化错误码，包含机器可读的 `code`、`transient` 标记、`retryAfterMs` 和 `nextActions` 自动恢复指引。

## 错误格式

```json
{
  "ok": false,
  "requestId": "req-xxx",
  "error": {
    "code": "E_INSTANCE_CONFLICT",
    "message": "Multiple Unity Editor sessions claim the selected project.",
    "details": { "instanceId": "unity-...", "status": "conflict" },
    "nextActions": [
      "Close every duplicate Editor process except the intended session.",
      "Run unity_cli instances list to verify one session remains."
    ],
    "transient": false,
    "retryAfterMs": 0
  }
}
```

`error` 只允许上述协议字段；`details` 仅承载业务诊断，不公开内部日志、审计、endpoint、port、listener/process identity、native window handle 或 Windows command line。blocker 使用固定公开 DTO。

普通 Tool 或 Context 在业务 effect 前确认阻断时返回 `E_UNITY_BLOCKED`，同一响应的 `details.blockers` 包含已清洗 blocker。session recovery 可在同一次调用内续接完全相同的请求；调用返回后 CLI 不会自行启动新调用，也不提供请求级重放门。超时或断连后统一返回 `E_EXECUTION_TIMEOUT`，只表示没有可信终态；是否观察状态、重试、重复调用或继续 mutation 由调用方决定。

## 错误分类

### Project Identity / Instance 错误

| 错误码 | 含义 | 恢复 |
|--------|------|------|
| `E_INSTANCE_REQUIRED` | 没有显式 selector，CWD 也不在 Unity 项目中 | 传完整 `--instance <id|projectPath|alias>` 或从项目目录调用 |
| `E_INSTANCE_NOT_FOUND` | selector 无法解析到 canonical project identity | 运行 `instances list` 或核对项目路径/alias |
| `E_INSTANCE_NOT_RUNNING` | 项目 identity 已确定，但没有可验证的运行中 Editor | 打开该项目并等待 Bridge 注册 |
| `E_INSTANCE_CONFLICT` | 同一项目被多个 Unity Editor process/session 声明，或 process claim 无法唯一证明 | 关闭重复 Editor，只保留目标 session |
| `E_INSTANCE_IDENTITY_MISMATCH` | registry、health 或请求中的 canonical identity 不一致 | 停止调用并检查项目移动、registry claim 或 Bridge 安装 |
| `E_INSTANCE_PROJECT_MISMATCH` | 显式项目约束与 selector 指向不同 identity | 统一 `--project` 和 `--instance` |
| `E_INSTANCE_PATH_UNAVAILABLE` | 无法取得可证明的物理 canonical path | 修复不可访问路径、broken link 或 reparse 配置 |
| `E_INSTANCE_REMOTE_PATH_UNSUPPORTED` | 项目位于不支持的远程路径 | 使用本机可验证项目路径 |

### Runtime / Play Mode 错误

| 错误码 | 含义 | 恢复 |
|--------|------|------|
| `E_RUNTIME_NOT_READY` | Runtime 工具需 Play Mode | 先 `tools run editor.play`，transient 可重试 |
| `E_RUNTIME_SESSION_STALE` | 运行时会话过期 | 重新进入 Play Mode |
| `E_RUNTIME_TIMEOUT` | 运行时操作超时 | 增加 `--timeout-ms` 或重试 |

### Tool / Discovery 错误

| 错误码 | 含义 | 恢复 |
|--------|------|------|
| `E_TOOL_NOT_FOUND_FOR_TARGET` | 名称不属于当前实例完整 Tool 目录 | 重新运行 `tools group` / `tools search` |
| `E_CONTEXT_NOT_FOUND` | 名称不属于当前实例完整 Context 目录 | 重新运行 `context list` / `context search` |
| `E_TOOL_REGISTRY_UNHEALTHY` | 当前实例 Tool catalog 含 malformed descriptor、重复名称或 ownership 冲突 | 修复实例 registry 后重新 discovery |
| `E_CONTEXT_REGISTRY_UNHEALTHY` | 当前实例 Context catalog 含 malformed descriptor、重复名称或 ownership 冲突 | 修复实例 registry 后重新 discovery |
| `E_SCHEMA_INVALID` | 输入不符合 schema | 查看对应 `tools describe` / `context describe` 的 inputSchema |

### 可选预览错误

| 错误码 | 含义 | 恢复 |
|--------|------|------|
| `E_DRY_RUN_UNSUPPORTED` | 工具不支持 dry-run | 去除可选 `--dry-run` 后直接调用 |

### Scene / Asset / Prefab 错误

| 错误码 | 含义 |
|--------|------|
| `E_ASSET_PATH_REQUIRED` | 资产路径缺失 |
| `E_ASSET_NOT_FOUND` | 资产未找到 |
| `E_ASSET_OUTPUT_EXISTS` | 输出已存在（用 `--overwrite`） |
| `E_PREFAB_NOT_FOUND` | Prefab 未找到 |
| `E_PREFAB_STAGE_NOT_OPEN` | Prefab Stage 未打开 |
| `E_CAMERA_NOT_FOUND` | 相机未找到 |
| `E_SCENE_NOT_FOUND` | 场景 path 或唯一场景资产名未找到 |
| `E_SCENE_AMBIGUOUS` | 场景资产名匹配多个 `.unity` 文件；改传精确 `Assets/*.unity` path |
| `E_SCENE_VIEW_NOT_AVAILABLE` | Scene 视图不可用 |
| `E_GAMEOBJECT_DELETE_FAILED` | GameObject 在同步删除后仍存在；按 `nextActions` 检查目标与 Unity Console |

### Package 错误

| 错误码 | 含义 |
|--------|------|
| `E_PACKAGE_NOT_FOUND` | 包未安装 |
| `E_VFX_PACKAGE_MISSING` | VFX Graph 包未安装 |
| `E_CINEMACHINE_PRESET_INVALID` | `cinemachine.create` 的 preset 不在 descriptor 声明的集合中 |

### Reflection 错误

| 错误码 | 含义 | 恢复 |
|--------|------|------|
| `E_METHOD_NOT_FOUND` | 方法名或显式参数签名不存在 | 运行 `reflection.method.find` 并核对 `parameterType` |
| `E_METHOD_AMBIGUOUS` | 多个 overload 与参数数量匹配 | 将选中签名的 `parameterType` 数组作为 `parameterTypes` 重试 |
| `E_TYPE_NOT_FOUND` | 目标类型或参数类型不存在 | 使用完整 C# type name |

### 执行错误

| 错误码 | 含义 |
|--------|------|
| `E_UNITY_BLOCKED` | effect 前已证明 Unity/UI blocker；原错误的 `details.blockers` 含结构化弹窗摘要 |
| `E_UNITY_TRANSITION_TIMEOUT` | 可恢复 lifecycle transition 未在 absolute deadline 内稳定 |
| `E_CONTEXT_READ_TIMEOUT` | Context 未在同一 validated generation 上完成读取 |
| `E_EXECUTION_TIMEOUT` | Tool 未在调用 deadline 内返回终态，或连接在终态前断开；调用返回后 CLI 不自行启动新调用，是否观察、重试、重复调用或继续 mutation 由调用方决定 |
| `E_REQUEST_ID_REUSED` | 同一 root request ID 被不同 exact request bytes 使用 |
| `E_TOOL_COMPLETION_PROOF_INVALID` | Tool 返回值不满足其 completion contract 或 proof 校验 |
| `E_UNITY_COMPILE_REQUIRES_EDIT_MODE` | `editor.compile` 在稳定 Play Mode 或 queued-before-effect PlayMode 中被拒绝 |
| `E_UNITY_COMPILE_FAILED` | owned compilation cycle 完成且包含 compiler error；details 带本次 diagnostics |
| `E_CODE_COMPILE_FAILED` | Runtime Code 编译失败 |
| `E_CODE_RUNTIME_EXCEPTION` | Runtime Code 执行异常 |
| `E_IO_ERROR` | IO 错误 |

## AI Agent 错误处理流程

1. 读取 `error.code` 确定错误类别
2. 将 `transient`、`retryAfterMs` 和 `nextActions` 作为恢复建议，不把它们当成权限门
3. `E_EXECUTION_TIMEOUT` 只说明没有可信终态；是否观察状态、重试或继续 mutation 由调用方决定
4. 可恢复 compile/import/reload/listener transition 已由 supervisor 内部处理

### nextActions 规则

- Instance 错误的 `nextActions` 必须指向 `instances list`、显式 `--instance`、项目路径或关闭重复 Editor
- Runtime 错误的 `nextActions` 必须指向 `tools run editor.play` 或 Play Mode 相关操作
- Discovery 错误的 `nextActions` 必须指向 `tools group`、`tools group list`、`tools search` 或 `tools describe`
- 弹窗 blocker 的 `nextActions` 必须指向先 `context read editor.dialogs`，再 `tools run editor.dialog.click`；不得要求人工处理、预定义按钮列表、launch-only 入口或额外确认
- 诊断错误的 `nextActions` 必须指向 `doctor` 或 `tools run diagnose.preflight`

## JSON 字段命名

所有错误响应使用 camelCase：
- `nextActions`
- `retryAfterMs`

错误恢复逻辑必须读取上述 camelCase 字段。
