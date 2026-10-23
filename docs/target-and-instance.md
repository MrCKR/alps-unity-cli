# Target 与 Instance

> 发现路径：`instances list` -> 显式传`--instance`、`command --project-path`或从Unity项目CWD解析 -> discovery / describe / run

## 稳定项目身份

一个 Unity instance 表示由 **CanonicalPath v1** 确定的项目身份，不表示某个端口或进程。公开选择名是 `name`：默认取项目目录名，同名实例按 canonical project path 稳定排序追加 `-2`、`-3`，因此同一实例集合内名称唯一。`instanceId`（canonical project path 的 SHA-256 identity，`unity-<64 hex>`）只作为内部身份锚点，不进入 `instances list` 输出；Unity Editor 重启、domain reload 或 listener 换端口不会改变它。PID 只属于当前 Editor session，不参与 `instanceId`。

CLI 只接受本机可证明的 Unity 项目目录。目录大小写、尾分隔符、子目录、junction、directory symlink 和 SUBST 会先归约到同一物理项目根；broken link、reparse loop、不可访问目录或无法证明 case 语义时 fail closed。

## 查看实例

```powershell
unitycli instances list
unitycli instances list --all
unitycli instances prune --instance <name|alias|projectPath>
```

`instances list` 默认排除 `status="offline"` 的记录；存在隐藏项时返回 `hiddenOfflineCount`。只有需要审计历史/offline identity 时使用 `--all`，此时返回完整合法集合并省略 `hiddenOfflineCount`。每项公开字段固定为 `name`、可选 `alias`、`canonicalProjectPath`、`displayProjectPath`、`status` 和可选 `unityVersion`；`instanceId` 与 `projectName` 不再公开，`--all` 不会恢复内部 session、快照、进程 claim 或诊断字段。

实例目录按文件独立验证。旧格式、损坏、不可读或identity不匹配的记录不参与列表和路由，也不阻断其他合法Registry v3实例；`instances list`存在这类文件时返回`invalidRecordCount`。当前项目的Unity在启动或重载完成后会使用当前进程和连接重建自己的记录；CLI在能够证明目标Unity正在运行时等待该结果。不要兼容解析、转换、移动或手工删除内部记录。

实例状态：

| 状态 | 含义 |
|------|------|
| `ready` | 唯一session、listener identity与近期Unity主线程pulse均已证明 |
| `transitioning` | compile、import、domain reload、listener交接或其他转换正在进行 |
| `blocked` | 已证明存在需要外部处理的精确modal blocker |
| `unresponsive` | transport identity存在，但近期Unity主线程pulse或Bridge响应无法证明 |
| `unverifiable` | PID/start、project claim或文件观察不完整，无法安全证明唯一ownership；`editor.state.observation.reason`给出本次失败归因 |
| `offline` | 该项目没有已证明存活的Editor session |
| `conflict` | 至少两个已验证live process claim同属该project identity |

`instances prune --instance <name|alias|projectPath>`只授权选定的当前Unity执行有界历史清理，CLI本身不删除记录。Unity保留当前、存活、无法确认、链接或被占用的记录；关闭记录最多保留最近100个，损坏记录最多保留最近20个，30天前的记录优先清理。每次最多检查2000个、删除20个。结果中的`removed`、`remaining`、`deferred`、`scanned`和`truncated`描述本次真实结果；`deferred>0`时可在当前清理完成后再次调用。

不要读取 registry 文件、listener ID、端口或 URL，也不要直接调用 Bridge HTTP。transport binding 是 session supervisor 的内部职责。

## 选择规则

Tool discovery、describe、`tools run`与`command`使用同一严格选择规则：

1. 显式 `--instance <name|alias|projectPath>`。
2. 未传 selector 时，从 CWD 向上寻找最近的 Unity project root，并解析其 canonical identity。
3. 无法得到唯一项目身份时返回结构化 instance error。

目标项目已有唯一Unity进程但尚未发布记录、正在编译/导入/重载或正在更换连接时，CLI会在原命令截止时间内等待；Unity没有运行时不会进行无意义的完整等待。完整instance ID无法反推出项目路径，因此显式ID会直接等待其固定记录直到截止时间。

不存在按PID、短hash、单个存活Editor或任意健康实例自动选取的路径。只有至少两个已验证live process claim才返回`E_INSTANCE_CONFLICT`；权限失败、超时或PID/start/project identity无法证明时返回`E_INSTANCE_UNVERIFIABLE`。选定项目已证明无运行进程时返回`E_INSTANCE_NOT_RUNNING`。

`E_INSTANCE_UNVERIFIABLE`的`details.reason`区分`access-denied`、`timeout`、`unavailable`与`mismatch`。`access-denied`表示当前宿主拒绝OS进程查询，与Unity侧状态无关；此类`nextActions`指向宿主会话而不是Unity。归因只改变`nextActions`，不改变identity证明强度或fail-closed语义。

```powershell
unitycli tools describe editor.compile --instance <project-path>
unitycli tools run editor.compile --instance my-project --timeout-ms 120000
unitycli tools run editor.state --instance unity-<64-hex> --input '{}'
```

## Alias

`target` 只管理 canonical project identity alias，不保存全局活动目标：

```powershell
unitycli target alias set <name|projectPath> <alias>
unitycli target alias list
unitycli target alias unset <alias>
```

alias 绑定完整 `instanceId`、canonical project path 和 canonicalization version（绑定值本身不在公开输出里回显）。Editor offline 时 alias 仍可解析 identity，但 Tool discovery 要求运行中的 Editor 并返回 `E_INSTANCE_NOT_RUNNING`。`--instance` 或 CWD 始终决定本次调用的目标。

## Session Supervisor

普通 `tools run` 不直接绑定某个 endpoint。session supervisor 先在准入deadline内选择唯一project identity和已验证session，并等待该target session的任务池放行；放行后创建一份新的execution absolute deadline：

- 每个Editor或Development Player session独立维护默认5/3高低水位，不跨进程共享计数；
- 对一次transport尝试绑定listener generation；
- 首次记录尚未出现或在compile、import、domain reload、listener restart期间，等待当前ready generation，但不刷新execution deadline；
- 同一次mutation调用只在`editorSessionId`未改变时，使用完全相同的root request ID与request bytes续接durable canonical terminal；
- 首次mutation POST后若`editorSessionId`改变，禁止向新session再次POST，返回`E_EXECUTION_TIMEOUT`与outcome unknown；
- 准入deadline到达返回effect-before `E_UNITY_BUSY`并证明业务请求未发送；
- 放行后的超时或终态前断连返回`E_EXECUTION_TIMEOUT`，调用方必须先观察业务状态，再作新的提交决策。

普通读取型Tool的成功结果来自同一validated generation的完整读取。exact `editor.state`、`editor.dialogs`、`editor.dialog-click`和六个域专用durable status Tool使用frozen本地descriptor进入同一dispatcher，不依赖可能被transition、modal或业务mutation阻断的Live catalog，三个Editor观察Tool不执行普通业务admission acquire/revoke；域status仍严格选择目标实例、取得独立只读root admission并只发送域绑定SafeStatus读取，但不取得mutation lease。`editor.state`在同一dispatch absolute deadline内合并Registry v3、Health v3内存主线程pulse、一次private snapshot Bridge观察、PID/start、project claim和Windows modal证据，返回七态、freshness与本次identity证据归因；4秒/10秒子上限只能缩短该deadline。dialogs保持Windows-only，click仍重新证明exact PID/start/dialog/button并只执行一次effect。其他Tool与catalog listing仍严格依赖Live实例和普通准入。Domain Reload只由Registry lifecycle、新listener generation和该generation首个主线程pulse证明，不读取固定时长marker文件。

主线程被 debug pause 断点停住时，supervisor 的选择、准入与最后创建 execution deadline 的规则不变，但业务请求不再排到被停住的主线程队列后面：可达读取、现场操作与解除路径在发起请求的线程上直接服务，其他业务 mutation 在任何 effect 之前返回 `E_DEBUG_MAIN_THREAD_PAUSED`。该拒绝携带实际 `pauseId`、`timeoutKind` 与 `remainingMs`，并把下一步指向 `debug.pause-list` 与 `debug.pause-resume`；它不是编译或导入 busy。`editor.state` 在同一读取中也会单独证据化该暂停：只有当前 live pause publication 证明主线程暂停时才返回 `blocked` 与 `blocker.type="debug-pause"`；pulse 已陈旧但没有该证据时仍是 `unresponsive`，worker 现场不能被当成主线程卡死的原因。解除路径必须在任何 pause 资源安装之前已发布，否则 `debug.hook-install/replace` 在任何 effect 之前返回 `E_DEBUG_CONTROL_PLANE_UNAVAILABLE`。`pause-only` 断点不是一种有效行为：`debug.hook-install/replace` 仍要求至少一个捕获字段或回调代码，只有与断点一起声明时才被接受。触发线程决定调用方能否拿到终态：主线程命中会停掉答复它自己的 Editor 队列，该请求只能得到 typed partial outcome（`error.code` 为 `E_DEBUG_MAIN_THREAD_PAUSED`，`error.details.outcome`/`dispatch` 为 `may-have-started`，边界事实读不到时 `details.pauseFacts` 为 `not-read-in-time`）；`pauseTimeout.durationMs` 只约束暂停，且主线程暂停期间单次 off-main 控制等待硬上限为 30000 ms，所以需要终态时必须让暂停从 dispatch 执行点触发、或声明短于该上限的有限暂停、或声明 `unbounded` 并显式解除。

## Development Player Sidecar

Development Player只接受构建时注入的严格`runtime-config.json` version 2。sidecar包含canonical project/build identity和`taskPoolMaxActive`、`taskPoolReopenAt`；字段缺失、多余、非法，旧version，非development build或identity校验失败时，Runtime Bridge fail closed且不发布runtime session。Player每次重启创建新的runtime session和任务池generation，旧session的未消费grant与consumed identity不得进入新进程。
