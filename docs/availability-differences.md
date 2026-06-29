# 可用性差异（Availability Differences）

> 为什么不同 Unity 实例可能返回不同的工具可用性

## 核心原则

工具可用性是 **per-instance** 的，不是全局的。`tools group list` 和 `tools describe` 反映已解析目标实例的能力。不同实例可能显示不同的工具集。

## 可用性差异原因

### 1. 已安装 Package

依赖特定 Unity Package 的工具只在目标项目安装了对应包时可用：

| 工具 | Package 依赖 | 不可用时行为 |
|------|-------------|-------------|
| ProBuilder 扩展工具（create-shape/extrude-faces/delete-faces 等） | `com.unity.probuilder` | 返回 `E_PROBUILDER_PACKAGE_NOT_FOUND` |
| `vfx.graph/vfx.effect` | `com.unity.visualeffectgraph` | 返回 `E_VFX_PACKAGE_MISSING` |

Package 依赖通过 `requiresPackages` 字段声明在工具 descriptor 中：

```json
{
  "name": "vfx.graph",
  "requiresPackages": ["com.unity.visualeffectgraph"]
}
```

执行前可通过 `tools describe` 检查 `requiresPackages` 和 `available` 字段。

### 2. 项目工具注册

项目工具（`[UnityCliTool]` C# 特性）通过 Bridge 运行时发现。不同项目注册不同工具。`tools group list custom --include-custom` 对实例 A 可能显示 `sample.echo`，对实例 B 可能显示 `acme.custom-builder`。

项目工具发现需要可达的 Bridge。无 Bridge 连接时，只能发现内置工具。

### 3. Bridge 可达性

| Bridge 状态 | 可用工具 |
|-------------|---------|
| 可达（`/health` OK） | 所有内置 + 项目工具（`--include-custom`） |
| 不可达 | 仅 `offline` / `cli-only` 工具 |
| 过期（心跳超时） | 无 Bridge 命令；返回 `E_TARGET_STALE` |
| 不可用（`/health` 失败） | 无 Bridge 命令；返回 `E_TARGET_UNREACHABLE` |

离线/CLI-only 能力不受 Bridge 状态影响：
- `tools run package.list` / `package.info` / `package.search` / `package.install` / `package.remove` / `package.deploy` / `package.restore` / `package.bridge-remove`
- `batch schema` / `batch run`
- `tools run docs.get` / `docs.search` / `reflect.type` / `reflect.member` / `reflect.search`
- `jobs status` / `jobs logs` / `jobs cancel`
- `init --check`, `doctor`, `instances list` / `instances prune`
- `target current` / `target set` / `target clear` / `target alias`

### 4. 编辑器状态（PlayMode）与 Runtime 工具可用性

`target: "runtime"` 和 `availabilityScope: "playModeSession"` 的工具仅在 Play Mode 下可用：

| 编辑器状态 | `target: "editor"` 工具 | `target: "runtime"` 工具 |
|-----------|------------------------|-------------------------|
| 编辑模式 | `available: true` | `available: false` → `E_RUNTIME_NOT_READY`（transient） |
| 播放模式 | `available: true` | `available: true` |

使 runtime 工具可用的步骤：
1. `tools run editor.play` 进入 Play Mode
2. `tools run editor.state` 确认 `playMode: playing`
3. 重试 runtime 工具

### 5. Domain Reload

Unity domain reload 期间（如脚本编译后），Bridge 标记目标为过期并停止服务请求。CLI 返回 `E_UNITY_COMPILING` 或 `E_UNITY_BUSY`。reload 完成后，Bridge 使用更新后的能力重新注册。

### 6. 工具目录 Fingerprint

每个实例携带 `toolCatalogFingerprint` 捕获当前工具目录状态。如果发现与执行之间 fingerprint 变化（如安装了包或发生 domain reload），CLI 可能返回 fingerprint mismatch 警告。`doctor` 命令检查 fingerprint 一致性。

## AI Agent 处理方式

1. **先解析目标。** 执行任何工具前先 `instances list` + `target set`
2. **执行前先 describe。** descriptor 显示 `handler`、`supportStatus`、`target`、`availabilityScope`、`available`
3. **优雅处理错误。** Package-not-found、Bridge-unreachable 和 `E_RUNTIME_NOT_READY` 包含 `next_actions`
4. **状态变化后重新发现。** 安装包或 domain reload 后，重新 `tools group list custom --include-custom`
5. **检查 `editor.state`** 再调用 PlayMode 敏感工具。`target: "runtime"` 需先进入 Play Mode

## 交叉引用

- 目标选择：[target-and-instance](./target-and-instance.md)
- 工具发现：[tool-discovery](./tool-discovery.md)
- 错误码：[error-codes](./error-codes.md)
