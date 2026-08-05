# 扩展包（Extensions）

扩展包使用与开发指南。

## 当前状态：已实现

扩展包系统已完成，包括 Bridge 包安装、项目工具发现、内部完整性校验、可选包可用性检测，以及独立的 `extensions` 命令组。当前准源是 TS-local `defineTool` registry、`src/bridge/domains/*`、Core typed Tool/Context registry 与目录扩展包 `extensions/<id>/`。

## 当前扩展能力

### Bridge 包安装

通过 `init` 命令安装 Unity Editor Bridge 包：

```powershell
# 默认安装 Bridge；若项目已有 .pi/.claude/.agents，则自动安装 Skill
unity_cli init --project C:\path\MyProject

# 仅安装 Bridge 包
unity_cli init --project C:\path\MyProject --pkg-only

# 检查安装状态
unity_cli init --check --project C:\path\MyProject
```

安装内容：
- `Packages/com.unity_cli/` — Bridge 包（copy 模式）
- `Packages/unity_cli.install.json` — 安装元数据（项目 hash、版本、支持范围）
- `.pi/skills/unity_cli/SKILL.md`、`.claude/skills/unity_cli/SKILL.md` 或 `.agents/skills/unity_cli/SKILL.md` — 仅当对应 Agent 目录已存在，或显式传入 `--agent` 时安装

### 项目自定义工具发现

Bridge 包安装后，项目可以通过 `IUnityCliTool<TReq,TRes>` 注册自定义工具，通过 `--include-custom` 标志发现：

```powershell
unity_cli tools group list custom --include-custom
unity_cli tools describe my.custom-tool
unity_cli tools run my.custom-tool --input '{}'
```

项目自定义 Tool 通过同一公开 descriptor 和 Live Editor direct-final 调用使用。

### 诊断

```powershell
# 检查 CLI 控制面、Bridge 连接和项目工具完整性
unity_cli doctor

# 诊断预检（Unity Bridge 工具）
unity_cli tools run diagnose.preflight
```

## 扩展包命令

`extensions` 命令组负责扩展包状态、复制和诊断：

```powershell
unity_cli extensions list --project <path>          # 列出扩展状态
unity_cli extensions install <name> --project <path> # 安装已满足依赖的 Unity 侧扩展目录
unity_cli extensions remove <name> --project <path>  # 只移除 manifest 管理的 bundle 文件
unity_cli extensions doctor --project <path>         # 扩展诊断
```

### init 自动检测

`init` 会根据 Unity 项目已安装的 Package 自动匹配并安装对应的 Unity 侧扩展目录；不会自动安装 Unity Package：

```
项目检测到 com.unity.probuilder@5.2.4 → 复制 ProBuilder bundle → `tools run probuilder.*` 可用
项目检测到 com.unity.visualeffectgraph → 复制 VFX Graph bundle → `tools run vfx.*` 可用
项目检测到 com.unity.cinemachine → 复制 Cinemachine bundle → `tools run cinemachine.*` 可用
项目检测到 com.unity.render-pipelines.universal → 复制 URP bundle → `tools run urp.*` 可用
项目检测到 com.unity.render-pipelines.high-definition → 复制 HDRP bundle → `tools run hdrp.*` 可用
```

## 扩展开发

### 扩展包组成

一个完整的 CLI 扩展包包含：

| 组件 | 说明 |
|------|------|
| Manifest | 扩展元数据（名称、版本、依赖） |
| Descriptor | 工具/Context 的 descriptor 与派生 JSON Schema |
| C# Bridge 实现 | `unitypkg/Editor` 下的 `IUnityCliTool<TReq,TRes>` / `IUnityCliContext<TReq,TRes>` 实现，安装后位于 `Packages/com.unity_cli/Extensions/<id>/Editor` |
| 测试 | 单元测试和集成测试 |
| 文档片段 | 扩展使用说明 |

### 缺依赖行为

当扩展依赖的 Unity Package 未安装或不满足 manifest 的精确版本时：

- 扩展工具通过 `tools group/list/describe/run` 暴露；缺依赖时 `tools describe` 返回 `available: false`、`unavailableReason` 和 `requiresPackages`
- 执行时返回错误，`error.nextActions` 指向手动安装 Unity Package 与随后运行 `extensions install <id>`
- 不自动安装 Unity Package，不产生静默失败

```json
{
  "name": "probuilder.create-shape",
  "requiresPackages": ["com.unity.probuilder"],
  "available": false,
  "unavailableReason": "E_EXTENSION_DEPENDENCY_MISSING: required Unity package profile com.unity.probuilder@5.2.4 is unavailable for the selected project."
}
```

## 已实现的扩展

| 扩展 | 状态 | 说明 |
|------|------|------|
| ProBuilder | ✅ 已实现 | 36 个扩展工具（create-shape/extrude-faces/delete-faces 等），只接受 `com.unity.probuilder@5.2.4` |
| Cinemachine | ✅ 已实现 | Cinemachine camera/target/lens/shot 等工具，需要 `com.unity.cinemachine` |
| VFX Graph | ✅ 已实现 | VFX Graph、ParticleSystem、LineRenderer、TrailRenderer 相关工具；VFX Graph 子集需要 `com.unity.visualeffectgraph` |
| URP | ✅ 已实现 | URP renderer feature、volume、settings 等工具，需要 `com.unity.render-pipelines.universal` |
| HDRP | ✅ 已实现 | HDRP volume、settings 等工具，需要 `com.unity.render-pipelines.high-definition` |

## 安全边界

| 规则 | 说明 |
|------|------|
| 扩展不能绕过 descriptor/envelope | 必须通过统一注册和执行流程 |
| 扩展不能提供任意反射 | 不允许 escape hatch |
| 内部完整性校验 | handler 代码变更时默认阻止 |
| 缺依赖不可用 | unavailable + nextActions，不静默失败 |

## 取证与依赖

- 扩展基础设施：`extensions list/install/remove/doctor`、`init --auto-detect`、缺依赖 descriptor 的 `available:false` / `unavailableReason` 与执行错误的 `nextActions`。
- ProBuilder：源码位于 `extensions/probuilder/`，安装后位于 `Packages/com.unity_cli/Extensions/probuilder/`，通过 project-tool bridge 路由提供扩展工具；所有 operation replay 固定于 `com.unity.probuilder@5.2.4`，版本不匹配时在 effect 前拒绝。
- Cinemachine/VFX/URP/HDRP：源码位于对应 `extensions/<id>/` 目录，安装后位于 `Packages/com.unity_cli/Extensions/<id>/`，通过 metadata、bridge domain 和 C# command handler 提供 live Bridge 工具。
- 验证入口：`npm test`、`npm run contracts:validate`、extension availability tests。
