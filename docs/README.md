# 文档索引

按"快速开始 / 使用 / 扩展 / 诊断 / 维护"组织的 alps-unity-cli 文档。

## 快速开始

| 文档 | 内容 |
|------|------|
| [Target 与 Instance](target-and-instance.md) | 实例发现、目标选择、别名管理 |
| [工具发现](tool-discovery.md) | group-first 分层发现流程 |

| Topic | Title | Discovery Path |
|-------|-------|----------------|
| [target-and-instance](./target-and-instance.md) | Targets and Instances | Start here |
| [tool-discovery](./tool-discovery.md) | Tool Discovery | `instances → tools group/group list/search/describe` |
| [tool-execution](./tool-execution.md) | Tool Execution | `tools describe → tools run → verify` |
| [availability-differences](./availability-differences.md) | Availability Differences | Why different instances show different tools |
| [error-codes](./error-codes.md) | Error Codes and next_actions | Structured error recovery |
| [risk-and-side-effects](./risk-and-side-effects.md) | Risk and Side Effects | `destructiveHint`, `readOnlyHint`, `--yes`, `--dry-run` |
| [critical-references](./critical-references/README.md) | C# Critical References | What was referenced, rejected, and adapted from unity-mcp |

## 使用

| 文档 | 内容 |
|------|------|
| [工具执行](tool-execution.md) | `tools run` 统一执行入口与执行通道 |
| [安全边界](safety-boundaries.md) | 风险模型、破坏性确认、fingerprint |
| [错误码](error-codes.md) | 结构化错误码与 `next_actions` |
| [可用性差异](availability-differences.md) | 不同实例/状态的工具有效性 |

## 扩展

| 文档 | 内容 |
|------|------|
| [自定义工具](custom-tools.md) | 项目侧统一 Tool 注册与 `target` 约束 |
| [自定义 Context](custom-contexts.md) | 项目侧只读 Context 注册 |
| [扩展包](extensions.md) | 扩展包使用与开发 |

## 维护

| 文档 | 内容 |
|------|------|
| [功能等价追踪](unity-mcp-functional-parity.md) | unity-mcp 操作能力到 unity_cli 的功能等价矩阵 |

## AI Agent 阅读顺序

1. 先读 `skills/unity_cli/SKILL.md` 了解操作流程
2. 需要细节时按主题查阅本目录
3. 示例代码见 `examples/`
4. Schema 定义见 `schemas/`

## 工具发现规则

工具发现以 `tools group` → `tools group list <group>` → `tools describe <tool>` 为主路径；`tools search <keyword>` 仅用于辅助定位。
