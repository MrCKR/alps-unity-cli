# 错误码（Error Codes）

alps-unity-cli 使用结构化错误码，包含机器可读的 `code`、`transient` 标记、`retryAfterMs` 和 `nextActions` 自动恢复指引。

## 错误格式

```json
{
  "ok": false,
  "requestId": "req-xxx",
  "error": {
    "code": "E_INSTANCE_CONFLICT",
    "message": "Multiple Unity Editor sessions claim the selected project.",
    "details": { "name": "YourProject", "status": "conflict" },
    "nextActions": [
      "Close every duplicate Editor process except the intended session.",
      "Run unitycli instances list to verify one session remains."
    ],
    "transient": false,
    "retryAfterMs": 0
  }
}
```

`error` 只允许上述协议字段；`details` 仅承载业务诊断，不公开内部日志、审计、endpoint、port、listener/process identity、native window handle 或 Windows command line。blocker 使用固定公开 DTO。

普通Tool在业务effect前确认阻断时返回`E_UNITY_BLOCKED`，同一响应的`details.blockers`包含已清洗blocker。同一次调用只在`editorSessionId`未改变时续接完全相同的请求；首次mutation POST后若session改变，禁止向新session再次POST。超时或断连统一返回`E_EXECUTION_TIMEOUT`与outcome unknown；调用方先观察业务状态，再作新的提交决策。blocker补充只能消费当前dispatch/admission或execution absolute deadline，最多缩短到10秒；剩余时间为零或没有blocker proof时保留原错误及其字段。

## 错误分类

### CLI 参数错误

| 错误码 | 含义 | 恢复 |
|--------|------|------|
| `UNKNOWN_COMMAND` | 顶层命令不在冻结的公开命令集合中 | 运行 `unitycli --help` |
| `E_UNKNOWN_OPTION` | option 未在 CLI arity 表中声明，或不属于当前命令 | 删除该 option 并查看 help |
| `E_OPTION_VALUE_REQUIRED` | 带值 option 缺少值；命令 token 不会被当作值消费 | 传 `--option <value>` 或 `--option=<value>` |
| `E_OPTION_VALUE_INVALID` | boolean option 的 attached value 不是 `true` 或 `false` | 使用 `--option`、`--option=true` 或 `--option=false` |
| `E_OBSOLETE_OPTION` | option 已从当前公开语义删除 | 按 `nextActions` 删除旧 option |

### Init 安装错误

| 错误码 | 含义 | 恢复 |
|--------|------|------|
| `E_INIT_EDITOR_RUNNING` | 准确目标Unity项目正在运行，未授权安装器关闭 | 只询问一次；用户同意后对同一项目使用`init package --close-editor` |
| `E_EDITOR_STATE_UNVERIFIABLE` | 安装前无法严格判断准确项目的Editor进程 | 确认目标项目未在Unity中打开，再重新执行安装 |
| `E_EDITOR_CLOSE_FAILED` | 无法向准确目标Editor发送正常关闭请求 | 用户正常关闭该项目后重新安装；不得强制终止 |
| `E_EDITOR_CLOSE_BLOCKED` | 正常关闭后Editor仍存在，通常有未保存或modal提示 | 由用户处理Unity提示并正常关闭，再重新安装 |
| `PACKAGE_DIRECTORY_LOCKED` | Windows持续占用现有Bridge目录，原子替换无法提交 | 关闭使用该Package的Unity、IDE或扫描进程，再重新安装；不要手工部分删除 |
| `PACKAGE_ASSEMBLY_CONFLICT` | 项目其他来源提供了UnityCLI必须独占的随包程序集（文件名或assembly identity命中） | 预检在effect前，未写入任何文件。按`details.conflicts`判定：带`identicalToPackageDependency:true`的是与包内字节一致的冗余副本，可由用户移除；项目或Player构建确实需要的副本无法用包内（仅Editor）副本代替，此时应如实报告该项目无法安装Bridge。不得自行删除/移动/重命名项目文件，也存在Assets/Packages/PackageCache内部改名或搬移都清不掉的identity匹配 |
| `PACKAGE_ASSEMBLY_CONFLICT_LIMIT` | 冲突数量超过可枚举上限 | 按`details.conflicts`处理已列出的冲突后重跑；不要删除整个目录 |
| `PACKAGE_ASSEMBLY_SCAN_BUDGET_EXHAUSTED` | 扫描规模超出兜底预算，**不代表存在冲突** | 按`details.budget`、`details.limits`、`details.roots`与`details.largestDirectories`报告规模；不得为此删除项目内容或修改已安装包 |
| `PACKAGE_ASSEMBLY_SCAN_UNSAFE` | 扫描遇到符号链接条目，无法证明扫描范围 | 将`details.path`指出的链接替换为真实项目内容后重跑 |
| `E_INIT_SKILL_TARGET_REQUIRED` | 既未给出明确Skill目录，也未给出预设agent/scope | 使用`--target <SkillDirectory>`，或选择一个预设目标 |
| `E_INIT_SKILL_TARGET_CONFLICT` | 明确`--target`与预设agent/scope参数混用 | 保留明确目录或预设形式之一 |
| `E_INIT_SKILL_TARGET_INVALID` | 明确目标不是安全的本地`unitycli`目录，或位于安装事务目录 | 传独立的固定本地完整目录，例如`.pi/skills/unitycli` |
| `E_SKILL_TARGET_OVERLAPS_SOURCE` | 安装目标与npm内置Skill准源互相包含 | 选择独立安装目录 |
| `E_SKILL_TARGET_OWNERSHIP_UNPROVEN` | 非空目标没有UnityCLI `SKILL.md`，不能证明归安装器所有 | 选择准确UnityCLI目录或先清空明确的新目标 |
| `E_SKILL_TARGET_UNREADABLE` | 目标因权限或文件占用无法只读验证 | 恢复读取权限后重试；不得把不可读当成损坏并覆盖 |

### Project Identity / Instance 错误

| 错误码 | 含义 | 恢复 |
|--------|------|------|
| `E_INSTANCE_REQUIRED` | 没有显式 selector，CWD 也不在 Unity 项目中 | 传完整 `--instance <name|alias|projectPath>` 或从项目目录调用 |
| `E_INSTANCE_NOT_FOUND` | selector 在当前截止时间内没有对应项目记录 | 核对项目路径/alias；完整instance ID在目标Unity启动时会等待首次记录 |
| `E_INSTANCE_NOT_RUNNING` | 项目 identity 已确定，且没有可验证的运行中 Editor | 打开该项目；CLI会在本次调用内等待首次ready记录 |
| `E_INSTANCE_CONFLICT` | 至少两个已验证live process claim声明同一项目 | 关闭重复Editor，只保留目标session |
| `E_INSTANCE_UNVERIFIABLE` | PID/start、project claim、Registry读取或权限证据不完整 | 先读`details.reason`：`access-denied`表示当前宿主拒绝OS进程查询，应在能读取Unity进程身份的会话中重新观察；不得把未知mutation自动重投 |
| `E_INSTANCE_IDENTITY_MISMATCH` | 当前Unity无法在截止时间内重建并证明一致的项目身份 | 更新匹配的CLI/Package，打开该项目并重新观察；不要手工修改内部记录 |
| `E_UNITY_UNRESPONSIVE` | transport identity存在，但近期Unity主线程pulse或Bridge响应不可证明 | 读取`editor.state`与`editor.dialogs`后处理transition/modal |
| `E_INSTANCE_PROJECT_MISMATCH` | 显式项目约束与 selector 指向不同 identity | 统一 `--project` 和 `--instance` |
| `E_INSTANCE_PATH_UNAVAILABLE` | 无法取得可证明的物理 canonical path | 修复不可访问路径、broken link 或 reparse 配置 |
| `E_INSTANCE_REMOTE_PATH_UNSUPPORTED` | 项目位于不支持的远程路径 | 使用本机可验证项目路径 |

### Runtime / Play Mode 错误

| 错误码 | 含义 | 恢复 |
|--------|------|------|
| `E_RUNTIME_NOT_READY` | Runtime 工具需 Play Mode | 先 `tools run editor.play-mode.start`，transient 可重试 |
| `E_RUNTIME_SESSION_STALE` | 运行时会话过期 | 重新进入 Play Mode |
| `E_RUNTIME_TIMEOUT` | 运行时操作超时 | 增加 `--timeout-ms` 或重试 |

### Tool / Discovery 错误

| 错误码 | 含义 | 恢复 |
|--------|------|------|
| `E_TOOL_NOT_FOUND_FOR_TARGET` | 名称不属于当前实例完整 Tool 目录 | 重新运行 `tools group` / `tools search <k1|k2|...>` |
| `E_TOOL_NAME_CONFLICT` | 同一完整 Tool 名称在当前 domain 有多个实现；全部冲突实现不可调用 | 修正重复的命令名后让 Unity 重新编译并 reload domain，再重新 discovery |
| `E_TOOL_REGISTRATION_FAILED` | 工具已声明但在构建 catalog 时被拒绝 | 读取 `doctor` 的隔离摘要与 `error.details.sources`，修正该工具定义后 reload |
| `E_TOOL_REGISTRY_UNHEALTHY` | 当前 domain 的 Tool catalog 整体不可用：共享发现基础设施失败，无法构建可信执行表 | 修复后让 Unity 重新编译并 reload domain；局部故障改用 `isolatedCount` 与 `E_TOOL_NAME_CONFLICT` / `E_TOOL_REGISTRATION_FAILED` |
| `E_SCHEMA_INVALID` | 输入不符合 schema，包括公开数值范围 | 查看对应 `tools describe` 的 inputSchema |
| `E_REQUEST_BUDGET_INVALID` | request-wide预算为零、负数或超过公开上限 | 传正值且不超过descriptor/schema最大值 |
| `E_REQUEST_BUDGET_EXCEEDED` | 读取在完整结果前耗尽scan/node/getter/reflection/time/UTF-8预算 | 使用返回的continuation，或收紧selector/提高上限内预算 |
| `E_CONTINUATION_STALE` | opaque cursor的selector或Unity/assembly generation已变化 | 删除cursor并开始一次fresh读取 |

### 可选预览错误

| 错误码 | 含义 | 恢复 |
|--------|------|------|
| `E_DRY_RUN_UNSUPPORTED` | 工具不支持 dry-run | 去除可选 `--dry-run` 后直接调用 |

### Scene / Asset / Prefab 错误

| 错误码 | 含义 |
|--------|------|
| `E_ASSET_PATH_REQUIRED` | 资产路径缺失 |
| `E_ASSET_NOT_FOUND` | 资产未找到 |
| `E_ASSET_OUTPUT_EXISTS` | 输出已存在（用 `--overwrite`） |
| `E_PREFAB_NOT_FOUND` | Prefab 未找到 |
| `E_PREFAB_STAGE_NOT_OPEN` | Prefab Stage 未打开 |
| `E_CAMERA_NOT_FOUND` | 相机未找到 |
| `E_SCENE_NOT_FOUND` | 场景 path 或唯一场景资产名未找到 |
| `E_SCENE_AMBIGUOUS` | 场景资产名匹配多个 `.unity` 文件；改传精确 `Assets/*.unity` path |
| `E_SCENE_VIEW_NOT_AVAILABLE` | Scene 视图不可用 |
| `E_GAMEOBJECT_DELETE_FAILED` | GameObject 在同步删除后仍存在；按 `nextActions` 检查目标与 Unity Console |
| `E_SCREENSHOT_LIMIT_INVALID` | dimensions/pixels/capture count/temp/artifact计划输入无效或超过上限 |
| `E_SCREENSHOT_LIMIT_EXCEEDED` | effect后编码结果违反首effect前冻结的artifact计划 |
| `E_PROFILER_SNAPSHOT_PATH_INVALID` | snapshot不是governed root下的简单`.snap`文件名 |
| `E_PROFILER_SNAPSHOT_LIMIT_EXCEEDED` | snapshot超过单文件或latest-100/8-GiB保留策略 |
| `E_PROFILER_SNAPSHOT_CATALOG_UNBOUNDED` | governed snapshot目录超出产品可恢复观察边界 |

### Occlusion 错误

| 错误码 | 含义 | 恢复 |
|--------|------|------|
| `E_OCCLUSION_BAKE_ACTIVE` | 已存在原生或本CLI拥有的Occlusion bake | 读取`occlusion.bake-status`，等待或精确取消当前operation |
| `E_OCCLUSION_SCENE_REQUIRED` | 没有loaded saved scene，或scene文件/GUID不可证明 | 保存并加载明确的目标scene后重试 |
| `E_OCCLUSION_SCENE_DIRTY` | effect前目标scene已有未保存改动 | 先保存或丢弃该scene的既存改动 |
| `E_OCCLUSION_SETTINGS_VALUE_INVALID` | bake setting不是有限正数，或backface threshold不在`[1,100]` | 按`tools describe occlusion.bake`修正输入 |
| `E_OCCLUSION_TARGET_CHANGED` | frozen scene集合、GUID、文件或Umbra状态在dispatch前漂移 | 检查当前scene和status后提交全新请求 |
| `E_OCCLUSION_OPERATION_MISMATCH` | cancel的operationId不是当前active Occlusion operation | 重新读取`occlusion.bake-status`，只对exact operation调用cancel |
| `E_OCCLUSION_BAKE_CANCELLED` | exact queued Occlusion operation已到达cancelled terminal | 这是effect前已证明的取消终态，不应把原bake调用重试为同一effect；running operation因无native identity返回cancelled=false |

### Project Settings 错误

| 错误码 | 含义 | 恢复 |
|--------|------|------|
| `E_PROJECT_SETTINGS_UNAVAILABLE` | 对应ProjectSettings asset不存在或Unity无法加载 | 检查目标项目和Unity版本后重试 |
| `E_PROJECT_SETTINGS_VERSION_UNAVAILABLE` | 当前Unity版本、active build target或serialized字段surface未经过支持验证 | 使用descriptor声明的Unity版本；不要猜测字段fallback |
| `E_PROJECT_SETTINGS_NO_FIELDS` | setter没有提供任何批准字段 | 按`tools describe project.*-settings.set`提供至少一个字段 |
| `E_PROJECT_SETTINGS_INVALID` | 数值边界、分页、axis或Player enum输入非法 | 按错误中的精确字段约束修正输入 |
| `E_PROJECT_SETTINGS_LIMIT_EXCEEDED` | legacy Input Manager超过4096个axis，无法在公开有界读取内安全扫描 | 减少Input Manager axis数量后重试 |
| `E_PROJECT_INPUT_AXIS_NOT_FOUND` | 没有完全同名的legacy Input Manager axis | 先分页读取`project-settings.input-settings`并传精确名称 |
| `E_PROJECT_SETTINGS_TARGET_CHANGED` | 冻结的Input axis index/name在effect前漂移 | fresh读取Input设置后提交新请求 |
| `E_PROJECT_SETTINGS_VERIFY_FAILED` | typed readback或ProjectSettings文件SHA未证明持久化终态 | 读取对应`project-settings.*`后再决定是否重试 |

### Editor Utility 错误

| 错误码 | 含义 | 建议 |
|--------|------|------|
| `E_EDITOR_AUTOTICK_UNAVAILABLE` | 当前Unity没有可调用的`EditorApplication.SignalTick` | 保持autotick disabled，使用支持该API的Editor |
| `E_EDITOR_AUTOTICK_INTERVAL_INVALID` | `intervalMs`不在`0..60000` | 按`tools describe editor.autotick.set`修正输入 |
| `E_EDITOR_FOCUS_UNAVAILABLE` | 当前平台不支持Win32 exact foreground proof | 在Windows Unity Editor执行 |
| `E_EDITOR_FOCUS_WINDOW_NOT_FOUND` | selected Unity process没有visible top-level main window | 打开该实例主窗口后重试 |
| `E_EDITOR_FOCUS_TARGET_CHANGED` | frozen HWND不再属于selected process | fresh读取`editor.state`后重试 |
| `E_EDITOR_FOCUS_UNPROVEN` | frozen HWND未成为稳定native foreground window | 检查OS foreground限制和selected instance |
| `E_EDITOR_SEARCH_QUERY_REQUIRED` | Unity Search query为空 | 提供非空query |
| `E_EDITOR_SEARCH_QUERY_TOO_LONG` | query超过2048字符 | 缩短query |
| `E_EDITOR_SEARCH_PAGE_INVALID` | offset不在`0..10000`或limit不在`1..200` | 使用bounded分页参数 |
| `E_EDITOR_SEARCH_PROVIDER_INVALID` | providers不是1..16个unique exact ID | 使用descriptor与result返回的provider ID |
| `E_EDITOR_SEARCH_PROVIDER_NOT_FOUND` | exact provider当前未active | 删除该ID或在Unity Search启用provider |
| `E_EDITOR_SEARCH_FAILED` | Unity Search同步请求未产生有效页 | 检查query/provider后重试 |
| `E_EDITOR_ELEMENT_CAPTURE_UNAVAILABLE` | 当前Unity早于6000.7，没有`CaptureToRenderTexture` | 使用Unity6000.7+实例 |
| `E_EDITOR_ELEMENT_WINDOW_REQUIRED` | 没有提供window type/title | 提供exact EditorWindow type或title |
| `E_EDITOR_ELEMENT_WINDOW_NOT_FOUND` | 没有attached EditorWindow匹配 | 打开对应Editor window后重试 |
| `E_EDITOR_ELEMENT_SELECTOR_INVALID` | selector为空、过长或超过16段 | 使用bounded type/#id/.class链 |
| `E_EDITOR_ELEMENT_NOT_FOUND` | frozen window中没有匹配元素 | 收紧或修正selector |
| `E_EDITOR_ELEMENT_LIMIT_INVALID` | dimension或byte ceiling超出公开上限 | 使用1..4096尺寸与1024..33554432 bytes |
| `E_EDITOR_ELEMENT_LIMIT_EXCEEDED` | window/element scan、resolved尺寸或PNG超过上限 | 缩小selector、元素或上限内的capture目标 |
| `E_EDITOR_ELEMENT_TARGET_CHANGED` | element detach或尺寸在effect后漂移 | fresh解析window/selector后重试 |
| `E_EDITOR_ELEMENT_CAPTURE_FAILED` | UI Toolkit render未产生有效texture | 检查元素attached/layout状态 |
| `E_EDITOR_ELEMENT_ARTIFACT_UNAVAILABLE` | request artifact目录不可用 | 检查CLI临时目录权限 |
| `E_EDITOR_ELEMENT_ARTIFACT_INVALID` | 落盘PNG与encoded size/hash proof不一致 | 检查磁盘后再决定是否重试 |
| `E_TEST_RUN_NOT_ACTIVE` | 没有本CLI跟踪的active`test.run` | 先启动`test.run` |
| `E_TEST_CANCEL_NOT_OWNED` | active Test Framework run不属于当前coordinator root | 不控制该run；等待其自然终态 |
| `E_TEST_CANCEL_IDENTITY_UNPROVEN` | root/run GUID/native TestJobData无法唯一匹配 | 等待running identity或检查原`test.run` |
| `E_TEST_RUN_CANCELLED` | exact tracked run已取消且GUID不再active | 这是原`test.run`的已证明取消终态，不应重放 |

### Package 错误

| 错误码 | 含义 |
|--------|------|
| `E_PACKAGE_NOT_FOUND` | 包未安装 |
| `E_VFX_PACKAGE_MISSING` | VFX Graph 包未安装 |
| `E_CINEMACHINE_PRESET_INVALID` | `cinemachine.create` 的 preset 不在 descriptor 声明的集合中 |

### Reflection 错误

| 错误码 | 含义 | 恢复 |
|--------|------|------|
| `E_METHOD_NOT_FOUND` | 方法名或显式参数签名不存在 | 运行 `debug.method.find` 并核对 `parameterType` |
| `E_METHOD_AMBIGUOUS` | 多个 overload 与参数数量匹配 | 将选中签名的 `parameterType` 数组作为 `parameterTypes` 重试 |
| `E_TYPE_NOT_FOUND` | 目标类型或参数类型不存在 | 使用完整 C# type name |

### Method Hook 错误

`debug.hook.*` 与 `debug.events.read` 复用统一 envelope，并使用下列稳定业务错误码。`E_UNITY_BUSY` 与 `E_EXECUTION_TIMEOUT` 保持既有准入前无 effect 与执行后 outcome-unknown 语义。

| 错误码 | 含义 | 恢复 |
|--------|------|------|
| `E_SCHEMA_INVALID` | DTO 字段、枚举或跨字段约束不合法（例如没有任何捕获或回调行为、activation 取值非法） | 按 `message` 指出字段与合法形状后重新提交 |
| `E_HOOK_BACKEND_UNAVAILABLE` | 受包内 Harmony 依赖支持的后端不可用，或 hook host 未在当前 domain 初始化 | 确认包内含 `Editor/Plugins/0Harmony.dll` 后让 Unity reload domain |
| `E_HOOK_DEPENDENCY_CONFLICT` | 已加载的依赖身份不是随包的 `0Harmony`，或其物理位置不在本 Package 内 | 移除项目中重复的 Harmony 程序集，再重装 Package |
| `E_HOOK_TARGET_NOT_FOUND` | exact target 或捕获字段不存在（程序集、类型、方法、字段或签名不匹配） | 用 `debug.method.find` 确认真实签名后重新提交 |
| `E_HOOK_TARGET_AMBIGUOUS` | 同名程序集或同签名方法存在多个匹配 | 用精确 `parameterTypes` 缩小到唯一方法 |
| `E_HOOK_METHOD_UNSUPPORTED` | 目标属于不支持类别（抽象、native/extern、无 IL、开放泛型、构造器、值类型实例、ref-return、指针、状态机方法） | 改选受支持的托管实例或静态方法，或观察其调用方 |
| `E_HOOK_NOT_FOUND` | 当前 session 没有该 hookId 的定义 | 读取 `debug.hook.list` 获取定义 |
| `E_HOOK_SESSION_STALE` | 定义属于其他 Editor session | 在当前 session 重新读取定义后重试 |
| `E_HOOK_REVISION_STALE` | `expectedRevision` 与当前定义不一致 | 读取 `debug.hook.info` 取得当前 revision 后重试 |
| `E_CODE_COMPILE_FAILED` | 回调源码编译失败 | 按 `details.diagnostics` 修正回调源码 |
| `E_HOOK_PATCH_FAILED` | 建立或撤销本 CLI 自己的 Patch 失败 | 检查 Unity Console 与目标签名后重新提交 |
| `E_HOOK_STORE_FAILED` | 定义快照无法发布或读回，终态无法证明 | 检查项目 `Library/UnityCli/debug` 可写性；先观察业务状态再决定后续 |
| `E_HOOK_CALLBACK_FAILED` | 回调抛错导致该定义停止派发（worker 线程上的故障按发生时的 revision 记录，由主线程提交状态） | 读取 `debug.hook.info` 的 last fault 与 staleFaults，修正后显式 replace 或 enable |
| `E_HOOK_CALLBACK_OVERRUN` | 回调正常返回但超过 overrun 阈值 | 缩短回调耗时；该阈值不是抢占式超时 |
| `E_DEBUG_RESOURCE_CONFLICT` | 同一 exact target 上已有活动的回调定义；回调体的写范围无法静态证明，因此不允许第二个干预 owner（effect 之前拒绝） | 改用或删除已有的回调定义；需要独立观察时安装 capture-only 观察（可共存且顺序确定） |
| `E_HOOK_LIMIT_EXCEEDED` | 定义、源码、State 或快照超出已声明的硬上限 | 按上限摘要缩小输入；`disable`/`remove` 仍然可用 |
| `E_HOOK_COMPILE_BUDGET_EXHAUSTED` | 当前域共享的编译产物预算耗尽（`debug.code.execute`、Hook 回调、program、pause evaluate 共用同一份计数；错误码保留既有名称） | 读取 `error.details` 的占用与剩余额度，停用或删除不再需要的定义，或 reload domain；删除不会回收已加载程序集，不会自动 reload |
| `E_HOOK_EVENT_CURSOR_EXPIRED` | cursor 早于事件保留窗口 | 用返回的 `oldestAvailableCursor` 重新开始读取 |
| `E_CONTINUATION_STALE` | cursor 绑定的 Editor session 或筛选条件已变化 | 不使用 cursor 重新读取当前尾页 |

### Debug Session 错误

`debug.session.*` 复用统一 envelope，并使用下列稳定业务错误码。session 定义只属于 canonical 项目路径加当前 `editorSessionId`，owner 不会从选择、上次调用或 CLI 进程推断。

| 错误码 | 含义 | 恢复 |
|--------|------|------|
| `E_DEBUG_LIMIT_EXCEEDED` | session 定义数、单 session 活动资源数或初始 state 字节数达到已声明上限；失败发生在任何 effect 之前 | 读取 `error.details.used` 与 `error.details.max`；先用 `debug.session.remove` 释放不再需要的已停止 session，或缩小 `initialState` 后重试 |
| `E_DEBUG_CLEANUP_PENDING` | `debug.session.remove` 的目标不是已停止 session，或该 session 仍有未完成的清理 | 先运行 `debug.session.stop`；待清理明细在 `error.details.cleanup`，可重复 `debug.session.stop` 重试 |
| `E_DEBUG_SESSION_NOT_FOUND` | 当前 Editor session 没有该 sessionId 的已加载定义 | 运行 `debug.session.list` 读取当前定义；`error.details.storeUsable` 与 `error.details.storeReason` 说明存储文件是否被判定为不可用 |
| `E_DEBUG_SESSION_LABEL_CONFLICT` | label 已被同一 Editor session 内另一个 session 定义使用 | 换一个当前未被使用的 label 后重试 |
| `E_DEBUG_SESSION_SCOPE_CONFLICT` | 请求的 `activationScope` 在当前 Editor 模式下永远无法激活 | 改用 `editor-and-play`，或先切换到匹配的 Edit/Play 模式再创建 |
| `E_DEBUG_SESSION_NOT_OPEN` | 目标 session 不是 `open`，不能接受新的计划注入；`error.details.state` 给出实际状态 | 用 `debug.session.info` 读取状态与待清理明细；为新调查建立新 session |
| `E_DEBUG_STORE_FAILED` | session 存储文件的单写者发布或语义 compare-and-swap 失败，终态无法证明 | 检查项目 `Library/UnityCli/debug` 可写性；先用 `debug.session.list` 观察当前状态，再决定是否提交新调用 |
| `E_DEBUG_RECIPE_INVALID` | `debug.session.import` 的 recipe 未通过 fail-closed 校验：内容、格式、checksum、路径、预算或被排除键（pause 现场、句柄、Thread/Task/delegate、未完成命令）。`error.details.reason` 给出稳定原因，并始终报告本次已加载而不可回收的候选程序集数量与字节 | 用 `debug.session.export` 重新导出同一 session 的 recipe；不要手改 recipe 文本，也不要把它套到另一个项目路径上 |
| `E_DEBUG_EXPORT_CURSOR_EXPIRED` | `debug.session.export` 的 cursor 指向的快照已被同一 session 的新导出替换、已因 Domain Reload 失效或 offset 越界 | 不带 cursor 再次调用 `debug.session.export` 冻结当前 publication，然后从第一页顺序读取 |
| `E_DEBUG_RUN_NOT_FOUND` | 当前 domain generation 没有该 `runId` 的存活证据窗口：从未创建，已被最旧摘要淘汰，或属于上一次 domain generation / 上一次 Editor 启动。`error.details` 给出 `runId`、`domainGeneration` 与当前已知窗口 | 用 `debug.run.list` 读取当前 generation 的窗口；需要新证据时用 `debug.run.begin` 建立新窗口。断言失败不会返回错误码，而是 `ok:true` + `verdict:failed`；证据不足返回 `ok:true` + `verdict:indeterminate` |

字段级与跨字段输入非法仍使用 `E_SCHEMA_INVALID`（例如 label 为空或超过声明字节上限）；continuation 与当前 Editor session 不再匹配时使用 `E_CONTINUATION_STALE`。

### Debug Trigger 与 Hook 所有权错误

`debug.hook.*` 的 listener 与 program binding 共用一套触发预算与一个 hook host。

| 错误码 | 含义 | 恢复 |
|--------|------|------|
| `E_DEBUG_RESOURCE_CONFLICT` | 该 exact target 上已有一个活动的任意干预 owner（回调定义或 pause 断点）：`error.details.scope` 为 `arbitrary-intervention`，`ownerKind` 为 `callback` 或 `pause-breakpoint`。pause 断点允许 `debug.pause.evaluate` 改参数、跳过原方法并写结果，因此与回调体共用同一干预权，两个 pause 断点不能落在同一 exact target 上 | 用 `debug.hook.replace` 或 `debug.hook.remove` 把该 target 的 owner 换成唯一的定义；只需要观察时安装 capture-only 或 snapshot 断点 |
| `E_DEBUG_TRIGGER_BUDGET_EXCEEDED` | 该 session 的计划注入与输入预算已经用尽，listener 在任何 effect 之前被拒绝 | 用 `debug.hook.remove` 释放不再需要的 listener，或用 `debug.session.info` 读取 `handles.listeners` 与 `handles.injections` |
| `E_DEBUG_SIGNAL_RECURSION` | host signal 的同步派发链达到声明递归深度，本次派发被拒绝 | 用 `debug.events.read` 确认哪条信号在自触发；把链式触发改成显式有限步骤，再重新提交 |
| `E_HOOK_OWNED_BY_PROGRAM` | 该 hook 定义由 program binding 拥有，直接的 `debug.hook.*` 变更会静默改掉 program-owned revision | 用 `debug.program.replace` 修改程序，或用 `debug.program.stop`/`debug.program.remove` 释放 binding |
| `E_HOOK_LISTENER_NOT_REARMED` | 有限命中 listener 在 Domain Reload 后不会被自动重新武装（旧启动的命中次数不得被当成仍然成立） | 用 `debug.hook.enable` 显式开始新的命中预算，或重新 `debug.hook.replace` 声明 listener |

### Debug Input 错误

`debug.input.info` 是纯读取；`debug.input.key`、`debug.input.pointer` 与 `debug.input.release` 只操作本 debug owner 为指定 session 建立的虚拟设备，从不 reset 真实设备或其他 owner 的设备，也从不安装任何包。缺少可选设备后端时状态是 `unavailable`，不是半套支持。

| 错误码 | 含义 | 恢复 |
|--------|------|------|
| `E_DEBUG_BINDING_UNSUPPORTED` | 没有可用的设备后端，或设备后端拒绝建立本 owner 的设备：`error.details.status` 为 `unavailable`，`backend` 为后端身份（无实现时为 `none`），`reason` 为 `backend-not-loaded` 或 `api-member-missing`，`missingApiMembers` 给出缺失的 API 成员，`installedByThisOwner` 恒为 `false`。失败发生在任何 effect 之前 | 用 `debug.input.info` 读取后端状态；改用通用输入路径驱动这个输入：`debug.method.invoke` 调业务入口、`debug.hook.install` 干预可拦截的 managed wrapper、`debug.code.execute` 执行显式代码，或 `debug.program.run` 做有界跨线程投递 |

输入路径复用的既有错误码含义在这里同样是 effect 前拒绝：

| 错误码 | 输入路径上的含义 |
|--------|------|
| `E_DEBUG_SCOPE_UNSUPPORTED` | 本次调用不在 Unity 主线程上，或没有可读的 Game 视图/屏幕尺寸：`error.details.rule` 为 `off-main-thread` 或 `target-space-unresolved`，因此坐标空间无法在 effect 之前固定 |
| `E_DEBUG_RESOURCE_CONFLICT` | `error.details.reason` 为 `device-name-owned-elsewhere`（同名设备已存在且不是本 owner 建立的）或 `hold-conflict`（同一控制已有不同结束边界的未释放记录） |
| `E_DEBUG_TRIGGER_BUDGET_EXCEEDED` | 该 session 的计划注入与输入共用预算已用尽；输入下发与 listener 一起计入 |
| `E_DEBUG_LIMIT_EXCEEDED` | 该 session 的记录预算已满且没有已确认释放的记录可淘汰时拒绝新的按下（`error.details.limit` 为 `input-records`）；分配资源槽位失败同样在此码下报告 |

释放语义不表达为错误码：没有被主线程读回确认的释放一律以 `ok:true` 加 `releaseState:"pending"` 与精确原因返回，因为“还没确认”是事实而不是失败。

### Debug Program 错误

`debug.program.install/replace/run/wait/stop/info/list/remove` 管理 session 内调试程序的定义、revision 与运行。`wait`、`info`、`list` 是纯读取。

| 错误码 | 含义 | 恢复 |
|--------|------|------|
| `E_DEBUG_PROGRAM_NOT_FOUND` | 当前 domain 没有该 programId 的已加载定义（含域重载后旧身份作废） | 运行 `debug.program.list` 读取当前定义；必要时重新 `debug.program.install` |
| `E_DEBUG_PROGRAM_RUN_NOT_FOUND` | 没有该 programRunId 的运行记录 | 用 `debug.program.info` 读取该 program 的活动运行 |
| `E_DEBUG_PROGRAM_SESSION_REQUIRED` | 未提供 `sessionId`：program 必须属于明确的 session owner | 先运行 `debug.session.create` 建立 owner |
| `E_DEBUG_PROGRAM_SESSION_STOPPED` | 所属 session 不是 `open`，不再接受新 program 或新运行 | 用 `debug.session.info` 读取状态；为新调查建立新 session |
| `E_DEBUG_PROGRAM_SESSION_UNAVAILABLE` | 请求的 `sessionId` 在当前 Editor session 中不存在；`error.details.sessionError` 给出底层错误码 | 运行 `debug.session.list` 读取当前 session 定义 |
| `E_DEBUG_PROGRAM_LABEL_CONFLICT` | label 已被同一 session 内另一个 program 定义使用 | 换一个 label 后重试 |
| `E_DEBUG_PROGRAM_REVISION_CONFLICT` | `expectedRevision` 与当前 revision 不匹配；旧配置未被改动，`error.details.currentRevision` 给出实际值 | 用 `debug.program.info` 读取当前 revision 后重新提交 replace |
| `E_DEBUG_PROGRAM_STATE_CONFLICT` | 候选准备期间 state epoch 已变化；旧配置与旧状态被保留，不自动合并 | 读取 `debug.program.info` 的 stateEpoch 后重新提交完整候选状态 |
| `E_DEBUG_PROGRAM_WRONG_STATE` | 已 `stopped` 的定义不接受 replace 或 run | 重新 `debug.program.install`，或对已停止定义执行 `debug.program.remove` |
| `E_DEBUG_PROGRAM_RUN_ACTIVE` | 同一 program 已有活动运行、仍有在途调用，或 remove 目标仍持有活动运行 | 用 `debug.program.wait` 观察或 `debug.program.stop` 关闭该运行；`error.details.activeRuns` 给出数量 |
| `E_DEBUG_PROGRAM_DRIVER_CONFLICT` | 该 program 已拥有一个持续 step driver，第二个 driver 在任何 effect 之前被拒绝 | 先 `debug.program.stop` 关闭当前运行，再提交新的 driver 运行 |
| `E_DEBUG_PROGRAM_ENTRY_MISSING` | 定义不包含该 entry 名，或该 entry 在当前 domain 没有已解析方法 | 用 `debug.program.info` 读取已声明 entry；必要时 `debug.program.replace` 在当前 domain 重新发布 |
| `E_DEBUG_PROGRAM_ENTRY_INVALID` | entry 未匹配其类别签名、step 未返回 `UnityCliDebugStepResult`、或声明了不存在的 next entry | 按 `error.details.signatures` 修正签名后重新 install/replace |
| `E_DEBUG_PROGRAM_ENTRY_SITE_MISMATCH` | 执行点与 entry 类别不相容（`error.details.category`/`executionSite`） | step/oneshot 用 `editor-tick`；method/oneshot 用 `method-prefix`、`method-postfix` 或 `worker-thread`；listener/oneshot 用 `signal`；cleanup entry 只能由宿主在停止时调用 |
| `E_DEBUG_PROGRAM_ENTRY_FAILED` | entry 在运行时抛出；该运行进入 `failed`，已发生的效果不回滚 | 用 `debug.events.read` 读取该 program 的 fault 事件，修正后 `debug.program.replace` |
| `E_DEBUG_PROGRAM_CLEANUP_FAILED` | 程序的 cleanup entry 抛出；清理未完成的事实被记录，不伪装成已清理 | 用 `debug.program.info` 读取未完成情况，修正 cleanup entry 后重新运行 |
| `E_DEBUG_PROGRAM_ABI_INVALID` | 编译产物中的 entry 未通过精确类别签名校验（含 async void、歧义重载、未标记 wrapper、缺失类型）；拒绝发生在任何 effect 之前 | 按 `error.details.signatures` 与 `error.details.failure` 修正 entry 声明后重新提交 |
| `E_DEBUG_PROGRAM_BINDING_INVALID` | binding 的 phase 与 entry 类别不一致、bindingId 重复，或 target 缺失 | 让 prefix/postfix binding 指向 method entry，signal binding 指向 listener entry |
| `E_DEBUG_PROGRAM_BINDING_NOT_PREPARED` | 已声明的 binding 没有可用的挂载计划，因此不能激活 | 用相同 bindings 重新提交 `debug.program.replace`，让编译与挂载计划一起准备 |
| `E_DEBUG_PROGRAM_SITE_INVALID` | `executionSite` 不是已声明取值 | 使用 `error.details.availableSites` 中的一个值 |
| `E_DEBUG_PROGRAM_SITE_UNAVAILABLE` | `player-loop-phase` 只在 Editor Play 下存在，Edit 模式拒绝该 phase 现场（`debug.hook.install/replace` 的 `listener.trigger` 请求同一取值时共用同一错误码），绝不用 editor tick 冒充 | 进入 Editor Play 后重试；确实需要中央 editor tick 时用 `executionSite` `editor-tick`，或 `method-prefix`/`method-postfix`/`signal`/`worker-thread`；listener 改用 `method-hit`、`log` 或 `host-signal` |
| `E_DEBUG_PLAYER_LOOP_PHASE_UNKNOWN` | 声明的 phase 名称不在封闭集合内（`before-update`、`after-update`、`fixed-update`、`late-update`），或 `phase` 出现在没有声明 phase 执行点/触发源的调用里 | 使用 `error.details.availablePhases` 中的一个取值；同一错误只在 `executionSite: player-loop-phase` 或 `listener.trigger: player-loop-phase` 时才提交 `phase` |
| `E_DEBUG_PHASE_TRIGGER_REQUIRES_INVOCATION` | `listener.trigger: player-loop-phase` 上声明了参数条件（`conditions[].kind: argument`）或实例身份筛选（`instanceIdentity`）：一个 phase 到达没有方法实例也没有参数，这种组合永远不可能触发 | 改用字段条件（`conditions[].kind: field`，在 phase 自己的主线程上求值）；确实需要参数或实例筛选时把 `listener.trigger` 改为 `method-hit` |
| `E_DEBUG_PLAYER_LOOP_UNAVAILABLE` | Editor 当前 PlayerLoop 没有该 phase 的锚点或 `PlayerLoop.SetPlayerLoop` 失败，因此没有安装任何子系统（不是调用方输入问题） | 保持 Editor Play 运行；重进 Play 让默认 PlayerLoop 重新建立后重试，并用 `scheduler.playerLoop.installedPhases` 复核实际安装情况 |
| `E_DEBUG_PROGRAM_SOURCE_UNREADABLE` | 声明的 `.cs` 文件不存在或不可读 | 检查路径与权限；文件只在 prepare 阶段读取一次 |
| `E_DEBUG_PROGRAM_SIGNAL_TARGET_UNBOUND` | signal binding 指名了一个没有任何活动 hook 或 program binding 绑定的 exact target，投递永远无法取得执行权 | 在 program 上为同一 target 加一个 prefix/postfix binding，或先用 `debug.hook.install` 绑定该 target，或省略 `bindings[].target` 改为投递给中央 `editor-tick` phase |
| `E_DEBUG_PROGRAM_DOMAIN_CHANGED` | 准备与提交之间 Unity domain 已变化，已准备的程序被丢弃 | 在当前 domain 重新提交同一个 `debug.program.install` |
| `E_DEBUG_PROGRAM_RUN_EXPIRED` | pending 运行在取得执行权前已到期，执行权被关闭且永不迟到执行；dispatch 执行点（`method-prefix`、`method-postfix`、`worker-thread`、`signal`）的运行在目标命中前始终是 pending，因此目标始终不到达时它由声明时长以本错误关闭为 `cancelled` | 用 `debug.program.run` 重新排定一次运行 |
| `E_DEBUG_PROGRAM_RUN_CANCELLED` | 运行的未来步骤被关闭（`debug.program.stop` 或声明时长到期）；已进入的同步代码只报告在途 | 用 `debug.program.wait` 读取最终状态；需要时重新运行 |
| `E_DEBUG_PROGRAM_RUN_INTERRUPTED` | runtime 段变化或 session 停止中断了运行 | 读取 `debug.program.info` 后在新 runtime 段重新运行 |
| `E_DEBUG_PROGRAM_WAIT_TIMEOUT` | step 声明的等待计划在超时内未被满足，driver 停止 | 用 `debug.program.wait` 读取终态；修正等待条件后重新运行 |
| `E_DEBUG_PROGRAM_TERMINAL_PENDING` | 终态无法发布，因为原同步代码仍在途；系统不声称已终止 | 稍后用 `debug.program.wait` 重新观察该运行 |

program 的预算拒绝复用 `E_DEBUG_LIMIT_EXCEEDED`（`error.details.limit` 为 `programs`、`active-runs`、`active-resources`、`program-source-bytes`、`program-state-bytes` 或 `program-args-bytes`）；共享编译产物预算耗尽使用 `E_HOOK_COMPILE_BUDGET_EXHAUSTED`；源码编译失败使用 `E_CODE_COMPILE_FAILED`（`error.details.stage` 为 `program` 或 `program-replace`）。

### Debug Thread 与 Value 错误

`debug.thread.list` 是纯读取。`debug.value.read` 与 `debug.value.write` 复用现有根解析（静态字段、精确 Unity selector、当前托管句柄）与有界字段路径，并使用下列稳定业务错误码。

| 错误码 | 含义 | 恢复 |
|--------|------|------|
| `E_DEBUG_SCOPE_UNSUPPORTED` | 请求的形状不在结构化字段读取/写入支持的纯读取与类型检查写入范围内：`error.details.rule` 给出具体规则（`property-getter-not-executed`、`array-index-required`、`member-not-a-field`、`static-type-not-initialized`、`guard-requires-comparable-member`、`handle-requires-reference-root`、`root-object-write-not-supported`、`logical-clock-value-not-numeric`、`path-member-null`）。失败发生在任何 effect 之前 | 需要实际执行这些效果时使用 `debug.code.execute`；`static-type-not-initialized` 表示声明类型有静态构造函数，纯读取不能证明它已运行；`member-not-a-field` 表示字段名不存在；`path-member-null` 表示路径中间成员为 null，`debug.value.write` 不会创建中间对象 |
| `E_DEBUG_FIELD_NOT_WRITABLE` | `debug.value.write` 的目标字段不是可变状态：`error.details.rule` 为 `field-is-literal`（const）或 `field-is-init-only`（readonly）。拒绝发生在任何 effect 之前 | 用 `debug.value.read` 读取该类型的声明字段，改向一个非 const、非 readonly 的可变字段；显式代码执行也不能改写 const 字段或对象构造后的 readonly 字段 |
| `E_DEBUG_TARGET_STALE` | 根句柄或已解析字段目标已失效：句柄已被回收、Unity 对象已销毁，或 runtime 与 domain generation 已变化。`error.details.handleState`/`reason` 给出实际状态；代码从未把“GC 后找不到”当成字段为 null，也从不隐式重绑 | 用 `debug.value.read` 重新读取根并取新的 handleId；句柄不跨 runtime 与 reload |
| `E_DEBUG_CLEANUP_PENDING` | 传入的 session 不是 open：已停止或正在停止的 session 不再接收新的句柄与条件恢复记录 | 用 `debug.session.info` 读取当前 session 状态与清理明细，或建立新 session |
| `E_DEBUG_LIMIT_EXCEEDED` | 字段路径分段数、提交 JSON 值字节数，或每 session/全局对象句柄数量达到已声明上限；判定发生在任何 effect 之前 | 读取 `error.details.used/max` 缩短路径或值；先 `debug.session.stop` 释放不再需要的句柄后再重试 |

### Debug Pause 错误

断点配置、暂停现场与现场操作使用下列稳定错误码；全部拒绝都发生在任何 effect 之前，失败后现场仍可 `debug.pause.inspect` 与 `debug.pause.resume`。

| 错误码 | 含义 | 恢复 |
|--------|------|------|
| `E_DEBUG_PAUSE_STALE` | `pauseId` 已 resume、已超时、已被 session stop / hook disable / remove / Play 退出 / 域重载 / Editor 关闭释放，或从未在本 Editor session 与 domain generation 发布过。`error.details.state`/`reason` 给出实际终态；现场永不复活，pauseId 永不复用 | 用 `debug.pause.list` 读取当前仍停住的现场；需要新断点就重新提交 `debug.hook.install` 或 `debug.hook.replace` |
| `E_DEBUG_PAUSE_OPERATION_FAILED` | 现场操作（`debug.pause.inspect`、`debug.pause.evaluate` 或命名了 `pauseId` 的值读写）在暂停线程上自身抛出；现场保持 paused，用户代码已发生的效果不回滚 | 用 `debug.pause.list` 确认现场仍停住，再用 `debug.pause.inspect` 读取现场后重新提交更小的操作，或 `debug.pause.resume` 释放该现场 |
| `E_DEBUG_MAIN_THREAD_PAUSED` | 主线程正停在一个 debug pause 断点上，因此本次业务调用在任何 effect 之前被拒绝：因为正常队列由被停住的主线程驱动，排队只会变成无界等待。`error.details` 给出 `pauseId`、`threadId`、`timeoutKind` 与 `remainingMs`，以及 `error.nextActions` 指向的 `debug.pause.list` 与 `debug.pause.resume`。它不是 `E_UNITY_COMPILING`/`E_UNITY_IMPORTING` 的替代说法，也不是排队中 | 先用 `debug.pause.list` 读现场，再按需用 `debug.pause.resume` 释放主线程；等待期间 `debug.pause.list/wait/inspect/evaluate`、`debug.events.read`、`debug.hook.list/info`、`debug.session.list/info`、`debug.program.list/info`、`debug.thread.list`、命名了 `pauseId` 的值读写与 `tools` discovery/describe 仍可用 |
| `E_DEBUG_COMPILE_BUDGET_EXCEEDED` | 现场求值的共享编译没能在 `DebugLimits.PauseOperationTimeoutMs`（10000 ms）的 on-site 操作预算内产出可用结果，因此本次求值没有编译、加载或运行任何用户代码；`error.details.stage` 为 `reference-resolution`、`compile` 或 `compile-output`，`error.details.budgetMs` 为实际预算。它不是编译失败（源码未报错），也不是用户代码没返回（用户代码从未开始），暂停现场保持安装且 `debug.pause.inspect`/`debug.pause.resume` 仍可达 | 用 `debug.pause.list` 确认现场仍停住；先在非暂停路径用 `debug.code.execute` 以同一源码预热唯一共享编译器，再重新提交 `debug.pause.evaluate`；或 `debug.pause.resume` 释放现场后在非暂停路径求值 |
| `E_DEBUG_CONTROL_PLANE_UNAVAILABLE` | 暂停控制通道尚未在本 domain generation 发布，此时安装 pause 断点会让现场没有可达的解除路径，因此在 effect 之前被拒绝 | 让 Unity 完成编译与 domain reload 使 Bridge 重新发布控制通道后再安装 pause 断点；只需要取值时改用 snapshot 断点 |

pause mailbox 数量上限复用 `E_DEBUG_LIMIT_EXCEEDED`（`error.details.limit` 为 `pause-mailbox-waiting`）；缺少 `pauseTimeout`、`pauseTimeout.durationMs` 为 0 / null / 负数、`unbounded` 携带 `durationMs` 以及 snapshot 断点携带 `pauseTimeout` 都属于 `E_SCHEMA_INVALID`，绝不解释为无限；现场编译与加载产物失败复用 `E_CODE_COMPILE_FAILED`、`E_CODE_ENTRYPOINT_INVALID`、`E_CODE_RUNTIME_EXCEPTION`、`E_UNITY_BUSY` 与 `E_HOOK_COMPILE_BUDGET_EXHAUSTED`；共享编译没在 on-site 操作预算内产出结果使用 `E_DEBUG_COMPILE_BUDGET_EXCEEDED`，绝不复用 `E_EXECUTION_TIMEOUT` 的“用户代码未返回”含义。

主线程暂停期间，控制通道在发起请求的线程上直接服务可达能力，绝不排到被停住的主线程队列后面；不可达的业务 mutation 在任何 effect 之前返回 `E_DEBUG_MAIN_THREAD_PAUSED`。`debug.pause.evaluate` 的现场编译仍在现场线程上按同一有界预算推进，取不到共享编译器时按 `E_UNITY_BUSY` 返回忙并保留现场（仍可 inspect 与 resume），绝不等待旧的主线程 continuation。

已接受的长任务在读取自身状态时命中该拒绝，只表示这次观察暂时不可用，而不是任务失败：`test.run` 的 `internal.test.run.status` 读取会继续轮询到本次请求的 immutable deadline，因为解除路径在任何 pause 资源安装之前就已发布，有限暂停还会按自己的声明边界自动解除。只有该暂停在 deadline 结束时仍在，调用方才收到原始 `E_DEBUG_MAIN_THREAD_PAUSED`（含实际 `pauseId`、`remainingMs` 与 `debug.pause.list`/`debug.pause.resume` 路由）；解除现场后用同一 `requestId` 重提即可取回该 run 的 durable terminal，不丢已确认的运行。

主线程触发（`debug.program.run` 的主线程执行点与 `debug.code.execute`）自身到达已声明的 main-thread pause 时，就是另一种事实：请求已经发出、可能已经生效，但停住的主线程不可能再写完终态，因此终态永远不会到达。此时失败报 `E_DEBUG_MAIN_THREAD_PAUSED` 而不是 `E_EXECUTION_TIMEOUT`，`error.details.outcome` 固定为 `may-have-started`（绝不是 `unknown`），并带上当前 live publication 的现场事实 `pauseId`/`threadId`/`timeoutKind`/`remainingMs` 与指向 `debug.pause.list`/`debug.pause.resume` 的 `nextActions`；`error.message` 如实说明该次 dispatch 未能证明，`error.transient` 为 false 且 `retryAfterMs` 为 0。它不是失败结论，也不证明 entry 或用户代码没有执行，更没有自动重投、预占或强制解除：调用方先用 `debug.pause.list` 读现场、用 `debug.pause.resume` 释放，再读回业务状态后自行决定是否提交全新请求；program 运行仍保有自身状态机，释放后用同一 `programRunId` 读回即可。

该 typed partial outcome 也覆盖调用方自己的 `--timeout-ms` 先到、暂停仍被持有的情形，且对 `debug.code.execute` 这类本地/hybrid Tool 同样成立：它们的失败信封只在本次调用确实跨过 dispatcher 的首个 effect barrier 时才声明 `dispatch`（`debug.code.execute` 在代码源与引用校验完成后、Bridge dispatch 之前提交该 barrier），未跨过 barrier 的失败仍是原始 `E_EXECUTION_TIMEOUT` 与 `outcome:unknown`；而暂停短于剩余预算时请求正常等待、暂停自到期并返回真实结果，不进入该 partial outcome。

该 typed partial outcome 的信封形状固定，调用方不需要从文字猜测它是不是失败：`ok` 为 false，`error.code` 为 `E_DEBUG_MAIN_THREAD_PAUSED`，分类事实全部位于 `error.details` —— `outcome` 与 `dispatch` 恒为 `may-have-started`、`replay` 为 `new-decision-required`，`pauseTool`/`resumeTool` 为 `debug.pause.list`/`debug.pause.resume`，`error.transient` 为 false、`error.retryAfterMs` 为 0。现场边界事实 `timeoutKind`/`remainingMs` 来自当前 live publication 并原样带出；`details.pauseFacts` 说明它们是否读到：读到为 `read`，只读到 `pauseId`/`threadId` 而边界事实不可读时为 `not-read-in-time`，此时 `error.message` 明确写出边界事实未被读到，绝不用旧值或默认值补写。归因读取本身有有界预算（`DISPATCH_PAUSE_DIAGNOSIS_BUDGET_MS` 为 5000 ms，其中单次 health 探测不超过 300 ms），且该预算独立于已经耗尽的业务 deadline；读不到任何暂停证据时原 `E_EXECUTION_TIMEOUT`/`outcome:unknown` 保持不变，绝不把暂停当成超时的通用解释。

deadline 关系与实际约束：`pauseTimeout.durationMs` 只约束暂停现场，`debug.program.run` 的 `durationMs` 只约束该次 run，而本次调用由调用方自己的 `--timeout-ms` 约束（默认 60000 ms，最大 600000 ms）；主线程暂停期间单次 off-main 控制等待的硬上限是 30000 ms。因此比剩余请求预算更长的暂停不可能被持有它的那个请求观察到，需要终态答案或及时读回的调用方只能三选一：让暂停从 dispatch 执行点（`method-prefix`/`method-postfix`/`worker-thread`）触发，使创建结果在暂停开始前就已经答复；或把 `pauseTimeout.durationMs` 声明得短于 30000 ms；或声明 `unbounded` 并用 `debug.pause.list`/`debug.pause.resume` 显式解除。暂停确实更长时，上述 typed partial outcome 就是文档化的最终答复。

### 执行错误

| 错误码 | 含义 |
|--------|------|
| `E_UNITY_BLOCKED` | effect 前已证明 Unity/UI blocker；原错误的 `details.blockers` 含结构化弹窗摘要 |
| `E_UNITY_BUSY` | 任务池、修改owner或实例清理owner正在工作；按message与`nextActions`区分是否尚未发送业务请求，`instances prune`可按`retryAfterMs`稍后重试 |
| `E_UNITY_TRANSITION_TIMEOUT` | 首次ready记录或可恢复状态变化未在原命令截止时间内稳定；确认Unity仍在推进后才使用更大的timeout重试 |
| `E_EXECUTION_TIMEOUT` | 已放行调用或本地执行未在既有absolute deadline内返回终态、连接提前断开，或首次mutation POST后Editor session改变；outcome unknown，公开terminal固定且迟到settlement不能改写或重投effect |
| `E_REQUEST_ID_REUSED` | 同一 root request ID 被不同 exact request bytes 使用 |
| `E_TOOL_COMPLETION_PROOF_INVALID` | Tool 返回值不满足其 completion contract 或 proof 校验 |
| `E_UNITY_COMPILE_REQUIRES_EDIT_MODE` | `editor.compile` 在稳定 Play Mode 或 queued-before-effect PlayMode 中被拒绝 |
| `E_UNITY_COMPILE_FAILED` | owned compilation cycle 完成且包含 compiler error；details 带本次 diagnostics |
| `E_CODE_COMPILE_FAILED` | Runtime Code 编译失败 |
| `E_CODE_RUNTIME_EXCEPTION` | Runtime Code 执行异常；message 指向最深可操作 cause，details.causes 为循环安全且受深度/字节限制的 cause chain |
| `E_IO_ERROR` | IO 错误 |

## AI Agent 错误处理流程

1. 读取 `error.code` 确定错误类别
2. 将 `transient`、`retryAfterMs` 和 `nextActions` 作为恢复建议，不把它们当成权限门
3. 任务池准入`E_UNITY_BUSY`且message证明`no business request was sent`时，可以稍后提交新root；mutation lease busy不得声称effect未开始
4. `E_EXECUTION_TIMEOUT`只说明没有可信终态；先观察业务状态，再形成全新提交决策
5. 同session的compile/import/reload/listener transition由supervisor在放行后的同一execution deadline内处理；cross-session mutation禁止自动重投

### nextActions 规则

- Instance 错误的 `nextActions` 必须指向 `instances list`、显式 `--instance`、项目路径或关闭重复 Editor
- Runtime 错误的 `nextActions` 必须指向 `tools run editor.play-mode.start` 或 Play Mode 相关操作
- Discovery 错误的 `nextActions` 必须指向 `tools group`、`tools group list`、`tools search <k1|k2|...>` 或 `tools describe`
- 弹窗 blocker 的 `nextActions` 必须指向先 `tools run editor.dialogs`，再 `tools run editor.dialog.click`；不得要求人工处理、预定义按钮列表、launch-only 入口或额外确认
- 诊断错误的 `nextActions` 必须指向顶层 `doctor` 或对应 owner（`instances list`、`editor.state`、`extensions doctor`、Unity Console）

## JSON 字段命名

所有错误响应使用 camelCase：
- `nextActions`
- `retryAfterMs`

错误恢复逻辑必须读取上述 camelCase 字段。

