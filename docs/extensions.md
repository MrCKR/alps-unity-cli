# 扩展包（Extensions）

扩展包使用与开发指南。

## 当前状态：已实现

扩展包系统已在 P5 阶段（[ALP-541](https://github.com/MrCKR/alps-unity-cli/issues)）完成，包括 Bridge 包安装、项目工具发现、fingerprint 校验，以及独立的 `extensions` 命令组。

## 当前扩展能力

### Bridge 包安装

通过 `init` 命令安装 Unity Editor Bridge 包：

```powershell
# 完整安装（Bridge 包 + Skill）
unity_cli init --project C:\path\MyProject

# 仅安装 Bridge 包
unity_cli init --project C:\path\MyProject --pkg-only

# 检查安装状态
unity_cli init --check --project C:\path\MyProject
```

安装内容：
- `Packages/com.unity_cli/` — Bridge 包（copy 模式）
- `Packages/unity_cli.install.json` — 安装元数据（项目 hash、版本、兼容性范围）

### 项目自定义工具发现

Bridge 包安装后，项目可以通过 `[UnityCliTool]` 注册自定义工具，通过 `--include-custom` 标志发现：

```powershell
unity_cli tools group list custom --include-custom
unity_cli tools describe my.custom-tool
unity_cli tools run my.custom-tool --input '{}'
```

详见 [自定义工具](custom-tools.md)。

### 诊断

```powershell
# 检查 Bridge 连接和项目工具 fingerprint
unity_cli doctor

# 诊断预检
unity_cli diagnose preflight
```

## 扩展包命令

P5 阶段已实现独立的 `extensions` 命令组：

```powershell
unity_cli extensions list                          # 列出已安装扩展
unity_cli extensions install <name>                 # 安装扩展
unity_cli extensions remove <name>                  # 卸载扩展
unity_cli extensions doctor                         # 扩展诊断
```

### init 自动检测

`init --auto-detect` 会根据 Unity 项目已安装的 Package 自动匹配并安装对应的 CLI 扩展：

```
项目检测到 com.unity.probuilder → 安装 ProBuilder 扩展
项目检测到 com.unity.visualeffectgraph → 安装 VFX Graph 扩展（P8）
```

## 扩展开发

### 扩展包组成

一个完整的 CLI 扩展包包含：

| 组件 | 说明 |
|------|------|
| Manifest | 扩展元数据（名称、版本、依赖） |
| Descriptor | 工具/Context 的 JSON Schema |
| C# Bridge 实现 | `[UnityCliTool]` / `[UnityCliContext]` 标记的 handler |
| 测试 | 单元测试和集成测试 |
| 文档片段 | 扩展使用说明 |

### 缺依赖行为

当扩展依赖的 Unity Package 未安装时：

- 扩展工具在 `tools group list custom --include-custom` 中可见但标记为 `available: false`
- 执行时返回错误，`next_actions` 指向安装所需 Package
- 不产生静默失败

```json
{
  "name": "probuilder.shape",
  "available": false,
  "requiresPackages": ["com.unity.probuilder"],
  "next_actions": ["Install com.unity.probuilder via Package Manager."]
}
```

## 已实现的扩展

| 扩展 | 状态 | 说明 |
|------|------|------|
| ProBuilder | ✅ 已完成（P7） | shape/create/edit/csg，需要 com.unity.probuilder |
| Cinemachine | 📋 Backlog（P8） | 待实现 |
| VFX Graph | 📋 Backlog（P8） | 仅基础 vfx 命令，扩展包待实现 |
| URP/HDRP | 📋 Backlog（P8） | 待实现 |

## 安全边界

| 规则 | 说明 |
|------|------|
| 扩展不能绕过 descriptor/envelope | 必须通过统一注册和执行流程 |
| 扩展不能提供任意反射 | 不允许 escape hatch |
| Fingerprint 校验 | handler 代码变更时默认阻止 |
| 缺依赖不可用 | unavailable + next_actions，不静默失败 |

## 取证与依赖

- P5 扩展系统基础设施：ALP-541（done）
- P7 ProBuilder 扩展：ALP-543（done，保留 P7-T99 观察项：主基线合入、动态加载、实机边缘验证等）
- P8 Cinemachine/VFX/URP/HDRP：ALP-544（backlog），不得写成已可用

> **注意**：P8 扩展包（Cinemachine/VFX/URP/HDRP）仍处于 backlog，文档只能写机制与计划映射，不能写已可用。
