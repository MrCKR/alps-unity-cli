# 工具发现（Tool Discovery）

`unitycli` 的 Tool 目录属于一个已连接 Unity 实例。没有连接实例时，discovery 返回稳定 instance error，不返回静态或空目录。

## 实例绑定

每次调用可选传入 `--instance <full-instance-id|project-path|alias>`；显式 selector 优先于 CWD。未传时，CLI 只从当前 Unity 项目 CWD 解析目标，不保存 active target，也不猜测唯一存活实例。

```powershell
unitycli instances list
unitycli tools group --instance <id|projectPath|alias>
```

本文示例省略 `--instance` 时，默认命令运行在目标 Unity 项目 CWD。

## 推荐流程

```text
tools group
    -> tools group list <group>
    -> tools describe <tool>
    -> tools run <tool> --input '<json>'
```

不确定分组时使用 `unitycli tools search '<k1|k2|...>'`，多个关键词用 `|` 分隔按 OR 匹配。在PowerShell中必须用单引号包住整段关键词，否则`|`会被解释为shell管道。

### `tools group`

列出当前实例完整 Tool 目录中的分组与计数：

```powershell
unitycli tools group
unitycli tools group --instance my-project
```

### `tools group list`

列出一个真实分组中的全部 Tool 摘要：

```powershell
unitycli tools group list camera
unitycli tools group list probuilder --instance my-project
```

分组名称必须来自当前实例的`tools group`输出，不要假设项目能力都位于`custom`分组。`tools group list`返回`nameSemantics.exact=true`与`groupPrefix=false`：每个`tools[].name`已经是完整精确调用名，group只用于筛选，绝不能拼到name前。

### `tools search`

按名称、summary、description 和 group 搜索当前实例完整目录：

```powershell
unitycli tools search 'screenshot|camera'
unitycli tools search 'project-summary|scene' --instance my-project
```

### `tools describe`

执行前读取单个 Tool 的完整 descriptor：

```powershell
unitycli tools describe camera.screenshot
```

公开字段：

| 字段 | 说明 |
|------|------|
| `name` / `description` | 精确名称、调用边界、默认行为与主要结果 |
| `inputSchema` | 完整输入 JSON Schema；结果 schema 由内部 contract 校验，不进入 describe |
| `destructiveHint` / `sideEffects` | 效果说明，不构成执行门；空 sideEffects 省略 |
| `supportsDryRun` | 是否支持可选 `--dry-run` |
| `target` / `playModeSupport` | Editor、Runtime 路由与 PlayMode 语义 |
| `available:false` / `unavailableReason` | 仅当前不可执行时返回；字段缺失表示当前可用 |
| `requiresPackages` | 非空 Unity Package 依赖 |
| `examples` | `description`、结构化业务 `input` 与按需 `execution` |

`group`、`summary` 和 `source` 已在发现层使用，不在单 Tool describe 重复；`outputSchema`、`permissions`、fingerprint、completion 和 handler 只保留在内部 metadata。

## 完整目录语义

一个已连接实例的 catalog 同时包含 package 内置能力、已安装 Extension 能力、项目 `IUnityCliTool<TReq,TRes>` 能力，以及 Editor、Runtime 和 package-local Tool。动作与读取能力都使用同一 `tools group/list/search/describe/run` 路径；读取型 Tool 通过无副作用 descriptor 明确其边界。

目录不按 source、EditMode/PlayMode 或当前可执行性过滤。当前不可执行的能力仍显示，由 descriptor 和执行错误说明原因。静态 capability snapshot 只供 package 生成、contract 和审计，不进入公开 discovery。

## AI Agent 工作流

1. 用 `instances list` 确认 canonical project identity，或切换到目标 Unity 项目 CWD。
2. 用 `tools group` 读取当前实例目录。
3. 用 `tools group list`、`tools search '<k1|k2|...>'` 缩小范围。
4. 用 `tools describe` 获取 schema、状态约束、副作用和示例。
5. 用 `tools run` 调用同一名称。
6. 安装 Package、安装 Extension、domain reload 或切换 PlayMode 后重新 discovery。

## 错误处理

- `E_INSTANCE_REQUIRED`：未传 selector，CWD 也不在 Unity 项目中。
- `E_INSTANCE_NOT_FOUND`：显式 selector 无法解析。
- `E_INSTANCE_NOT_RUNNING`：目标 identity 已确定，但 Unity Editor 未连接。
- `E_INSTANCE_CONFLICT`：同一项目存在至少两个已验证live Editor claim。
- `E_INSTANCE_UNVERIFIABLE`：PID/start或project ownership证据不完整，不能安全选定session。
- `E_UNITY_UNRESPONSIVE`：transport identity存在，但近期Unity主线程pulse无法证明。
- `E_TOOL_REGISTRY_UNHEALTHY`：实例 catalog 有 malformed、重复或 ownership 冲突；修复 registry 后重新发现。
- `E_TOOL_NOT_FOUND_FOR_TARGET`：名称不属于当前实例目录；运行 `tools search <k1|k2|...>` 重新发现相关能力。
- `E_RUNTIME_NOT_READY`：Runtime Tool 已显示，但执行需要稳定 PlayMode。
