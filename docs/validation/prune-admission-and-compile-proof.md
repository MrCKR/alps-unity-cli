# prune 准入结算与 compile proof 错误修复验证

## AffectedValidationScope

- Owners: `unitypkg/Editor/Infrastructure/UnityCliAdmission.cs`、`unitypkg/Editor/Bridge/BridgeWorkQueue.cs`、`src/tools/editor/compile.ts`、`unitypkg/Editor/Infrastructure/UnityCliCompileCoordinator.cs`。
- 直接调用链: `instances prune` → `internal.instances.prune` → 队列完成 → `SettleAdmissionAfterWorkItem` → `TransferAdmissionAfterAcceptedWork`；`editor.compile` → `internal.editor.compile.start/status` → `terminalResult`。
- 公开契约: `E_TOOL_COMPLETION_PROOF_INVALID` 新增稳定 `details.reason` 与 `details.causeCode`；公开错误文本不再把本机绝对路径判为泄露；prune 完成后的准入归属为 `direct-mutation` scope 的 durable owner。
- 受影响契约: `contracts/package-files.json`（sourceHash c4a0ab2c744944d0ee24955e5f403c73e7a8089d97be45851245caa70a0c19f3，551 文件）。
- 选中测试: `dist/tests/editor-compile.test.js`；EditMode `UnityCliBridgeAdmissionIntegrationTests`、`UnityCliAdmissionCoordinatorIntegrationTests`、`UnityCliBridgeQueuePolicyTests`。
- Dev 切片: 真实 `instances prune` 与随后只读调用；真实失败编译与恢复；健康编译。
- 排除: 其他 direct mutation 命令语义、其他 coordinator 的准入转移、`editor.refresh` 公开行为、Extension 投影。

## 变更

1. `UnityCliAdmission`：新增单一判定 `IsDirectMutationCommand(command)`（`catalog.editor.run`、`debug.code-execute`、`internal.instances.prune`），并在 `AdmissionTerminalScope`、`TransferAdmissionAfterAcceptedWork`、`PersistWorkItemRecoveryAfterTransferFailure`、`TryHasExactDurableBusinessRoot` 复用，删除三处重复白名单。
2. `BridgeWorkQueue`：每 tick 新增 `ExpireQueuedWork`。只回收没有可用预算且 `CancelBeforeStart()` 成立的排队项；锁内只做移出，锁外按 no-effect 结算；`TryDeadlineRemainingMs` 为 false 与 `TryStart` 一致按过期处理。
3. `compile.ts`：新增 `proofError(reason, message, nextActions, causeCode)`，10 个 proof 分支全部带 `details.reason`；`containsPrivateCompileText` 去掉绝对路径与回环地址模式。
4. `UnityCliCompileCoordinator`：刷新失败的公开 message 改为稳定文本，异常原文写入 Bridge 日志。

## 自动化

- PASS: `npm run build:test`（构建、契约生成、package-closure/init/instances 校验 `failures []`）。
- PASS: `node --test dist/tests/editor-compile.test.js` 18/18。
- PASS: EditMode 新增用例 2/2：`ExpiredQueuedReadSettlesBeforeStartAndReleasesItsAdmission`、`CompletedInstancePruneTransfersItsAdmissionToTheDurableOwner`。
- PASS: EditMode 受影响 fixture 98/98（admission integration、admission coordinator integration、queue policy）。
- PASS: `node .pi/skills/unity-cli-issue-lifecycle/scripts/validate-issues.mjs`。
- 首轮失败与修正：`ExpiredQueuedRead…` 初次 FAIL，原因是 `TryDeadlineRemainingMs` 对过期返回 false 而被跳过；修正后重跑通过，未放宽断言。

## Dev 真实验证

Unity 2022.3.61f1；CLI `node dist/cli.js`；逐进程注入 `UNITYCLI_DATA_DIR=%LOCALAPPDATA%\UnityCli-Dev`；项目 `<repo>/Dev/UnityProject`。

- `init package`：`installed`，sourceHash c4a0ab2c744944d0ee24955e5f403c73e7a8089d97be45851245caa70a0c19f3；`verify:installed` 551 文件 `missing []`、`mismatched []`。
- PASS 健康编译：`editor.compile` 返回 `unchanged`、`currentVerified=true`、`currentFailed=false`、`errorCount=0`。
- PASS prune 结算（`req_17a0c032-ca9a-4a10-9cee-d9da8c0bcbcb`）：`ok=true`、`removed=0/remaining=1`；Bridge 日志紧接着 `admission_terminal outcome=ExactCurrentCompleted active=0`，**没有** `admission_settlement_deferred`；随后 `editor.state` 立即返回 `ready`。
- PASS 失败编译（`req_04b9f570-ff91-400f-8327-8fc33b1d020e`）：夹具 `Assets/UnityCliProofProbe.cs` 故意写入非法表达式，返回 `E_UNITY_COMPILE_FAILED`、`scope=current`，`compileErrors` 含 `Assets\UnityCliProofProbe.cs(3,24): error CS1525`，没有被替换为 `E_TOOL_COMPLETION_PROOF_INVALID`。
- PASS 恢复：删除夹具与 `.meta` 后 `editor.refresh` 生效，`editor.compile` 回到 `unchanged`、`currentVerified=true`、`errorCount=0`。

## 陌生 AI 门禁

- 编译任务（`8070838c-5f00-47b`，fresh，`LMD/aliyun-deepseek-v4.1-flash`）：PASS。发现链 `--help` → `tools search compile` → `tools describe editor.compile --detail full` → 一次 `editor.compile`；返回 `unchanged`、`currentVerified=true`，零重试零纠参。
- 实例清理任务首轮（`28b46beb-b0f1-403`，fresh）：FAIL。首次调用 `instances prune` 返回 `E_INSTANCE_REQUIRED`，原因是公开 help 漏写必需的 `--instance`，不是本项行为；已登记 ISSUE-129 并修正 `src/cli.ts` 命令表。
- 实例清理任务复跑（`6636f8ca-8f77-4ed`，fresh）：PASS。首次调用即按 `--instance <projectPath>` 执行，`removed=0/remaining=1/scanned=1`；随后 `doctor` 全部 checks pass（pulse `fresh=true`、`playMode=false`、场景已加载）。
- 三轮门禁均未指导重试或纠参；首轮 FAIL 原文保留，不用复跑结果覆盖。
