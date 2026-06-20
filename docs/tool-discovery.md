# 工具发现（Tool Discovery）

alps-unity-cli 使用 group-first 分层发现模型：先看分组概览，再按组深入，最后查看单个工具完整描述。

## 推荐发现流程

```
tools groups                    → 查看所有工具分组
       ↓
tools list --group <group>      → 查看某个分组的工具摘要
       ↓
tools describe <tool>           → 查看工具完整描述（执行前必做）
       ↓
tools run <tool>                → 执行工具
```

辅助发现：

```
tools search <keyword>          → 按关键词搜索工具
```

## tools groups

列出所有工具分组名称。

```powershell
unity_cli tools groups
```

输出：

```json
{
  "ok": true,
  "data": {
    "command": "tools.groups",
    "count": 25,
    "groups": ["scene", "camera", "go", "component", "asset", "prefab", "material", "graphics", "physics", "profiler", "probuilder", "shader", "texture", "animation", "vfx", "ui", "script", "editor", "diagnose", "screenshot", "code", "cli", "test", "build", "tools"]
  }
}
```

### 包含项目自定义工具分组

当 Bridge 可达时，`tools groups` 会自动发现项目自定义工具的分组：

```powershell
unity_cli tools groups --bridge-endpoint http://127.0.0.1:47392
```

## tools list

列出工具摘要信息。

```powershell
# 列出所有工具
unity_cli tools list

# 按分组过滤（推荐）
unity_cli tools list --group camera

# 包含项目自定义工具
unity_cli tools list --include-custom

# 按分组 + 自定义工具
unity_cli tools list --group custom --include-custom

# 文本格式
unity_cli tools list --group camera --format text
```

输出摘要包含：

| 字段 | 说明 |
|------|------|
| `name` | 工具唯一标识 |
| `title` | 人类可读标题 |
| `group` | 所属分组 |
| `readOnlyHint` | 是否只读 |
| `destructiveHint` | 是否有破坏性 |
| `handler` | 执行通道类型 |
| `supportStatus` | 支持状态标签 |
| `target` | 目标类型（editor/runtime/both） |
| `availabilityScope` | 可用性范围 |
| `requiresPackages` | 依赖的 Unity 包 |

### 为什么不推荐 tools list 无过滤全量展开

`tools list` 不带 `--group` 参数时会返回所有 100+ 工具的完整摘要，这对 AI Agent 来说信息量过大，容易导致上下文溢出或误选工具。推荐使用 `--group` 参数逐组查看。

## tools search

按关键词搜索工具名称、标题、描述和分组。

```powershell
unity_cli tools search screenshot
unity_cli tools search camera --include-custom
```

搜索是辅助手段，不应替代 group-first 发现流程。当你知道关键词但不确定分组时使用。

## tools describe

查看单个工具的完整描述。**执行任何工具前必须先 describe。**

```powershell
unity_cli tools describe camera.screenshot
```

输出包含完整的工具 descriptor：

| 字段 | 说明 |
|------|------|
| `name` | 工具唯一标识 |
| `title` | 人类可读标题 |
| `description` | 详细说明 |
| `group` | 所属分组 |
| `inputSchema` | 输入参数 JSON Schema |
| `outputSchema` | 输出格式 JSON Schema |
| `readOnlyHint` | 是否只读 |
| `destructiveHint` | 是否有破坏性 |
| `supportsDryRun` | 是否支持 `--dry-run` |
| `longRunning` | 是否长时间运行 |
| `architectureSensitive` | 是否架构敏感 |
| `frozenDecision` | 冻结决策编号 |
| `handler` | 执行通道 |
| `supportStatus` | 支持状态 |
| `target` | 目标类型 |
| `availabilityScope` | 可用性范围 |
| `available` | 当前是否可用 |
| `requiresPackages` | 依赖的 Unity 包 |
| `examples` | 使用示例 |
| `docsTopic` | 相关文档主题 |
| `permissions` | 所需权限 |
| `cancellationSemantics` | 取消行为说明 |
| `timeoutMs` | 超时时间 |

### 自定义工具描述

当工具名不在内置列表中时，`tools describe` 会自动查询项目自定义工具：

```powershell
unity_cli tools describe my.custom-tool --bridge-endpoint http://127.0.0.1:47392
```

## Descriptor 字段用途

### 安全判断

- `destructiveHint: true` → 执行前必须确认风险、使用 `--dry-run` 或 `--yes`
- `readOnlyHint: true` → 只读操作，不会修改 Unity 状态
- `architectureSensitive: true` → 修改前需审查 `frozenDecision`

### 执行通道判断

- `handler: "bridge"` → 实时 Bridge 执行
- `handler: "bridge-runtime"` → 需要 Play Mode 的 Runtime 工具
- `handler: "hybrid"` → 支持 Bridge + job 双通道
- `handler: "job"` → batchmode/job 执行
- `handler: "offline"` → 本地 CLI 执行

### 可用性判断

- `target: "editor"` → 编辑器主线程可用
- `target: "runtime"` → Play Mode 下可用
- `target: "both"` → 编辑器和运行时都可用
- `availabilityScope` → 详细可用性范围
- `available` → 当前目标实例上是否可用
- `requiresPackages` → 需要安装的 Unity 包

## AI Agent 发现工作流

1. `tools groups` — 了解能力分类
2. `tools list --group <relevant-group>` — 查看相关分组工具
3. `tools describe <tool>` — 获取完整 descriptor，确认 inputSchema、安全提示和可用性
4. `tools run <tool> --input '...'` — 执行

### 错误处理

- `E_TOOL_NOT_FOUND_FOR_TARGET` → 工具在当前目标不可用，检查 `requiresPackages` 和 `available`
- `E_RUNTIME_NOT_READY` → Runtime 工具需要 Play Mode 活跃，检查 `target: "runtime"`
- `E_TARGET_AMBIGUOUS` → 多个实例可用，需要显式指定 `--target`
