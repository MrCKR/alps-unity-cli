# 工具发现（Tool Discovery）

`unitycli` 的 Tool 目录属于一个已连接 Unity 实例。没有连接实例时，discovery 返回稳定 instance error，不返回静态或空目录。

## 实例绑定

每次调用可选传入 `--instance <name|alias|projectPath>`；显式 selector 优先于 CWD。未传时，CLI 只从当前 Unity 项目 CWD 解析目标，不保存 active target，也不猜测唯一存活实例。

```powershell
unitycli instances list
unitycli tools group --instance <name|alias|projectPath>
```

本文示例省略 `--instance` 时，默认命令运行在目标 Unity 项目 CWD。

## 推荐流程

```text
tools group
    -> tools group list <group> [--page-size <n>] [--cursor <nextCursor>]
    -> tools describe <tool>
    -> tools run <tool> --input '<json>'
```

不确定分组时使用 `unitycli tools search '<k1|k2|...>'`，多个关键词用 `|` 分隔按 OR 匹配。在PowerShell中必须用单引号包住整段关键词，否则`|`会被解释为shell管道。

### `tools group`

列出当前实例完整 Tool 目录中的分组与计数。分组目录是一次性完整返回，不受 discovery 分页约束，也没有 `pageInfo`：

```powershell
unitycli tools group
unitycli tools group --instance my-project
```

### `tools group list`

按名称稳定排序并返回一个真实分组中的 Tool 摘要页。默认 `pageSize=20`，最大 100：

```powershell
unitycli tools group list camera --page-size 20
unitycli tools group list probuilder --page-size 20 --cursor <nextCursor> --instance my-project
```

分组名称必须来自当前实例的`tools group`输出，不要假设项目能力都位于`custom`分组。每个`tools[].name`已经是完整精确调用名，group只用于筛选，绝不能拼到name前；`available`仅在不可用时以`false`出现。`pageInfo.nextCursor`是不透明的 generation-bound continuation，必须原值传回`--cursor`；目录 generation 改变时返回`E_TOOL_CATALOG_CHANGED`，应从第一页重新读取。

### `tools search`

按名称、summary、description 和 group 搜索当前实例完整目录，并只返回名称、summary、group组成的有界页。默认 `pageSize=20`，最大 100：

```powershell
unitycli tools search 'screenshot|camera' --page-size 20
unitycli tools search 'project-summary|scene' --page-size 20 --cursor <nextCursor> --instance my-project
```

结果按精确 Tool name 排序；存在`pageInfo.nextCursor`时，将其原值传回同一关键词查询的`--cursor`。cursor绑定原查询和当时的Editor/Runtime catalog generation，不能跨查询或跨generation复用。generation同时绑定当前editor session、listener和listener generation；调用过程中发生reload或listener交接时，旧cursor稳定返回`E_TOOL_CATALOG_CHANGED`。

### `tools describe`

执行前读取单个 Tool 的完整 descriptor：

```powershell
unitycli tools describe screenshot.from-camera
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

`group`和`summary`已在发现层使用，不在单 Tool describe重复；`source`、`outputSchema`、`permissions`、completion 和 handler 只保留在内部 metadata。

## 完整目录语义

一个已连接实例的 catalog 同时包含 package 内置能力、已安装 Extension 能力、项目 `IUnityCliTool<TReq,TRes>` 能力，以及 Editor、Runtime 和 package-local Tool。Unity 的 `[InitializeOnLoad]` 构造入口在每个 domain 内从编译元数据构建并完整验证一次 Tool table；后续 discovery、describe、run 和 Runtime availability 都只读取这个已发布的只读 table，不重新扫描 assembly、源码或持久化 Tool 状态。

`tools group`、`tools group list`、`tools search`和`tools describe`是冻结的顶层CLI控制面，不是可由`tools run`调用的Tool identity；业务动作与读取能力才使用`tools run <tool>`。每个 discovery root 对每个在线target只取得一次准入并读取一份listener-generation-bound snapshot。group/list/search只传输有界路由摘要，完整input schema、examples和风险字段仅由exact-name describe读取；output schema、permissions、completion和内部路由字段始终不进入summary/search传输。

目录不按 source、EditMode/PlayMode 或当前可执行性过滤。availability 在投影条目时读取，不会重建 table；当前不可执行的能力仍显示，由 descriptor 和执行错误说明原因。静态 capability snapshot 只供 package 生成、contract 和审计，不进入公开 discovery。

每次顶层`tools`或`command`调用只冻结一组私有 discovery provenance：exact editor session、listener ID和listener generation。执行前会把同一 provenance 带入 root admission，并在首次业务 POST前再次比较；若 session、listener或generation已变化，返回`E_TOOL_CATALOG_CHANGED`，不会发送业务请求。provenance不进入业务 input、公开成功响应或公开 error details。

## AI Agent 工作流

1. 用 `instances list` 确认 canonical project identity，或切换到目标 Unity 项目 CWD。
2. 用 `tools group` 读取当前实例目录。
3. 用 `tools group list`、`tools search '<k1|k2|...>'` 缩小范围，并消费完所需的`pageInfo.nextCursor`页面。
4. 用 `tools describe` 获取 schema、状态约束、副作用和示例。
5. 用 `tools run` 调用同一名称。
6. 安装 Package、安装 Extension、编译/domain reload 或 listener generation 替换后重新 discovery；仅切换 PlayMode 时读取更新后的 availability。

## 错误处理

- `E_INSTANCE_REQUIRED`：未传 selector，CWD 也不在 Unity 项目中。
- `E_INSTANCE_NOT_FOUND`：显式 selector 无法解析。
- `E_INSTANCE_NOT_RUNNING`：目标 identity 已确定，但 Unity Editor 未连接。
- `E_INSTANCE_CONFLICT`：同一项目存在至少两个已验证live Editor claim。
- `E_INSTANCE_UNVERIFIABLE`：PID/start或project ownership证据不完整，不能安全选定session。
- `E_UNITY_UNRESPONSIVE`：transport identity存在，但近期Unity主线程pulse无法证明。
- `E_TOOL_REGISTRY_UNHEALTHY`：实例 catalog 有 malformed、重复或 ownership 冲突；修复 Tool 定义并让 Unity compile/reload 后重新发现。该错误不表示 Bridge transport 不可达。
- `E_EXTENSION_INVENTORY_MISMATCH`：generated Extension owner、exact compiled assembly、implementation type与manifest contribution无法闭合；重新生成统一 inventory并让 Unity reload。
- `E_DISCOVERY_CURSOR_INVALID`：cursor损坏或来自其他查询；从当前查询第一页重新读取。
- `E_TOOL_CATALOG_CHANGED`：continuation绑定的catalog generation已改变；从当前查询第一页重新读取。
- `E_TOOL_TARGET_NOT_FOUND`：名称不属于当前实例目录；运行 `tools search <k1|k2|...>` 重新发现相关能力。
- `E_RUNTIME_UNAVAILABLE`：Runtime Tool 已显示，但执行需要稳定 PlayMode。
