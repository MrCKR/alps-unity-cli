---
name: unity-cli-issue-lifecycle
description: 管理 UnityCLI 产品缺陷从登记、canonical 源码实现、自动化与契约验证、授权测试安装、真实 Unity 验证到归档的闭环。准备修复 UnityCLI 产品缺陷并维护 issue 状态时必须使用；用户明确要求在指定真实或 Dev 项目紧急热修已安装 Package 时也必须使用。普通无关的一次性单元测试失败、纯问答和只调查不修改不触发。
compatibility: 适用于 alps-unity-cli；需要 read、edit/write、pwsh、真实 Unity 验证和项目测试。
metadata:
  internal: true
---

# UnityCLI Issue Lifecycle

本 Skill 约束“登记 -> canonical 源码实现与验证 -> 授权测试安装 -> 真实 Unity 验证与门禁 -> 归档”。问题根目录为 `.issues/`，状态目录仅为 `in-progress/` 和 `completed/`。

## 核心规则

- 每个独立根因使用一个 `<三位序号>-<english-slug>.md`；序号跨两个状态目录递增且永不复用。
- 首次修改 canonical 产品源码前须在 `in-progress/` 登记；紧急例外须在首次修改指定项目 Package 前登记。工作已开始则立即补记真实顺序与状态，不得伪造历史。
- 按 [issue-record-template.md](references/issue-record-template.md) 持续记录问题、证据、根因、修复、源码与安装 Package 状态、验证及涉及文件。局部编译、单行日志或暂时消失均不能支持归档。

## 执行顺序

1. **登记**：检查两个状态目录无同根因记录，并创建或更新唯一问题文件。
2. **源码实现**：先在 canonical owners 完整实现最终语义，包括适用的 `unitypkg`、TypeScript、tests、contracts、docs 和获明确授权的公开 Skill 投影；不得加入项目特例、临时旁路或兼容路径。
3. **源码验证**：冻结实现后完成必需 build、受影响自动化和契约验证；结果立即入档。失败或证据不足时保持 `in-progress`，记录 blocker 与下一步。
4. **测试安装**：从 canonical 源码安装或同步到授权测试环境，通常为 `Dev/UnityProject/Packages/com.alpsckr.unitycli`；使用真实用户项目须获项目与动作的精确授权。
5. **真实验证**：运行受影响的真实 Unity 与 Development Player 矩阵、其余 canonical 产品验证及适用门禁并立即记录。Package 行为须确认安装副本来自当前 canonical 源码且最终语义一致。
6. **归档**：满足全部门槛后写入 `Status: completed`，再将原文件从 `in-progress` 移至 `completed`；只移动，不复制或保留双份。
7. **校验**：每次创建、更新或移动问题文件后运行 `node .pi/skills/unity-cli-issue-lifecycle/scripts/validate-issues.mjs`；失败则状态转换无效。

触发边界由 `evals/trigger-cases.json` 与 `evals/semantic-config.json` 维护；修改 frontmatter `description` 后须重跑 trigger eval。

## 紧急例外

仅当用户明确要求在具名真实或 Dev 项目紧急热修已安装 Package 时，才可目标优先实验：修改前登记并标明真实顺序；证明方向后在 canonical 源码重新实现完整最终语义，移除项目特例与临时诊断。随后重新构建、从 canonical 源码安装并重跑最终 Package 验证；实验 PASS 不算最终证据，项目专用诊断、临时代码或特例不得进入产品。

## 状态语义

- `ProductSourceStatus`：`not-synced` -> `modified-awaiting-validation` -> `verified | failed | blocked`；首次 canonical 修改后立即进入 `modified-awaiting-validation`。
- `TargetPackageStatus`：`not-modified` -> `modified-awaiting-validation` -> `partially-verified` -> `verified | failed | blocked`，表示源码实现的安装验证投影；仅无需安装 Package 或 Unity 验证的 owner 可用 `not-applicable`。
- 完成须同时为 `ProductSourceStatus: verified`，且 `TargetPackageStatus: verified` 或 `not-applicable`。

## 完成门槛

仅当以下条件全部满足才能移至 `completed`：

- 可复核证据确认根因，而非末行日志或时间相关性猜测；canonical 源码已实现并验证最终语义，临时诊断已删除或属于最终有界诊断。
- Package 行为已用 canonical 源码安装副本通过真实 Unity 验证；必需 build、自动化、契约、Live 矩阵和适用门禁全部通过，未执行项有明确批准的非适用理由。
- 状态满足上述完成要求，且无 unresolved error、blocker、用户改动覆盖、范围漂移或待同步文件。

## 边界

本 Skill 仅管理缺陷记录与状态转换，不替代命中的 `unity-cli-capability`、`unity-cli-validation` 或 `unity-cli-wait`。记录独立于普通任务跟踪；只保留影响工程判断的证据和 terminal 结果，不把 `.issues/` 当执行日志仓库。除非另行要求，不提交、发布或升级版本。
