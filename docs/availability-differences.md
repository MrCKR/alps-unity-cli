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

执行前可通过 `tools describe` 检查 `requiresPackages`、`extensionAvailability`、`missingPackageIds`；执行失败时跟随错误里的 `nextActions`。

### 2. 项目工具注册

项目工具（`IUnityCliTool<TReq,TRes>` C# 实现）通过 Bridge 运行时发现。不同项目注册不同工具。`tools group list custom --include-custom` 对实例 A 可能显示 `sample.echo`，对实例 B 可能显示 `acme.custom-builder`。

项目工具发现需要可验证的 live Bridge generation。无 live Bridge 时，CLI 仍提供 packaged built-in/Extension static catalog；项目自定义能力只能来自带 identity/freshness 的已验证 snapshot，否则返回结构化 unavailable。

### 3. Bridge 可达性

| Instance 状态 | 发现与执行 |
|---------------|------------|
| `ready` | static catalog + 当前 validated generation 的 live/project custom descriptors；可执行 Bridge Tool |
| `transitioning` | static catalog 立即可用；直接调用由 supervisor 在 absolute deadline 内等待恢复 |
| `blocked` | static catalog 立即可用；`editor.state` 返回 blocker，普通调用返回结构化 blocked error |
| `offline` | static catalog 与 TS-local 能力可用；live project custom discovery 不伪造结果 |
| `conflict` | static catalog 可用；Bridge 调用返回 `E_INSTANCE_CONFLICT` |

离线/CLI-only 能力不受 Bridge 状态影响：
- `tools run package.list` / `package.info` / `package.search` / `package.install` / `package.remove` / `package.deploy` / `package.restore` / `package.bridge-remove`
- `batch schema` / `batch run`
- `tools run docs.get` / `docs.search` / `reflect.type` / `reflect.member` / `reflect.search`
- `jobs status` / `jobs logs` / `jobs cancel`
- `init --check`, `doctor`, `instances list` / `instances prune`
- `target alias list` / `target alias set` / `target alias unset`

### 4. 编辑器状态（PlayMode）与 Runtime 工具可用性

`target: "runtime"` 和 `availabilityScope: "playModeSession"` 的工具仅在 Play Mode 下可用：

| 编辑器状态 | `target: "editor"` 工具 | `target: "runtime"` 工具 |
|-----------|------------------------|-------------------------|
| 编辑模式 | 可执行 | 执行返回 `E_RUNTIME_NOT_READY`（transient） |
| 播放模式 | 可执行 | 可执行 |

使 runtime 工具可用的步骤：
1. `tools run editor.play` 进入 Play Mode
2. `context read editor.state --input '{}'` 确认 `state.playMode` 的已知值为 `play`
3. 重试 runtime 工具

### 5. Domain Reload

Unity domain reload 期间，registry 将 project identity 归约为 `transitioning`，listener generation 可以变化。session supervisor 在同一 logical request 和 absolute deadline 内重新绑定已验证 generation；可恢复 reload 不作为直接调用的最终错误暴露。deadline 耗尽时返回该 completion contract 固定的 timeout 或 outcome-unknown 结果。

### 6. 工具目录 Fingerprint

每个实例携带 `toolCatalogFingerprint` 捕获当前工具目录状态。如果发现与执行之间 fingerprint 变化（如安装了包或发生 domain reload），CLI 可能返回 fingerprint mismatch 警告。`doctor` 命令检查 fingerprint 一致性。

## AI Agent 处理方式

1. **先解析项目身份。** 执行任何工具前先 `instances list`，然后在 discovery、describe 和执行命令上显式传 `--instance`；也可以从目标 Unity 项目 CWD 调用
2. **执行前先 describe。** descriptor 显示 `supportStatus`、`target`、`availabilityScope`、`playModeSupport`
3. **按结构化终态处理。** Package-not-found、instance conflict、blocker、timeout、outcome unknown 和 `E_RUNTIME_NOT_READY` 都包含 `nextActions`
4. **能力变化后重新发现。** 安装包或 descriptor fingerprint 改变后，重新 `tools group list custom --include-custom`
5. **需要即时状态时读取 Context。** 使用 `context read editor.state --input '{}'`；`target: "runtime"` 的 Tool 需先进入 Play Mode

## 交叉引用

- 目标选择：[target-and-instance](./target-and-instance.md)
- 工具发现：[tool-discovery](./tool-discovery.md)
- 错误码：[error-codes](./error-codes.md)
