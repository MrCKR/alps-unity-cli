---
name: unity_cli
description: 所有涉及 Unity 或 Unity Editor 或 UnityPipeline 的任务都优先使用本 Skill，以便发现唯一可用的 `unity_cli` 控制入口。适用于 Scene、GameObject、Prefab、Asset、Package、Build、Test、GameView、SceneView、日志、截图、Bridge、instance、Editor/PlayMode 状态和项目自定义能力；纯源码或文本工作可直接处理，但需要观察、操作或验证已打开 Editor 时必须使用 CLI。
---

# unity_cli

FeedbackMode: off

`unity_cli` 是 AI 操作已打开 Unity Editor 的能力目录。不要记忆或猜能力：先发现，再读 descriptor，最后调用。

## 路由

- 所有Unity动作与观察能力都属于Tool；读取型Tool必须纯只读并准确声明空副作用。
- 纯 `.cs`、JSON、YAML、manifest 或文档编辑可直接使用文件工具。需要 Unity 内存状态、导入结果、编译、物理、渲染或业务效果证明时使用 CLI。
- 公开顶层只有`tools`、`command`、`init`、`extensions`、`target`、`instances`、`doctor`；Unity能力域不作为顶层命令。
- auth、Unity Hub、Editor安装或打开、Cloud、License、proxy配置属于外部控制面；直接使用系统或官方工具，不在catalog中猜测替代Tool。
- Extension 管理只用 `extensions list`、`extensions install`、`extensions remove`、`extensions doctor`。
- 已连接实例的目录同时包含 built-in、Extension、project、Editor 和 Runtime 能力，不按 `source` 或当前模式过滤。
- 完全兼容 Unity 官方 CLI 特性体系：项目与第三方 Package 中以官方 `[CliCommand]`/`[CliArg]`/`IStructuredCommandInput` 声明的命令直接进入同一 catalog，固定投影为 `UnityPipeline_<原名>`、group `UnityPipeline`，用 `command <原名>` 或完整前缀名调用；`com.unity.pipeline` 包自身命令已由 native 能力吸收并按 provider identity 抑制。native 精确重名优先。

## 执行骨架

```powershell
unity_cli instances list
unity_cli tools group --instance <id|projectPath|alias>
unity_cli tools group list <group> --instance <id|projectPath|alias>
unity_cli tools search <keyword> --instance <id|projectPath|alias>
unity_cli tools describe <tool> --instance <id|projectPath|alias>
unity_cli tools run <tool> --input '<json>' --instance <id|projectPath|alias>

# 同一catalog的schema参数调用；省略name时列出command投影
unity_cli command --project-path <projectPath>
unity_cli command <name> --field <value> --instance <id|projectPath|alias>
```

显式`--instance`优先；`command --project-path <path>`绑定同一canonical项目实例；未传时只能从当前Unity项目CWD解析。没有连接实例时discovery返回稳定instance error，不返回静态或空目录。执行不了解的Tool前必须`tools describe`，输入和行为以当前descriptor为准。schema default与`examples[].argv`描述合法默认行为和调用形状；任务明确要求的业务值优先，不能把default/example当作连通性或probe调用。`tools group list`/`tools search`返回的Tool `name`就是exact调用名；group只用于筛选，不能拼到name前。

`command`不建立第二套目录：native Tool使用完整精确名；Unity官方`[CliCommand]`完全兼容，项目或其他Package的`[CliCommand("test")]`在`tools`中固定名为`UnityPipeline_test`、group为`UnityPipeline`，可由`command test`或完整前缀名选择。`com.unity.pipeline` package自身命令已由native能力吸收并按provider identity抑制，不应期待其attribute原名存在。native精确重名优先；不得把不存在的名称映射到相似Tool。业务参数支持`--field value`、`--field=value`、boolean裸开关和按schema声明顺序的positional。

## 使用说明

- 调用即授权。`destructiveHint`、`sideEffects`、`supportsDryRun` 只描述效果；仅在确需预览时使用 `--dry-run`。Skill 不增加确认、意图复核、重放门或预定义操作范围。
- 建议通过 `instances`、`doctor`、`tools run editor.state` 观察状态，不读取内部 Bridge 注册文件、端口或 URL，也不直接 ping transport。
- `E_EXECUTION_TIMEOUT`、连接丢失或缺少可信 terminal envelope 只表示没有可信终态。可以先用只读 Tool 或业务 readback 观察；是否立即重试、重复调用或继续 mutation 完全由调用方决定。`transient:true` 与 `nextActions` 也只是建议，不构成权限或重放门。
- `available:false`、`unavailableReason` 和 `playModeSupport` 说明当前可执行性；`source` 不参与路由。
- blocker 在 `error.details.blockers` 时，可先读 `editor.dialogs`，再按返回的 dialogId 与 enabled button 调用 `editor.dialog.click`。
- 大输出、envelope、artifact、分页和 session 说明见 `references/operation-protocol.md`。
- 内置能力不足时，一次性操作可使用 `code.exec`；反复需要再读 `references/custom-tools.md`。`code.exec`不限制提交的同步C#，也不会拒绝死循环、长时间sleep、阻塞等待、锁等待或长期占用Unity API的代码。提交这类代码前必须先判断它对当前结果是否确有必要，并考虑有界写法或现有Tool能否达到同一结果；确有必要时可以原样执行。代码一旦在Unity主线程开始，`--timeout-ms`只能停止CLI等待，不能中止代码；Unity可能无响应、mutation owner不能收尾、业务状态部分改变或未知，恢复可能需要结束Editor进程。timeout不证明代码已停止，重试前必须观察业务状态。`--dry-run`只预览source hash，不编译、不执行，也不证明代码会结束。

## 问题反馈

仅当上方 marker 为 `on` 且本次实际遇到失败、歧义、缺失、误导或成功绕过的坑点时，读取 `references/feedback.md` 并调用 `scripts/record-feedback.ps1`。正常无摩擦调用不记录；marker 为 `off` 时不得运行脚本。

记录时只传简短结构化摘要，不传原始 prompt、完整 CLI 输入输出、源码、日志、绝对路径、URL、token、endpoint 或秘密。记录成功后才在正常回复末尾追加一段简短反馈摘要，包含类别、问题、workaround（如有）、recordId 和本地路径；这不是通用最终回复格式。脚本失败不改变 Unity 任务结论，但要说明反馈未写入。
