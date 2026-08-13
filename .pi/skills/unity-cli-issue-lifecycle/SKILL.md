---
name: unity-cli-issue-lifecycle
description: 管理 unity_cli 在真实 Unity 项目或 Dev 项目中发现的产品缺陷闭环。只要准备根据 Live/自动化失败修改已安装的 Packages/com.unity_cli、先在目标项目热修验证、再同步仓库 unitypkg 源码，或用户提到问题登记、in-progress/completed、根因与修复记录时必须使用。纯问答、尚未准备修改产品的调查不创建记录。
compatibility: 适用于 alps-unity-cli；需要 read、edit/write、pwsh、真实 Unity 验证和项目测试。
---

# Unity CLI Issue Lifecycle

本 Skill 约束“真实项目发现问题 -> 目标 Package 验证 -> 产品源码同步 -> 完成归档”的固定流程。问题根目录为仓库 `.issues/`，状态目录只使用英文：

```text
.issues/
  in-progress/
  completed/
```

## 核心规则

- 每个独立根因对应一个 Markdown 文件；文件名为 `<三位序号>-<english-slug>.md`，序号在两个状态目录中全局递增且永不复用。
- 准备第一次修改目标项目 `Packages/com.unity_cli` 前，必须先在 `.issues/in-progress/` 创建记录。
- 若 Skill 启用前已经修改，立即补登记并如实写明当前状态，不得伪造“先登记后修改”。
- 问题记录必须使用 [issue-record-template.md](references/issue-record-template.md) 的字段，持续更新问题、直接证据、根因、修复方法、目标 Package 状态、产品源码状态、验证和涉及文件。
- 不得因局部编译通过、日志出现预期行或问题暂时消失而归档完成。

## 执行顺序

1. **登记**：读取 `.issues/in-progress/` 与 `.issues/completed/`，确认没有同根因记录；创建或更新唯一问题文件。
2. **目标验证**：只在用户批准的真实项目或 Dev 项目 `Packages/com.unity_cli` 内实施最小诊断和修复。不得顺带修改业务代码、其他 Package、ProjectSettings、资源或用户状态。若根因 owner 明确只在 TypeScript CLI、安装器或其他非 Package 产品层，记录 `TargetPackageStatus: not-applicable` 和 canonical owner，直接在该 owner 修复并用同一真实项目验证；不得用此分支绕过本应在 Package 验证的问题。
3. **记录结果**：每次验证后立即更新问题文件。失败或证据不足时保持 `in-progress`，写明 blocker 与下一步。
4. **同步产品**：只有目标 Package 已真实验证通过，才把同一语义修复同步到 `alps-unity-cli` 的 canonical owner，通常为 `unitypkg/`；非 Package owner 问题直接修改已登记的 canonical owner。不得把目标项目中的临时诊断或项目特例带回产品。
5. **产品验证**：运行受影响自动化、契约检查和规定的真实 Unity 验证。确认目标 Package 与产品源码的最终语义一致。
6. **归档**：所有完成门槛满足后，更新 `Status: completed`，再把原文件从 `in-progress` 移动到 `completed`。移动是状态转换，不复制文件、不保留双份。
7. **校验**：每次创建、更新或移动问题文件后运行 `node .pi/skills/unity-cli-issue-lifecycle/scripts/validate-issues.mjs`；校验失败时状态转换无效。

触发边界由 `evals/trigger-cases.json` 与 `evals/semantic-config.json` 维护；修改 frontmatter `description` 后必须重跑 trigger eval。

## 完成门槛

问题文件只有同时满足以下条件才能移动到 `completed`：

- 根因已由可复核证据确认，不是基于最后一行日志或时间相关性的猜测。
- 目标项目 Package 的最终修复通过真实 Editor 验证。
- canonical 产品源码已实施同一修复，临时诊断已删除或证明属于最终有界诊断。
- 相关自动化、契约和真实验证全部通过；未执行项有明确批准的非适用理由。
- `TargetPackageStatus` 为 `verified`，或缺陷 owner 明确不在 Package 时为 `not-applicable`；`ProductSourceStatus` 必须为 `verified`。
- 不存在 unresolved error、blocker、用户改动覆盖、范围漂移或待同步文件。

## 边界

- 本 Skill 管理缺陷记录与状态转换，不替代 `unity-cli-plan`、`unity-cli-capability`、`unity-cli-validation` 或 `unity-cli-wait`；任务命中时仍需同时加载。
- 用户明确不要任务包时，不创建 `.plan`；问题记录本身仍必须维护。
- 不提交、不发布、不升级版本，除非用户另行要求。
- 不把 `.issues/` 当执行日志仓库；只记录影响工程判断的证据和 terminal 结果。
