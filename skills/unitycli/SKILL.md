---
name: unitycli
description: 所有涉及 Unity、Unity Editor 或 UnityPipeline 的任务都优先使用本 Skill，用于判断直接处理项目文件，或通过`unitycli` 入口发现当前 Editor 能力。
---
# UnityCLI

FeedbackMode: off

`unitycli` 是 AI 操作已打开 Unity Editor 的运行时导航入口，不是固定工具手册。根据任务发现当前 catalog，读取所选 descriptor，再构造调用。

用户明确要求安装、更新或管理扩展，或需要诊断扩展状态时，读取 [安装、更新与扩展管理](references/installation.md)。确认当前任务确实需要且 CLI 或 Bridge 未安装时，可以说明缺失组件与安装范围，询问是否安装；用户明确同意后按本次安装指令进入该流程。已有明确指令不重复询问，不因缺失自动安装、更新或联网检查版本。连接失败、Editor 未运行或暂时不可用不代表未安装。

## 使用方法

### 1. 确定操作与目标

普通源码或文本直接使用文件工具；需要 Unity 内存、Editor API、导入/编译、PlayMode 或业务效果证明时使用 CLI。将复杂任务拆成具体的观察与操作，允许多个 Tool 组合完成。

用户已给出目标或当前 Unity 项目 CWD 能唯一解析时可以直接使用。目标未知、需要审计当前连接或错误要求检查实例时，执行：

```powershell
unitycli instances list
```

需要 selector 时优先只传一种；使用 `--instance <id|projectPath|alias>` 或 `command --project-path <projectPath>`，冲突的两种 selector 会失败。Unity 动作与观察能力都从同一 Tool catalog 发现。公开顶层只有 `tools`、`command`、`init`、`extensions`、`target`、`instances`、`doctor`；不要猜 Unity 能力域是顶层命令。

### 2. 首次读取能力分组

当前会话首次发现或调用目标项目的 Unity 能力前，必须先执行 `tools group`，阅读分组名、summary 和工具数量，建立该项目的能力概览：

```powershell
unitycli tools group --instance <id|projectPath|alias>
```

同一目标的概览仍在上下文且目录未发生变化时复用，不必每次任务重复读取。切换项目、概览已不在上下文，或发生影响目录的 Package/Extension 安装、编译、重载后，重新执行 `tools group`。仅切换 PlayMode 时读取更新后的 availability。

安装、更新、连接或弹窗故障诊断不受此顺序限制；先处理阻塞，目标可用后再读取分组。

### 3. 查找并确认工具

根据分组概览选择相关领域浏览，或提取业务关键词搜索，得到候选后 describe：

```powershell
unitycli tools group list <group> --instance <id|projectPath|alias>
unitycli tools search '<k1|k2|...>' --instance <id|projectPath|alias>
unitycli tools describe <tool> --instance <id|projectPath|alias>
```

- 分组名取自当前概览。返回的 `name` 已是完整调用名，group 和 `source` 不参与名称拼接或 owner 路由。
- search 对名称、summary、description 和 group 做文本包含匹配，`|` 表示 OR；提取 Unity 对象、动作的英文术语及少量同义词，不把整句自然语言当作语义搜索。
- 搜索未找到合适候选时，按概览选择相关分组，用 `tools group list <group>` 补查。结果太宽时缩小关键词或改查相关分组，不连续猜词或遍历整个目录。
- group list 和 search 按 Tool 名排序，不按相关度排序。有 `pageInfo.nextCursor` 表示还有结果；找到候选即可 describe，判定相关分组没有合适能力前须检查剩余页。续页保持原查询，原值传回 `--cursor`。
- 第一次执行不了解的 Tool 前先 describe，核对作用对象、操作效果和状态约束。优先使用满足需求的专用 Tool 或组合，确认适用后停止发现。
- `available:false` 表示能力存在但当前不可用，按原因处理；连接、编译或目录错误表示发现受阻。单次搜索未命中、只看第一页或发现失败，都不能作为“没有工具”的依据；覆盖不足时只说明“当前未找到”。

### 4. 执行并检查结果

```powershell
unitycli tools run <tool> --input '<json>' --instance <id|projectPath|alias>
unitycli command <name> --field <value> --instance <id|projectPath|alias>
```

`tools run` 与 `command` 复用同一 descriptor 和 schema。`[CliCommand("name")]` 投影为 `UnityPipeline_name`，也可由 `command name` 选择；native exact name 优先。省略 name 的 `unitycli command --project-path <projectPath>` 可按需列出该目标的命令。

PowerShell 中以单引号包住内联 JSON 和 `tools search '<k1|k2|...>'`。任务明确要求的业务值优先于 schema default 和 example；default/example只说明合法形状，不能把default/example当作连通性或probe调用，也不作为 smoke 调用。分页、artifact、大输出、弹窗和 host 等待按需读取 `references/operation-protocol.md`。

等待 CLI 进程退出，`ok:true` 后检查任务相关的关键业务字段；结果不足或任务明确要求验证时，再使用只读 Tool 或业务 readback。

## 通用规则

- descriptor 是输入、风险、availability、PlayMode 和依赖的当前契约；根据任务决定是否切换状态、预览或改用其他能力。
- 调用公开 Tool 或 CLI 控制面即为授权。风险 metadata 只描述效果与可选 dry-run；Skill 不增加确认或预定义操作范围。
- `--timeout-ms` 应为 host 保留至少 5000 ms 的进程退出时间。不得手工每100毫秒循环轮询；超时不会停止 Unity、已开始的 effect 或 C# 工作。
- `instances list` 默认过滤 offline 实例并只列出可用实例；需要完整盘点时使用 `instances list --all`，并读取 `invalidRecordCount`。旧、损坏或不可读记录由 CLI 隔离，不要读取、删除或改写内部记录文件；需要清理时使用 `instances prune --instance <id>`，依据返回的 `removed`、`deferred`、`remaining` 计数。启动、编译或 reload 后只等待有界 readiness。
- 失败时根据结构化 error 和 `nextActions` 形成恢复决策。`transient:true` 与 `nextActions` 仍是建议，不构成额外授权门。
- `E_EXECUTION_TIMEOUT`、放行后连接丢失或缺少可信 terminal envelope只表示没有可信终态。对于`asset.refresh`、player build、Play Mode、Package install/remove/update和`test.run`，先按需发现对应域的`.status` Tool并只传原root `requestId`读取同一Editor session的active phase或exact terminal；status不重放effect。其他能力使用只读Tool或业务readback形成新的业务决策；CLI不自动重放，首次mutation POST后Editor session改变时也不会向新session重投。
- 状态观察使用 `instances`、`doctor` 或 `editor.state`，不操作内部 registry 或 transport。
- `auth`、Unity Hub、Cloud、License 和 proxy 属于外部控制面，不由 `unitycli` 代管。

## 能力扩展

普通源码或文本仍可直接处理。需要 Unity 侧执行时，先按上述规则检查相关候选和分组分页；只有专用 Tool 或组合无法满足需求时，才考虑 `code.execute` 或项目自定义 Tool。发现受阻时先处理阻塞，不以扩展能力代替尚未完成的发现。

有界的临时 Unity 内存或 Editor API 逻辑可以考虑 `code.execute`。同步 C# 开始后不能由 CLI timeout 中止，复杂等待或不确定终态先读 `references/operation-protocol.md`。

当能力需要稳定发现、typed schema、结构化结果、明确副作用、重复使用或团队共享时，读取 `references/custom-tools.md` 并实现项目自定义 `IUnityCliTool<TReq,TRes>`。自定义 Tool 仍从当前 catalog 发现、describe 和调用，不假设名称、group 或输入。

## 问题反馈

仅当上方 marker 为 `on` 且本次实际遇到失败、歧义、缺失、误导或成功绕过的坑点时，读取 `references/feedback.md` 并调用 `scripts/record-feedback.ps1`。正常无摩擦调用不记录；marker 为 `off` 时不得运行脚本。

记录时只传简短结构化摘要，不传原始 prompt、完整 CLI 输入输出、源码、日志、绝对路径、URL、token、endpoint 或秘密。记录成功后才在正常回复末尾追加一段简短反馈摘要，包含类别、问题、workaround（如有）、recordId 和本地路径；这不是通用最终回复格式。脚本失败不改变 Unity 任务结论，但要说明反馈未写入。
