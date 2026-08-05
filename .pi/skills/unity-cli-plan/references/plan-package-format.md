# Plan Package Format

创建、更新、迁移或恢复计划时使用本格式。计划文件夹名必须稳定、可读、无日期，并在同一目标后续更新时复用。

## 目录结构

```text
.plan/<stable-short-name>/
├── plan.md
├── context.md
├── files.md
├── decisions.md
├── execution-log.md
├── directions/
│   └── 001-<direction-name>.md
└── evidence/
    ├── index.md
    ├── static/
    ├── live/
    └── gates/
```

大型 artifact 可以保存在 `evidence/` 的子目录；`index.md` 必须记录其 hash、size、source stamp 和状态。迁移旧计划时不批量移动旧 evidence，先以外部路径登记；迁移后的新 evidence 必须写入计划包。

## 状态值

计划状态只允许：

- `draft`
- `active`
- `paused`
- `blocked`
- `completed`
- `superseded`

Direction 状态只允许：

- `pending`
- `active`
- `completed`
- `blocked`
- `decision-required`
- `drift`

计划为 `active` 时恰有一个 active direction，`CurrentDirection` 必须指向它。其他计划状态的 `CurrentDirection` 必须为 `none`，且不能存在 active direction。

## plan.md

```md
# <计划名称>

Status: active
CurrentDirection: directions/001-<direction>.md
SourceOfTruth: <用户请求、Issue 或治理文件>

## 目标

<一句可验证的最终目标。>

## 目标来源

- <来源和原文引用>

## 已确认决策

- <只列当前有效决策；细节链接 decisions.md>

## 明确不做

- <非目标和禁止扩张>

## 要求映射

| ID | 来源 | 要求 | 完成证据 |
|---|---|---|---|
| R1 | <ref> | <requirement> | <pending/path> |

## 架构与公开契约

<最终状态，不写迁移过程。>

## 路线缓存

- <未来方向，只描述结果，不表示已授权执行>

## 验证原则

- <静态、Live、gate 和 evidence 规则>

## 清理要求

- <fixture、artifact、进程、临时文件>

## 完成条件

- <全部可判定条件>
```

`plan.md` 不保存 PID、session、命令流水、历史失败过程或旧实现说明。

## context.md

```md
# Current Context

CapturedAt: <UTC ISO-8601>
PlanStatus: active
CurrentDirection: directions/001-<direction>.md
NextSafeAction: <一个可执行且不越界的下一动作>

## Repository

- Branch: <name>
- HEAD: <sha>
- Worktree: <summary>
- UserChanges: <paths/unknown>

## Required Skills

- <skill path and reason>

## Runtime Environment

- Project: <stable project path>
- UnityVersion: <current fact or not-observed>
- Instance: <current fact or not-observed>
- Process: <current fact or not-observed>
- Session: <current fact or not-observed>
- ObservedAt: <timestamp>

## Evidence State

- CurrentStamp: <stamp or none>
- CurrentValid: <paths or none>
- Stale: <paths>
- Failed: <paths>
- Blocked: <paths/reason>

## Safety State

- MustNotReplay: <request IDs or none>
- UnknownRoots: <resource and readback>
- UnrelatedProcesses: <PID/project or none>

## Open Decisions

- <decision or none>

## Current WIP

- <files, fixtures and uncommitted semantic state>
```

恢复任务时先 fresh 核验动态字段，再重写本文件。旧值进入 execution log，不保留在当前上下文。

## files.md

```md
# File Manifest

## 文件清单

| Path / Glob | Category | Requirement | Allowed Change | Sync Owner | Ownership | Status |
|---|---|---|---|---|---|---|
| src/example.ts | 产品文件 | R1 | <delta> | tests/example.test.ts | task | planned |

## 分类

- 产品文件
- 生成文件
- contract/schema/catalog
- tests
- docs/Skill/AGENTS
- 删除文件
- 条件发现文件
- 禁止触碰文件
- 其他任务或所有权未知

## 禁止触碰

- <path and reason>

## 条件发现规则

- <允许搜索的边界和登记要求>

## 删除 successor mapping

| Deleted Path | Replacement Coverage | Evidence |
|---|---|---|
| <path> | <new owner/test> | <path> |
```

未登记文件不可编辑。`Status` 可使用 `planned`、`touched`、`verified`、`deleted`、`excluded`、`decision-required`。

## decisions.md

```md
# Owner Decisions

## D001 <决策名称>

Status: active
DecidedAt: <UTC ISO-8601>
Decision: <当前决定>
Reason: <原因>
Scope: <受影响要求、能力和文件>
Supersedes: none
Evidence: <对话、Issue 或审计路径>
```

公开契约、能力范围、版本支持、proof 语义和验证范围必须有 owner decision。被替代决定保留历史，但标记 `superseded` 并指向新决定。

## execution-log.md

```md
# Execution Log

## <UTC ISO-8601> | <direction-or-plan> | <event-type>

- Trigger: <发生原因>
- Requirement: <requirement ID/ref>
- Action: <工程事件摘要>
- Files: <paths or none>
- Validation: <terminal result or not-run>
- FailureClass: <class or none>
- DriftResult: <PASS/DECISION_REQUIRED/DRIFT/BLOCKED>
- Evidence: <relative paths or none>
- NextSafeAction: <下一安全动作>
```

日志 append-only。工程事件类型包括 `plan-created`、`migration-baseline`、`plan-resumed`、`direction-started`、`change-set`、`validation-terminal`、`failure-classified`、`owner-decision`、`direction-completed`、`handoff`、`plan-completed`。

不记录每条工具调用或大段原始输出。

## evidence/index.md

```md
# Evidence Index

## 证据索引

| Evidence | Kind | Source Stamp | SHA-256 | SizeBytes | Status | Requirements | Notes |
|---|---|---|---|---:|---|---|---|
| static/<run>/result.json | static | <stamp> | sha256:<64-hex> | <bytes> | CURRENT | R1 | <note> |

## 状态定义

- `CURRENT`
- `STALE`
- `DIAGNOSTIC`
- `FAILED`
- `BLOCKED`
- `MISSING`
```

源码、harness、fixture、计划或 Dev payload 改变后，受影响 evidence 必须在 index 中降为 `STALE`，不能删除失败记录或改写成 PASS。

`CURRENT` evidence 必须位于本计划 `evidence/` 内，使用 algorithm-qualified source stamp，并登记可现场复算的 SHA-256 与 size。manifest 自身为 `CURRENT` 时，其 `files[]` 必须无遗漏、无重复地登记 manifest 同目录及子目录内除 manifest 自身外的全部 raw 文件，并逐项记录相对路径、SHA-256 和 size；包外文件只能标为历史 `STALE`、`DIAGNOSTIC`、`FAILED` 或 `BLOCKED` 引用。

## Superseded plan

旧计划被替代时：

```md
Status: superseded
CurrentDirection: none
SupersededBy: ../<new-plan>/plan.md
```

旧目录保留日志和 evidence，不继续新增产品修改。一个目标不得存在两个 active 计划包。
