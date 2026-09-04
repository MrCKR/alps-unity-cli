---
name: unitycli
description: 所有涉及 Unity、Unity Editor 或 UnityPipeline 的任务都优先使用本 Skill，用于判断直接处理项目文件，或通过`unitycli` 入口发现当前 Editor 能力。
---
# UnityCLI

FeedbackMode: off

`unitycli` 是 AI 操作已打开 Unity Editor 的运行时导航入口，不是固定工具手册。根据任务发现当前 catalog，读取所选 descriptor，再构造调用。

## 路由

- 需要 Unity 内存、Editor API、导入/编译、PlayMode 或业务效果证明时使用 CLI。
- Unity 动作与观察能力都从同一 Tool catalog 发现。公开顶层只有 `tools`、`command`、`init`、`extensions`、`target`、`instances`、`doctor`；不要猜 Unity 能力域是顶层命令。
- 不知道能力领域时可看 group，已有业务关键词时可 search，得到 exact Tool 名后 describe。返回的 `name` 是调用名，group 和 `source` 不参与名称拼接或 owner 路由。
- `tools run` 与 `command` 复用同一 descriptor 和 schema。`[CliCommand("name")]` 投影为 `UnityPipeline_name`，也可由 `command name` 选择；native exact name 优先。

## 发现与调用

```powershell
# 目标未知、需要审计当前连接或错误要求检查实例时
unitycli instances list

unitycli tools group --instance <id|projectPath|alias>
unitycli tools group list <group> --instance <id|projectPath|alias>
unitycli tools search '<k1|k2|...>' --instance <id|projectPath|alias>
unitycli tools describe <tool> --instance <id|projectPath|alias>
unitycli tools run <tool> --input '<json>' --instance <id|projectPath|alias>
unitycli extensions list
unitycli extensions install <extension>
unitycli extensions remove <extension>
unitycli extensions doctor

unitycli command --project-path <projectPath>
unitycli command <name> --field <value> --instance <id|projectPath|alias>
```

用户已给出目标或当前 Unity 项目 CWD 能唯一解析时可以直接使用；需要 selector 时优先只传一种，冲突的 `--instance` 与 `command --project-path` 会失败。不了解能力领域时可先看 group，已有业务关键词时可直接 search，第一次执行不了解的 Tool 前先 describe。

PowerShell 中以单引号包住内联 JSON 和 `tools search '<k1|k2|...>'`。任务明确要求的业务值优先于 schema default 和 example；default/example只说明合法形状，不作为 connectivity、smoke 或 probe 调用。分页、artifact、大输出、弹窗和 host 等待按需读取 `references/operation-protocol.md`。

- 等待 CLI 进程退出；`--timeout-ms` 应为 host 保留至少 5000 ms 的进程退出时间。不得手工每100毫秒循环轮询；超时不会停止 Unity、已开始的 effect 或 C# 工作。
- `instances list` 默认过滤 offline 实例并只列出可用实例；需要完整盘点时使用 `instances list --all`，并读取 `invalidRecordCount`。旧、损坏或不可读记录由 CLI 隔离，不要读取、删除或改写内部记录文件；需要清理时使用 `instances prune --instance <id>`，依据返回的 `removed`、`deferred`、`remaining` 计数。启动、编译或 reload 后只等待有界 readiness。
- `extensions list` 查看浅状态，`extensions install/remove` 管理安装，`extensions doctor` 执行完整检查；不要把 extension 文件系统检查当成普通 Tool 执行的隐式 probe。
- `auth`、Unity Hub、Cloud、License 和 proxy 属于外部控制面，不由 `unitycli` 代管。

## 通用规则

- descriptor 是输入、风险、availability、PlayMode 和依赖的当前契约；根据任务决定是否切换状态、预览或改用其他能力。
- 调用公开 Tool 或 CLI 控制面即为授权。风险 metadata 只描述效果与可选 dry-run；Skill 不增加确认或预定义操作范围。
- 等待 CLI 进程退出。`ok:true` 后检查任务相关的关键业务字段；返回结果不足或任务明确要求验证时，再使用只读 Tool 或业务 readback。
- 失败时根据结构化 error 和 `nextActions` 形成恢复决策。`transient:true` 与 `nextActions` 仍是建议，不构成额外授权门。
- `E_EXECUTION_TIMEOUT`、放行后连接丢失或缺少可信 terminal envelope只表示没有可信终态。先用只读Tool或业务readback观察并形成新的业务决策；CLI不自动重放，首次mutation POST后Editor session改变时也不会向新session重投。
- 状态观察使用 `instances`、`doctor` 或 `editor.state`，不操作内部 registry 或 transport。

## 能力扩展

现有 Tool 不适合时，先判断是否真的需要 Unity 侧执行。普通源码或文本仍可直接处理；有界的临时 Unity 内存或 Editor API 逻辑可以考虑 `code.exec`。同步 C# 开始后不能由 CLI timeout 中止，复杂等待或不确定终态先读 `references/operation-protocol.md`。

当能力需要稳定发现、typed schema、结构化结果、明确副作用、重复使用或团队共享时，读取 `references/custom-tools.md` 并实现项目自定义 `IUnityCliTool<TReq,TRes>`。自定义 Tool 仍从当前 catalog 发现、describe 和调用，不假设名称、group 或输入。

## 问题反馈

仅当上方 marker 为 `on` 且本次实际遇到失败、歧义、缺失、误导或成功绕过的坑点时，读取 `references/feedback.md` 并调用 `scripts/record-feedback.ps1`。正常无摩擦调用不记录；marker 为 `off` 时不得运行脚本。

记录时只传简短结构化摘要，不传原始 prompt、完整 CLI 输入输出、源码、日志、绝对路径、URL、token、endpoint 或秘密。记录成功后才在正常回复末尾追加一段简短反馈摘要，包含类别、问题、workaround（如有）、recordId 和本地路径；这不是通用最终回复格式。脚本失败不改变 Unity 任务结论，但要说明反馈未写入。
