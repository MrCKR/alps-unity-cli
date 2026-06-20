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
| [自定义工具](custom-tools.md) | 项目侧 Editor Tool / Runtime Tool 注册 |
| [自定义 Context](custom-contexts.md) | 项目侧只读 Context 注册（规划中） |
| [扩展包](extensions.md) | 扩展包使用与开发（规划中） |

## 维护

| 文档 | 内容 |
|------|------|
| [覆盖率矩阵](coverage-matrix.md) | v1.0 归档：功能域覆盖状态 |
| [风险登记册](risk-register-v1.0.md) | v1.0 归档：已知风险与缓解措施 |

> **归档文档**标记为 v1.0 archive，不代表当前用户指南。请以主题索引中的文档为准。

## AI Agent 阅读顺序

1. 先读 `skills/unity_cli/SKILL.md` 了解操作流程
2. 需要细节时按主题查阅本目录
3. 示例代码见 `examples/`
4. Schema 定义见 `schemas/`

## 废弃路径说明

以下发现路径已废弃，不得作为使用指导：

| 废弃路径 | 替代路径 |
|----------|---------|
| `tools list` 无过滤全量展开 | `tools group` → `tools group list <group>` 分层发现 |
| `tools list --include-custom` 作为首选发现 | 先 `tools group` 确认分组，再 `tools group list <group> --include-custom` |
| `tools groups` | `tools group`（`tools groups` 已移除，返回 E_TOOLS_LIST_REMOVED） |
| `tools search` 作为唯一发现手段 | `tools search` 仅作为辅助搜索，不应替代 group-first 流程 |

> **注意**：`tools list` 和 `tools groups` 命令已被移除，调用时返回 `E_TOOLS_LIST_REMOVED` 引导错误。请使用 `tools group` + `tools group list <group>` 的 group-first 分层发现。
