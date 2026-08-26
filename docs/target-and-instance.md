# Target 与 Instance

> 发现路径：`instances list` -> 显式传`--instance`、`command --project-path`或从Unity项目CWD解析 -> discovery / describe / run

## 稳定项目身份

一个 Unity instance 表示由 **CanonicalPath v1** 确定的项目身份，不表示某个端口或进程。`instanceId` 是 canonical project path 的稳定 SHA-256 identity，格式为 `unity-<64 hex>`；Unity Editor 重启、domain reload 或 listener 换端口不会改变它。PID 只属于当前 Editor session，不参与 `instanceId`。

CLI 只接受本机可证明的 Unity 项目目录。目录大小写、尾分隔符、子目录、junction、directory symlink 和 SUBST 会先归约到同一物理项目根；broken link、reparse loop、不可访问目录或无法证明 case 语义时 fail closed。

## 查看实例

```powershell
unitycli instances list
unitycli instances list --all
unitycli instances prune
```

`instances list` 默认排除 `status="offline"` 的记录；存在隐藏项时返回 `hiddenOfflineCount`。只有需要审计历史/offline identity 时使用 `--all`，此时返回完整合法集合并省略 `hiddenOfflineCount`。每项公开字段固定为 `instanceId`、可选 `alias`、`canonicalProjectPath`、`displayProjectPath`、`projectName`、`status` 和可选 `unityVersion`；`--all` 不会恢复内部 session、快照、进程 claim 或诊断字段。

实例目录按文件独立验证。旧格式、损坏、不可读或identity不匹配的记录不参与列表和路由，也不阻断其他合法Registry v3实例；存在时`instances list`和`instances prune`增加`invalidRecordCount`。CLI不会兼容解析、转换、移动或删除这些记录，`doctor`以warn报告合法和无效数量。显式选择无效记录对应的instanceId仍严格失败；应更新或重启该项目使用的匹配Unity Package，而不是绕过校验。

实例状态：

| 状态 | 含义 |
|------|------|
| `ready` | 唯一session、listener identity与近期Unity主线程pulse均已证明 |
| `transitioning` | compile、import、domain reload、listener交接或其他转换正在进行 |
| `blocked` | 已证明存在需要外部处理的精确modal blocker |
| `unresponsive` | transport identity存在，但近期Unity主线程pulse或Bridge响应无法证明 |
| `unverifiable` | PID/start、project claim或文件观察不完整，无法安全证明唯一ownership |
| `offline` | 该项目没有已证明存活的Editor session |
| `conflict` | 至少两个已验证live process claim同属该project identity |

`instances prune` 不直接删除进程、无效记录或伪造状态；PID/start-time 无效 claim 和过期 offline record 只由 Unity Bridge registry writer 清理。`invalidRecordCount`只报告本次被隔离的文件数量。

不要读取 registry 文件、listener ID、端口或 URL，也不要直接调用 Bridge HTTP。transport binding 是 session supervisor 的内部职责。

## 选择规则

Tool discovery、describe、`tools run`与`command`使用同一严格选择规则：

1. 显式 `--instance <full-instance-id|canonical-project-path|alias>`。
2. 未传 selector 时，从 CWD 向上寻找最近的 Unity project root，并解析其 canonical identity。
3. 无法得到唯一项目身份时返回结构化 instance error。

不存在按PID、短hash、单个存活Editor或任意健康实例自动选取的路径。只有至少两个已验证live process claim才返回`E_INSTANCE_CONFLICT`；权限失败、超时或PID/start/project identity无法证明时返回`E_INSTANCE_UNVERIFIABLE`。选定项目已证明无运行进程时返回`E_INSTANCE_NOT_RUNNING`。

```powershell
unitycli tools describe editor.compile --instance <project-path>
unitycli tools run editor.compile --instance my-project --timeout-ms 120000
unitycli tools run editor.state --instance unity-<64-hex> --input '{}'
```

## Alias

`target` 只管理 canonical project identity alias，不保存全局活动目标：

```powershell
unitycli target alias set <projectPath|instanceId> <alias>
unitycli target alias list
unitycli target alias unset <alias>
```

alias 绑定完整 `instanceId`、canonical project path 和 canonicalization version。Editor offline 时 alias 仍可解析 identity，但 Tool discovery 要求运行中的 Editor 并返回 `E_INSTANCE_NOT_RUNNING`。`--instance` 或 CWD 始终决定本次调用的目标。

## Session Supervisor

普通 `tools run` 不直接绑定某个 endpoint。session supervisor 先在准入deadline内选择唯一project identity和已验证session，并等待该target session的任务池放行；放行后创建一份新的execution absolute deadline：

- 每个Editor或Development Player session独立维护默认5/3高低水位，不跨进程共享计数；
- 对一次transport尝试绑定listener generation；
- 在compile、import、domain reload或listener restart后重新验证generation，但不刷新execution deadline；
- 同一次mutation调用只在`editorSessionId`未改变时，使用完全相同的root request ID与request bytes续接durable canonical terminal；
- 首次mutation POST后若`editorSessionId`改变，禁止向新session再次POST，返回`E_EXECUTION_TIMEOUT`与outcome unknown；
- 准入deadline到达返回effect-before `E_UNITY_BUSY`并证明业务请求未发送；
- 放行后的超时或终态前断连返回`E_EXECUTION_TIMEOUT`，调用方必须先观察业务状态，再作新的提交决策。

普通读取型Tool的成功结果来自同一validated generation的完整读取。`editor.state`是唯一即时观察例外：它使用frozen本地descriptor进入同一dispatcher，不依赖可能被transition/modal阻断的Live catalog；随后在固定预算内合并Registry v3、Health v3内存主线程pulse、Bridge、PID/start、project claim和Windows modal证据，返回七态与freshness。`editor.dialogs`与`editor.dialog.click`同样保持catalog-independent recovery。其他Tool与catalog listing仍严格依赖Live实例。Domain Reload只由Registry lifecycle、新listener generation和该generation首个主线程pulse证明，不读取固定时长marker文件。

## Development Player Sidecar

Development Player只接受构建时注入的严格`runtime-config.json` version 2。sidecar包含canonical project/build identity和`taskPoolMaxActive`、`taskPoolReopenAt`；字段缺失、多余、非法，旧version，非development build或identity校验失败时，Runtime Bridge fail closed且不发布runtime session。Player每次重启创建新的runtime session和任务池generation，旧session的未消费grant与consumed identity不得进入新进程。
