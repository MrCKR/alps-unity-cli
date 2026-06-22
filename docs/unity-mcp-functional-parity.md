# unity-mcp 等价追踪

> 本文追踪 `CoplayDev/unity-mcp` 到 `unity_cli` 的三条等价线：
>
> 1. **Tools 功能点等价**：智能体能执行的 Unity Editor / Project 操作能力要等价。
> 2. **Resources → Context 语义等价**：`unity-mcp` Resource 的只读语义要由 `unity_cli context` 或 read-only tool 等价覆盖。
> 3. **Extensions 分类与实现等价**：可选包能力的分类、依赖、可用性和实现链路要与参考项目的 tool groups / optional packages 对齐。
>
> 本文不要求 MCP 协议形态等价；本项目保持 CLI-first、Bridge-first 定位。

## 边界

### 纳入范围

- `unity-mcp` 中用于智能体操作 Unity 的 tools/action 能力。
- `unity-mcp` Resources 的只读状态语义，映射到本项目 `context list/describe/read` 或 read-only tools。
- ProBuilder、Cinemachine、VFX、URP、HDRP 等可选能力包的分类、依赖包、可用性、工具组和执行链路。
- C# handler、CLI command、bridge spec、tool/context descriptor、contract、测试和 smoke 证据。

### 排除范围

| 排除项 | 排除原因 | 本项目替代机制 |
|---|---|---|
| MCP server / FastMCP runtime | 协议运行时形态，不是能力语义 | `unity_cli` CLI 入口 |
| MCP stdio / HTTP transport | 连接形态 | CLI 调用 + Unity Bridge HTTP |
| MCP URI / JSON-RPC 呈现形态 | 呈现方式，不等于只读语义 | `context read <name>` / read-only tools |
| MCP prompts | 提示词分发形态 | `skills/unity_cli/SKILL.md` 与 docs |
| Client auto-config | 客户端集成体验 | `init --agent` 安装 agent skill |
| Remote hosted auth / API key | 远程 MCP 部署安全 | 本地 CLI + target/instance 选择 |
| Unity 配置窗口 UI | 插件配置体验 | CLI docs、doctor、tools discovery |

## 等价判定

| 状态 | 含义 |
|---|---|
| `covered` | handler、descriptor/metadata、contract 或测试/smoke 证据齐全 |
| `covered-adapted` | 功能语义等价，但接口形态不同，例如 `manage_scene` 拆成多个 dotted tools |
| `partial` | 主链路存在，但参数、细节、metadata、测试、风险标记或可用性不完整 |
| `not-covered` | 属于目标范围，但当前没有等价能力 |
| `deferred` | 有意延后，需说明依赖和下一步 |
| `excluded` | 不属于本文目标范围 |
| `unknown` | 已识别来源但尚未判定 |

## 验收标准

### Tools 功能点等价

- `unity-mcp` 的 `manage_* action` 可映射为多个 `unity_cli` dotted tools。
- 每个 `covered` tool 必须至少具备：CLI/bridge 路由、C# handler 或 CLI handler、metadata/contract、测试或 smoke 证据。
- 如果本项目故意拆分、合并或收窄参数，需要标 `covered-adapted` 或 `partial`，并写明适配原因。
- destructive / code execution 类能力必须有风险标记、确认或 dry-run / fingerprint 等安全证据。

### Resources → Context 语义等价

- `unity-mcp` Resource URI 形态不需要保留，但只读语义必须能由 `context read` 或 read-only tool 取得。
- Context 必须只读、结构化、可描述、有 `readOnlyHint`，不得修改 Unity 状态。
- Resource 的 path 参数应转为 context input schema，例如 `{ instanceId }`、`{ prefabPath }`、`{ mode }`。
- 能力发现类 Resource（tool groups / custom tools / instances）可用只读 command 等价，但最终应有 context 入口，便于智能体统一读取状态。

### Extensions 分类与实现等价

- 可选包能力必须作为 extension group 建模，而不是混入 core group。
- 每个 extension 必须声明：Unity package id、tool group、tool count、requiresPackages、availability/unavailable 行为、bridge routing、C# entry。
- 运行时准源以 `src/tools/metadata/groups/*`、`src/bridge/domains/*`、`unitypkg/Editor/Commands|ProBuilder/*` 和 `src/extensions/contribution-loader.ts` 为准。
- `extensions/*`、`src/extensions/registry-data.ts`、`src/extensions/metadata/index.ts` 若保留，必须与运行时准源一致，或明确标注为 descriptor/diagnostic 来源。

## Tools 功能点矩阵

### Editor / Console / Batch

| unity-mcp 能力 | unity_cli 等价 | 状态 | 证据 | 备注 |
|---|---|---|---|---|
| `manage_editor.play/pause/stop` | `editor.play` / `editor.pause` / `editor.stop` | covered-adapted | `unitypkg/Editor/Commands/EditorCommands.cs`, `src/tools/metadata/groups/editor.ts` | 拆分为独立工具 |
| `manage_editor.set_active_tool` | `context read editor.active-tool` / `context read editor.tool-states` | partial | `src/context/builtin.ts` | 当前偏读取；设置 active tool 待补或确认 |
| `manage_editor.add_tag/remove_tag/add_layer/remove_layer` | `project.tag` / `project.layer` + `context read project.tags/layers` | covered-adapted | `unitypkg/Editor/Commands/EditorCommands.cs`, `src/tools/metadata/groups/editor.ts` | TagManager 写操作与只读 context 分离 |
| `manage_editor.set_quality` | `graphics.quality` | covered-adapted | `unitypkg/Editor/Commands/GraphicsCommands.cs`, `src/tools/metadata/groups/graphics.ts` | 归入 graphics 域 |
| `manage_editor.set_resolution` | `editor.resolution` | covered-adapted | `unitypkg/Editor/Commands/EditorCommands.cs`, `src/tools/metadata/groups/editor.ts` | 写 PlayerSettings/GameView 相关分辨率 |
| `manage_editor.undo/redo` | `editor.undo` / `editor.redo` | covered | `unitypkg/Editor/Commands/EditorCommands.cs`, `src/bridge/domains/editor.ts` | 使用 Unity Undo API |
| `execute_menu_item` | `editor.menu` | covered | `unitypkg/Editor/Commands/EditorCommands.cs`, `src/bridge/domains/editor.ts` | 直接等价 |
| `refresh_unity` | `editor.refresh --compile` / `init` live refresh | covered-adapted | `src/cli-commands/editor.ts`, `src/cli-commands/unity-job.ts` | job/Bridge 双路径 |
| `read_console` | `editor.console` | covered | `unitypkg/Editor/Commands/EditorCommands.cs`, `tests/contract-snapshots/editor-console.json` | 读取/清空/过滤参数需持续核对 |
| `batch_execute` | `batch run` | covered-adapted | `src/batch.ts`, `tests/batch.test.ts` | CLI batch orchestrator 替代 MCP 批处理 |

### Scene

| unity-mcp 能力 | unity_cli 等价 | 状态 | 证据 | 备注 |
|---|---|---|---|---|
| `manage_scene.create/load/save` | `scene.create` / `scene.load` / `scene.save` | covered | `unitypkg/Editor/Commands/SceneCommands.cs`, `src/tools/metadata/groups/scene.ts` | 直接拆分 |
| `get_hierarchy/get_active/get_build_settings` | `scene.hierarchy` / `scene.active` / `scene.build-settings` | covered | `contracts/command-contract.json`, `tests/contract-snapshots/scene-hierarchy.json` | read-only |
| `screenshot` | `scene.screenshot` / `screenshot.capture` | covered-adapted | `unitypkg/Editor/Commands/SceneCommands.cs`, `src/tools/metadata/groups/screenshot.ts` | 截图能力拆分 |
| `scene_view_frame` | 无明确等价 | not-covered | — | 可考虑 `scene.view-frame` |
| `close_scene/set_active_scene/get_loaded_scenes/move_to_scene` | 无明确等价或未确认 | partial | `unitypkg/Editor/Commands/SceneCommands.cs` | 需逐项核 handler/metadata |
| `modify_build_settings` | `scene.build-settings` 只读为主 | partial | `src/tools/metadata/groups/scene.ts` | 缺 build scenes 修改工具 |
| scene templates `empty/default/3d_basic/2d_basic` | 无明确等价 | not-covered | — | 低优先级脚手架能力 |
| `validate/auto_repair` | 部分由 `doctor` / `diagnose` 覆盖 | partial | `src/doctor.ts`, `unitypkg/Editor/Commands/DiagnoseCommands.cs` | 缺 scene-specific validate/repair |

### GameObject / Component / Audio

| unity-mcp 能力 | unity_cli 等价 | 状态 | 证据 | 备注 |
|---|---|---|---|---|
| `find_gameobjects` | `go.find` | covered | `unitypkg/Editor/Commands/GameObjectCommands.cs`, `src/tools/metadata/groups/game-object.ts` | 搜索维度可能少于 unity-mcp |
| `manage_gameobject.create/modify/delete` | `go.create` / `go.modify` / `go.delete` | covered | `contracts/command-contract.json`, `tests/security.test.ts` | delete 有 destructive 标记 |
| `duplicate` | `go.duplicate` | covered | `unitypkg/Editor/Commands/GameObjectCommands.cs`, `src/tools/metadata/groups/game-object.ts` | 复制 GameObject，保留 transform 层级语义 |
| `move_relative/look_at` | `go.move-relative` / `go.look-at` | covered | `unitypkg/Editor/Commands/GameObjectCommands.cs`, `src/bridge/domains/game-object.ts` | transform convenience tools |
| component add/remove/set | `component.add/remove/set` | covered-adapted | `unitypkg/Editor/Commands/GameObjectCommands.cs`, `src/tools/metadata/groups/game-object.ts` | 拆为 component 域 |
| component get / properties | `go.inspect` / `context read scene.gameobject.components` / `context read scene.gameobject.component` | covered-adapted | `src/context/builtin.ts`, `unitypkg/Editor/Commands/ContextCommands.cs` | Resource 语义由 context 只读覆盖 |
| AudioSource play/stop/volume helper | 间接通过 `component.set` | partial | `unitypkg/Editor/Commands/GameObjectCommands.cs` | 缺 `audio.*` convenience tools |

### Asset / Prefab

| unity-mcp 能力 | unity_cli 等价 | 状态 | 证据 | 备注 |
|---|---|---|---|---|
| asset import/create/modify/delete/duplicate/move/rename | `asset.import/create/modify/delete/duplicate/move/rename` | covered | `unitypkg/Editor/Commands/AssetCommands.cs`, `src/tools/metadata/groups/asset.ts` | 直接等价 |
| asset search/info/create_folder/get_components | `asset.search/info/create` + `go.inspect` | covered-adapted | `src/tools/metadata/groups/asset.ts`, `unitypkg/Editor/Commands/AssetCommands.cs` | `get_components` 可能需通过 prefab/go 读取 |
| prefab create/info/hierarchy | `prefab.create/info/hierarchy` | covered | `unitypkg/Editor/Commands/PrefabCommands.cs`, `src/tools/metadata/groups/prefab.ts` | 直接等价 |
| `modify_contents` | `prefab.modify` | covered | `src/tools/metadata/groups/prefab.ts` | 参数覆盖需持续核对 |
| prefab stage open/save/close | `prefab.stage-open/save/close` | covered | `unitypkg/Editor/Commands/PrefabCommands.cs` | 直接等价 |

### Material / Shader / Texture

| unity-mcp 能力 | unity_cli 等价 | 状态 | 证据 | 备注 |
|---|---|---|---|---|
| material create/set property/color/assign/info | `material.create/set/assign/info` | covered-adapted | `unitypkg/Editor/Commands/MaterialCommands.cs`, `src/tools/metadata/groups/material.ts` | Renderer color helper 未单列 |
| shader create/read/update/delete | `shader.create/read/update` | partial | `unitypkg/Editor/Commands/ShaderCommands.cs`, `src/tools/metadata/groups/shader.ts` | `shader.delete` 需核对 |
| texture create/sprite/import settings/info | `texture.create/sprite/import-settings/info` | covered | `unitypkg/Editor/Commands/TextureCommands.cs`, `src/tools/metadata/groups/texture.ts` | 基础覆盖 |
| texture pattern/gradient/noise/pixels | 无明确等价或部分参数覆盖 | partial | `unitypkg/Editor/Commands/TextureCommands.cs` | 程序化生成细节需核对 |
| texture delete/modify | `asset.modify/delete` | covered-adapted | `src/tools/metadata/groups/asset.ts` | 文件层操作由 asset 覆盖 |

### Script / Code / ScriptableObject

| unity-mcp 能力 | unity_cli 等价 | 状态 | 证据 | 备注 |
|---|---|---|---|---|
| script create/read/edit/validate/hash/apply edits | `script.create/read/hash/edit/apply-edits/validate` | covered-adapted | `unitypkg/Editor/Commands/ScriptCommands.cs`, `src/tools/metadata/groups/script.ts` | `script.edit` 支持 precondition；`script.apply-edits` 支持 text/anchor/declaration edits |
| script delete | `asset.delete` | covered-adapted | `src/tools/metadata/groups/asset.ts`, `contracts/command-contract.json` | 文件删除统一走 asset destructive path |
| `apply_text_edits` | `script.edit` / `script.apply-edits` | covered-adapted | `unitypkg/Editor/Commands/ScriptCommands.cs`, `tests/bridge-commands.test.ts` | 支持 precondition sha / 多 edit 结构 |
| `script_apply_edits` AST/anchor/class/method 编辑 | `script.apply-edits` | covered-adapted | `unitypkg/Editor/Commands/ScriptCommands.cs`, `src/bridge/domains/script.ts` | 当前为 text/anchor/declaration DSL，不承诺完整 AST parser |
| `get_sha` | `script.hash` / `script.read` 返回 `sha256` | covered | `unitypkg/Editor/Commands/ScriptCommands.cs`, `contracts/command-contract.json` | 编辑前置条件 |
| `find_in_file` | CLI 内无 tool | not-covered | — | 是否纳入 Unity 工具需决策 |
| `execute_code` | `code.exec` | covered | `unitypkg/Editor/Commands/CodeExecCommand.cs`, `tests/runtime-code.test.ts` | live/batch/job 覆盖 |
| Roslyn runtime compilation | `code.exec` 部分覆盖 | partial | `unitypkg/Editor/Commands/CodeExecCommand.cs` | 缺加载程序集历史、类型查询、长期 runtime compilation 管理 |
| 通用 ScriptableObject create/info/modify | `scriptable-object.create/info/modify` | covered | `unitypkg/Editor/Commands/ScriptableObjectCommands.cs`, `src/tools/metadata/groups/scriptable-object.ts` | 通用 `.asset` ScriptableObject CRUD 子集 |

### Build / Test / Package

| unity-mcp 能力 | unity_cli 等价 | 状态 | 证据 | 备注 |
|---|---|---|---|---|
| build build/status/cancel | `build.run/status/logs/cancel` | covered-adapted | `src/cli-commands/build.ts`, `src/tools/metadata/groups/cli-only.ts` | CLI job 模型 |
| build platform/settings/scenes/profiles/batch | 部分由 `build.run` 参数和 `scene.build-settings` 覆盖 | partial | `src/cli-commands/build.ts`, `src/tools/metadata/groups/cli-only.ts` | 缺 build profile/settings 专用管理 |
| run_tests/get_test_job | `test.run/poll/logs/cancel` | covered-adapted | `src/cli-commands/test.ts`, `src/test-results.ts` | CLI job 模型 |
| package add/remove/list/search/info/registry/resolve/status | `package install/remove/list/search/info/registry/scoped-registry/resolve/status` | covered-adapted | `src/cli-commands/package.ts`, `src/package-manager.ts` | 命名不同；`package status` 输出 Bridge 安装状态 |
| embed_package | 无明确等价 | deferred | — | 需评估是否与 copy-mode Bridge 安装冲突 |

### Camera / Graphics / Lighting / Physics

| unity-mcp 能力 | unity_cli 等价 | 状态 | 证据 | 备注 |
|---|---|---|---|---|
| camera list/info/screenshot/create/lens/target/priority | `camera.*` | covered | `unitypkg/Editor/Commands/CameraCommands.cs`, `src/tools/metadata/groups/camera.ts` | 直接或拆分等价 |
| screenshot_multiview / orbit batch | 部分由 camera/scene screenshot 覆盖 | partial | `unitypkg/Editor/Commands/CameraCommands.cs` | 多视角批量截图需核对 |
| graphics pipeline/quality/settings/volume/stats/screenshot | `graphics.*` | covered-adapted | `unitypkg/Editor/Commands/GraphicsCommands.cs`, `src/tools/metadata/groups/graphics.ts` | 多数已覆盖 |
| renderer features | `urp.*` / `graphics.pipeline` | covered-adapted | `extensions/urp/unitypkg/Editor/Commands/UrpCommands.cs`, `src/tools/metadata/groups/urp.ts` | URP extension |
| skybox/ambient/fog/reflection/sun | `graphics.skybox-*` / `graphics.reflection-probe` | covered | `unitypkg/Editor/Commands/GraphicsCommands.cs`, `src/tools/metadata/groups/graphics.ts` | lighting 子集 |
| light baking/probes/reflection probe | `graphics.bake-*` / `graphics.light-probe-group` / `graphics.reflection-probe` | covered | `unitypkg/Editor/Commands/GraphicsCommands.cs` | 归 graphics 域 |
| Light component CRUD | 间接通过 `component.add/set` | partial | `unitypkg/Editor/Commands/GameObjectCommands.cs` | 缺 `lighting.light.*` convenience tools |
| physics settings/raycast/simulation | `physics.settings/raycast/simulation` | covered-adapted | `unitypkg/Editor/Commands/PhysicsCommands.cs`, `src/tools/metadata/groups/physics.ts` | 基础覆盖 |
| physics collision matrix/material/joints/forces/overlap/rigidbody validate | `physics.collision-matrix/material/joint/force/overlap/rigidbody` | covered-adapted | `unitypkg/Editor/Commands/PhysicsCommands.cs`, `src/tools/metadata/groups/physics.ts` | shapecast 仍未单列；overlap 覆盖常用查询 |

### Animation / UI / VFX / ProBuilder / Profiler

| unity-mcp 能力 | unity_cli 等价 | 状态 | 证据 | 备注 |
|---|---|---|---|---|
| Animator info/parameter/play/crossfade/speed/enabled | `animator.*` / `animation.*` | covered | `unitypkg/Editor/Commands/AnimationCommands.cs`, `contracts/command-contract.json` | 已拆分 |
| AnimatorController state/transition/parameter/layer/blend tree | `animation-controller.*` 或相关 commands | covered | `unitypkg/Editor/Commands/AnimationCommands.cs`, `contracts/command-contract.json` | 需保持 metadata 清晰 |
| Clip curve/vector curve/preset/event | `animation.*` | covered | `unitypkg/Editor/Commands/AnimationCommands.cs` | 子命令较多 |
| UI Toolkit create/read/update/list/delete/attach/render/visual tree/element modify | `ui.*` | partial | `unitypkg/Editor/Commands/UiCommands.cs`, `src/tools/metadata/groups/ui.ts` | PanelSettings/element modify 需核细节 |
| ParticleSystem/VFX Graph/LineRenderer/TrailRenderer | `vfx.*` extension | partial | `extensions/vfx-graph/unitypkg/Editor/Commands/VfxCommands.cs`, `src/tools/metadata/groups/vfx.ts` | 需和 unity-mcp 60+ action 逐项核 |
| ProBuilder shape/edit/vertex/material/uv/smoothing/pivot/repair | `probuilder.*` extension | covered | `extensions/probuilder/unitypkg/Editor/ProBuilder`, `src/tools/metadata/groups/probuilder.ts` | 可选包能力，覆盖较完整 |
| Profiler session/counters/memory/frame/export | `profiler.*` | covered | `unitypkg/Editor/Commands/ProfilerCommands.cs`, `src/tools/metadata/groups/profiler.ts` | 架构敏感域 |

### Docs / Reflect / Custom Tools

| unity-mcp 能力 | unity_cli 等价 | 状态 | 证据 | 备注 |
|---|---|---|---|---|
| Unity docs get/search/lookup | `docs get/search/topic` | covered-adapted | `src/docs-reflect.ts`, `tests/docs-reflect.test.ts` | 离线文档能力 |
| Unity reflect type/member/search | `reflect type/member/search` | covered-adapted | `src/docs-reflect.ts`, `tests/docs-reflect.test.ts` | 离线反射缓存 |
| project custom tools | `[UnityCliTool]` + `tools describe/run --include-custom` | covered-adapted | `unitypkg/Editor/ProjectTools`, `src/project-tool-discovery.ts` | descriptor/fingerprint 更强 |
| runtime custom tools | `runtime-tool.*` internal + `tools run --runtime` | covered-adapted | `unitypkg/Editor/Commands/RuntimeToolCommands.cs` | 统一 tools 表面暴露 |
| tool-level `equivalentUnityMcp` parity | metadata 字段存在，Context 已系统填写；Tool 级映射保留追踪项 | deferred | `schemas/tool-metadata.schema.json`, `src/tools/metadata/types.ts`, `src/context/metadata/index.ts` | 延期理由：249 个 Tool 需逐项语义确认，当前以本文矩阵和 contract 作为准源，避免批量误标 |

## Resources → Context 语义矩阵

| unity-mcp Resource | 语义 | unity_cli 等价 | 状态 | 备注 |
|---|---|---|---|---|
| `mcpforunity://editor/state` | Editor ready、编译、PlayMode、活动场景 | `context read editor.state` | covered | 已有 built-in context |
| `mcpforunity://editor/selection` | 当前选中对象 | `context read editor.selection` | covered | 已有 |
| `mcpforunity://editor/active-tool` | 当前 Editor 工具 | `context read editor.active-tool` | covered | 已有 |
| `mcpforunity://editor/windows` | 打开窗口列表 | `context read editor.windows` | covered | 已有 |
| `mcpforunity://editor/prefab-stage` | Prefab Stage 状态 | `context read prefab.stage` | covered-adapted | 名称适配；可加 alias `editor.prefab-stage` |
| `mcpforunity://project/info` | 项目信息 | `context read project.info` | covered | 已有 |
| `mcpforunity://project/tags` | 项目 tags | `context read project.tags` | covered | 已有 |
| `mcpforunity://project/layers` | 项目 layers | `context read project.layers` | covered | 已有 |
| `mcpforunity://menu-items` | Unity 菜单项 | `context read editor.menu-items` | covered | 已有 |
| `mcpforunity://scene/gameobject-api` | GameObject Resource 使用说明 | docs topic 或新增 `scene.gameobject-api` | partial | 建议补 context/docs 明确入口 |
| `mcpforunity://scene/gameobject/{instance_id}` | 单个 GameObject 基础数据 | `context read scene.gameobject --input '{"instanceId":...}'` | covered | 已有 |
| `mcpforunity://scene/gameobject/{id}/components` | GameObject 全组件与属性分页 | `context read scene.gameobject.components --input '{"instanceId":...}'` | covered | Context 已补齐 |
| `mcpforunity://scene/gameobject/{id}/component/{name}` | 单组件完整属性 | `context read scene.gameobject.component --input '{"instanceId":...,"component":"..."}'` | covered | Context 已补齐 |
| `mcpforunity://scene/cameras` | 场景 Camera/Cinemachine 概览 | `context read scene.cameras` | covered | 已有 |
| `mcpforunity://scene/volumes` | 场景 Volume 列表 | `context read scene.volumes` | covered | 已有 |
| `mcpforunity://rendering/stats` | 渲染统计 | `context read scene.rendering-stats` | covered | 已有 |
| `mcpforunity://pipeline/renderer-features` | URP renderer features | `context read scene.renderer-features` | covered-adapted | 名称适配 |
| `mcpforunity://prefab-api` | Prefab Resource 使用说明 | docs topic 或新增 `prefab.api` | partial | 建议补 docs/context |
| `mcpforunity://prefab/{path}` | Prefab asset 信息 | `context read prefab.info --input '{"path":"Assets/...prefab"}'` / `prefab.info` | covered | context + read-only tool 双入口 |
| `mcpforunity://prefab/{path}/hierarchy` | Prefab 层级 | `context read prefab.hierarchy --input '{"path":"Assets/...prefab"}'` / `prefab.hierarchy` | covered | context + read-only tool 双入口 |
| `mcpforunity://tests` | 测试列表 | `context read tests.list` | covered | 已有 |
| `mcpforunity://tests/{mode}` | 按 mode 测试列表 | `context read tests.list --input '{"mode":"EditMode"}'` | covered | 已有 |
| `mcpforunity://instances` | Unity 实例列表 | `context read instances.list` / `instances list` | covered | context 可离线读取实例 registry |
| `mcpforunity://tool-groups` | 工具组与启用状态 | `context read tools.groups` / `tools group` | covered | context 可离线读取内置/扩展工具组 |
| `mcpforunity://custom-tools` | 项目自定义工具清单 | `context read tools.custom` / `tools group list custom --include-custom` | covered | Bridge 项目工具 registry |

## Extensions 分类与实现矩阵

| extension | unity-mcp 对应分类 | unity_cli 运行时准源 | 状态 | 缺口/风险 |
|---|---|---|---|---|
| ProBuilder | optional `probuilder` tool group / `com.unity.probuilder` | `src/tools/metadata/groups/probuilder.ts`, `src/bridge/domains/probuilder.ts`, `extensions/probuilder/unitypkg/*`, `src/extensions/contribution-loader.ts` | covered-adapted | 运行时通过 `project-tool.run` 适配；Unity 侧代码作为物理 extension bundle 安装 |
| Cinemachine | camera/Cinemachine optional capability / `com.unity.cinemachine` | `src/tools/metadata/groups/cinemachine.ts`, `src/bridge/domains/cinemachine.ts`, `extensions/cinemachine/unitypkg/Editor/Commands/CinemachineCommands.cs`, `src/extensions/metadata/index.ts` | partial | `providesTools` 已从 runtime metadata 派生；`force_camera/release_override` 等 convenience action 仍需单独子矩阵 |
| VFX Graph / VFX | `vfx` tool group / VisualEffect、ParticleSystem、LineRenderer、TrailRenderer | `src/tools/metadata/groups/vfx.ts`, `src/bridge/domains/vfx.ts`, `extensions/vfx-graph/unitypkg/Editor/Commands/VfxCommands.cs`, `src/extensions/metadata/index.ts` | partial | `providesTools` 已从 runtime metadata 派生；60+ action 逐项 parity 和 Particle/Line/Trail 包依赖策略仍需单独子矩阵 |
| URP | graphics/URP optional capability / `com.unity.render-pipelines.universal` | `src/tools/metadata/groups/urp.ts`, `src/bridge/domains/urp.ts`, `extensions/urp/unitypkg/Editor/Commands/UrpCommands.cs`, `src/extensions/metadata/index.ts` | covered-adapted | 从 graphics 拆为 `urp` group；`providesTools` 已从 runtime metadata 派生 |
| HDRP | graphics/HDRP optional capability / `com.unity.render-pipelines.high-definition` | `src/tools/metadata/groups/hdrp.ts`, `src/bridge/domains/hdrp.ts`, `extensions/hdrp/unitypkg/Editor/Commands/HdrpCommands.cs`, `src/extensions/metadata/index.ts` | covered-adapted | 从 graphics 拆为 `hdrp` group；`providesTools` 已从 runtime metadata 派生 |

### Extension 漂移风险

| 风险 | 证据 | 处理建议 |
|---|---|---|
| `docs/extensions.md` 陈旧 | 曾写 Cinemachine/VFX/URP/HDRP backlog | 已更新为当前实现状态 |
| `src/extensions/metadata/index.ts` 旧分支 | 曾存在 Cinemachine/URP/HDRP `providesTools` 为空、VFX 仅 2 个 | 已改为从 runtime tool arrays 派生 |
| `src/extensions/registry-data.ts` 与运行时数量不一致 | contributions 是 descriptor/availability 发现入口，不是 runtime tool 完整清单 | 以 `src/tools/metadata/groups/*` 和 `src/extensions/metadata/index.ts` 为运行时工具准源 |
| `extensions/*` 外置副本分叉 | 当前仓库未使用外置 `extensions/*` 作为 runtime 准源 | 保留运行时准源说明，后续若恢复外置包再生成同步 |
| VFX group 依赖语义混杂 | ParticleSystem/LineRenderer/TrailRenderer 不一定需要 VFX Graph 包 | 拆分包依赖或写明 group 级依赖策略 |

## 缺口队列

| 优先级 | 缺口 | 原因 | 下一步 | 验证方式 |
|---|---|---|---|---|
| Done | `script_apply_edits` 等价能力 | 智能体安全改脚本核心能力 | 已新增 `script.apply-edits`：text/declaration/anchor edits + precondition hash | `npm test` + contract/source tests |
| Done | script SHA / precondition | 防止覆盖用户并发修改 | `script.read` / `script.hash` 输出 SHA；`script.edit` 支持 `preconditionSha256` | `npm test` + contracts |
| Done | Resource→Context 缺口 | Resources 语义需 context 等价 | 已新增 `scene.gameobject.components`、`scene.gameobject.component`、`prefab.info`、`prefab.hierarchy`、`instances.list`、`tools.groups`、`tools.custom` | context metadata + bridge source tests |
| Done | 通用 ScriptableObject CRUD | Unity 项目常见资产类型 | 已新增 `scriptable-object.create/info/modify` | C# handler + metadata + contract tests |
| Deferred | Tool-level `equivalentUnityMcp` | 防漂移 | 延期到独立语义映射任务；避免 249 个 Tool 批量误标，当前以本文矩阵和 contract 为准源 | metadata 测试 |
| Done | Extension 准源同步 | 扩展分类与实现要等价 | 已同步 `docs/extensions.md` 与 `src/extensions/metadata/index.ts`；`src/extensions/registry-data.ts` 保持 descriptor/availability 准源定位 | extension tests |
| Done | Editor undo/redo / tag/layer 管理 | unity-mcp tools 有操作能力 | 已新增 `editor.undo/redo`、`editor.resolution`、`project.tag/layer` | bridge tests |
| Done | Physics 高级能力 | collision matrix/material/joint/force/query/rigidbody | 已新增 collision matrix/material/joint/force/overlap/rigidbody | contract + C# source tests |
| Deferred | `embed_package` | package management 等价缺口 | 保留评估；当前 copy-mode Bridge 安装是 `init` 不变量，避免引入冲突语义；`package status` 已完成 | CLI tests |
| Extension follow-up | VFX 60+ action 逐项 parity | 可选包域当前 partial | 建 VFX 子矩阵，补缺口 | extension contract tests |
| UI follow-up | UI PanelSettings / visual element modify | UI Toolkit workflow 细颗粒度 | 核对并补 `ui.*` 子命令 | bridge + source tests |
| Texture follow-up | Texture pattern/gradient/noise | 程序化美术生成 | 补 `texture.pattern/gradient/noise` 或确认已有参数 | artifact smoke |
| Scene follow-up | Scene advanced operations | loaded scenes / set active / move / validate / templates | 补 scene 子命令 | bridge + smoke |
| Build follow-up | Build profile/settings/scenes 管理 | build workflow 完整性 | 补 build profile/settings 子命令 | CLI job tests |
| UX follow-up | Audio convenience tools | 可由 component 间接完成，但智能体体验差 | 新增 `audio.source.play/stop/volume` wrapper | metadata + bridge tests |
| UX follow-up | SceneView frame / multiview screenshot | 调试体验能力 | 新增 view-frame / multiview | screenshot smoke |

## 维护规则

- 新增或修改 Unity 操作工具时，同步更新 Tools 矩阵。
- 新增或修改只读状态能力时，同步更新 Resources → Context 矩阵，并保证 context 只读硬边界。
- 新增或修改可选包能力时，同步更新 Extensions 矩阵，明确是否 core、extension、requires package。
- 如果实际参考新的 `unity-mcp` 文件，应同步更新 `docs/critical-references/README.md` 的“参考/舍弃/适配”记录。
- `covered` 必须至少有 handler、metadata/contract、测试或 smoke 证据；缺任一关键环节应标为 `partial`。
- `covered-adapted` 必须写明适配方式，避免后续误判为缺失。
- 每次发布前应清理 `unknown`，所有 `not-covered/partial` 必须有负责人、验证方式和下一步。
