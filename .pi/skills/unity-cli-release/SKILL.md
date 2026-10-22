---
name: unity-cli-release
description: unitycli 的版本号语义与 npm 发布流程硬规范。升版本、打 tag、npm publish、push、本地安装验证时必须读取；判断一次改动应升大版本还是小版本、判断是否达到正式发布标准时也必须读取。
compatibility: 适用于 alps-unity-cli；需要 pwsh、npm、git 与已登录 npm 账号。
metadata:
  internal: true
---

# UnityCLI 版本与发布规范

约束「升版本 -> 门禁 -> tag -> publish -> push」的固定流程。发布是独立动作，不与功能提交捆绑：功能改动先提交，版本号与发布作为单独 `chore(release)` commit + tag。

## 版本号语义（a.b.c）

- **a = 正式版本号**。`0` 表示开发/内部版本；达到正式发布标准时升到 `1`（`1.0.0`），后续主序列递增。
- **b = 大版本**。功能、契约、公开输出结构重大变更时递增。
- **c = 小版本**。小功能、缺陷修复、优化、非契约/非破坏性变更时递增。

### 递增规则

| 变更类型 | 递增位 | 示例 |
|---|---|---|
| 契约/公开输出变更（删工具、改公开 schema/descriptor、改输出语义、破坏性行为、工具形态变化） | b | `0.3.0 -> 0.4.0` |
| 小功能、修复、优化、无行为变化的内部改动 | c | `0.3.0 -> 0.3.1` |
| 从开发版转为正式发布 | a | `0.3.1 -> 1.0.0` |

判断口径：涉及公开 contract、descriptor、outputSchema、命令/工具形态、错误码、输出字段的变化一律视为「契约变更」（升 b）；仅内部修复、性能、纯内部实现的改动升 c。无法确定时升更大的位（保守）。

## 版本同步点（必须全部一致）

- `package.json` → `version`
- `unitypkg/package.json` → `version`
- `skills/unitycli/manifest.json` → `version`
- `tests/package-manager.test.ts` → 当前版本断言
- `src/package-manager.ts` → `cliCompatibilityRange`，必须写成当前小版本线 `>=a.b.0 <a.(b+1).0`（例如 0.7.1 时为 `>=0.7.0 <0.8.0`）；它是安装元数据的兼容声明，遗漏会让 `checkPackageBridge` 报 drift
- `skills/unitycli/reports/trust-report.md` → `Reviewed: ... for release A.B.C`，治理测试断言它提到当前版本
- `dist/` → 由 build 重新生成，不手改

改完后验证三处 version 一致：
`node -e "console.log(require('./package.json').version, require('./unitypkg/package.json').version)"` 与 `skills/unitycli/manifest.json` 一致。

## 发布前置门禁（全部满足才发布）

1. 发布所含增删改及其直接影响面的自动化测试全部通过，并记录测试命令与变更覆盖关系；不得默认运行无关的全仓测试。允许的唯一失败必须是在 `.issues/completed/` 已登记的 pre-existing 项。
2. `node .pi/skills/unity-cli-issue-lifecycle/scripts/validate-issues.mjs` 输出 `failures: []`。
3. 涉及工具/契约变更时，所有生成产物已从 canonical Tool 源码重新生成，并且二次生成无差异。
4. 涉及 skill 版本时，`skills/unitycli/manifest.json` 与 `package.json` 一致。
5. 发布前 `npm whoami` 确认已登录 npm。

## 发布流程（严格顺序）

1. **升版本**：所有同步点改为目标 `A.B.C`。
2. **门禁**：运行发布所含增删改及其直接影响面的完整定向测试。
3. **提交**：`git add` 版本文件，`git commit -m "chore(release): A.B.C"`。
4. **tag**：`git tag vA.B.C`。
5. **publish**：`npm publish --access public`（prepack 自动 build）。
6. **push**：`git push origin main` + `git push origin vA.B.C`。
7. **本地验证**：`npm i -g @alpsckr/unitycli@A.B.C` + `unitycli --version` 确认版本。

## 边界

- 不擅自发布：只有用户明确要求（升版本/tag/发布）才执行。
- 功能提交与发布提交分离：功能 commit 之后，版本号 + 发布单独 commit + tag。
- 发布后发现产品缺陷：走 `.issues/` 生命周期登记修复，不直接改已发布版本。
- 版本号一旦发布即视为契约，回退/重发需用户明确批准。
