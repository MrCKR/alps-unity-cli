---
name: unity-cli-plan
description: unity_cli 的文件夹式计划创建、更新、迁移、恢复与方向审计硬规范。只要用户要求写计划、更新计划、继续/恢复长期任务，或任务预计跨多个文件、多个步骤、一个上下文窗口，必须先使用本 Skill；验证失败准备转为产品修改、计划范围或公开语义可能变化时也必须使用。计划不是单个 Markdown，而是带 context、files、decisions、directions、execution log 和 evidence 的持久执行包。
compatibility: 适用于 alps-unity-cli；需要 read、Todo、Git 只读检查和项目 .plan 目录。
---

# Unity CLI Plan

本 Skill 让计划成为跨对话、跨压缩可恢复的执行边界。计划只描述当前目标和已批准语义；执行日志保存历史；当前上下文保存仍然成立的动态事实。

## 何时必须使用

以下任一条件成立时，先完整读取本 Skill，再创建 Todo 或修改产品文件：

- 用户要求创建、更新、迁移、继续或恢复计划。
- 任务预计需要多个步骤、多个文件或超过一个上下文窗口。
- 当前工作已经存在 `.plan` 计划包或遗留单文件计划。
- 上下文压缩、会话恢复或 Agent 交接后继续长期任务。
- 验证失败准备转化为代码修改。
- 准备新增未列入范围的文件、能力、公开语义、版本支持、proof 或验证要求。

简单问答、单一只读查询和无需持久跟踪的一次性微小修改不创建计划包。

## 规范优先级

发生冲突时按以下顺序裁决：

1. 用户当前明确决定。
2. 仓库 `AGENTS.md` 和命中的项目 Skill。
3. 当前计划包的 `decisions.md`、`plan.md` 与 `files.md`。
4. 当前 direction。
5. Todo、测试、历史 evidence 和实现现状。

测试、旧代码、旧 evidence 或 Agent 推断不能反向创造产品要求。

## 写计划前的只读调查

在写入计划包前完成：

1. 完整读取 `AGENTS.md` 和所有命中的项目 Skill。
2. 扫描 `.plan/*/plan.md` 与 `.plan/*.md`，完整读取可能相关的计划。
3. 检查当前 goal、Todo、branch、HEAD、dirty worktree 和未提交用户改动。
4. 使用 CodeGraph 理解相关产品 owner、调用路径和影响范围；Markdown 与 evidence 使用只读文件检查。
5. 记录稳定约束与动态事实，禁止把旧 PID、session、stamp 或历史 PASS 当作当前状态。
6. 识别未知所有权、禁止回退、不得重放和其他任务目录。

调查阶段不得修改产品代码、运行 destructive 命令或执行 Live Unity mutation。

## 决定更新、新建或迁移

先查找同一最终目标的计划包。

### 更新现有计划包

满足以下任一情况时更新原目录，不创建平行计划：

- 最终目标和公开产品语义相同。
- 只是补充实现细节、文件、风险、验证或当前上下文。
- 计划状态为 `draft`、`active`、`paused` 或 `blocked`。
- 新工作仍用于满足原完成条件。

### 新建计划包

只有以下情况新建：

- 最终目标不同且可独立交付。
- 用户明确改变公开契约、能力范围或支持边界，并要求独立计划。
- 原计划已完成，新需求不属于原完成条件。
- 新计划明确替代旧计划。

同一目标只能有一个非 terminal 计划包。替代时旧计划标记 `superseded` 并指向新目录，不保留两条活动实现路径。

### 迁移遗留单文件计划

触及 `.plan/*.md` 遗留计划时迁移为文件夹：

1. 使用稳定、可读、无日期的短目录名。
2. 把原文件内容移动到 `<plan>/plan.md`，保留现有未提交修改。
3. 创建全部必需文件和目录。
4. 只写一个可信的 `migration-baseline` 日志事件，不伪造历史流水。
5. 更新仓库内所有当前、可执行且可修改的引用；外部 goal 记录在 `context.md` 标为 stale reference。历史 raw evidence 保持不可变，在 index 标为 `STALE` 且禁止执行其中引用旧路径的脚本。
6. 删除原单文件，不保留双格式或兼容 stub。
7. 旧 raw evidence 先由 index 引用；新 evidence 必须写入计划包。

## 计划包格式

创建、更新或迁移前必须完整读取 [plan-package-format.md](references/plan-package-format.md)。

计划根目录固定为：

```text
.plan/<stable-short-name>/
```

必需内容：

```text
plan.md
context.md
files.md
decisions.md
execution-log.md
directions/*.md
evidence/index.md
evidence/static/
evidence/live/
evidence/gates/
```

- `plan.md` 是稳定规格，不保存命令流水和易失运行状态。
- `context.md` 只保存当前仍成立的动态事实，每次恢复先验证后重写。
- `files.md` 是文件与所有权边界；文件未登记前不得编辑。
- `decisions.md` 保存项目所有者批准的公开与范围决策。
- `execution-log.md` 按工程事件 append-only。
- `directions/*.md` 每个文件是一份上下文窗口大小的结果 checklist。
- `evidence/` 保存新 raw evidence，`index.md` 管理有效性。

写入后运行：

```powershell
node .pi/skills/unity-cli-plan/scripts/validate-plan-package.mjs .plan/<stable-short-name>
```

validator 失败时计划包未建立完成，不得进入产品实施。

## 文件边界

`files.md` 必须在首次产品编辑前列出：

- 产品文件。
- 生成文件及唯一 generator owner。
- contract、schema、catalog。
- tests。
- docs、Skill、AGENTS。
- 删除文件及 successor coverage。
- 允许条件发现的文件边界。
- 禁止触碰、其他任务和所有权未知文件。

每项写明路径或受控 glob、对应要求、允许变化、同步 owner 和当前状态。

准备编辑未登记文件时先更新 `files.md`。若新增文件意味着新能力、公开契约、版本支持、proof 强化或验证缩窄，必须返回 `DECISION_REQUIRED`，由项目所有者批准后才能继续。

## Direction 生命周期

创建或激活任务时同步创建 direction。完整规则和模板见 [direction-checklist.md](references/direction-checklist.md)。

硬规则：

- 一个 direction 的总工作量约一个上下文窗口。
- 每个 direction 只覆盖一个主要设计轴，包含 3–6 个结果型 outcome。
- outcome 描述完成状态，不描述编辑文件、运行命令等具体动作。
- 计划状态为 `active` 时恰有一个 active direction；其他状态不得有 active direction。
- 每个 Todo 必须映射到一个 outcome；无法映射的 Todo 不得执行。
- 方向预计超出一个窗口时，在实施前拆分，不依赖后续压缩继续堆积。
- 方向完成前执行 exit audit，并把结果与 evidence 写入 direction 和 execution log。

## 执行日志

`execution-log.md` 只追加工程事件：

- 计划创建、迁移、暂停、恢复或完成。
- direction 创建、激活、完成、阻塞或漂移。
- owner decision。
- 文件范围或公开语义变化。
- 一批相关产品修改完成。
- build、test、Live 或 gate 的 terminal 结果。
- 验证失败分类、outcome unknown、独立 readback。
- 上下文交接和下一安全动作。

不记录每条命令、重复轮询、大段原始输出或没有改变工程判断的信息。原始数据写入 `evidence/`，日志只保存结论和相对路径。

历史只属于 execution log、decision 记录和 evidence。`plan.md`、`context.md`、公开 docs 与代码注释只描述当前最终语义。

## 验证失败分流

任何失败转为代码修改前，先写日志并归类：

- `PRODUCT_DEFECT`：产品违反已批准 spec，可以在当前 direction 内修复。
- `HARNESS_DEFECT`：测试、fixture 或断言错误，只修改验证 owner。
- `ENVIRONMENT_BLOCKED`：记录 blocker 和解锁条件，不修改产品。
- `SPEC_GAP`：source of truth 没有定义，返回 `DECISION_REQUIRED`。
- `TEST_OVERREACH`：测试强于 spec，禁止反向强化产品。

“修根因”不能自动授权跨能力重构。失败不属于当前 direction outcome 时，停止当前方向并由所有者决定更新计划还是新建计划。

## 漂移硬阻断

以下任一情况必须停止并记录 `DECISION_REQUIRED` 或 `DRIFT`：

- 改动无法映射到计划要求和 active outcome。
- 触及未授权能力或文件 owner。
- 新增或强化公开 schema、错误、安全、proof、版本支持或行为。
- 新增大型生成物或新的 canonical 数据 owner，而计划未列出。
- 测试要求高于计划原文。
- 为减少成本而缩窄已批准验证矩阵。
- 当前 evidence stamp 已失效却继续宣称完成。
- 出现阶段性双轨、兼容路径、临时旁路或未归档 WIP。

公开契约、能力范围、版本支持、proof 语义和验证范围的变化只能由项目所有者批准。范围内实现细节由 Agent 决定。

## 上下文恢复顺序

压缩、重启或交接后按固定顺序恢复：

1. `plan.md`
2. `decisions.md`
3. `context.md`
4. `files.md`
5. `plan.md` 指向的 current direction
6. `execution-log.md` 最后若干工程事件
7. `evidence/index.md` 中当前有效、失败和 blocked 项
8. Git、进程、Editor 和 stamp 的 fresh 只读核验

恢复前不得从旧摘要直接继续 mutation，也不得重放 outcome-unknown request。

## 完成与交接

Direction 只有在以下条件全部满足时可完成：

- 所有 outcome 都有当前证据。
- 七项 exit audit 全部通过。
- 没有 unresolved decision、drift、失败或 blocker。
- Todo 与文件改动都能映射到方向。
- execution log 和 context 已更新。

计划只有在自身完成条件和项目 validation Skill 全部满足后才能标记 `completed`。否则保持 `active`、`paused` 或 `blocked`，不得用“基本完成”替代。
