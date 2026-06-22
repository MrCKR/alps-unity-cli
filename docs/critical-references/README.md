# C# 域批判性参考记录

本目录记录 alps-unity-cli 在 C# Editor Bridge 与项目工具域实现中对 `unity-mcp` 的批判性参考。记录目的不是证明逐行移植，而是显式说明：参考了什么、舍弃了什么、如何适配到本项目的 CLI-first、统一 envelope、descriptor 与 target-scoped 架构。

## 总体取舍

### 参考基线

- `unity-mcp` 提供了较完整的 Unity Editor 工具域样本：`MCPForUnity/Editor/Tools/` 下的 `Manage*` C# handler、`Server/src/services/tools/manage_*.py` Python MCP facade，以及 `website/docs/reference/tools/` 下的工具说明。
- 参考重点是 Unity API 使用边界、可选 Unity 包探测、GameObject/Scene/Asset 操作的风险点、工具参数组织方式，以及自定义工具通过 C# attribute + reflection 暴露能力的思路。

### 统一舍弃

- 不采用 FastMCP/Python server 作为主要执行入口；本项目以本地 CLI 为用户入口，通过 Bridge HTTP/Editor 执行器完成 Unity 调用。
- 不采用 `manage_*` 单工具多 action 的主形态；本项目更偏向可发现、可描述、可路由的 dotted tool descriptor，例如 `scene.hierarchy`、`go.create`、`vfx.graph`、项目工具描述符等。
- 不复用 unity-mcp 的响应对象、MCP resource URI、session/user 上下文和动态 tool-group 可见性模型；本项目统一使用 request/response envelope、结构化错误码、`plannedEffects`、`next_actions` 与 target-scoped availability。

### 统一适配

- 所有 C# 域都通过 Bridge command registry、项目工具 registry 或 runtime executor 接入 CLI 的 `tools group/group list/search/describe/run` 路径。
- 每个可执行面都要能映射到 descriptor 元数据：`handler`、`supportStatus`、`target`、`availabilityScope`、风险提示、输入输出 schema、文档 topic 与示例。
- 对 Unity 状态变更优先保留可审计输出：成功 envelope、结构化错误、`plannedEffects`、`logPath`，以及在可行处支持 `--dry-run` 或显式 destructive/read-only 标记。

## ProBuilder 域

### 参考点

- 参考 `unity-mcp/MCPForUnity/Editor/Tools/ProBuilder/ManageProBuilder.cs` 的 ProBuilder 可选包处理方式：通过 reflection 探测 `UnityEngine.ProBuilder.ProBuilderMesh`、`ShapeGenerator`、`MeshOperations.*`、`EditorMeshUtility` 等类型，避免在未安装 `com.unity.probuilder` 时硬依赖编译失败。
- 参考 `unity-mcp/MCPForUnity/Editor/Tools/ProBuilder/ProBuilderMeshUtils.cs` 与 `ProBuilderSmoothing.cs` 的网格查询、face/edge/vertex 语义、smoothing/material/UV 等操作分层思路。
- 参考 `unity-mcp/Server/src/services/tools/manage_probuilder.py` 与 `unity-mcp/website/docs/reference/tools/probuilder/manage_probuilder.md` 对 ProBuilder action 分类的覆盖边界：shape creation、mesh editing、vertex operations、selection、UV/material、query。

### 舍弃点

- 舍弃 `manage_probuilder(action=...)` 的单工具多 action 入口，原因是它在 CLI-first 场景下不利于独立 descriptor、独立风险标记、单 action 发现与单 action 示例。
- 舍弃 Python facade 与 MCP tool registry；本项目不要求通过 FastMCP server 转发到 Unity，也不把 ProBuilder 可见性绑定到 MCP session 的 tool group 开关。
- 舍弃把所有 ProBuilder 操作集中在一个超大 C# handler 内；该形态便于 MCP 单工具暴露，但会削弱本项目按 descriptor 追踪单个能力的清晰度。

### 适配点

- alps-unity-cli 将 ProBuilder Unity 侧代码放在 `extensions/probuilder/unitypkg/*`；安装后通过 `[UnityCliTool]` 单 action 项目工具由 `ProjectToolRegistry.DiscoverProjectTools()` 统一发现。
- `extensions/probuilder/unitypkg/Editor/ProBuilder/ProBuilderReflection.cs` 承接 unity-mcp 的可选包 reflection 思路，但输出改为本项目的结构化错误、fingerprint/descriptor 与 CLI descriptor 能力模型。
- `src/bridge/domains/probuilder.ts` 与 `src/tools/metadata/groups/probuilder.ts` 把 ProBuilder 暴露为 CLI tool descriptor，而不是 MCP action 表；这使每个操作能独立表达 `readOnlyHint`、`destructiveHint`、`target=editor` 与文档 topic。

## VFX Graph / VFX 域

### 参考点

- 参考 `unity-mcp/MCPForUnity/Editor/Tools/Vfx/ManageVFX.cs` 对 VFX 域的分组：`particle_*`、`vfx_*`、`line_*`、`trail_*`，以及 VFX Graph、ParticleSystem、LineRenderer、TrailRenderer 共存的域边界。
- 参考 `unity-mcp/MCPForUnity/Editor/Tools/Vfx/VfxGraphAssets.cs`、`VfxGraphWrite.cs`、`VfxGraphRead.cs`、`VfxGraphControl.cs` 对 `VisualEffectAsset`、`VisualEffect`、exposed parameter、template/asset search、playback/event 的 API 使用路径。
- 参考 `unity-mcp/Server/src/services/tools/manage_vfx.py` 与 `unity-mcp/website/docs/reference/tools/vfx/manage_vfx.md` 中对 VFX Graph 可选包和 action prefix 的用户侧说明。

### 舍弃点

- 舍弃 `UNITY_VFX_GRAPH` 预处理符号作为唯一可用性判定，原因是本项目 Bridge 包需要尽量在不同 Unity/UPM 状态下稳定加载，并通过运行期探测给出结构化错误。
- 舍弃 60+ VFX action 一次性全部搬迁；当前实现只收敛到本阶段冻结范围内需要的 `vfx.graph` 与 `vfx.effect`，避免扩大 P10 交付范围。
- 舍弃 unity-mcp 的 `properties` 参数内嵌和 snake/camel alias 大量兼容层；本项目优先由 descriptor 的 input schema 明确参数形态。

### 适配点

- alps-unity-cli 在 `extensions/vfx-graph/unitypkg/Editor/Commands/VfxCommands.cs` 中通过运行期 `FindType` / PackageManager 检查 `VisualEffectAsset` 与 `VisualEffect`，并将缺包、路径非法、创建失败等情况映射为 `E_VFX_*` 结构化错误。
- `vfx.graph` 聚焦 VFX Graph asset 创建/查询，`vfx.effect` 聚焦目标 GameObject 上 `VisualEffect` component 的添加与 graph 绑定，符合 CLI-first 的小工具组合方式。
- `src/bridge/domains/vfx.ts` 与 `src/tools/metadata/groups/vfx.ts` 将 VFX 能力纳入统一 descriptor、target 和 envelope，而不是 MCP server 的 VFX tool group。

## Project Tool / Custom Context 域

### 参考点

- 参考 `unity-mcp/MCPForUnity/Editor/Tools/McpForUnityToolAttribute.cs` 与 `MCPForUnity/Editor/Services/ToolDiscoveryService.cs` 的 attribute + reflection 发现模式：项目内 C# 静态工具通过 attribute 标记，由 Editor 侧扫描后生成工具元数据。
- 参考 `unity-mcp/Server/src/services/tools/execute_custom_tool.py` 的项目作用域执行入口思路：调用方传入 tool name 与 parameters，由服务层定位 Unity instance/project 后执行。
- 参考 `unity-mcp/website/docs/guides/custom-tools.md` 对项目自定义工具的作者体验：在 `Editor/` 目录放置 handler、声明参数、刷新客户端后可发现并调用。

### 舍弃点

- 舍弃 `execute_custom_tool(tool_name, parameters)` 作为用户主入口；本项目要求项目工具和内置工具共享 `tools describe/run`，避免自定义工具成为独立命令族。
- 舍弃 MCP context 中的 user/session/project 解析与 Python `CustomToolService`；alps-unity-cli 的 project tool 运行在已选 target 的 Unity Bridge 上，由 CLI target resolution 与 Bridge endpoint 决定上下文。
- 舍弃仅靠 attribute 自动生成弱元数据；本项目需要 descriptor 能表达 capability type、target、availabilityScope、fingerprint、artifactKinds、requiredInput、timeout 和风险提示。

### 适配点

- alps-unity-cli 在 `unitypkg/Editor/ProjectTools/UnityCliToolAttribute.cs`、`UnityCliProjectToolDefinition.cs`、`UnityCliProjectToolContext.cs` 和 `ProjectToolRegistry.cs` 中实现项目工具注册，并把工具上下文约束为本项目 envelope/descriptor 模型。
- `unitypkg/Editor/Commands/ProjectToolCommands.cs` 只保留 `project-tool.list` 与 `project-tool.run` 的 Bridge 内部能力；外部文档明确以统一 `tools` 表面为入口。
- `ProjectToolRegistry` 引入 fingerprint 校验、runtime/editor target 分流、artifact kind、输入 schema 和 structured descriptor，以补足 unity-mcp 自定义工具模式在 CLI 审计与安全边界上的不足。

## Scene 域

### 参考点

- 参考 `unity-mcp/MCPForUnity/Editor/Tools/ManageScene.cs` 与 `unity-mcp/Server/src/services/tools/manage_scene.py` 的场景管理边界：active scene 查询、hierarchy、create/load/save、build settings、截图等高频 Editor 操作。
- 参考 unity-mcp 对 scene 操作与 resource/read-only 查询分离的理念：读操作应低风险，写操作应显式改变 Unity Editor 状态。

### 舍弃点

- 舍弃 `manage_scene(action=...)` 聚合入口，原因是 scene 的读写风险差异明显，CLI descriptor 需要分别表达 `scene.active`、`scene.hierarchy`、`scene.create`、`scene.save` 等能力。
- 舍弃 MCP resource URI 作为 hierarchy/active scene 的读取通道；本项目统一由 CLI tool 输出 envelope，避免用户在 resource 与 tool 两套语义间切换。
- 舍弃隐式场景路径宽松处理；本项目对 `Assets/*.unity` 路径和存在性做更明确的错误码约束。

### 适配点

- alps-unity-cli 在 `unitypkg/Editor/Commands/SceneCommands.cs` 中拆出 `scene.active`、`scene.hierarchy`、`scene.build-settings`、`scene.screenshot`、`scene.create`、`scene.load`、`scene.save`，每个命令独立声明是否 mutating。
- 对 create/load/save 使用 `EnsureEditorNotBusy`、`EditorSceneManager`、结构化 `E_SCENE_*` 错误和 `SceneMutationJson`，使 CLI 调用方能明确失败原因与下一步。
- `src/bridge/domains/scene.ts` 与 `src/tools/metadata/groups/scene.ts` 负责把这些 C# 命令映射到统一发现/执行路径，而不是通过 Python facade 暴露。

## GameObject / Component 域

### 参考点

- 参考 `unity-mcp/MCPForUnity/Editor/Tools/GameObjects/ManageGameObject.cs`、`GameObjectCreate.cs`、`GameObjectModify.cs`、`GameObjectDelete.cs`、`ManageGameObjectCommon.cs` 对 GameObject CRUD、target 查找、transform 修改和 component helper 的边界划分。
- 参考 `unity-mcp/Server/src/services/tools/manage_gameobject.py` 与 `find_gameobjects.py` 中对查找和变更分离的设计思路。

### 舍弃点

- 舍弃 `searchMethod` 和多种宽松 target 输入兼容矩阵的完整复制；本项目当前聚焦 name、hierarchy path、instance id 三类高确定性 target，降低歧义。
- 舍弃把 GameObject 与 Component 操作混在一个 MCP action router 中；本项目将 `go.*` 与 `component.*` 拆成不同 descriptor，以便风险标记和文档更清晰。
- 舍弃返回 unity-mcp 风格 helper response；本项目统一输出 target、plannedEffects、logPath 与结构化 error envelope。

### 适配点

- alps-unity-cli 在 `unitypkg/Editor/Commands/GameObjectCommands.cs` 中实现 `go.find`、`go.inspect`、`go.create`、`go.modify`、`go.delete`、`component.add/remove/set`，并通过 `ResolveUniqueTarget` 对多匹配目标返回 `E_TARGET_AMBIGUOUS`。
- 变更操作使用 `Undo.RecordObject` / `Undo.RegisterCreatedObjectUndo` / `Undo.DestroyObjectImmediate` 与 `EditorSceneManager.MarkSceneDirty`，适配 CLI 调用后仍保留 Unity Editor 可撤销和脏标记语义。
- `src/bridge/domains/game-object.ts` 与 `src/tools/metadata/groups/game-object.ts` 将 GameObject 能力按 descriptor 暴露，支持 target-scoped 发现与执行。

## Editor 域

### 参考点

- 参考 `unity-mcp/MCPForUnity/Editor/Tools/ManageEditor.cs`、`ExecuteMenuItem.cs`、`ReadConsole.cs` 和 Editor resource 类对 play mode、pause、menu、console、window/editor state 的覆盖边界。
- 参考 unity-mcp 对长耗时/状态切换操作避免阻塞 Unity 主线程的经验，尤其是 play mode 状态变化需要让出 Editor event loop。

### 舍弃点

- 舍弃把 Editor 状态作为 MCP resource 热路径输出；本项目把 `editor.state` 做成 read-only tool descriptor，仍遵循统一 envelope。
- 舍弃直接同步等待 PlayMode 完成的模式；这类等待容易在 Unity main thread 上造成死锁或误超时。
- 舍弃 MCP server 的 polling middleware；本项目由 Bridge work queue、deferred wait 与 CLI job/batch 语义承接长耗时执行。

### 适配点

- alps-unity-cli 在 `unitypkg/Editor/Commands/EditorCommands.cs` 中实现 `editor.state`、`editor.play`、`editor.pause`、`editor.stop`、`editor.menu`、`editor.console`。
- `editor.play` 使用 deferred wait 等待 PlayMode 进入，`editor.stop` 明确不阻塞等待完成，调用方通过 `editor.state` 验证最终状态。
- Editor 状态输出包含 busy、activeScene、prefabStage、bridge build stamp 与 runtime session，适配本项目 target/instance 与 runtime-aware descriptor 模型。

## Script / Code 域

### 参考点

- 参考 `unity-mcp/MCPForUnity/Editor/Tools/ManageScript.cs`、`ExecuteCode.cs`、`Server/src/services/tools/manage_script.py`、`script_apply_edits.py` 对脚本创建、读取、编辑、校验和代码执行风险的分层。
- 参考 unity-mcp 后续将 read/update/edit 逐步拆分到更明确工具的方向：脚本读写和任意代码执行不应混为同一安全级别。

### 舍弃点

- 舍弃运行期 Roslyn 编译/执行作为默认能力；本项目仅保留明确的 `code.exec` / script 操作边界，并通过 descriptor 风险提示表达危险能力。
- 舍弃复杂 patch/edit DSL 的完整复制；当前 `script.edit` 只支持 full content、find/replace 或 patch 内容替换，减少 CLI 输入歧义。
- 舍弃将 script read 作为 MCP resource；本项目仍通过 `script.read` 返回 envelope、encoding、script metadata 与 logPath。

### 适配点

- alps-unity-cli 在 `unitypkg/Editor/Commands/ScriptCommands.cs` 中实现 `script.create`、`script.read`、`script.edit`、`script.validate`，并强制 `Assets/*.cs` 路径与 UTF-8 写入。
- `script.validate` 当前做轻量语法前置检查（空内容、括号/花括号平衡），不声称替代 Unity 编译或 Roslyn 诊断，避免超过冻结范围。
- `src/bridge/domains/script.ts` 与 `src/tools/metadata/groups/script.ts` 将脚本工具纳入统一 discovery/execution；风险更高的 code execution 单独在 `CodeExecCommand.cs` / `code` descriptor 中表达。

## 维护规则

- 新增 C# 工具域时，应在本目录补充对应域的参考点、舍弃点、适配点。
- 如果后续实际参考了新的 unity-mcp 路径，应记录具体文件路径和参考维度；不要只写“参考 unity-mcp”。
- 如果实现已偏离本记录，应优先更新本记录或在方案审查中说明偏离原因，避免文档成为阶段来源说明而非批判性参考记录。
