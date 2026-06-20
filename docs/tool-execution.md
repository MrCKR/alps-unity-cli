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

`tools run` 根据 descriptor 的 `handler` 和 `target` 自动路由：

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

`tools run` 会用 `inputSchema` 校验输入参数。校验失败返回 `E_INPUT_VALIDATION_FAILED`。

```powershell
# 正确：提供 inputSchema 要求的字段
unity_cli tools run camera.screenshot --input '{"target":"Main Camera","output":"shot.png"}'

# 错误：缺少必需字段
unity_cli tools run camera.screenshot --input '{}'
# → E_INPUT_VALIDATION_FAILED, next_actions 指向 tools describe
```

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

## Job 模式

handler 为 `hybrid` 的工具支持 `--job` 标志，切换为 batchmode/job 执行：

```powershell
# 实时 Bridge 执行（默认）
unity_cli tools run probuilder.shape --input '{"type":"Cube"}'

# Job 模式
unity_cli tools run probuilder.shape --input '{"type":"Cube"}' --job

# 查看任务状态
unity_cli job status --id <jobId>
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
| `E_RUNTIME_NOT_READY` | Runtime 工具但非 Play Mode | 先执行 `editor play`，transient 可重试 |
| `E_INPUT_VALIDATION_FAILED` | 输入不符合 inputSchema | 查看 `tools describe` 的 inputSchema |
| `E_TOOL_FINGERPRINT_MISMATCH` | handler 代码变更 | 审计后 `--fingerprint-mode warn` |
| `E_DRY_RUN_UNSUPPORTED` | 工具不支持 dry-run | 直接使用 `--yes` 确认 |
| `E_DESTRUCTIVE_CONFIRMATION_REQUIRED` | 破坏性工具未确认 | 添加 `--yes` 标志 |
| `E_TARGET_AMBIGUOUS` | 多个实例可用 | 显式指定 `--target` |
| `E_TARGET_NOT_FOUND` | 无可用实例 | 检查 `instances list` |
