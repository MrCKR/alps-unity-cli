# 安全边界（Safety Boundaries）

alps-unity-cli 的安全模型围绕三个核心原则：Tool 可有副作用但必须声明、Context 必须只读、Extension 不能绕过 descriptor/envelope。

## 风险标记体系

每个工具 descriptor 包含以下安全相关字段：

| 字段 | 类型 | 说明 |
|------|------|------|
| `destructiveHint` | boolean | true = 有破坏性，执行前需要确认 |
| `supportsDryRun` | boolean | true = 支持 `--dry-run` 预览 |
| `longRunning` | boolean | true = 可能长时间运行 |
| `permissions` | string[] | 执行所需权限 |
| `cancellationSemantics` | string | 取消行为描述 |
| `playModeSupport` | string | Play Mode 行为：`supported` / `runtime-only` / `session-affecting` / `editor-only` / `unknown` |

## Tool 安全规则

### 破坏性工具

`destructiveHint: true` 的工具执行前必须：
1. 先 `tools describe` 确认风险
2. 如支持 `--dry-run`，先预览 `plannedEffects`
3. 使用 `--yes` 确认执行

当前标记为破坏性的工具包括：

| 工具 | 说明 |
|------|------|
| `tools run scene.save` | 保存场景 |
| `tools run asset.delete` | 删除资产 |
| `tools run scriptable-object.create` | 创建 ScriptableObject 资产 |
| `tools run scriptable-object.modify` | 修改 ScriptableObject 资产 |
| `tools run texture.modify` | 修改 Texture 资产 |
| `tools run texture.delete` | 删除 Texture 资产 |
| `tools run shader.delete` | 删除 Shader 资产 |
| `tools run object.modify` | 深度修改对象/资产数据 |
| `tools run probuilder.delete-faces` | ProBuilder Delete Faces |
| `tools run probuilder.combine-meshes` | ProBuilder Combine Meshes |
| `tools run probuilder.merge-objects` | ProBuilder Merge Objects |
| `tools run editor.menu` | 执行编辑器菜单项 |
| `tools run package.install` | 安装包 |
| `tools run package.remove` | 移除包 |
| `tools run code.exec` | 执行代码 |
| `jobs cancel` | 取消测试/构建/刷新等后台任务 |

### 确认流程

```
tools describe <tool>        → 确认 destructiveHint 和 supportsDryRun
       ↓
--dry-run（如支持）           → 查看 plannedEffects
       ↓
--yes                         → 确认执行
```

未确认时返回 `E_DESTRUCTIVE_CONFIRMATION_REQUIRED`。

### Play Mode 支持

- `supported`：Play Mode 下可正常执行，语义基本不变。
- `runtime-only`：Play Mode 下可执行，但只影响当前运行会话，退出 Play Mode 后不持久。
- `session-affecting`：允许执行，但可能触发 asset import、compile、domain reload 或 Play Mode 状态变化。
- `editor-only`：没有合理运行态语义，Play Mode 下返回 `E_TOOL_PLAYMODE_UNAVAILABLE`。
- `unknown`：迁移期状态，应逐步消除。

## Context 只读硬边界

Context 不得：
- 修改 Unity 状态（场景、资产、GameObject、Component）
- 修改文件系统
- 修改 Package 依赖
- 触发 Play Mode 变化
- 产生任何副作用


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

## 安全确认证明（Security Proof）

破坏性 Bridge 命令的请求 payload 包含 `security` 证明对象：

```json
{
  "security": {
    "confirmationFingerprint": "destructive-safety-v1",
    "dryRunPreview": { ... }
  }
}
```

这确保确认请求无法被伪造或重放。
