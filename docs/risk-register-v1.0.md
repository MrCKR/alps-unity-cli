# Unity CLI v1.0 — 已知风险与限制清单

> 基准：origin/main `d2287a5` | 2026-06-16 | P0-P2 全链路收口

## 冻结决策边界内限制（Q1-Q7 明确）

| # | 限制 | 来源 | 影响范围 | 风险等级 | 建议 |
|---|------|------|----------|----------|------|
| R1 | 逐 action parity 未覆盖 | Q7 | 36 功能域按功能域等价覆盖，不逐 action parity 验证 | 低 | 后续排期补 action parity 回归 |
| R2 | ProBuilder 延后为可选 capability | Q4 | ProBuilder 4 命令已注册但需要项目安装 ProBuilder 包 | 低 | 按 Q4 决策延后；当前实机 smoke 已确认 package 未安装时返回预期错误码 |
| R3 | reflect 作为后续可选 capability | Q5 | reflect 3 命令已实现（offline cache），但不作为 v1.0 核心交付 | 低 | 已实现，按 Q5 标记为可选 |
| R4 | VFX package 依赖 | Q4 | vfx.graph/vfx.effect 需要 Unity 项目安装 Visual Effect Graph 包 | 低 | P1-2 smoke 确认未安装时返回预期错误码 |

## 安全模型限制（P2-5 验收确认）

| # | 限制 | 影响 | 风险等级 | 缓解 |
|---|------|------|----------|------|
| R5 | 黑名单机制未独立实现 | Q6 要求"局部黑名单"但 v1.0 无独立命令黑名单 | 中 | destructive `--yes` 确认 + fingerprint 校验提供等效保护；后续补黑名单 |
| R6 | 网络默认安全未独立实现 | Q6 要求"网络默认安全"但 v1.0 无独立网络安全策略 | 中 | Bridge 仅监听 127.0.0.1；无外部网络调用；后续补网络策略 |
| R7 | Dry-run 仅覆盖 5/12 destructive 命令 | `asset.delete`/`go.delete`/`scene.save`/`code.exec`/`cli.batch.run` 支持 dry-run；`component.remove`/`probuilder.csg`/`editor.menu`/`script.edit`/`cli.package.remove`/`test.cancel`/`build.cancel` 不支持 | 中 | 所有 destructive 命令均需 `--yes` 确认；dry-run 后续补齐 |
| R8 | Fingerprint 仅覆盖 custom tool | 内置命令无 fingerprint 完整性校验 | 低 | 内置命令受合约校验（contracts:validate）+ destructive security proof + security-policy（12 条已补齐）双重保护 |

## 稳定性风险

| # | 风险 | 影响 | 风险等级 | 状态 |
|---|------|------|----------|------|
| R9 | Bridge domain reload 后 stale endpoint | domain reload 后可能出现 E_UNITY_COMPILING 或 stale endpoint | 中 | [ALP-353](mention://issue/9232c636-3566-424f-bebb-44454389d99c) backlog 已跟踪 |
| R10 | Unity Bridge 60s 超时边界 | 长时间操作可能触发 E_UNITY_BUSY | 低 | 已有 transient retry 机制；用户可通过 `--timeout` 调整 |
| R11 | Windows-only | Unity Bridge 仅在 Windows Unity Editor 上运行 | 低 | 设计决策；macOS/Linux 用户无法使用 Bridge 功能 |

## 后续优化（不阻塞 v1.0 发布）

| # | 事项 | 来源 | 优先级 | Issue |
|---|------|------|--------|-------|
| O1 | CLI discovery/docs 优化 | P2-4 代码审查建议 | 低 | [ALP-352](mention://issue/874e3a6a-8c6a-43e9-9f7d-9b30ca681d7e) |
| O2 | Bridge domain reload 稳定性 | 实机 smoke 发现 | 中 | [ALP-353](mention://issue/9232c636-3566-424f-bebb-44454389d99c) |
| O3 | 剩余 5 个 destructive 命令 dry-run | P2-5 安全验收 | 低 | 待排期 |
| O4 | 内置命令 fingerprint 保护 | P2-5 安全验收 | 低 | 待排期 |
| O5 | 黑名单/网络策略独立实现 | Q6 决策 | 中 | 待排期 |

## 回滚建议

- **代码回滚**：`git revert <commit-sha>` 逐 commit 回滚；所有 P0-P2 合入均为 fast-forward merge，可精确回滚
- **npm 回滚**：未发布到 npm，无回滚风险
- **Unity 包回滚**：manifest.json 中 `file:../unitypkg` 引用，checkout 对应 commit 即可回滚
- **关键回滚点**：
  - P2-5 安全闸门 (d2287a5) — 回滚后 destructive 命令无确认保护
  - P2-3 Fingerprint (b376722) — 回滚后 custom tool 无完整性校验
  - ALP-340 Profiler fix (71b18128) — 回滚后 profiler start/pause 产生 Console error
