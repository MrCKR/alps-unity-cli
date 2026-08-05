# Direction Checklist

Direction 是一个上下文窗口内可以完成、审核和交接的方向，不是具体 Todo 列表。

## 创建规则

- 创建或激活执行任务时同步创建 direction 文件。
- 一个 direction 只覆盖一个主要设计轴。
- 总工作量约一个上下文窗口；预计超出时在实施前拆分。
- 包含 3–6 个结果型 outcome。
- outcome 描述最终状态，不写“修改文件”“运行测试”等动作。
- 每个 Todo 必须引用一个 outcome ID。

## 模板

```md
# <方向名称>

Status: active
DirectionId: DIR-001
SourceRequirements: R1, R2
CreatedAt: <UTC ISO-8601>
ExpectedContextBudget: one-window

## 目标边界

- Goal: <本方向完成后的状态>
- AllowedFileGroups: <files.md 分类或条目>
- ForbiddenDrift: <本方向不得改变的语义>

## Outcomes

- [ ] O1 <结果状态>
  - DoneEvidence: <预期证据>
  - Todos: <Todo IDs or pending>
- [ ] O2 <结果状态>
  - DoneEvidence: <预期证据>
  - Todos: <Todo IDs or pending>

## Failure Classification

- CurrentFailure: none
- Class: none
- Evidence: none

## Completion Evidence

- <完成后填写相对路径和结论>

## Exit Audit

- [ ] 所有改动都映射到 SourceRequirements 和 outcome。
- [ ] 没有触及 files.md 未授权 owner。
- [ ] 没有新增或强化未批准的公开语义。
- [ ] 没有改变未批准的版本、schema、安全或 proof。
- [ ] 测试只验证批准要求，没有创造更严格语义。
- [ ] 没有临时双轨、旁路、未归档 WIP 或失效 evidence 冒充当前结果。
- [ ] 下一方向仍服务于 plan.md 的最终目标。

ExitResult: <PASS/DECISION_REQUIRED/DRIFT/BLOCKED>

## Handoff

- Completed: <outcomes>
- Pending: <outcomes>
- Blockers: <none/details>
- MustNotReplay: <none/request IDs>
- NextSafeAction: <one action>
```

## 正确 outcome 示例

- 公开 fingerprint 行为与单轨契约一致。
- ProBuilder strict proof 只覆盖批准的十五项能力。
- 当前源码树具有同 stamp 的静态完成证据。
- 所有公开结果不包含内部执行字段。

## 错误 outcome 示例

- 修改 `src/cli.ts`。
- 删除 warn 分支。
- 运行单元测试。
- 修复 create-shape。

错误示例是 Todo 或动作，不能用于判断方向是否漂移。

## Todo 映射

Todo 描述必须包含 direction 和 outcome：

```text
Direction: DIR-001
Outcome: O2
Action: 删除非计划 ProBuilder Tool 的额外 strict proof。
```

无法映射时不创建或不启动 Todo。需要新增 outcome 时先检查当前 direction 是否仍能在一个上下文窗口内完成；涉及公开或范围变化时由所有者批准。

## 失败分流

验证失败先选择一种：

| Class | 含义 | 允许动作 |
|---|---|---|
| PRODUCT_DEFECT | 产品违反已批准 spec | 在当前 outcome 内修产品 |
| HARNESS_DEFECT | 测试、fixture 或断言错误 | 只修改验证 owner |
| ENVIRONMENT_BLOCKED | 外部环境阻塞 | 记录解锁条件，不改产品 |
| SPEC_GAP | source of truth 未定义 | `DECISION_REQUIRED` |
| TEST_OVERREACH | 测试强于 spec | 删除或收敛测试要求，不强化产品 |

失败分类写入 direction、execution log 和 evidence index。超时或终态无法确认时必须先做独立业务 readback，是否重试由调用方决定，不得自动重放。

## 上下文边界

以下任一情况说明 direction 过大，开始前必须拆分：

- 同时涉及两个以上独立公开契约决策。
- 同时要求产品重构、全量静态验收和全量 Live 验收。
- 需要跨多个 capability group 且没有单一共享 owner。
- 存在未决 owner decision。
- 预计必须依靠上下文压缩才能理解全部改动。

压缩或交接前必须完成 Handoff。恢复 Agent 只从计划包恢复，不从旧对话摘要直接继续 mutation。
