# Issue Record Template

```markdown
# <问题名称>

Status: in-progress
IssueId: ISSUE-<三位序号>
CreatedAt: <UTC ISO-8601>
UpdatedAt: <UTC ISO-8601>
ObservedProject: <绝对路径>
TargetPackagePath: <绝对路径>/Packages/com.unity_cli

## Problem

<用户可观察的现象、失败输出和影响。>

## Direct Evidence

- <可复核日志、异常、时间点、端口、状态或测试输出。>

## Root Cause

<明确到代码逻辑的原因；未确认时写 `not-confirmed`，不得把推测写成事实。>

## Fix Method

<最终修复策略及其安全边界。>

## Target Package Status

TargetPackageStatus: not-modified | modified-awaiting-validation | partially-verified | verified | failed | blocked | not-applicable

- <修改与真实项目验证状态；`not-applicable` 时写明缺陷为何不属于 Package 及其 canonical owner。>

## Product Source Status

ProductSourceStatus: not-synced | modified-awaiting-validation | verified | failed | blocked

- <canonical 仓库源码同步和验证状态。>

## Validation

- <命令或验证方式>: <PASS/FAIL/BLOCKED> - <关键结果>

## Files

- Target package: <路径或 none>
- Product source: <路径或 none>
- Tests: <路径或 none>

## Blockers

- <none 或精确 blocker 与解锁条件。>

## Next Action

<一个不越界的下一动作。>
```

约束：

- `Status` 只允许 `in-progress` 或 `completed`，并必须与父目录一致。
- 未确认根因时不得填写确定性修复结论。
- 验证失败保留原结论，不得用后续局部 PASS 覆盖。
- 完成时保留关键历史证据，但移除已失效的“下一步”叙述，`Next Action` 写 `none`。
