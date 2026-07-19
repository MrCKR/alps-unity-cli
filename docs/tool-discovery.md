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
      { "name": "scene", "summary": "...", "toolCount": 12, "source": "built-in", "cliCommand": "tools group list scene" },
      { "name": "object", "summary": "...", "toolCount": 2, "source": "built-in", "cliCommand": "tools group list object" },
      { "name": "reflection", "summary": "...", "toolCount": 2, "source": "built-in", "cliCommand": "tools group list reflection" }
    ]
  }
}
```

核心分组包含：scene、camera、go、component、object、reflection、type、asset、prefab、material、graphics、physics、profiler、shader、texture、animation、ui、script、editor、console、package、build、screenshot、code、diagnose。可选 extension 分组（probuilder/cinemachine/vfx/urp/hdrp）仅在目标项目安装对应 Unity 包时出现。

### 包含项目自定义工具分组

当 Bridge 可达时，`tools group` 会自动发现项目自定义工具的分组：

```powershell
unity_cli tools group --include-custom --instance <instanceId|projectPath|alias>
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

按关键词搜索工具名称、摘要、描述和分组。

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
| `summary` | 简短摘要 |
| `description` | 详细说明 |
| `group` | 所属分组 |
| `source` | 来源：`built-in` / `extension` / `project` |
| `inputSchema` | 输入参数 JSON Schema；TS Tool 来自 `defineTool`，C# Tool 来自 Request DTO 派生 |
| `outputSchema` | 输出格式 JSON Schema；TS Tool 来自 `defineTool`，C# Tool 来自 Result DTO 派生 |
| `destructiveHint` | 是否有破坏性 |
| `supportsDryRun` | 是否支持 `--dry-run` |
| `riskLevel` | 根据权限和副作用推导的风险等级 |
| `longRunning` | 是否长时间运行，仅长任务出现 |
| `cancellation` | 长任务取消语义：`cooperative` / `job`，无特殊取消语义时省略 |
| `supportStatus` | 支持状态 |
| `target` | 目标类型 |
| `availabilityScope` | 可用性范围 |
| `playModeSupport` | Play Mode 行为：`supported` / `runtime-only` / `session-affecting` / `editor-only` / `unknown` |
| `requiresPackages` | 依赖的 Unity 包 |
| `examples` | 使用示例 |
| `permissions` | 所需权限，仅需要特殊权限时出现 |
| `cliCommand` | 可直接执行的 CLI 命令 |

### 自定义工具描述

当工具名不在内置列表中时，先通过 `--include-custom` 让 CLI 查询目标 Unity Bridge 中的项目自定义工具：

```powershell
unity_cli tools group list <real-group> --include-custom
unity_cli tools describe my.custom-tool --include-custom
```

## Descriptor 字段用途

### 安全判断

- `destructiveHint: true` → 执行前必须确认风险、使用 `--dry-run` 或 `--yes`

### 执行通道判断

- `supportStatus: "live-bridge"` → 实时 Bridge 执行
- `supportStatus: "live-bridge-runtime"` → 需要 Play Mode 的 Runtime 工具
- `supportStatus: "bridge-job"` → 支持 Bridge + job 双通道
- `supportStatus: "batch-job"` → batchmode/job 执行
- `supportStatus: "cli-only"` → 本地 CLI 执行

### 可用性判断

- `target: "editor"` → 编辑器主线程可用
- `target: "runtime"` → Play Mode 下可用
- `target: "both"` → 编辑器和运行时都可用
- `availabilityScope` → 详细可用性范围
- `playModeSupport: "supported"` → Play Mode 下语义基本不变
- `playModeSupport: "runtime-only"` → Play Mode 可执行，但结果只影响当前运行会话
- `playModeSupport: "session-affecting"` → 可能触发 import、compile、domain reload 或 Play Mode 状态变化
- `playModeSupport: "editor-only"` → Play Mode 下不可执行，返回 `E_TOOL_PLAYMODE_UNAVAILABLE`
- `extensionAvailability` / `missingPackageIds` → 扩展依赖在当前项目中的可用性（仅扩展工具缺依赖时出现）
- `requiresPackages` → 需要安装的 Unity 包

## AI Agent 发现工作流

1. `tools group` — 了解能力分类
2. `tools group list <relevant-group>` — 查看相关分组工具
3. `tools describe <tool>` — 获取完整 descriptor，确认 inputSchema、安全提示和可用性
4. `tools run <tool> --input '...'` — 执行

### 错误处理

- `E_TOOL_NOT_FOUND_FOR_TARGET` → 工具在当前目标不可用，检查 `requiresPackages`、`extensionAvailability` 和目标实例状态
- `E_RUNTIME_NOT_READY` → Runtime 工具需要 Play Mode 活跃，检查 `target: "runtime"`
- `E_INSTANCE_CONFLICT` -> 同一 canonical project 存在多个 Unity Editor process/session claim，关闭重复 Editor 后再执行
- `E_INSTANCE_NOT_RUNNING` -> 选定项目没有可验证的运行中 Editor；静态内置/Extension catalog 仍可离线发现
