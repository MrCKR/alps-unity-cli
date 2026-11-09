# editor.compile 当前错误检查验证

## AffectedValidationScope

- Owners: UnityCliCompileCoordinator.Complete/BuildSuccessData -> DoctorCompileDiagnostic；compile.ts terminalResult -> dispatcher 的 editor.compile completion。
- 扩展修复范围（用户要求解决阻断）：compile.ts bridge status 不携带旧 rootAdmission；同一 deadline/root digest 保留，只读 status 重新绑定 listener。自动化增加 editor-refresh.test.ts 的 observer 凭据约束。Dev 增加实际成功编译并跨 reload 返回 clean、之后 unchanged，失败周期返回 cycle，再次调用返回 current。
- Node/libuv 退出修复范围：src/cli.ts finishCliProcess 正常完成只设置 exitCode 并 unref 原 watchdog；一秒强制兜底不变。不修改 shared transport 或 Node 版本。自动化 tests/cli.test.ts 仅选 CLI exit / compiled CLI 名称；实际 Dev search、describe、业务成功/失败验证退出码与单行 envelope。
- Contract: 成功需 currentVerified=true/currentFailed=false；无新周期但 scriptCompilationFailed=true 返回 E_UNITY_COMPILE_FAILED scope=current；本轮失败 scope=cycle。doctor 使用同一权威失败位，Console 样例不决定健康。
- 自动化: tests/editor-compile.test.ts、editor-compile-durable-source.test.ts、doctor.test.ts；TypeScript 构建、受影响 schema、package-files 与 command contract 生成/校验。
- Dev: 无变化且干净；产生错误；已有错误后无变化（includeWarnings=false/default）；修复后恢复；dry-run。PlayMode 拒绝路径未改，自动化覆盖，不扩大到其他状态 Tool。
- Gate: fresh capability gate，检查现有脚本错误。
- Excluded: build、refresh、其他 Tool；公开 Skill 无修改（本次未授权该保护范围）。
- Issue: 按用户本轮明确要求跳过登记。

## 自动化

- PASS: npx tsc -p tsconfig.json；npx tsc -p tsconfig.test.json。
- PASS: node --test dist/tests/editor-compile.test.js dist/tests/editor-compile-durable-source.test.js dist/tests/doctor.test.js，29/29。
- PASS: node scripts/audit-package-files.mjs；node dist/contracts/validate-command-contract.js。
- PASS: git diff --check。
- 首轮编译发现新增测试回调返回 unknown（HARNESS_DEFECT），已改为 async/await 后重新编译并运行上述完整定向集合。
- 首轮 command contract 校验路径尚未生成；运行产品 tsc 后校验通过。

## Dev 安装与隔离

Unity 2022.3.61f1；CLI node dist/cli.js；逐进程 UNITYCLI_DATA_DIR=%LOCALAPPDATA%\UnityCli-Dev。项目 D:/workspace/alps-unity-cli/Dev/UnityProject。

- init package 同步 canonical Package；verify-installed-package 两次确认全部 551 文件一致，sourceHash=25a43472a4d7e7d2a3004fdaddb09bc4361d15a071c1c4fcbd4453b74165c920。
- 启动前正式 Unity PID 20504、104520、102408；结束仍存在同一项目和进程。Dev PID 30152，仅 Dev 根注册。
- 正式版 instances list 前后仅 Client-Jump ready、hiddenOfflineCount=1，没有 Dev 注册。
- 正式根文件清单存在并行业务活动：Client-Jump 的 resource lock 曾出现后消失，新增该项目 test-results/req_ab284ac6-832c-4bde-944e-6d3b349aa7d8.xml。不能声称正式根文件集合完全未变化；变动路径属于正式项目，不是 Dev 注册。未修改或清理这些文件。

## Dev 调用证据

通用前缀：node dist/cli.js，--instance D:/workspace/alps-unity-cli/Dev/UnityProject。编译调用 --timeout-ms 60000。

- PASS 无变化干净：req_758792cd-a1cf-450e-9b99-2b19936b270d，tools run editor.compile --input '{"includeWarnings":false}'；ok=true，unchanged，currentVerified=true，currentFailed=false。
- 夹具：Assets/UnityCliCurrentCompileProbe.cs，第 2 行非法表达式 int value = ;。Unity 自动编译后 CS1525。
- PASS 已有错误且无新周期：req_6ea61f6c-9dcd-482c-b602-5fd78d8f1260，同一 includeWarnings=false 调用；ok=false，E_UNITY_COMPILE_FAILED，scope=current，compile.compilationTriggered=false，errorCount=0；current.hasCompileErrors=true，scanComplete=true，compileErrorCount=1，compileErrors 包含 Assets\\UnityCliCurrentCompileProbe.cs(2,79) 和 CS1525，transient=false，retryAfterMs=0，nextActions 指向修复并重新编译。
- PASS 默认参数：req_ba11ba72-ca2a-4993-9b34-9da36a21b12f，同样 current 失败且带定位文本。
- PASS doctor 编译项：hasCompileErrors=true，compileErrorCount=1，与 Unity 当前失败位一致。
- PASS dry-run：req_9a958ff6-793c-4b9c-9bfd-4833b22c6bad，仅返回 plannedEffects，不执行当前检查或刷新。
- 恢复/清理：先修复夹具，再删除夹具及 .meta。两次恰逢脚本变更的调用 req_23d186cd-7091-42ce-b15e-78040a5e6bff、req_e14c445d-f620-4a0f-8ae2-cafdf1cab819 均返回 E_EXECUTION_TIMEOUT，causeCode=E_EXECUTION_TIMEOUT。Bridge 日志显示 domain reload/listener generation 交接；本轮不修改其 transport/recovery owner。该路径不能记 PASS，根因未完全确定。
- 恢复后的稳定只读状态 ready/edit、无 compile/import，doctor compile=pass。
- PASS 独立恢复后无变化调用：req_21d96d83-28b0-4d15-8cba-50340c0ee0ef，ok=true，unchanged，currentVerified=true/currentFailed=false。

## 陌生 AI 门禁

- Agent: 5ca1d9f3-3f35-42f；model: LMD/aliyun-deepseek-v4.1-flash；fresh，无继承上下文。
- 原始任务：检查指定 Dev 项目脚本是否编译通过并报告位置；禁止修改脚本、清 Console、管理进程；仅提供公开 CLI 前缀、项目事实、隔离环境变量。
- 发现：--help 成功；tools search 'compile|compilation' --instance <Dev> 返回 ok=true 搜索 JSON 后触发 Node/libuv 断言：Assertion failed: !(handle->flags & UV_HANDLE_CLOSING), file src\win\async.c, line 94；exit=1。
- FAIL：首次非预期失败后停止，未读取 descriptor、未执行编译业务调用、无副作用。未重试或将后续主代理调用替代门禁。
- 精确根因尚未确定；故障在搜索进程退出阶段，超出本次 compile 终态变更 owner，不扩展修复范围。
- 完整原始输出位于本目录 editor-compile-current-state-gate.txt。

## 后续修复与重新验证

用户要求继续解决阻断后，完成以下 canonical 修复：

1. compile status 不再使用 start rootAdmission。旧凭据绑定 listener，reload 更换 listener 后 assertAdmissionBinding 会拒绝；status 保留原 deadline、rootRequestId 和 digest，并重新绑定当前 listener。首次 mutation 不重投。
2. finishCliProcess 正常完成设置 process.exitCode，stdout/owners 完成后 unref 原单一 watchdog；事件循环自然结束。仍有活跃句柄或 owner/输出不结束时，原 1000ms timer 强制退出。独立 Node spawn 三次原入口复现原生 0xC0000409，对照自然退出两次正常；调查见 editor-compile-exit-investigation.txt。

重新验证：

- PASS 定向退出测试 5/5：自然退出 beforeExit、残留 interval、一秒未settle owner、失败退出码与单 envelope、flush/watchdog 分离。
- PASS 编译/刷新 observer/doctor 定向测试 34/34；tsconfig.json、tsconfig.test.json；command/package-files contract；diff check。
- PASS Dev 真编译跨 reload：req_07215a1d-00ed-4bb7-8fd6-34c1f2fdf62e，compilationTriggered=true，status=clean，currentVerified=true/currentFailed=false，耗时15267ms，无 timeout。
- PASS Dev 本轮编译失败：req_49aeea1e-800a-483e-8c26-76ef6e7f7658，E_UNITY_COMPILE_FAILED scope=cycle，errorCount=1，结构化 CS1525，Assets\\UnityCliCurrentCompileProbe.cs(1,63)。
- PASS Dev 后续无变化失败：req_22f9cffb-e897-46e0-be50-14c2e7274122，scope=current，compilationTriggered=false，同一错误定位。
- PASS 最终 CLI 搜索退出验证：req_a8788cc8-77e5-43ee-a624-9a84f129c90a、req_0af0b126-c631-42dd-bc7c-deba217ac976、req_445a164c-4af5-4c2a-a3c5-fd1b59ebe422，三次串行 search 均 ok=true、单行 envelope、exit=0，无原生断言。
- 原失败门禁结论保留；Node 修改前再次运行的 3f904df3-bd1b-49a 也在 search 断言后停止，见 editor-compile-gate-before-exit-fix.txt。不得用主代理结果覆盖这些 FAIL。
- PASS 最终 fresh gate：e61c3765-caf6-4fc，LMD/aliyun-deepseek-v4.1-flash。原始任务仍只要求检查当前脚本编译错误，提供 CLI 前缀、项目、隔离变量和安全边界。help -> search -> 扩大分页 -> 单次 describe -> 首次 editor.compile。req_9d85c0f0-95a7-49d6-b7f6-c74ebf70154f 返回预期 E_UNITY_COMPILE_FAILED scope=current，正常 exit=1，无断言。代理正确报告 Assets/UnityCliCurrentCompileProbe.cs(1,63) CS1525，理解本轮 errorCount=0 不代表项目干净。无纠参、重试或文件修改。原始 prompt/transcript 见 editor-compile-current-state-gate-pass.txt。
- PASS 最终清理与恢复：仅删除本次夹具 .cs/.meta；req_4a66a57b-fe3c-463b-bf31-45dccaedc296 真实编译跨 reload 返回 clean，耗时8274ms；req_686c7d2a-fd38-4922-991e-787ca7ff2ba1 后续调用返回 unchanged，currentVerified=true/currentFailed=false；doctor compile=pass。
- 安装文件再次全部一致。正式 Unity 进程保持原 PID/项目，正式 instances list 仍只有 Client-Jump ready/hiddenOfflineCount=1；无 Dev 注册。
- 完整 Live envelopes 与搜索输出见 editor-compile-current-state-live.json、editor-compile-current-state-search.json。

## 最终结论

受影响自动化39项、契约、Dev真实周期/当前错误/恢复、退出复现路径及 fresh capability gate 均通过。两个阻断已修复。原失败记录保留用于审计；不表示最终版本仍失败。未发布、未对正式项目安装 Package。Dev Editor 保持打开，夹具已清理。
