# Target 与 Instance

> **发现路径**：`instances list` → 在发现与执行命令上显式传 `--instance` → 进入工具发现

## 什么是 Unity Instance？

**Unity instance** 是加载了 `com.unity_cli` Bridge 包的运行中 Unity Editor 进程。Editor 编译该包时，`UnityCliBridge` 启动 Windows loopback HTTP 监听器并向 CLI 注册。

> ⚠️ **不要手动读取注册文件或 ping 端点。** 始终使用 `unity_cli instances list` 和 `unity_cli target current` 命令。内部文件路径和端点 URL 仅供理解系统架构，不是 CLI 调用接口。

每个实例注册包含：

| 字段 | 含义 |
|------|------|
| `instanceId` | Editor 进程的唯一标识 |
| `projectPath` | 加载的 Unity 项目根路径 |
| `projectHash` | 派生 hash，用于快速匹配 |
| `processId` | Unity Editor 进程的 Windows PID |
| `bridgeEndpoint` | 提供 `/bridge/execute` 和 `/health` 的 HTTP 端点 |
| `status` | `ready` / `stale` / `unreachable` |
| `editorState` | 当前编辑器状态快照（Play Mode、暂停等） |
| `lastSeenAt` | 最后心跳时间戳（约每 5 秒刷新） |
| `toolCatalogFingerprint` | 此实例的工具目录 fingerprint |

## 实例状态生命周期

| 状态 | 含义 | 恢复方式 |
|------|------|---------|
| `ready` / `healthy` | `/health` 可达，且响应中的 target ID 与注册实例一致 | - |
| `stale` | 心跳超时；可能只是 domain reload 的瞬时 registry 快照 | CLI 自动重探；持续失败时检查 Editor 后执行 `instances prune` |
| `unreachable` | 注册存在，但 `/health` 无响应、响应畸形或 target ID 不匹配 | 检查 Editor 进程与端点身份；确认失效后执行 `instances prune` |

## 发现可用实例

```powershell
# 列出所有注册实例
unity_cli instances list

# 清理过期/不可达注册
unity_cli instances prune --yes
```

`instances prune` 只删除失效的 registry 记录，不会终止记录中的 PID。domain reload 期间的 Editor 可能暂时显示为 `stale`；命令会先执行实时、带 target ID 校验的 health probe，仍可达的实例会恢复为 `ready` 并保留。

## 选择目标

执行任何 Bridge 工具前，CLI 必须知道目标 Unity 实例。解析优先级：

### 1. 显式 `--instance <id|alias>`

```powershell
unity_cli tools group list <group> --instance <instanceId|alias>
unity_cli tools run camera.list --instance my-project
```

`--instance`（别名 `--target`）接受实例 ID、别名或项目 hash 前缀，是最确定性的解析方式。调用方已经知道目标时，应在每条 Bridge 命令上显式传入，不需要先执行 `target set`。

### 2. 活动目标（`target set`）

```powershell
unity_cli target set <instanceId|alias>
unity_cli target current       # 查看当前活动目标
unity_cli target clear         # 清除活动目标
```

该命令会把可变的全局活动目标持久化到 `config.json`，属于 CLI 控制面副作用；只适合调用方明确希望后续命令省略 `--instance` 的交互式会话。已经显式传入 `--instance` 时不要执行 `target set`，它不会增加目标绑定保证。

### 3. CWD 匹配

无 `--target` 和活动目标时，CLI 查找 `projectPath` 包含当前工作目录的就绪实例。

### 4. 单一就绪实例

仅一个 `ready` 实例时自动使用。

### 5. 歧义（错误）

多个就绪实例且无选择器时，返回 `E_TARGET_AMBIGUOUS`，包含 `targets` 列出所有候选。

### 别名管理

```powershell
unity_cli target alias set <instanceId> <alias>     # 创建别名
unity_cli target alias list                           # 列出别名
unity_cli target alias unset <alias>                  # 删除别名
```

别名在 Editor 重启后保留。

## 目标解析与工具发现

目标解析在工具发现之前。解析后的目标决定：
- 使用哪个 Bridge 端点
- 发现哪些项目工具（项目工具是 per-instance 的）
- 检查哪个工具可用性 fingerprint

无目标且无 `--bridge-endpoint` 时，只能发现离线和 CLI-only 工具。

## 核心原则

**CLI 绝不猜测目标。** 多个就绪实例时返回 `E_TARGET_AMBIGUOUS`，附带可操作的 `nextActions`。调用者应优先在当前命令上显式传 `--instance`；只有明确需要持久化全局默认值时才使用 `target set`。
