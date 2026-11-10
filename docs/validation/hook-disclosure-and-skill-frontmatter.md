# Hook 披露与 Skill frontmatter 修复验证

## AffectedValidationScope

- Owners: `unitypkg/Editor/Tools/debug/DebugDomainNotes.cs`（HookListener、HookCallbackRules、overrun 段）、`docs/error-codes.md` 的 `E_HOOK_CALLBACK_OVERRUN` 行、`skills/unitycli/SKILL.md` frontmatter、`unitypkg/Tests/Editor/UnityCliMethodHookIntegrationTests.cs`、`tests/method-hook-contract.test.ts`、`scripts/audit-descriptor-budget.mjs`、`tests/descriptor-budget.test.ts`、`tests/docs-stale-wording.test.ts`。
- 直接调用链: `tools describe debug.hook-install --fields notes` → `ToolDescriptor.Notes` → `DebugDomainNotes.HookInstall`；前端 Skill 加载 → `SKILL.md` frontmatter 首行标量。
- 公开契约: `debug.hook-install` notes 文本；overrun 阈值与回调耗时纪律；有限 `maxHits` 的重新武装规则；`SKILL.md` description plain scalar。
- 受影响契约: `contracts/package-files.json`（`sourceHash` 0ede5f5e478c76d3948563982f9195842661382eb0f9bbbb9b17e9bcc9950246，551 文件）。
- 选中测试: `dist/tests/method-hook-contract.test.js`、`dist/tests/descriptor-budget.test.js`、`dist/tests/docs-stale-wording.test.js`、`dist/tests/debug-listener-trigger-source.test.js`、EditMode `UnityCli.Tests.UnityCliMethodHookIntegrationTests.InstallDescriptorDisclosesOverrunAndListenerRearmRules` 与 `.InstallDescriptorDisclosesCallbackFaultSemantics`。
- Dev 切片: `describe --fields notes` 实读、edit 模式编译健康、上述两条 EditMode 披露断言、安装字节逐文件比对。
- 排除: 其他 debug Tool 的行为、hook 调度与 fault 状态机、`editor.compile`/`editor.refresh` 语义、Extension 投影。

## 变更

1. `skills/unitycli/SKILL.md`：description 行去掉行首反引号。YAML 1.2 把 `` ` `` 列为保留指示符，plain scalar 不得以它开头，严格解析器整文件失败，Skill 完全加载不到。
2. `DebugDomainNotes.HookListener`：把「有限 `maxHits` 不自动重装」提升为任何 listener 的通用规则，写明 fault `E_HOOK_LISTENER_NOT_REARMED` 与 `debug.hook-enable` 恢复路径；删掉 phase 段内重复表述。
3. `DebugDomainNotes.HookCallbackRules`：overrun 段落作为数组元素，阈值通过字符串拼接引用 `DebugLimits.CallbackOverrunMs`，并写明该 fault 码与耗时纪律（用 `ctx.Emit`，回调内不做阻塞等待、文件/网络 IO、资产操作）。
4. `docs/error-codes.md`：`E_HOOK_CALLBACK_OVERRUN` 行补 10 ms 阈值、faulted 后果与耗时纪律。
5. 测试：`tests/method-hook-contract.test.ts` 增加披露断言；`UnityCliMethodHookIntegrationTests` 增加 `InstallDescriptorDisclosesOverrunAndListenerRearmRules`。

## 自动化

- PASS: `npm run build:test` 全链（生成 package-files、package-closure、init 契约、instances 契约）`failures []`。
- PASS: `node --test dist/tests/method-hook-contract.test.js` 6/6。
- PASS: `node --test dist/tests/debug-listener-trigger-source.test.js` 12/12。
- PASS: `node scripts/audit-package-files.mjs`，`sourceHash` 0ede5f5e478c76d3948563982f9195842661382eb0f9bbbb9b17e9bcc9950246，551 文件，`failures []`。
- PASS: PyYAML 严格解析 `skills/unitycli/SKILL.md` frontmatter 成功；HEAD 版本同一解析器报 `ScannerError`。
- PASS: `node scripts/audit-descriptor-budget.mjs`：`failures 0`；`debug.hook-install` notes 1715 / 2000，为全仓最高项。详见《预算扫描加固》。

## Dev 真实验证

Unity 2022.3.61f1；CLI `node dist/cli.js`；逐进程注入 `UNITYCLI_DATA_DIR=%LOCALAPPDATA%\UnityCli-Dev`；项目 `<repo>/Dev/UnityProject`。启动前正式 Unity PID 51204/87008/102160/104520，正式 `instances list` 仅 Client-Jump ready、hiddenOfflineCount=1。

- `init package --project <Dev>`：`installed`，sourceHash 0ede5f5e478c76d3948563982f9195842661382eb0f9bbbb9b17e9bcc9950246。
- `node scripts/verify-installed-package.mjs`：551 文件，`missing []`、`mismatched []`。
- `doctor`：`summary=pass`；`compile=pass`（`hasCompileErrors=false`），`catalog_integrity=pass`（397 Tool）。
- `tools describe debug.hook-install --fields notes`：12 条 notes，含 `E_HOOK_CALLBACK_OVERRUN`、`more than 10 ms is an overrun`、`ctx.Emit`、`file and network IO`、`E_HOOK_LISTENER_NOT_REARMED`、`never rearmed`；阈值文本来自常量，不是复制值。
- EditMode `test.run`（testNames 两条披露断言）：`passed 2`、`failed 0`。
- 隔离: 结束后正式 `instances list` 与启动前一致，仍只有 Client-Jump ready、hiddenOfflineCount=1；Dev 记录只写入 Dev 根。

## 预算扫描加固

同一轮完成 `scripts/audit-descriptor-budget.mjs` 与 `tests/descriptor-budget.test.ts` 的加固：

- 数组元素级核对：内联 notes 与共享 notes 常量都按顶层逗号切元素，每个元素必须至少含一段字面量，否则计 failure。
- 零参数 `static string` 方法引用按名称索引，用该方法体内字面量展开；重名或空文本视为不可解析并计 failure。
- `readCSharpDescriptor` 增加 `unresolvedNotesCalls`；`buildNoteIndex` 返回 `{ index, methods, failures }`；`collectCSharpDescriptors` 返回 `{ descriptors, failures }`。
- 新测试 `tests/descriptor-budget.test.ts`：真实 catalog 必须 `failures` 为空；合成描述符验证「字面量被计数、可解析引用被展开、不可解析引用与空元素被上报」。

效果（同一 catalog，444 Tool）：

- 加固前：`notesTokens` 16106，`failures` 0，但 `SessionLimits`、`ProgramAbi`、`PauseLifecycle`、`ValueSummary`、`RunSummary`、`StepContextAbi` 等 single-source 文本被静默丢弃。
- 加固后：`notesTokens` 21138（补回 5032 token），`failures` 0；最高项仍为 `debug.hook-install` 1715 / 2000。
- 反向验证：临时在 `HookQuotas` 插入不可解析引用后，报告出现 `reference DebugLimits.UnmeasurableProbe() cannot be resolved offline`；恢复后 `failures` 回到 0，文件 diff 回到预期 8 行。
- `npx tsc -p tsconfig.test.json` 与 `node --test dist/tests/descriptor-budget.test.js` 均为 2/2 PASS。

仍存在的口径差异：方法体里插值的数值（如 `10`、`30000`）不计入；一条包含拼接的 notes 会按字面量段数计为多条。两者都不隐藏文本。

## 发现的相邻缺陷

`InstallDescriptorDisclosesCallbackFaultSemantics`（`.issues/in-progress/114-hook-callback-fault-semantics-undisclosed.md` 引入）断言的是 `Describe().Description`，而该披露文本位于 `ToolDescriptor.Notes`，因此该用例在首次真实执行时失败。已改为读取 notes，行为断言未变；失败事实已按 owner 同意补记到 114 的 Validation 段，114 本身仍保持 `in-progress`。

与本轮无关、在 HEAD 上已经失败：`tests/docs-stale-wording.test.ts` 要求 README 含 `Unity 2022.3`，而 `README.md:9`、`unitypkg/package.json` 与 `contracts/package-assembly-closure.json` 均为 2021.3。已按 owner 同意改为从 `contracts/package-assembly-closure.json` 的 `minimumUnityVersion` 取值并断言 README 包含它；该文件 9/9 PASS。

## 陌生 AI 门禁

- Agent: f9baa2d1-9fc9-4dd；model: LMD/aliyun-deepseek-v4.1-flash；fresh，无继承上下文；14 次工具调用，121.6 s。
- 原始任务：确认 Dev 项目里 `UnityCliDebugFixture.DebugCounterModel.Update` 是否真的会被调用，并报告一次真实调用的实际参数与执行线程。仅提供公开 CLI 前缀、Dev 实例、隔离变量与安全边界（不可改工程文件、不可切 Play、不可管理进程）。
- 发现链：`--help` → 一次 `tools search` → `debug.method-find` → `debug.hook-install` → `debug.events-read` → `debug.hook-remove` → `editor.state`；每个 descriptor 只读一次。
- 首次业务调用：`debug.method-find` 取精确签名，正确；`debug.hook-install` 选择 `activation=editor-and-play`，因为默认 `play` 在 Edit 模式保持 dormant，判读正确。零重试、零纠参、零替代入口。
- 结论：观察窗口 10 s 内零 `hit`、零 `fault`，`dropped=0`、`gaps=[]`、`appendFailures=0`；代理用签名结构事实交叉验证，未把安装事件误当命中证据。清理 `removed=true`、`adapterReleased=true`、`remainingDefinitionsForTarget=0`；结束后 `playMode=edit`、`isDirty=false`。
- 判定：PASS（接口可用性）。本轮新增的 notes 披露不在默认 describe 输出中，本门禁未读取它；该披露由 Live `describe --fields notes` 与两条 EditMode 断言验证。

