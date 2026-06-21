# 安全边界（Safety Boundaries）

alps-unity-cli 的安全模型围绕三个核心原则：Tool 可有副作用但必须声明、Context 必须只读、Extension 不能绕过 descriptor/envelope。

## 风险标记体系

每个工具 descriptor 包含以下安全相关字段：

| 字段 | 类型 | 说明 |
|------|------|------|
| `readOnlyHint` | boolean | true = 只读，不会修改 Unity 状态 |
| `destructiveHint` | boolean | true = 有破坏性，执行前需要确认 |
| `supportsDryRun` | boolean | true = 支持 `--dry-run` 预览 |
| `longRunning` | boolean | true = 可能长时间运行 |
| `architectureSensitive` | boolean | true = 影响项目架构，修改前需审查冻结决策 |
| `permissions` | string[] | 执行所需权限 |
| `cancellationSemantics` | string | 取消行为描述 |

## Tool 安全规则

### 只读工具

`readOnlyHint: true` 的工具保证：
- 不修改 Unity 状态、文件、场景、资产
- 不触发 Play Mode 变化
- 不修改 Package 依赖
- 可安全重复调用

### 破坏性工具

`destructiveHint: true` 的工具执行前必须：
1. 先 `tools describe` 确认风险
2. 如支持 `--dry-run`，先预览 `plannedEffects`
3. 使用 `--yes` 确认执行

当前标记为破坏性的工具包括：

| 工具 | 说明 |
|------|------|
| `scene.save` | 保存场景 |
| `asset.delete` | 删除资产 |
| `go.delete` | 删除 GameObject |
| `component.remove` | 移除组件 |
| `probuilder.delete-faces` | ProBuilder Delete Faces |
| `probuilder.combine-meshes` | ProBuilder Combine Meshes |
| `probuilder.merge-objects` | ProBuilder Merge Objects |
| `editor.menu` | 执行编辑器菜单项 |
| `script.edit` | 编辑脚本文件 |
| `cli.package.remove` | 移除包 |
| `code.exec` | 执行代码 |
| `test.cancel` | 取消测试 |
| `build.cancel` | 取消构建 |

### 确认流程

```
tools describe <tool>        → 确认 destructiveHint 和 supportsDryRun
       ↓
--dry-run（如支持）           → 查看 plannedEffects
       ↓
--yes                         → 确认执行
```

未确认时返回 `E_DESTRUCTIVE_CONFIRMATION_REQUIRED`。

## Context 只读硬边界

Context（`readOnlyHint: true` 的工具）不得：
- 修改 Unity 状态（场景、资产、GameObject、Component）
- 修改文件系统
- 修改 Package 依赖
- 触发 Play Mode 变化
- 产生任何副作用

当前只读工具示例：`camera.list`、`camera.info`、`asset.search`、`asset.info`、`scene.hierarchy`、`scene.active`、`editor.state`、`editor.console`、`tools.list`、`tools.describe` 等。

## Extension 安全边界

项目自定义工具不能绕过 descriptor/envelope 安全模型：

| 规则 | 说明 |
|------|------|
| 必须有 descriptor | 通过 `[UnityCliTool]` 特性自动生成，不可省略 |
| 必须通过 envelope | 输入输出必须遵循统一 JSON envelope 格式 |
| Fingerprint 校验 | handler 代码变更时默认阻止执行 |
| 不可绕过 registry | 不允许直接调用未注册的方法 |
| 不可提供反射 escape hatch | 不允许通过自定义工具绕过安全检查 |

## Fingerprint 完整性

项目自定义工具的 fingerprint 机制：

- **计算方式**：`sha256(handler-code + descriptor-v1)`
- **存储位置**：工具 descriptor 中的 `fingerprint` 字段
- **校验时机**：`tools run` 执行前
- **校验失败行为**：
  - 默认 `block`：返回 `E_TOOL_FINGERPRINT_MISMATCH`
  - `warn` 模式：输出警告但继续执行

```powershell
# 检查 fingerprint 状态
unity_cli doctor
# doctor 会报告 project_tool_fingerprint 检查结果
```

## 架构敏感工具

`architectureSensitive: true` 的工具标记了影响项目架构的能力，修改前需审查 `frozenDecision` 字段：

- 所有 `profiler.*` 工具（ALP-165-Q4 冻结决策）
- 这些工具可以读取和修改 Unity Profiler 状态

## 安全确认证明（Security Proof）

破坏性 Bridge 命令的请求 payload 包含 `security` 证明对象：

```json
{
  "security": {
    "confirmationFingerprint": "alp332-destructive-safety-v1",
    "dryRunPreview": { ... }
  }
}
```

这确保确认请求无法被伪造或重放。
