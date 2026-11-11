# Release 0.10.9

- Version: 0.10.9（小版本，`a.b.c` 的 c）
- 基线: 0.10.8
- 分类依据: 本次为缺陷修复。`editor.dialogs` 与 `editor.dialog-click` 的输入、输出字段、错误码集合和 Tool 形态均未变化；变化只有对同一输入的错误归因（原本误报 `E_INSTANCE_NOT_RUNNING`，现在按已验证 Registry session 正常观察），以及 `E_INSTANCE_UNVERIFIABLE` 多两个可分类 `details.reason`。因此按小版本递增。

## 内容

- ISSUE-130 `editor.dialogs` 与 `editor.dialog-click` 把空显式项目 claim 判为 offline：新增纯函数 `attributeDialogProcess`，以已验证 Registry editor session 作为可注入窗口的 ownership 来源，命令行 `-projectPath` claim 只作额外正向证据；无 session 时才回退 claim 或判 offline。多个 claim 仍 `conflict`，claim 与 Registry PID 矛盾仍 `E_INSTANCE_UNVERIFIABLE`，Session 死亡仍 `offline`，PID 复用与 deadline 检查不变。
- Registry 的 `O` 格式启动时间归一到毫秒写法，保持与点击脚本的 `stale` 比对一致。
- `docs/target-and-instance.md` 记录 dialog 归属来源与 claim 的证据地位。

## 验证

- `npm run build:test`: PASS（含契约生成与 package-closure/init/instances 校验）。
- `node --test dist/tests/editor-dialogs.test.js dist/tests/powershell-transport.test.js dist/tests/dialog-recovery-routing.test.js dist/tests/docs-stale-wording.test.js dist/tests/package-manager.test.js`: PASS（74/74）。
- Dev 安装一致性：`init package`（0.10.7 漂移同步到当前源码）+ `editor.refresh` + `init package --check` + `verify:installed`（551 文件 `missing []`、`mismatched []`）。
- Dev 真实切片（相对 `-projectPath` 启动，`claimCount=0`，`editor.state=ready`）：已发布 0.10.8 的 `editor.dialogs` / `editor.dialog-click` 均返回 `E_INSTANCE_NOT_RUNNING`（复现）；当前源码 `editor.dialogs` 返回 `ok:true`，在真实 `EditorUtility.DisplayDialog` 模态下读出标题与按钮，`editor.dialog-click` 重放后 `clicked:true` 且弹窗消失。
- 陌生 AI 门禁：PASS（fresh 会话，`LMD/aliyun-deepseek-v4.1-flash`，单次 `editor.dialogs` 调用即正确）。
- `npm run release:check`: `pass: true`，`blockers []`；warnings 见 `release-0.10.9-warnings.md`。
