---
name: unity_cli
description: 通过 alps-unity-cli 操作 Unity Editor
---

# unity_cli — Agent 操作手册

本 Skill 指导 AI Agent 如何通过 `unity_cli` CLI 安全高效地操作 Unity Editor。

## 核心工作流

```
instances list → target set → tools group → tools group list <group> → tools describe <tool> → tools run <tool> → verify
```

## 步骤 1：发现目标

```powershell
unity_cli instances list
```

检查返回的实例列表。选择一个 `status: ready` 的实例：

```powershell
unity_cli target set <instanceId>
# 或使用别名
unity_cli target alias set <instanceId> my-project
unity_cli target set my-project
```

**如果没有就绪实例**：Unity Editor 未运行或未加载 Bridge 包。通知用户打开 Unity Editor 并运行 `unity_cli init`。

**如果多个就绪实例**：必须显式 `target set`，CLI 会返回 `E_TARGET_AMBIGUOUS` 而不猜测。

## 步骤 2：获取只读上下文

在执行任何操作前，先了解项目状态：

```powershell
# 编辑器状态
unity_cli tools run editor.state

# 诊断预检
unity_cli diagnose preflight

# 场景层级
unity_cli tools run scene.hierarchy --input '{}'

# 使用 context 命令获取只读项目状态
unity_cli context list
unity_cli context describe <name>
unity_cli context read <name>

# 常用只读 Context
unity_cli context read scene.gameobject.components --input '{"instanceId":123}'
unity_cli context read prefab.info --input '{"path":"Assets/Prefabs/Hero.prefab"}'
unity_cli context read tools.groups
```

> 只读工具（`readOnlyHint: true`）和 context 命令不会修改 Unity 状态，可安全重复调用。

## 步骤 3：发现工具能力

### 推荐：group-first 分层发现

```powershell
# 查看工具分组
unity_cli tools group

# 按组查看工具摘要
unity_cli tools group list camera

# 查看单个工具完整描述
unity_cli tools describe camera.screenshot
```

### 辅助：关键词搜索

```powershell
unity_cli tools search screenshot
```

### 包含项目自定义工具

```powershell
unity_cli tools group list custom --include-custom
unity_cli tools group list <group> --include-custom
```

## 步骤 4：执行工具

```powershell
# 基本执行
unity_cli tools run <tool-name> --input '<json>'

# 从文件读取输入
unity_cli tools run <tool-name> --input-file params.json

# 从 stdin 读取输入
echo '<json>' | unity_cli tools run <tool-name> --stdin
```

### 破坏性工具

执行前必须确认：

```powershell
# 1. describe 确认风险
unity_cli tools describe asset.delete

# 2. dry-run 预览（如支持）
unity_cli tools run asset.delete --input '{"path":"Assets/Temp.mat"}' --dry-run

# 3. 确认执行
unity_cli tools run asset.delete --input '{"path":"Assets/Temp.mat"}' --yes
```


## 步骤 5：验证结果

```powershell
# 只读验证
unity_cli tools run camera.list --input '{}'
unity_cli tools run editor.state --input '{}'
unity_cli tools run scene.hierarchy --input '{}'

# 诊断验证
unity_cli doctor
```

## 常见模式

### 开始会话

```powershell
unity_cli instances list
unity_cli target set <instanceId>
unity_cli tools run editor.state --input '{}'
unity_cli tools group
```

### 场景修改

```powershell
unity_cli tools describe asset.create
unity_cli tools run asset.create --input '{"path":"Assets/NewFolder","type":"folder"}'
unity_cli tools run editor.refresh --input '{"compile":true}' --job
```

### 脚本安全编辑

```powershell
unity_cli tools run script.hash --input '{"path":"Assets/Scripts/Player.cs"}'
unity_cli tools describe script.apply-edits
unity_cli tools run script.apply-edits --input-file edits.json --yes
```

`script.edit` / `script.apply-edits` 支持 `preconditionSha256`，编辑前先取 hash，避免覆盖用户并发修改。

### 使用 Runtime 工具

```powershell
unity_cli tools run editor.state --input '{}'
# 确认当前不在 Play Mode
unity_cli tools run editor.play --input '{}'
unity_cli tools run <runtime-tool> --input '<json>'
unity_cli tools run editor.stop --input '{}'
```

### 处理自定义工具与 Context

```powershell
unity_cli tools group list custom --include-custom
unity_cli tools describe my.custom-tool
unity_cli tools run my.custom-tool --input '<json>'
unity_cli context read tools.custom
```

### 常见对齐工具组

```powershell
unity_cli tools group list script
unity_cli tools group list scriptable-object
unity_cli tools group list physics
unity_cli tools group list project
```

### 诊断问题

```powershell
unity_cli doctor
unity_cli diagnose preflight
unity_cli tools describe <problematic-tool>
```

### 管理扩展包

```powershell
unity_cli extensions list
unity_cli extensions install <id> --project <path>
unity_cli extensions remove <id> --project <path>
unity_cli extensions doctor --project <path>
```

## 错误处理

### 统一错误格式

```json
{
  "ok": false,
  "requestId": "req-xxx",
  "error": {
    "code": "E_RUNTIME_NOT_READY",
    "message": "...",
    "transient": true,
    "retry_after_ms": 3000,
    "next_actions": ["Enter Play Mode first: unity_cli tools run editor.play --input '{}'"]
  }
}
```

### 关键规则

1. 读取 `error.code` 确定错误类别
2. 检查 `transient` — 若为 true，等待 `retry_after_ms` 后重试
3. 遵循 `next_actions` 恢复，不要猜测
4. `E_TARGET_AMBIGUOUS` → 显式 `target set`
5. `E_RUNTIME_NOT_READY` → 先 `editor.play`
6. `E_TOOL_FINGERPRINT_MISMATCH` → 通知用户，审计后可 `--fingerprint-mode warn`

### 瞬态错误重试

`transient: true` 的错误可以自动重试（如 `E_RUNTIME_NOT_READY`、`E_UNITY_BUSY`）：

```
等待 retry_after_ms → 重试原命令 → 最多重试 3 次
```

## 安全规则

### 必须遵守

- 执行 mutating tool 前必须先 `tools describe` 确认风险
- `destructiveHint: true` 的工具必须使用 `--dry-run`（如支持）+ `--yes`
- 检查 `target` 和 `availabilityScope` 确保工具在当前状态可用
- 检查 `requiresPackages` 确保所需 Unity Package 已安装
- Context（`readOnlyHint: true`）不能产生副作用
- 脚本编辑优先使用 `script.hash` + `preconditionSha256`，再执行 `script.edit` 或 `script.apply-edits`
- `editor.undo/redo`、`project.tag/layer`、`editor.resolution` 会修改 Editor/Project 状态，必须先 `tools describe`

### 不得违反

- 不得在未 `target set` 的情况下假设目标
- 不得跳过 `tools describe` 直接执行未知工具
- 不得忽略 `E_TARGET_AMBIGUOUS` 错误
- 不得在非 Play Mode 调用 `target: "runtime"` 工具（除非处理 `E_RUNTIME_NOT_READY` 重试）
- 不得手动读取 Bridge 注册文件或 ping 端点

## 不要做的事

| ❌ 不要 | ✅ 应该 |
|---------|---------|
| 用 `tools list` 无过滤全量展开作为首选发现 | 先 `tools group`，再 `tools group list <group>` |
| 混用 Context 和 Tool 概念 | Tool 可有副作用；Context 必须只读 |
| 跳过 `tools describe` 直接 `tools run` | 先 describe 确认 schema、风险、可用性 |
| 假设所有工具在所有实例可用 | 检查 `available` 和 `requiresPackages` |
| 用 `--input-json` 传参 | 使用 `--input`、`--input-file` 或 `--stdin` |
| 使用 `nextActions`（camelCase） | 使用 `next_actions`（snake_case） |
| 使用 `E_PLAYMODE_NOT_READY` | 使用 `E_RUNTIME_NOT_READY` |
| 手动读取 Bridge 端点或注册文件 | 使用 CLI 命令（`instances list`、`target get`） |
| 绕过 fingerprint 校验 | 默认 block，审计后可用 `--fingerprint-mode warn` |

## 详细文档

- [工具发现](../docs/tool-discovery.md) — group-first 分层发现详解
- [工具执行](../docs/tool-execution.md) — tools run 执行通道与安全确认
- [安全边界](../docs/safety-boundaries.md) — 风险模型与破坏性确认
- [错误码](../docs/error-codes.md) — 结构化错误与 next_actions
- [自定义工具](../docs/custom-tools.md) — 项目侧 Tool 注册开发
- [自定义 Context](../docs/custom-contexts.md) — 项目侧只读 Context
- [扩展包](../docs/extensions.md) — 扩展包使用与开发
- [示例](../examples/README.md) — 操作示例
