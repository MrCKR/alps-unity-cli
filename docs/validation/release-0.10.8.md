# Release 0.10.8

- Version: 0.10.8（小版本，`a.b.c` 的 c）
- 基线: 0.10.7
- 分类依据: 本次为缺陷修复与公开披露补全；新增字段只有 `E_TOOL_COMPLETION_PROOF_INVALID` 的 `details.reason` / `details.causeCode`，为附加字段，不改变既有码、既有输出结构或 Tool 形态，因此按小版本递增。

## 内容

- ISSUE-127 `instances prune` 完成后的准入结算死锁；`internal.instances.prune` 归入 direct mutation 单一判定，并在 `BridgeWorkQueue` 增加未开始排队项的到期回收。
- ISSUE-128 `editor.compile` 用 `E_TOOL_COMPLETION_PROOF_INVALID` 覆盖真实错误；proof 分支各自带 `details.reason`，私有文本判定不再把本机路径当泄露，刷新失败不再把异常原文拼进公开消息。
- ISSUE-129 `instances prune` 的公开 help 补上必需的 `--instance` 与 `--timeout-ms`。
- debug hook 披露：overrun 阈值与回调耗时纪律、有限 listener 不自动重装。
- `skills/unitycli/SKILL.md` frontmatter 去掉保留指示符开头的 plain scalar。
- `scripts/audit-descriptor-budget.mjs` 与 `tests/descriptor-budget.test.ts`：notes 逐元素核对，零参数文本引用按单一来源展开。
- `tests/docs-stale-wording.test.ts`：README 的最低 Unity 版本断言改为从 `contracts/package-assembly-closure.json` 取值。

## 验证

- `npm run build:test`: PASS（含契约生成与 package-closure/init/instances 校验）。
- `node --test dist/tests/package-manager.test.js editor-compile instances-prune method-hook-contract descriptor-budget docs-stale-wording`: PASS（111/111）。
- EditMode 受影响 fixture（admission/coordinator/queue policy）: PASS（98/98）。
- Dev 真实调用：prune 完成即 `active=0` 且无 `admission_settlement_deferred`；失败编译返回 `E_UNITY_COMPILE_FAILED` 与 `Assets\UnityCliProofProbe.cs(3,24): error CS1525`；夹具清理后回到 `unchanged`。
- 陌生 AI 门禁：编译任务 PASS；实例清理首轮 FAIL（ISSUE-129，已修）→ fresh 复跑 PASS。
- `npm run release:check`: `pass: true`，`blockers []`；warnings 见 `release-0.10.8-warnings.md`。
