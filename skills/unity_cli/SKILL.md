---
name: unity_cli
description: 所有涉及 Unity 或 Unity Editor 的任务都优先使用本 Skill，以便发现唯一可用的 `unity_cli` 控制入口。适用于 Scene、GameObject、Prefab、Asset、Package、Build、Test、GameView、SceneView、日志、截图、Bridge、instance、Editor/PlayMode 状态和项目自定义能力；纯源码或文本工作可直接处理，但需要观察、操作或验证已打开 Editor 时必须使用 CLI。
---

# unity_cli

FeedbackMode: off

`unity_cli` 是 AI 操作已打开 Unity Editor 的能力目录。不要记忆或猜能力：先发现，再读 descriptor，最后调用。

## 路由

- Tool 是动作，可能修改场景、资源、Package、Editor/PlayMode 或产生 artifact；Context 必须只读。
- 纯 `.cs`、JSON、YAML、manifest 或文档编辑可直接使用文件工具。需要 Unity 内存状态、导入结果、编译、物理、渲染或业务效果证明时使用 CLI。
- 公开顶层只有 `tools`、`context`、`init`、`extensions`、`target`、`instances`、`doctor`；Unity 能力不作为顶层命令。
- Extension 管理只用 `extensions list`、`extensions install`、`extensions remove`、`extensions doctor`。
- 已连接实例的目录同时包含 built-in、Extension、project、Editor 和 Runtime 能力，不按 `source` 或当前模式过滤。

## 执行骨架

```powershell
unity_cli instances list
unity_cli tools group --instance <id|projectPath|alias>
unity_cli tools group list <group> --instance <id|projectPath|alias>
unity_cli tools search <keyword> --instance <id|projectPath|alias>
unity_cli tools describe <tool> --instance <id|projectPath|alias>
unity_cli tools run <tool> --input '<json>' --instance <id|projectPath|alias>

unity_cli context list --instance <id|projectPath|alias>
unity_cli context search <keyword> --instance <id|projectPath|alias>
unity_cli context describe <context> --instance <id|projectPath|alias>
unity_cli context read <context> --input '<json>' --instance <id|projectPath|alias>
```

显式 `--instance` 优先；未传时只能从当前 Unity 项目 CWD 解析。没有连接实例时 discovery 返回稳定 instance error，不返回静态或空目录。执行不了解的 Tool/Context 前必须 `describe`，输入和行为以当前 descriptor 为准。

## 使用说明

- 调用即授权。`destructiveHint`、`sideEffects`、`supportsDryRun` 只描述效果；仅在确需预览时使用 `--dry-run`。Skill 不增加确认、意图复核、重放门或预定义操作范围。
- 建议通过 `instances`、`doctor`、`context read editor.state` 观察状态，不读取内部 Bridge 注册文件、端口或 URL，也不直接 ping transport。
- `E_EXECUTION_TIMEOUT`、连接丢失或缺少可信 terminal envelope 只表示没有可信终态。可以先用只读 Context 或业务 readback 观察；是否立即重试、重复调用或继续 mutation 完全由调用方决定。`transient:true` 与 `nextActions` 也只是建议，不构成权限或重放门。
- `available:false`、`unavailableReason` 和 `playModeSupport` 说明当前可执行性；`source` 不参与路由。
- blocker 在 `error.details.blockers` 时，可先读 `editor.dialogs`，再按返回的 dialogId 与 enabled button 调用 `editor.dialog.click`。
- 大输出、envelope、artifact、分页和 session 说明见 `references/operation-protocol.md`。
- 内置能力不足时，一次性操作可使用 `code.exec`；反复需要再读 `references/custom-tools.md` 或 `references/custom-contexts.md`。

## 问题反馈

仅当上方 marker 为 `on` 且本次实际遇到失败、歧义、缺失、误导或成功绕过的坑点时，读取 `references/feedback.md` 并调用 `scripts/record-feedback.ps1`。正常无摩擦调用不记录；marker 为 `off` 时不得运行脚本。

记录时只传简短结构化摘要，不传原始 prompt、完整 CLI 输入输出、源码、日志、绝对路径、URL、token、endpoint 或秘密。记录成功后才在正常回复末尾追加一段简短反馈摘要，包含类别、问题、workaround（如有）、recordId 和本地路径；这不是通用最终回复格式。脚本失败不改变 Unity 任务结论，但要说明反馈未写入。
