# Issue Record Template

```markdown
# <问题名称>

Status: in-progress
IssueId: ISSUE-<三位序号>
CreatedAt: <UTC ISO-8601>
UpdatedAt: <UTC ISO-8601>
ObservedProject: <绝对路径或 none>
TargetPackagePath: <授权测试环境的绝对路径>/Packages/com.alpsckr.unitycli 或 none

## Problem

<用户可观察的现象、失败输出和影响。>

## Direct Evidence

- <可复核日志、异常、时间点、端口、状态或测试输出。>

## Root Cause

<明确到代码逻辑的原因；未确认时写 `not-confirmed`，不得把推测写成事实。>

## Fix Method

<最终修复策略及其安全边界。>

## Product Source Status

ProductSourceStatus: not-synced | modified-awaiting-validation | verified | failed | blocked

- <canonical owners 的实现、build、自动化与契约验证状态；首次源码修改后立即写 `modified-awaiting-validation`。>

## Target Package Status

TargetPackageStatus: not-modified | modified-awaiting-validation | partially-verified | verified | failed | blocked | not-applicable

- <从 canonical 源码安装到授权测试环境后的同步与真实 Unity/Development Player 验证状态；通常使用 `Dev/UnityProject`。仅 owner 不需要安装 Package 或 Unity 验证时写 `not-applicable` 并说明理由。>

## Validation

- <命令或验证方式>: <PASS/FAIL/BLOCKED> - <关键结果>

## Files

- Product source: <路径或 none>
- Tests/contracts/docs: <路径或 none>
- Installed test package: <路径或 none>

## Blockers

- <none 或精确 blocker 与解锁条件。>

## Next Action

<一个不越界的下一动作。>
```

约束：

- `Status` 只允许 `in-progress` 或 `completed`，并必须与父目录一致。
- `ProductSourceStatus` 按 `not-synced -> modified-awaiting-validation -> verified | failed | blocked` 更新。
- `TargetPackageStatus` 描述源码实现后的安装验证投影，按 `not-modified -> modified-awaiting-validation -> partially-verified -> verified | failed | blocked` 更新；仅无需安装 Package 或 Unity 验证时使用 `not-applicable`。
- 紧急目标优先实验必须记录实际顺序；最终仍需从 canonical 源码重新安装并验证。
- 未确认根因时不得填写确定性修复结论。验证失败保留原结论，不得用后续局部 PASS 覆盖。
- 完成要求 `ProductSourceStatus: verified` 且 `TargetPackageStatus: verified` 或 `not-applicable`；保留关键历史证据，`Blockers` 写 `- none`，`Next Action` 写 `none`。
