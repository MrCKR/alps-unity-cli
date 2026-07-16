# 错误码（Error Codes）

alps-unity-cli 使用结构化错误码，包含机器可读的 `code`、`transient` 标记、`retryAfterMs` 和 `nextActions` 自动恢复指引。

## 错误格式

```json
{
  "ok": false,
  "requestId": "req-xxx",
  "error": {
    "code": "E_TARGET_AMBIGUOUS",
    "message": "Multiple ready instances found.",
    "details": "...",
    "nextActions": [
      "Run unity_cli instances list to discover targets.",
      "Run unity_cli target set <id|alias> to select a target."
    ],
    "transient": false,
    "retryAfterMs": 0
  }
}
```

## 错误分类

### Target / Instance 错误

| 错误码 | 含义 | 恢复 |
|--------|------|------|
| `E_TARGET_NOT_FOUND` | 无可用目标实例 | `instances list` 检查实例 |
| `E_TARGET_STALE` | target ID 校验后的 health probe 暂未恢复 stale 注册 | `transient:true`；按 `retryAfterMs` 重试，持续失败再清理注册 |
| `E_TARGET_UNREACHABLE` | `/health` 无响应、响应畸形或 target ID 不匹配 | `transient:true`；检查 Bridge 身份并按 `retryAfterMs` 重试 |
| `E_TARGET_AMBIGUOUS` | 多个就绪实例 | `target set <id>` 显式选择 |

### Runtime / Play Mode 错误

| 错误码 | 含义 | 恢复 |
|--------|------|------|
| `E_RUNTIME_NOT_READY` | Runtime 工具需 Play Mode | 先 `tools run editor.play`，transient 可重试 |
| `E_RUNTIME_SESSION_STALE` | 运行时会话过期 | 重新进入 Play Mode |
| `E_RUNTIME_TIMEOUT` | 运行时操作超时 | 增加 `--timeout-ms` 或重试 |

### Tool / Discovery 错误

| 错误码 | 含义 | 恢复 |
|--------|------|------|
| `E_TOOL_NOT_FOUND_FOR_TARGET` | 工具在目标不可用 | `tools describe` 检查可用性 |
| `E_TOOL_FINGERPRINT_MISMATCH` | handler 代码变更 | 审计后 `--fingerprint-mode warn` |
| `E_INPUT_VALIDATION_FAILED` | 输入不符合 schema | 查看 `tools describe` 的 inputSchema |

### 安全确认错误

| 错误码 | 含义 | 恢复 |
|--------|------|------|
| `E_DESTRUCTIVE_CONFIRMATION_REQUIRED` | 破坏性工具未确认 | 添加 `--yes` |
| `E_DRY_RUN_UNSUPPORTED` | 工具不支持 dry-run | 直接用 `--yes` 确认 |

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
| `E_PROBUILDER_PACKAGE_NOT_FOUND` | ProBuilder 包未安装 |
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
| `E_UNITY_BUSY` | Unity 正忙 |
| `E_EXECUTION_TIMEOUT` | 执行超时 |
| `E_CODE_COMPILE_FAILED` | 代码编译失败 |
| `E_CODE_RUNTIME_EXCEPTION` | 代码运行时异常 |
| `E_IO_ERROR` | IO 错误 |

## AI Agent 错误处理流程

1. 读取 `error.code` 确定错误类别
2. 检查 `transient` — 若为 true，等待 `retryAfterMs` 后重试
3. 遵循 `nextActions` 的建议恢复
4. 不要猜测恢复方式，使用建议的命令

### nextActions 规则

- Target 错误的 `nextActions` 必须指向 `instances list` 或 `target set`
- Runtime 错误的 `nextActions` 必须指向 `tools run editor.play` 或 Play Mode 相关操作
- Discovery 错误的 `nextActions` 必须指向 `tools group`、`tools group list`、`tools search` 或 `tools describe`
- 诊断错误的 `nextActions` 必须指向 `doctor` 或 `tools run diagnose.preflight`

## JSON 字段命名

所有错误响应使用 camelCase：
- `nextActions`
- `retryAfterMs`

不要使用旧的 snake_case 字段名；错误恢复逻辑必须读取上述 camelCase 字段。
