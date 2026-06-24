# 工具发现（Tool Discovery）

alps-unity-cli 使用 group-first 分层发现模型：先看分组概览，再按组深入，最后查看单个工具完整描述。

## 推荐发现流程

```
tools group                     → 查看所有工具分组
       ↓
tools group list <group>       → 查看某个分组的工具摘要
       ↓
tools describe <tool>          → 查看工具完整描述（执行前必做）
       ↓
tools run <tool>               → 执行工具
```

辅助发现：

```
tools search <keyword>          → 按关键词搜索工具
```

## tools group

列出所有工具分组名称。

```powershell
unity_cli tools group
```

输出：

```json
{
  "ok": true,
  "data": {
    "command": "tools.group",
    "count": 34,
    "groups": [
      { "name": "scene", "title": "...", "toolCount": 12, "source": "core", "cliCommand": "tools group list scene" },
      { "name": "object", "title": "...", "toolCount": 2, "source": "core", "cliCommand": "tools group list object" },
      { "name": "reflection", "title": "...", "toolCount": 2, "source": "core", "cliCommand": "tools group list reflection" }
    ]
  }
}
```

核心分组包含：scene、camera、go、component、object、reflection、type、asset、prefab、material、graphics、physics、profiler、shader、texture、animation、ui、script、editor、console、package、build、screenshot、code、diagnose。可选 extension 分组（probuilder/cinemachine/vfx/urp/hdrp）仅在目标项目安装对应 Unity 包时出现。

### 包含项目自定义工具分组

当 Bridge 可达时，`tools group` 会自动发现项目自定义工具的分组：

```powershell
unity_cli tools group --bridge-endpoint http://127.0.0.1:47392
```

## tools group list

列出指定分组的工具摘要信息。

```powershell
# 按分组查看工具（必填分组名）
unity_cli tools group list camera

# 包含项目自定义工具
unity_cli tools group list custom --include-custom

# 文本格式
unity_cli tools group list camera --format text
```

`tools group list` 是按分组查看工具的标准入口；需要全量视图时先用 `tools group` 获取分组，再逐组展开。

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
| `destructiveHint` | 是否有破坏性 |
| `supportsDryRun` | 是否支持 `--dry-run` |
| `longRunning` | 是否长时间运行 |
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

1. `tools group` — 了解能力分类
2. `tools group list <relevant-group>` — 查看相关分组工具
3. `tools describe <tool>` — 获取完整 descriptor，确认 inputSchema、安全提示和可用性
4. `tools run <tool> --input '...'` — 执行

### 错误处理

- `E_TOOL_NOT_FOUND_FOR_TARGET` → 工具在当前目标不可用，检查 `requiresPackages` 和 `available`
- `E_RUNTIME_NOT_READY` → Runtime 工具需要 Play Mode 活跃，检查 `target: "runtime"`
- `E_TARGET_AMBIGUOUS` → 多个实例可用，需要显式指定 `--target`
