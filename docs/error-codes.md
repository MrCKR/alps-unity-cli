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
    "details": { "instanceId": "unity-...", "status": "conflict" },
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

普通Tool在业务effect前确认阻断时返回`E_UNITY_BLOCKED`，同一响应的`details.blockers`包含已清洗blocker。同一次调用只在`editorSessionId`未改变时续接完全相同的请求；首次mutation POST后若session改变，禁止向新session再次POST。超时或断连统一返回`E_EXECUTION_TIMEOUT`与outcome unknown；调用方先观察业务状态，再作新的提交决策。

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
| `E_INIT_SKILL_TARGET_REQUIRED` | 既未给出明确Skill目录，也未给出预设agent/scope | 使用`--target <SkillDirectory>`，或选择一个预设目标 |
| `E_INIT_SKILL_TARGET_CONFLICT` | 明确`--target`与预设agent/scope参数混用 | 保留明确目录或预设形式之一 |
| `E_INIT_SKILL_TARGET_INVALID` | 明确目标不是安全的本地`unitycli`目录，或位于安装事务目录 | 传独立的固定本地完整目录，例如`.pi/skills/unitycli` |
| `E_SKILL_TARGET_OVERLAPS_SOURCE` | 安装目标与npm内置Skill准源互相包含 | 选择独立安装目录 |
| `E_SKILL_TARGET_OWNERSHIP_UNPROVEN` | 非空目标没有UnityCLI `SKILL.md`，不能证明归安装器所有 | 选择准确UnityCLI目录或先清空明确的新目标 |
| `E_SKILL_TARGET_UNREADABLE` | 目标因权限或文件占用无法只读验证 | 恢复读取权限后重试；不得把不可读当成损坏并覆盖 |

### Project Identity / Instance 错误

| 错误码 | 含义 | 恢复 |
|--------|------|------|
| `E_INSTANCE_REQUIRED` | 没有显式 selector，CWD 也不在 Unity 项目中 | 传完整 `--instance <id|projectPath|alias>` 或从项目目录调用 |
| `E_INSTANCE_NOT_FOUND` | selector 在当前截止时间内没有对应项目记录 | 核对项目路径/alias；完整instance ID在目标Unity启动时会等待首次记录 |
| `E_INSTANCE_NOT_RUNNING` | 项目 identity 已确定，且没有可验证的运行中 Editor | 打开该项目；CLI会在本次调用内等待首次ready记录 |
| `E_INSTANCE_CONFLICT` | 至少两个已验证live process claim声明同一项目 | 关闭重复Editor，只保留目标session |
| `E_INSTANCE_UNVERIFIABLE` | PID/start、project claim、Registry读取或权限证据不完整 | 重新观察identity；不得把未知mutation自动重投 |
| `E_INSTANCE_IDENTITY_MISMATCH` | 当前Unity无法在截止时间内重建并证明一致的项目身份 | 更新匹配的CLI/Package，打开该项目并重新观察；不要手工修改内部记录 |
| `E_UNITY_UNRESPONSIVE` | transport identity存在，但近期Unity主线程pulse或Bridge响应不可证明 | 读取`editor.state`与`editor.dialogs`后处理transition/modal |
| `E_INSTANCE_PROJECT_MISMATCH` | 显式项目约束与 selector 指向不同 identity | 统一 `--project` 和 `--instance` |
| `E_INSTANCE_PATH_UNAVAILABLE` | 无法取得可证明的物理 canonical path | 修复不可访问路径、broken link 或 reparse 配置 |
| `E_INSTANCE_REMOTE_PATH_UNSUPPORTED` | 项目位于不支持的远程路径 | 使用本机可验证项目路径 |

### Runtime / Play Mode 错误

| 错误码 | 含义 | 恢复 |
|--------|------|------|
| `E_RUNTIME_NOT_READY` | Runtime 工具需 Play Mode | 先 `tools run editor.play`，transient 可重试 |
| `E_RUNTIME_SESSION_STALE` | 运行时会话过期 | 重新进入 Play Mode |
| `E_RUNTIME_TIMEOUT` | 运行时操作超时 | 增加 `--timeout-ms` 或重试 |

### Tool / Discovery 错误

| 错误码 | 含义 | 恢复 |
|--------|------|------|
| `E_TOOL_NOT_FOUND_FOR_TARGET` | 名称不属于当前实例完整 Tool 目录 | 重新运行 `tools group` / `tools search <k1|k2|...>` |
| `E_TOOL_REGISTRY_UNHEALTHY` | 当前实例 Tool catalog 含 malformed descriptor、重复名称或 ownership 冲突 | 修复实例 registry 后重新 discovery |
| `E_SCHEMA_INVALID` | 输入不符合 schema | 查看对应 `tools describe` 的 inputSchema |

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
| `E_PROJECT_INPUT_AXIS_NOT_FOUND` | 没有完全同名的legacy Input Manager axis | 先分页读取`project.input-settings`并传精确名称 |
| `E_PROJECT_SETTINGS_TARGET_CHANGED` | 冻结的Input axis index/name在effect前漂移 | fresh读取Input设置后提交新请求 |
| `E_PROJECT_SETTINGS_VERIFY_FAILED` | typed readback或ProjectSettings文件SHA未证明持久化终态 | 读取对应`project.*-settings`后再决定是否重试 |

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
| `E_METHOD_NOT_FOUND` | 方法名或显式参数签名不存在 | 运行 `reflection.method.find` 并核对 `parameterType` |
| `E_METHOD_AMBIGUOUS` | 多个 overload 与参数数量匹配 | 将选中签名的 `parameterType` 数组作为 `parameterTypes` 重试 |
| `E_TYPE_NOT_FOUND` | 目标类型或参数类型不存在 | 使用完整 C# type name |

### 执行错误

| 错误码 | 含义 |
|--------|------|
| `E_UNITY_BLOCKED` | effect 前已证明 Unity/UI blocker；原错误的 `details.blockers` 含结构化弹窗摘要 |
| `E_UNITY_BUSY` | 任务池、修改owner或实例清理owner正在工作；按message与`nextActions`区分是否尚未发送业务请求，`instances prune`可按`retryAfterMs`稍后重试 |
| `E_UNITY_TRANSITION_TIMEOUT` | 首次ready记录或可恢复状态变化未在原命令截止时间内稳定；确认Unity仍在推进后才使用更大的timeout重试 |
| `E_EXECUTION_TIMEOUT` | 已放行调用未在execution deadline内返回终态、连接提前断开，或首次mutation POST后Editor session改变；outcome unknown，CLI不向新session自动重投 |
| `E_REQUEST_ID_REUSED` | 同一 root request ID 被不同 exact request bytes 使用 |
| `E_TOOL_COMPLETION_PROOF_INVALID` | Tool 返回值不满足其 completion contract 或 proof 校验 |
| `E_UNITY_COMPILE_REQUIRES_EDIT_MODE` | `editor.compile` 在稳定 Play Mode 或 queued-before-effect PlayMode 中被拒绝 |
| `E_UNITY_COMPILE_FAILED` | owned compilation cycle 完成且包含 compiler error；details 带本次 diagnostics |
| `E_CODE_COMPILE_FAILED` | Runtime Code 编译失败 |
| `E_CODE_RUNTIME_EXCEPTION` | Runtime Code 执行异常 |
| `E_IO_ERROR` | IO 错误 |

## AI Agent 错误处理流程

1. 读取 `error.code` 确定错误类别
2. 将 `transient`、`retryAfterMs` 和 `nextActions` 作为恢复建议，不把它们当成权限门
3. 任务池准入`E_UNITY_BUSY`且message证明`no business request was sent`时，可以稍后提交新root；mutation lease busy不得声称effect未开始
4. `E_EXECUTION_TIMEOUT`只说明没有可信终态；先观察业务状态，再形成全新提交决策
5. 同session的compile/import/reload/listener transition由supervisor在放行后的同一execution deadline内处理；cross-session mutation禁止自动重投

### nextActions 规则

- Instance 错误的 `nextActions` 必须指向 `instances list`、显式 `--instance`、项目路径或关闭重复 Editor
- Runtime 错误的 `nextActions` 必须指向 `tools run editor.play` 或 Play Mode 相关操作
- Discovery 错误的 `nextActions` 必须指向 `tools group`、`tools group list`、`tools search <k1|k2|...>` 或 `tools describe`
- 弹窗 blocker 的 `nextActions` 必须指向先 `tools run editor.dialogs`，再 `tools run editor.dialog.click`；不得要求人工处理、预定义按钮列表、launch-only 入口或额外确认
- 诊断错误的 `nextActions` 必须指向 `doctor` 或 `tools run diagnose.preflight`

## JSON 字段命名

所有错误响应使用 camelCase：
- `nextActions`
- `retryAfterMs`

错误恢复逻辑必须读取上述 camelCase 字段。
