# Target 与 Instance

> 发现路径：`instances list` -> 显式传 `--instance` 或从 Unity 项目 CWD 解析 -> discovery / describe / run / read

## 稳定项目身份

一个 Unity instance 表示由 **CanonicalPath v1** 确定的项目身份，不表示某个端口或进程。`instanceId` 是 canonical project path 的稳定 SHA-256 identity，格式为 `unity-<64 hex>`；Unity Editor 重启、domain reload 或 listener 换端口不会改变它。PID 只属于当前 Editor session，不参与 `instanceId`。

CLI 只接受本机可证明的 Unity 项目目录。目录大小写、尾分隔符、子目录、junction、directory symlink 和 SUBST 会先归约到同一物理项目根；broken link、reparse loop、不可访问目录或无法证明 case 语义时 fail closed。

## 查看实例

```powershell
unity_cli instances list
unity_cli instances prune
```

`instances list` 返回 project identity 和归约后的状态：

| 状态 | 含义 |
|------|------|
| `ready` | 唯一、已验证的 Editor session 和当前 listener generation 可用 |
| `transitioning` | compile、import、domain reload、listener restart 或其他可等待转换正在进行 |
| `blocked` | 已证明存在需要外部处理的 blocker |
| `offline` | 该项目没有可验证的运行中 Editor session |
| `conflict` | 同一 project identity 被多个 Unity Editor process/session 声明，CLI 不会猜测 |

`instances prune` 不直接删除进程或伪造状态；PID/start-time 无效 claim 和过期 offline record 只由 Unity Bridge registry writer 清理。

不要读取 registry 文件、listener ID、端口或 URL，也不要直接调用 Bridge HTTP。transport binding 是 session supervisor 的内部职责。

## 选择规则

Bridge-backed discovery、Tool 和 Context 使用同一严格选择规则：

1. 显式 `--instance <full-instance-id|canonical-project-path|alias>`。
2. 未传 selector 时，从 CWD 向上寻找最近的 Unity project root，并解析其 canonical identity。
3. 无法得到唯一项目身份时返回结构化 instance error。

不存在按 PID、短 hash、单个存活 Editor 或任意健康实例自动选取的路径。一个项目存在多个 Unity Editor process/session claim 时返回 `E_INSTANCE_CONFLICT`；即使重复进程尚未注册 Bridge session，也按其 `-projectPath` 的 CanonicalPath v1 claim fail closed。选定项目未运行时返回 `E_INSTANCE_NOT_RUNNING`。

```powershell
unity_cli tools describe editor.compile --instance <project-path>
unity_cli tools run editor.compile --instance my-project --timeout-ms 120000
unity_cli context read editor.state --instance unity-<64-hex> --input '{}'
```

## Alias

`target` 只管理 canonical project identity alias，不保存全局活动目标：

```powershell
unity_cli target alias set <projectPath|instanceId> <alias>
unity_cli target alias list
unity_cli target alias unset <alias>
```

alias 绑定完整 `instanceId`、canonical project path 和 canonicalization version，可在 Editor offline 时继续用于静态 discovery。`--instance` 或 CWD 始终决定本次调用的目标。

## Session Supervisor

普通 `tools run` 和 `context read` 不直接绑定某个 endpoint。session supervisor 在同一个 absolute deadline 内：

- 选择唯一 project identity 和已验证 session；
- 对一次 transport 尝试绑定 listener generation；
- 在 compile、import、domain reload 或 listener restart 后重新验证 generation；
- mutation 重投时保持完全相同的 root request ID 和 request bytes，并只读取 durable canonical terminal；
- 对超时或终态前断连返回 `E_EXECUTION_TIMEOUT`；调用方观察业务状态后自行决定是否提交新的调用。

普通 Context 的成功结果来自同一 validated generation 的完整读取。`editor.state` 是唯一即时观察例外：它在固定预算内合并 Bridge、registry、process 和 Windows UI 的只读证据，返回 freshness 与 unknown 字段，不等待状态变化。
