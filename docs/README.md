# 文档索引

本目录只保留当前 `unitycli` 的使用、扩展与诊断文档。

## 快速开始

| 文档 | 内容 |
|------|------|
| [Target 与 Instance](target-and-instance.md) | 运行中 Unity Editor 实例发现、目标选择、别名管理 |
| [工具发现](tool-discovery.md) | `tools group` → `tools group list` → `tools describe` 的自发现流程 |
| [工具执行](tool-execution.md) | `tools run`的结构化JSON执行、输入与验证方式 |
| [Command执行](command-execution.md) | 同一Tool catalog的schema参数调用、名称选择与argv绑定 |
| [可用性差异](availability-differences.md) | 为什么不同 Editor、Package、Play Mode 状态下工具可用性不同 |

## 安全与诊断

| 文档 | 内容 |
|------|------|
| [安全边界](safety-boundaries.md) | 调用授权、风险元数据与正确性边界 |
| [风险与副作用](risk-and-side-effects.md) | `destructiveHint`、`supportsDryRun`、`plannedEffects` 与可选预览 |
| [错误码](error-codes.md) | 结构化错误、`nextActions` 与恢复建议 |

## 扩展

| 文档 | 内容 |
|------|------|
| [扩展包](extensions.md) | bundled optional extensions 的安装、卸载、诊断和依赖边界 |
| [自定义 Tool](../skills/unitycli/references/custom-tools.md) | 项目侧 `IUnityCliTool<TReq,TRes>` 注册、发现与执行 |
| [Skill 操作协议](../skills/unitycli/references/operation-protocol.md) | 实例选择、descriptor、envelope、分页、artifact、弹窗与 outcome unknown |
| [Skill 问题反馈](../skills/unitycli/references/feedback.md) | `FeedbackMode`、受限字段、本地滚动 JSONL 与回复摘要 |

## AI Agent 阅读顺序

1. 先读 `skills/unitycli/SKILL.md` 获取最小入口规则。
2. 需要命令细节时读`target-and-instance.md`、`tool-discovery.md`、`tool-execution.md`和`command-execution.md`。
3. 涉及写操作或高风险操作时读 `safety-boundaries.md` 与 `risk-and-side-effects.md`。
4. 需要项目扩展能力时读 `extensions.md`。

## 相关目录

- `schemas/`：公开 JSON Schema 契约。
- `examples/`：Editor、runtime、自定义 Tool 示例。
- `skills/unitycli/`：AI Agent 入口 Skill 与按需引文。
