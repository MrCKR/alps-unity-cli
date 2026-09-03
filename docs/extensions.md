# 扩展包（Extensions）

扩展包使用与开发指南。

## 当前状态：已实现

扩展包系统已完成，包括 Bridge 包安装、项目工具发现、内部完整性校验、可选包可用性检测，以及独立的 `extensions` 命令组。当前准源是 package-local `defineTool` registry、连接实例中的 typed Tool registry 与目录扩展包 `extensions/<id>/`；公开 discovery 只返回选定实例合并后的完整目录。

## 当前扩展能力

### Bridge 包安装

Bridge 与 AI Skill 是两个独立安装对象。先用安装协议发现并选择项目，再单独安装或检查 Bridge：

```powershell
unitycli init help
unitycli init discover
unitycli init package --project C:\path\UnityProject
# 仅在用户批准正常关闭这个准确项目后使用
unitycli init package --project C:\path\UnityProject --close-editor
unitycli init package --project C:\path\UnityProject --check
```

Bridge 安装内容：
- `Packages/com.alpsckr.unitycli/` — canonical Bridge 包（copy 模式）
- `Packages/unitycli.install.json` — 安装元数据（项目 hash、source hash、managed-file hash、版本与支持范围）
- `Packages/com.alpsckr.unitycli/unitycli.files.json` — 受管文件路径清单

Skill 使用`init skill --target <SkillDirectory>`安装到用户明确目录，或使用`.agents`/Claude workspace/user预设；它不属于Unity项目Package安装，Bridge失败也不阻止这个独立效果。

### 项目能力发现

Bridge 包安装并连接 Unity Editor 后，项目可以通过 `IUnityCliTool<TReq,TRes>` 注册能力。它们自动进入该实例的完整目录，与 package 内置和 Extension 能力使用相同入口：

```powershell
unitycli tools group
unitycli tools group list <group>
unitycli tools describe my.custom-tool
unitycli tools run my.custom-tool --input '{}'
```

分组名称来自实际 discovery 输出，不假设项目能力位于 `custom` 分组。

### 诊断

```powershell
# 分别检查 CLI 控制面、Bridge transport 与已发布项目 Tool catalog 健康状态
unitycli doctor
```

## 扩展包命令

`extensions` 命令组负责扩展包状态、复制和诊断：

```powershell
unitycli extensions list --project <path>          # 列出扩展状态
unitycli extensions install <name> --project <path> # 安装已满足依赖的 Unity 侧扩展目录
unitycli extensions remove <name> --project <path>  # 只移除 manifest 管理的 bundle 文件
unitycli extensions doctor --project <path>         # 深度校验全部 source/managed-file hash
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
| Descriptor | Tool descriptor 与派生 JSON Schema |
| C# Bridge 实现 | `unitypkg/Editor` 下的 `IUnityCliTool<TReq,TRes>` 实现，安装后位于 `Packages/com.alpsckr.unitycli/Extensions/<id>/Editor` |
| 测试 | 单元测试和集成测试 |
| 文档片段 | 扩展使用说明 |

### 缺依赖行为

当扩展依赖的 Unity Package 未安装或不满足 manifest 的精确版本时：

- 扩展工具通过 `tools group/list/describe/run` 暴露；缺依赖时 `tools describe` 返回 `available: false`、`unavailableReason` 和 `requiresPackages`
- 每次调用冻结一份packages-lock/install manifest输入；group或command摘要页对每个distinct extension最多做一次target proof，普通扩展Tool执行只检查所选extension。selected dependency gate复用generated Tool owner与exact compiled assembly identity，不从Tool名称前缀推断owner
- selected target proof只校验该extension的managed files与unknown files，不读取source tree；`extensions list`返回`integrity:"shallow"`且不计算source/managed-file hash，完整全extension source与target hash只由显式`extensions doctor`执行并返回`integrity:"full"`
- 执行时返回错误，`error.nextActions` 指向手动安装 Unity Package 与随后运行 `extensions install <id>`
- 发布前generated inventory必须同时匹配Tool name、extension id、assembly identity和implementation type；缺项、多项或跨Extension归属会使catalog fail closed
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
- ProBuilder：源码位于 `extensions/probuilder/`，安装后位于 `Packages/com.alpsckr.unitycli/Extensions/probuilder/`，通过 project-tool bridge 路由提供扩展工具；所有 operation replay 固定于 `com.unity.probuilder@5.2.4`，版本不匹配时在 effect 前拒绝。
- Cinemachine/VFX/URP/HDRP：源码位于对应 `extensions/<id>/` 目录，安装后位于 `Packages/com.alpsckr.unitycli/Extensions/<id>/`，通过 metadata、bridge domain 和 C# command handler 提供 live Bridge 工具。
- 验证入口：`npm test`、`npm run contracts:validate`、extension availability tests。
