# Reference Projects Functional Alignment

本文档整理 `CoplayDev/unity-mcp` 与 `IvanMurzak/Unity-MCP` 的完整功能点，并与当前 `unity_cli` 已有能力对照。目标是后续完整对齐两者的 Unity 操作能力，但保留本项目的 CLI-first 使用形态：

- Tools 统一通过 `unity_cli tools group/list/describe/run` 暴露。
- 只读状态统一通过 `unity_cli context list/describe/read` 暴露。
- 不引入 MCP Server 作为主入口。
- 不把 gameplay/in-game API 或 AI 玩家输入模拟纳入本文档目标。

## 交付状态摘要（2026-06 全量对齐后）

首轮全量对齐已交付 52 个新工具（覆盖 object/component/reflection/type/scene/asset/prefab/shader/texture/script/screenshot/profiler/console/package/build/physics/ui 域），build/test(700)/contracts(311)/pack 全部通过。下表为参考项目源码深度对读（CoplayDev v9.6.6 commit 73eb27a、IvanMurzak main commit ca4d801）后的真实状态：

| 功能域 | 对齐前状态 | 对齐后状态 | 参考项目源码对读结论 |
| --- | --- | --- | --- |
| Object/Component/Reflection | Missing | ✅ Matched | Ivan ObjectRef/PathPatch/SerializedMember 已对齐：pathPatches 支持 slash+[i]/[key]，执行顺序 jsonPatch→pathPatches→diff。reflection 参数用 unity_cli native 简化（args vs SerializedMember type+name+value），功能等价。 |
| Scene | Partial | ✅ Matched | CoplayDev manage_scene 全 action 覆盖（unload/set-active/list-opened/data/view-frame 新增）。 |
| Asset/Prefab | Partial | ✅ Matched | asset.search-built-in/get-data/preview 新增；prefab.instantiate/unpack 新增。prefab headless modify_contents 记录为 Partial（可用 stage+component.modify 组合）。 |
| Script | Partial | ✅ Matched | update-or-create/find-in-file/capabilities 新增。 |
| Screenshot | Partial | ✅ Matched | game-view/scene-view/isolated-object/orbit 新增，camera.screenshot 保留主入口。 |
| Profiler | Partial | ✅ Matched | modules/areas/object-memory/rendering-stats/script-stats/clear/load 新增。frame-debugger/memory-snapshot 记录为依赖 Memory Profiler 包条件实现。 |
| Console/Editor/Package/Build | Partial | ✅ Matched | console.get/clear、editor.selection.set/active-tool.set、package.embed、build.scenes/platform/settings 新增。package.dev-deploy/restore 明确排除。 |
| Physics | Partial | ✅ Matched | raycast-all/linecast/shapecast(validate 新增；force 补 explosion/torque；shapecast 补 capsule。 |
| Texture/Material | Partial | ✅ Matched | texture.generate 补 checkerboard/stripes/dots/grid/brick/radial。material set_renderer_color mode 记录为 Partial（material.set+assign 部分覆盖）。 |
| UI | Partial | ✅ Matched | attach-document/create-panel-settings/get-visual-tree 新增。 |
| Camera/Cinemachine | Partial | Partial | cinemachine extension 覆盖 brain/blend；preset/force_camera/release_override/ensure_brain 记录为 extension 后续增强。 |
| Optional extensions | Partial | ✅ Extension | ProBuilder/Cinemachine/VFX/URP/HDRP 保持物理 extension 边界。Timeline/NavMesh/InputSystem/Terrain/Tilemap/Splines 记录为后续 optional extension 目标。 |

详细差异核对与取舍见 `.multica-evidence/reference-alignment-progress.md` 收口任务 A 节。
- 不把 Skill/Prompt 自动生成作为功能目标；文档只学习参考项目的 AI-facing 描述风格，示例必须使用 `unity_cli` 形态。

## 术语

| 术语 | 含义 |
| --- | --- |
| Tool | 可执行能力。只读 tool 可查询状态；写入 tool 会修改场景、资产、Editor 状态、文件或运行任务。 |
| Context | 只读状态面，对应 MCP Resource 语义。用于观察项目、Editor、Scene、Object、工具可用性等。 |
| High risk | 删除、批量写入、代码执行、反射调用、包安装/卸载、构建、测试、Profiler 清理/保存、菜单执行、物理模拟等。 |
| Existing | 当前项目已有或已有相近形态。 |
| Partial | 当前项目有一部分，但达不到参考项目完整语义。 |
| Missing | 当前项目缺失。 |
| Target shape | 对齐后在 `unity_cli` 中建议暴露的 tools/context 形态。 |

## 总览

| 功能域 | CoplayDev/unity-mcp | IvanMurzak/Unity-MCP | 当前 `unity_cli` | 对齐判断 |
| --- | --- | --- | --- | --- |
| Tools discovery | `manage_tools` group/list 等发现能力 | 动态 tool registry | `tools group/list/search/describe/run` | Existing：`unity_cli` 不采用 MCP 的预注册/启用会话模型；AI 按需发现工具、读取 `tools describe` 后即可调用。`manage_tools.activate/deactivate/sync/reset` 属 MCP 可见性机制，不纳入。 |
| Context/Resources | 项目、Editor、GameObject、Component、Prefab、Camera、Rendering、Tools、Tests 等 Resources | 1 个 `gameobject://currentScene/{path}` resource | 23 个 built-in contexts | Partial：Resource 覆盖较广，但 component/object 深度不足。 |
| GameObject | create/find/modify/delete/duplicate/move/look_at/set_parent | create/destroy/duplicate/find/modify/set-parent | `go.*` 多数已有 | Partial：缺独立 `go.set-parent`，搜索维度与深 object 数据不足。 |
| Component | add/remove/set/get/properties | add/destroy/get/modify/list-all | `component.add/remove/set` + context read | Partial：缺 `component.get/modify/list-all` 深能力。 |
| Generic Object | 无 Ivan 那样突出，但 Resources/Tools 分域覆盖 | `object-get-data/object-modify` | 无 | Missing。 |
| Object references | instance id、path、asset reference 等分散使用 | `ObjectRef/GameObjectRef/ComponentRef/AssetObjectRef/SceneRef` | target/path/id 分散 | Missing：缺统一引用模型。 |
| Deep serialization | Component Resource 全属性、Prefab/Asset 部分 | `SerializedMember` fields/props/value 深序列化 | 浅 properties/string summary | Missing/Partial。 |
| Reflection method | `unity_reflect` 偏 type/member/docs；custom tool execution | `reflection-method-find/call` | 离线 `reflect type/member/search` + `code.exec` | Missing：缺 live method find/call。 |
| Type schema | docs/reflect 查询 | `type-get-json-schema` | 离线 reflect，无 live schema | Missing。 |
| Scene | active/hierarchy/build-settings/create/load/save/screenshot/frame/orbit | create/open/save/unload/set-active/list-opened/get-data | active/hierarchy/build-settings/create/load/save/screenshot | Partial：缺 list-opened/unload/set-active/scene data 参数化/frame/orbit。 |
| Prefab | info/hierarchy/create/modify/stage open/save/close | create/instantiate/open/close/save | info/hierarchy/create/stage open/save/close/modify | Partial：缺深层 prefab component/property patch。 |
| Asset | search/info/create/import/modify/duplicate/move/rename/delete/preview | find/get-data/modify/copy/move/delete/refresh/create-folder | search/info/create/import/modify/duplicate/move/rename/delete | Partial：缺 deep asset get-data/patch、built-in asset search。 |
| Material | create/info/set/assign/color/property block | material create；shader list/get-data | material create/info/assign/set | Partial：shader data inspect 不完整，property block/unique material 需核对。 |
| Shader | create/read/update/delete | shader list/get-data | shader info/read/create/update | Partial：缺完整 shader data/pass/compile messages 或需验证。 |
| Texture | create/modify/delete/sprite/pattern/gradient/noise/import settings | 通过 assets/modify 覆盖部分 | texture create/info/sprite/import-settings | Partial：缺 pattern/gradient/noise/pixel modify。 |
| Screenshot | scene/camera/game view/surround/orbit/multiview | camera/game-view/scene-view/isolated | camera/scene/graphics/screenshot.capture/multiview | Partial：需强化 game-view/scene-view/isolated/orbit artifact metadata。 |
| Console | read/clear/filter/pagination/stacktrace | get/clear | `editor.console` | Partial：建议拆 `console.get/clear`，补过滤分页。 |
| Editor state/control | play/pause/stop、Unity Editor transform tool、tag/layer、prefab stage、undo/redo/menu | get/set application state、selection get/set | state/play/pause/stop/undo/redo/resolution/menu/tag/layer | Partial：缺 selection.set 与 `editor.active-tool.set`；`editor.active-tool` 是 Unity Editor Move/Rotate/Scale 工具，不是 CLI tool 可用性机制。 |
| Tests | run tests/get job | tests-run | test.run/poll/logs/cancel + tests.list context | Existing/Partial：测试列表 context 当前可靠性有限。 |
| Build | build/status/platform/settings/scenes/profiles/batch/cancel | 不突出 | build.run/status/logs/cancel | Partial：缺 platform/settings/scenes/profiles/batch build 管理。 |
| Package | list/search/info/add/remove/embed/resolve/registry/status；dev deploy/restore | list/add/remove/search | package list/info/search/install/remove/resolve/registry/scoped-registry/status | Partial：缺 `package.embed`；package dev deploy/restore 需决定实现或明确排除。 |
| Script editing | create/apply text edits/hash/apply structural edits/validate/delete | read/update-or-create/delete/execute | script create/read/hash/edit/apply-edits/validate + code.exec | Partial：缺 update-or-create 单工具语义、ObjectRef 参数化 script execute。 |
| Dynamic code | batch/code exec | script-execute Roslyn | code.exec | Partial：需支持 ObjectRef 参数和更强返回诊断。 |
| Profiler | session/counters/memory/frame/export/areas/object-memory/frame debugger/memory snapshot | start/stop/status/frame/memory/rendering/script/modules/clear/save/load | start/stop/pause/status/counter/memory/frame/capture/export | Partial：需补 modules、areas、object-memory、clear/load、rendering-stats、script-stats、frame debugger/memory snapshot。 |
| Physics | settings/raycast/linecast/shapecast/overlap/material/joint/force/rigidbody/simulation | 不突出 | settings/raycast/simulation/collision-matrix/material/joint/force/overlap/rigidbody | Partial：缺 shapecast/linecast/raycast_all 或需核对。 |
| Graphics/Rendering | pipeline/quality/settings/volume/lightmap/probes/reflection/URP features/stats | profiler rendering stats + shader | graphics.*、urp/hdrp extensions | Partial：URP/HDRP feature 操作需和 CoplayDev 逐项核对。 |
| Camera/Cinemachine | camera create/info/screenshot/lens/target/priority/brain/blend/orbit | screenshot camera | camera.* + cinemachine extension | Partial：需补 orbit/surround/scene-view frame 和 Cinemachine Tier 2 细项。 |
| UI | UI Toolkit UXML/USS/PanelSettings/UIDocument/visual tree；uGUI via GO/Component | prompts 参考为主 | ui canvas/text/button/image/document/hierarchy | Partial：缺 UI Toolkit element deep modify/PanelSettings 完整链路。 |
| Animation | Animator/Controller/Clip/BlendTree/Timeline 部分 | extension packages | animation.*、animator/controller tools | Partial：需补 Timeline 和 Animator/Clip 细项核对。 |
| VFX/Particle/Line/Trail | VFX Graph、ParticleSystem、VisualEffect、LineRenderer、TrailRenderer | extension packages | vfx extension | Partial：需逐 action 对齐。 |
| ProBuilder | shape、poly、face/edge/vertex/uv/material/smoothing/repair | AI ProBuilder extension | probuilder extension | Partial：能力多但当前物理 extension 边界待重构。 |
| Docs | Unity docs/manual/package docs | docs/wiki/prompts | docs get/search/topic | Partial：用户要求不把文档系统作为功能目标，仅保持 CLI docs。 |

## 参考项目功能点全集

### 1. Tools/Context Discovery

| 参考功能 | 来源 | 风险 | 当前状态 | Target shape |
| --- | --- | --- | --- | --- |
| 列出 tool groups | CoplayDev `manage_tools:list_groups` | 只读 | Existing：`tools group`、`context read tools.groups` | 保留 `tools group` / `context read tools.groups`。 |
| 按 group 列工具 | CoplayDev `manage_tools` | 只读 | Existing：`tools group list <group>` | 保留。 |
| 搜索工具 | 当前项目已有 | 只读 | Existing：`tools search` | 保留。 |
| 描述工具 | 两参考项目均重视 schema/description | 只读 | Existing：`tools describe` | 强化 AI-facing 描述，不改入口。 |
| 自定义工具发现 | CoplayDev resource、当前项目 ProjectTools | 只读 | Existing：`context read tools.custom`、`--include-custom` | 保留。 |
| 自定义工具执行 | CoplayDev `execute_custom_tool`、当前项目 ProjectTools | 写入/取决于工具 | Existing/Partial：`tools run <custom-tool> --include-custom` | 不新增专属 `execute_custom_tool`；发现后直接用统一 `tools run`。 |

### 2. Project / Editor / Instances Context

| 参考功能 | 来源 | 风险 | 当前状态 | Target shape |
| --- | --- | --- | --- | --- |
| 项目信息 | CoplayDev `project/info` | 只读 | Existing：`context read project.info` | 保留并补 pipeline/package capability 字段。 |
| Tags/Layers | CoplayDev resources | 只读 | Existing：`context read project.tags/layers` | 保留。 |
| Editor ready/compiling/playmode | CoplayDev `editor/state`、Ivan editor state | 只读 | Existing：`context read editor.state`、`tools run editor.state` | 保留。 |
| Editor selection read | 两参考项目 | 只读 | Existing：`context read editor.selection` | 保留。 |
| Editor selection set | Ivan `editor-selection-set` | 写入 | Missing | `tools run editor.selection.set`。 |
| Unity Editor active tool read | CoplayDev active-tool resource | 只读 | Existing | `context read editor.active-tool`；这里只表示 Unity Editor Move/Rotate/Scale 等当前编辑器工具，不表示 `unity_cli` tool activation。 |
| Unity Editor active tool set | CoplayDev `manage_editor.set_active_tool` | 写入 | Missing | `tools run editor.active-tool.set`；设置 Unity Editor Move/Rotate/Scale 等工具，不是 tool activation。 |
| Editor windows | CoplayDev resource | 只读 | Existing：`context read editor.windows` | 保留。 |
| Menu items | CoplayDev resource | 只读 | Existing：`context read editor.menu-items` | 保留。 |
| Execute menu item | CoplayDev `execute_menu_item`、当前 `editor.menu` | 高风险 | Existing | 保留 `tools run editor.menu`，加强 audit。 |
| 多 Unity 实例 | CoplayDev instances + set active instance | 只读/写入 target | Existing：`instances`/`target`/context | 保留。 |

### 3. Scene

| 功能点 | 来源 | 风险 | 当前状态 | Target shape |
| --- | --- | --- | --- | --- |
| 当前场景 | CoplayDev `manage_scene.get_active` | 只读 | Existing：`scene.active` | `tools run scene.active`。 |
| 场景层级 | CoplayDev `get_hierarchy` | 只读 | Existing：`scene.hierarchy` | 保留，增强 filter/depth/page。 |
| Build settings read | CoplayDev | 只读 | Existing：`scene.build-settings` | 保留。 |
| Build scenes modify | CoplayDev partial | 写入 | Missing/Partial | `scene.build-settings.modify` 或 scene build-scenes tool。 |
| 创建场景 | 两参考项目 | 写入 | Existing：`scene.create` | 保留。 |
| 打开/加载场景 | Ivan `scene-open`、CoplayDev load | 写入 | Existing：`scene.load` | 补 mode Single/Additive。 |
| 保存场景 | 两参考项目 | 高风险 | Existing：`scene.save` | 保留。 |
| 卸载场景 | Ivan `scene-unload` | 写入 | Missing | `tools run scene.unload`。 |
| 设置 active scene | Ivan `scene-set-active` | 写入 | Missing | `tools run scene.set-active`。 |
| 列出 opened scenes | Ivan `scene-list-opened` | 只读 | Missing | `tools run scene.list-opened` / `context read scene.opened`。 |
| 参数化 Scene Data | Ivan `scene-get-data` | 只读 | Partial | `tools run scene.data`，支持 depth/bounds/components/filter/maxResults；Context 只保留轻量稳定快照。 |
| SceneView frame | CoplayDev | 写入 Editor 视图 | Missing/Partial | `tools run scene.view-frame`。 |
| Scene/orbit/multiview screenshot | CoplayDev | 只读/artifact | Partial | 归 Screenshot 域。 |

### 4. GameObject / Component / Object

| 功能点 | 来源 | 风险 | 当前状态 | Target shape |
| --- | --- | --- | --- | --- |
| GameObject find/search | 两参考项目 | 只读 | Existing：`go.find` | 增强 tag/layer/component/path/id/filter/page。 |
| GameObject inspect | 当前项目/CoplayDev resource | 只读 | Existing：`go.inspect`、`context read scene.gameobject` | 保留轻量 inspect，不承担 deep object。 |
| GameObject create | 两参考项目 | 写入 | Existing：`go.create` | 保留。 |
| GameObject modify | 两参考项目 | 写入 | Existing：`go.modify` | 保留 transform/name/tag/layer/active。 |
| GameObject duplicate | 两参考项目 | 写入 | Existing：`go.duplicate` | 保留。 |
| GameObject delete/destroy | 两参考项目 | 高风险 | Existing：`go.delete` | 保留 destructive。 |
| Set parent | Ivan `gameobject-set-parent`、CoplayDev manage_gameobject | 写入 | Missing/Partial | `tools run go.set-parent`。 |
| Move relative / look at | CoplayDev | 写入 | Existing：`go.move-relative`、`go.look-at` | 保留。 |
| Component add | 两参考项目 | 写入 | Existing：`component.add` | 保留。 |
| Component remove/destroy | 两参考项目 | 高风险 | Existing：`component.remove` | 保留。 |
| Component set simple | 当前项目 | 写入 | Existing：`component.set` | 保留为快捷 simple setter。 |
| Component list all types | Ivan `gameobject-component-list-all` | 只读 | Missing | `tools run component.list-types`。 |
| Component deep get | Ivan `gameobject-component-get` | 只读 | Missing/Partial | `tools run component.get`，输出 fields/props，支持 paths/viewQuery/deepSerialization。 |
| Component deep modify | Ivan `gameobject-component-modify` | 高风险 | Missing | `tools run component.modify`，支持 diff/pathPatches/jsonPatch。 |
| Component context list | CoplayDev resource | 只读 | Existing：`context read scene.gameobject.components` | 保留为轻量列表/summary/page，不承诺 deep。 |
| Single component context | CoplayDev resource | 只读 | Existing：`context read scene.gameobject.component` | 保留轻量/预览，不替代 `component.get`。 |
| Generic object get data | Ivan `object-get-data` | 只读 | Missing | `tools run object.get-data`。 |
| Generic object modify | Ivan `object-modify` | 高风险 | Missing | `tools run object.modify`。 |
| Unified ObjectRef | Ivan refs | 基础设施 | Missing | 支持 ObjectRef/GameObjectRef/ComponentRef/AssetObjectRef/SceneRef；CLI 可提供 assetRef 友好别名。 |
| SerializedMember | Ivan ReflectorNet model | 基础设施 | Missing | 输出 typeName/name/value/fields/props/path/readOnly/writable/truncated。 |
| Object reference converter | Ivan converters | 基础设施 | Missing | patch/method call/code exec 可传 `{instanceId}`。 |

### 5. Assets / Prefabs / Materials / Shaders / Textures

| 功能点 | 来源 | 风险 | 当前状态 | Target shape |
| --- | --- | --- | --- | --- |
| Asset search | 两参考项目 | 只读 | Existing：`asset.search` | 保留，增强 filter/type/labels/packages。 |
| Built-in asset search | Ivan `assets-find-built-in` | 只读 | Missing | `tools run asset.search-built-in`。 |
| Asset info | 当前项目/CoplayDev | 只读 | Existing：`asset.info` | 保留。 |
| Asset deep data | Ivan `assets-get-data` | 只读 | Missing/Partial | `tools run asset.get-data`，复用 object serializer。 |
| Asset create folder/file | 两参考项目 | 写入 | Existing：`asset.create` | 保留。 |
| Asset import/refresh | 两参考项目 | 写入/高风险 | Existing：`asset.import`、`editor.refresh` | 保留。 |
| Asset modify deep patch | Ivan `assets-modify`、CoplayDev manage_asset.modify | 高风险 | Partial：`asset.modify` | 增强 jsonPatch/pathPatches/ObjectRef；文件级 asset 修改与 Unity Object patch 边界必须清晰。 |
| Asset preview | CoplayDev `manage_asset(generate_preview)` | 只读/artifact | Missing | `asset.preview` 或 `asset.info/search --preview`，输出 artifact metadata。 |
| Asset duplicate/move/rename/delete | CoplayDev/Ivan | 写入/高风险 | Existing | 保留。 |
| Prefab info/hierarchy | 两参考项目 | 只读 | Existing | 保留。 |
| Prefab create/instantiate | 两参考项目 | 写入 | create Existing；instantiate via prefab/go Partial | 统一 `prefab.create`、`prefab.instantiate`。 |
| Prefab unpack | CoplayDev prefab workflow | 写入/高风险 | Missing | `prefab.unpack`，将 prefab instance unpack 为普通 GameObjects。 |
| Prefab mode open/save/close | 两参考项目 | 写入/高风险 | Existing | 保留。 |
| Prefab deep modify | CoplayDev headless prefab modify | 高风险 | Partial | `prefab.modify` 增强 component/property patch。 |
| Material create/info/set/assign | 两参考项目 | 写入/只读 | Existing | 保留，补 property block/shared/instance/unique 语义。 |
| Renderer color/material helper | CoplayDev | 写入 | Partial | 可归 `material.assign`/`material.set-renderer`。 |
| Shader list/data | Ivan | 只读 | Partial | `shader.list`、`shader.get-data`，含 properties/subshaders/passes/messages。 |
| Shader file create/read/update/delete | CoplayDev | 只读/写入/高风险 | Partial | 保留现有 `shader.*`，删除走 `asset.delete` 或新增 `shader.delete`。 |
| Texture create/modify/delete | CoplayDev | 写入/高风险 | Partial | `texture.modify/delete` 或 asset.delete。 |
| Texture sprite/import settings | CoplayDev/current | 写入 | Existing/Partial | 保留。 |
| Texture pattern/gradient/noise/pixels | CoplayDev | 写入 | Missing | `texture.generate` / `texture.pixels`。 |

### 6. Script / Code / Reflection / Docs

| 功能点 | 来源 | 风险 | 当前状态 | Target shape |
| --- | --- | --- | --- | --- |
| Script create | 两参考项目 | 写入 | Existing | 保留。 |
| Script read | Ivan | 只读 | Existing | 保留。 |
| Script update-or-create | Ivan | 高风险 | Partial | `script.update-or-create` 或让 `script.create --overwrite` 对齐。 |
| Script delete | Ivan/CoplayDev | 高风险 | Partial：通过 asset.delete | 可保留 asset.delete 或新增 `script.delete`。 |
| Script hash | CoplayDev | 只读 | Existing：`script.hash` | 保留。 |
| Text edits | CoplayDev `apply_text_edits` | 高风险 | Existing：`script.edit` | 保留 SHA precondition。 |
| Structured/anchor edits | CoplayDev `script_apply_edits` | 高风险 | Existing：`script.apply-edits` | 保留并补 security proof 问题。 |
| Validate script | CoplayDev | 只读 | Existing | 保留，增强 Roslyn/Unity API diagnostics。 |
| Find in file | CoplayDev | 只读 | Missing | `tools run file.find` 或 `script.find-in-file`。 |
| Dynamic script execute | Ivan `script-execute` | 高风险 | Partial：`code.exec` | 增强 ObjectRef 参数、return schema、compile feedback。 |
| Live method find | Ivan `reflection-method-find` | 只读 | Missing | `tools run reflection.method.find`，不限制 public/private/static/instance。 |
| Live method call | Ivan `reflection-method-call` | 高风险 | Missing | `tools run reflection.method.call --yes`，不做调用范围限制，返回 audit。 |
| Type JSON schema | Ivan `type-get-json-schema` | 只读 | Missing | `tools run type.schema`。 |
| Script capabilities | CoplayDev `manage_script_capabilities` | 只读 | Missing/Adapted | `script.capabilities` 或明确由 `tools describe script.*` 等价。 |
| Legacy script router | CoplayDev `manage_script` | 兼容路由 | Adapted | 不新增主入口；由拆分后的 `script.*` / `asset.delete` / `script.find-in-file` 等价覆盖。 |
| Unity docs get/search/lookup | CoplayDev/Ivan docs | 只读 | Existing/Partial：`docs get/search/topic` | 保留 CLI docs 形式；`unity_docs.lookup` 的 asset fallback 需说明与 `asset.search` 的关系。 |
| Offline reflect type/member/search | 当前项目 | 只读 | Existing | 保留，与 live reflection 区分。 |

### 7. Screenshot / Visual Observation

| 功能点 | 来源 | 风险 | 当前状态 | Target shape |
| --- | --- | --- | --- | --- |
| Camera screenshot | 两参考项目 | 只读/artifact | Existing：`camera.screenshot` | 保留。 |
| Scene screenshot | CoplayDev/current | 只读/artifact | Existing：`scene.screenshot` | 保留。 |
| Game View screenshot | Ivan | 只读/artifact | Partial | `tools run screenshot.game-view`。 |
| Scene View screenshot | Ivan | 只读/artifact | Partial | `tools run screenshot.scene-view`。 |
| Isolated object screenshot | Ivan | 只读/artifact | Missing | `tools run screenshot.isolated-object`。 |
| Multiview | CoplayDev/current camera multiview | 只读/artifact | Partial | 保留 `tools run camera.multiview` 为主入口；`screenshot.multiview` 最多 alias。 |
| Orbit/surround batch | CoplayDev | 只读/artifact | Missing/Partial | `tools run screenshot.orbit`。 |
| Screenshot metadata | 两参考项目 | 只读 | Partial | 返回 path/source/camera/size/format/diagnostics/artifacts。 |

### 8. Console / Testing / Build / Jobs

| 功能点 | 来源 | 风险 | 当前状态 | Target shape |
| --- | --- | --- | --- | --- |
| Console get logs | 两参考项目 | 只读 | Existing：`editor.console` | 拆分或别名 `console.get`，支持 filter/page/stacktrace。 |
| Console clear | Ivan/CoplayDev | 写入 | Existing/Partial | `console.clear` 或 `editor.console --clear`。 |
| Run tests | 两参考项目 | 高风险 | Existing：`test.run` | 保留。 |
| Test job poll/logs/cancel | CoplayDev/current | 只读/高风险 cancel | Existing | 保留。 |
| Tests list context | CoplayDev Resource | 只读 | Existing：`context read tests.list` | 增强可靠枚举。 |
| Build run/status/logs/cancel | 当前项目 | 高风险 | Existing | 保留。 |
| Build platform/settings/scenes/profiles/batch | CoplayDev `manage_build` | 高风险 | Missing/Partial | 补 `build.platform`、`build.settings`、`build.scenes`、`build.profiles`、`build.batch` 或等价参数化工具。 |
| Batch execute | CoplayDev/current | 高风险 | Existing：`batch run` | 保留 fail-fast/parallel/preflight。 |
| Long-running processing model | Ivan/CoplayDev jobs | 写入/只读 | Existing for build/test/profiler | 统一 job envelope。 |

### 9. Profiler / Diagnostics

| 功能点 | 来源 | 风险 | 当前状态 | Target shape |
| --- | --- | --- | --- | --- |
| Profiler start/stop/pause/status | 两参考项目 | 写入/只读 | Existing | 保留。 |
| Counter/category | CoplayDev/current | 只读 | Existing：`profiler.counter` | 保留。 |
| Memory stats | Ivan/current | 只读 | Existing：`profiler.memory` | 保留，增强 reserved/allocated/mono/graphics。 |
| Frame stats/capture | 两参考项目 | 只读/artifact | Existing：`profiler.frame/capture` | 保留。 |
| Export/save/load data | Ivan/current export | 写入/只读 | Partial | 保留 `profiler.export` 主入口，补 `profiler.load`；`profiler.save` 只做 alias 或不新增。 |
| Module list/enable | Ivan | 只读/写入 | Missing | `profiler.modules`、`profiler.module-enable`。 |
| Profiler areas | CoplayDev | 写入 | Missing | `profiler.areas`，启停 Profiler area。 |
| Object memory | CoplayDev | 只读 | Missing | `profiler.object-memory`，查询指定对象内存。 |
| Clear profiler data | Ivan | 高风险 | Missing | `profiler.clear`。 |
| Rendering stats | Ivan/CoplayDev | 只读 | Existing as graphics/context partial | `profiler.rendering-stats` 表示采样/性能数据；`graphics.*` / Context 表示静态场景和管线状态。 |
| Script stats | Ivan | 只读 | Missing | `profiler.script-stats`。 |
| Frame Debugger events | CoplayDev | 写入/只读 | Missing | `profiler.frame-debugger.*`。 |
| Memory snapshot/list/compare | CoplayDev | 高风险/只读 | Missing | `profiler.memory-snapshot.*`，依赖 Memory Profiler 包。 |
| Diagnose/preflight | 当前项目 | 只读 | Existing | 保留。 |

### 10. Graphics / Camera / Render Pipelines

| 功能点 | 来源 | 风险 | 当前状态 | Target shape |
| --- | --- | --- | --- | --- |
| Pipeline info/settings/quality | CoplayDev/current | 只读/写入 | Existing | 保留。 |
| Graphics settings | Current/CoplayDev | 写入 | Existing | 保留。 |
| Volume list/create/effect params | CoplayDev/current | 只读/写入 | Partial | 增强 `graphics.volume`/`urp.volume`/`hdrp.volume`。 |
| Lightmap bake start/cancel/status/clear | CoplayDev/current | 高风险/只读 | Existing | 保留。 |
| Reflection probe/light probe | CoplayDev/current | 写入 | Existing | 保留。 |
| Skybox / ambient / fog / reflection / sun | CoplayDev/current | 写入/只读 | Existing/Partial | `graphics.skybox-*`，当前已有能力需在对齐目标中明确。 |
| URP renderer features list/add/remove/configure/toggle/reorder | CoplayDev | 写入/高风险 | Partial：`urp.feature` | 逐 action 对齐。 |
| HDRP settings/volume | CoplayDev/current | 写入 | Partial | 逐 action 对齐。 |
| Camera list/info/create | CoplayDev/current | 只读/写入 | Existing | 保留。 |
| Camera lens/target/priority | CoplayDev/current | 写入 | Existing/Partial | 保留，和 Cinemachine 区分。 |
| Cinemachine brain/blend/body/aim/noise/extensions | CoplayDev/current extension | 写入/高风险 | Partial | 逐 action 对齐，包含 force/release override、brain status、ensure brain；物理 extension 边界需重构。 |

### 11. Physics

| 功能点 | 来源 | 风险 | 当前状态 | Target shape |
| --- | --- | --- | --- | --- |
| Physics 3D/2D settings | CoplayDev/current | 写入/只读 | Existing | 保留。 |
| Raycast | 两项目/当前 | 只读 | Existing | 保留。 |
| Raycast all / linecast | CoplayDev | 只读 | Missing | `physics.raycast-all`、`physics.linecast`。 |
| Shapecast | CoplayDev | 只读 | Missing | `physics.shapecast`。 |
| Overlap | CoplayDev/current | 只读 | Existing | 保留。 |
| Collision matrix | CoplayDev/current | 写入 | Existing | 保留。 |
| Physics material 3D/2D | CoplayDev/current | 写入 | Existing | 保留。 |
| Joints | CoplayDev/current | 写入 | Existing | 保留。 |
| Force/torque/explosion | CoplayDev/current | 高风险 | Existing/Partial | 保留并核对 action。 |
| Rigidbody get/configure | CoplayDev/current | 只读/写入 | Existing/Partial | 保留。 |
| Physics validate | CoplayDev | 只读 | Missing/Partial | `physics.validate` 或 `physics.rigidbody --validate` alias，主语义必须明确。 |
| Simulation step | CoplayDev/current | 高风险 | Existing | 保留。 |

### 12. UI / Animation / VFX / ProBuilder / Optional Domains

| 功能域 | 来源功能点 | 当前状态 | Target shape |
| --- | --- | --- | --- |
| UI Toolkit | UXML/USS create/read/update、PanelSettings、UIDocument attach、visual tree | Partial | `ui.document.*`、`ui.panel-settings.*`、`ui.visual-tree`。 |
| uGUI | Canvas/Button/Image/Text/TMP/Slider/Toggle/InputField/EventSystem/InputModule | Partial | `ui.canvas/text/button/image/...` 或 go/component composition。 |
| Animation Clip | create/edit/bindings/events/presets | Partial | 增强 `animation.clip`。 |
| Animator | play/stop/crossfade/state/info/parameter/speed/enabled | Existing/Partial | 保留。 |
| AnimatorController | create/state/transition/parameter/layer/blend tree/assign | Existing/Partial | 保留，核对删除/修改 action。 |
| Timeline | Timeline asset/track/clip | Missing | 可作为 extension domain。 |
| VFX Graph | graph/effect/parameter/control/event/template | Partial | 逐 action 对齐。 |
| ParticleSystem | particle create/info/config | Partial | `vfx.particle-*` 对齐。 |
| LineRenderer/TrailRenderer | line/trail create/config | Partial | `vfx.line-*`、`vfx.trail` 对齐。 |
| ProBuilder | shape/poly/face/edge/vertex/material/uv/smoothing/merge/combine/repair | Partial/Existing | 保留 `probuilder.*`，但统一 tools 入口，移除专属顶层命令形态。 |
| Navigation | NavMesh surface/bake/agent/link | Missing | extension domain。 |
| Input System | InputAction asset inspect/modify | Missing | extension domain。 |
| Terrain | heights/textures/trees/details | Missing | extension domain。 |
| Tilemap | tilemap read/paint/batch modify | Missing | extension domain。 |
| Splines | spline read/edit | Missing | extension domain。 |
| Timeline | tracks/clips/bindings | Missing | extension domain。 |

## 当前 `unity_cli` 已有 Tools 清单按域

> 该清单来自当前源码与子代理盘点；最终实现时以 `src/tools/metadata/**`、`contracts/command-contract.json` 和 Bridge registry 为准。

### Existing core bridge / CLI tools

- Scene：`scene.active`、`scene.hierarchy`、`scene.build-settings`、`scene.screenshot`、`scene.create`、`scene.load`、`scene.save`。
- Camera：`camera.list`、`camera.info`、`camera.screenshot`、`camera.create`、`camera.target`、`camera.lens`、`camera.multiview`。
- Screenshot：`screenshot.capture`。
- GameObject：`go.find`、`go.inspect`、`go.create`、`go.modify`、`go.duplicate`、`go.move-relative`、`go.look-at`、`go.delete`。
- Component：`component.add`、`component.remove`、`component.set`。
- Asset：`asset.search`、`asset.info`、`asset.create`、`asset.import`、`asset.modify`、`asset.duplicate`、`asset.move`、`asset.rename`、`asset.delete`。
- Prefab：`prefab.info`、`prefab.hierarchy`、`prefab.create`、`prefab.modify`、`prefab.stage-open`、`prefab.stage-save`、`prefab.stage-close`。
- Material：`material.info`、`material.create`、`material.assign`、`material.set`。
- Shader：`shader.info`、`shader.read`、`shader.create`、`shader.update`。
- Texture：`texture.info`、`texture.create`、`texture.sprite`、`texture.import-settings`。
- Graphics：`graphics.quality`、`graphics.settings`、`graphics.pipeline`、`graphics.volume`、`graphics.screenshot`、`graphics.bake-start`、`graphics.bake-cancel`、`graphics.bake-status`、`graphics.bake-clear`、`graphics.bake-reflection-probe`、`graphics.bake-settings`、`graphics.light-probe-group`、`graphics.reflection-probe`、`graphics.probe-positions`、`graphics.stats`、`graphics.stats-counters`、`graphics.stats-memory`、`graphics.scene-debug`、`graphics.skybox-get`、`graphics.skybox-set-material`、`graphics.skybox-set-properties`、`graphics.skybox-set-ambient`、`graphics.skybox-set-fog`、`graphics.skybox-set-reflection`、`graphics.skybox-set-sun`、`graphics.pipeline-settings`、`graphics.pipeline-quality`、`graphics.pipeline-info`。
- Physics：`physics.settings`、`physics.raycast`、`physics.simulation`、`physics.collision-matrix`、`physics.material`、`physics.joint`、`physics.force`、`physics.overlap`、`physics.rigidbody`。
- Profiler：`profiler.status`、`profiler.start`、`profiler.stop`、`profiler.pause`、`profiler.counter`、`profiler.memory`、`profiler.frame`、`profiler.capture`、`profiler.export`。
- Editor：`editor.state`、`editor.play`、`editor.pause`、`editor.stop`、`editor.undo`、`editor.redo`、`editor.resolution`、`editor.menu`、`editor.console`、`editor.refresh`、`project.tag`、`project.layer`。
- Script：`script.create`、`script.read`、`script.hash`、`script.edit`、`script.apply-edits`、`script.validate`。
- ScriptableObject：`scriptable-object.create`、`scriptable-object.info`、`scriptable-object.modify`。
- Code：`code.exec`。
- Animation/Animator/Controller：`animation.clip`、`animation.state`、`animation.play`、`animation.stop`、`animator.get-info`、`animator.set-parameter`、`animator.get-parameter`、`animator.crossfade`、`animator.set-speed`、`animator.set-enabled`、`controller.create`、`controller.add-state`、`controller.add-transition`、`controller.add-parameter`、`controller.assign`、`controller.add-layer`、`controller.remove-layer`、`controller.set-layer-weight`、`controller.create-blend-tree-1d`、`controller.create-blend-tree-2d`、`controller.add-blend-tree-child`。
- UI：`ui.canvas.create`、`ui.text.create`、`ui.button.create`、`ui.image.create`、`ui.document.info`、`ui.document.create`、`ui.hierarchy`。
- Test/Build/Job：`test.run`、`test.poll/status`、`test.logs`、`test.cancel`、`build.run`、`build.status`、`build.logs`、`build.cancel`、`job status/logs/describe/cancel`。
- Package：`package list/info/search/install/remove/resolve/registry/scoped-registry/status`。
- Docs/Reflect：`docs topic/get/search`、`reflect type/member/search`。
- Batch/Instances/Target/Doctor：`batch schema/run`、`instances list/prune`、`target get/set/clear/alias`、`doctor`。
- Tools/Context/Extensions：`tools group/list/search/describe/run`、`context list/describe/read`、`extensions list/install/remove/doctor`。

### Existing contexts

- Project：`project.info`、`project.tags`、`project.layers`。
- Editor：`editor.state`、`editor.selection`、`editor.active-tool`、`editor.windows`、`editor.tool-states`、`editor.menu-items`。
- Prefab：`prefab.stage`、`prefab.info`、`prefab.hierarchy`。
- Scene：`scene.gameobject`、`scene.gameobject.components`、`scene.gameobject.component`、`scene.cameras`、`scene.volumes`、`scene.renderer-features`、`scene.rendering-stats`。
- Discovery/Test：`instances.list`、`tools.groups`、`tools.custom`、`tests.list`。

### Existing optional extension tool groups

- ProBuilder：`probuilder.*`，覆盖 shape、face/edge/vertex、material、uv、merge/combine、repair 等。
- Cinemachine：`cinemachine.*`，覆盖 create/target/lens/priority/blend/body/aim/noise/brain/extension。
- VFX：`vfx.*`，覆盖 graph/effect/parameter/control/event/template/particle/line/trail 等。
- URP：`urp.feature`、`urp.volume`、`urp.settings`。
- HDRP：`hdrp.volume`、`hdrp.settings`。

## 去重与合并规则

后续实现必须先套用本节规则，再新增工具。原则是：保留用户心智清晰的入口，但底层能力不要重复实现。

| 相似功能 | 重复风险 | 合并规则 |
| --- | --- | --- |
| `scene.gameobject.components` vs `component.get` | 都像“读组件”。 | 保留两者但分层：`context read scene.gameobject.components` 只做轻量组件列表/summary/page；`tools run component.get` 做完整 `fields/props/paths/viewQuery`。 |
| `scene.gameobject.component` vs `component.get` | 都读单个组件。 | Context 只做 preview/summary 或跳转入口；完整深读只放 `component.get`。 |
| `go.inspect` vs `object.get-data` | 都读 GameObject。 | `go.inspect` 保留轻量 GameObject 结构；`object.get-data` 做任意 `UnityEngine.Object` 深序列化。 |
| `asset.info` vs `asset.get-data` vs `object.get-data` | 都读 asset。 | `asset.info` 是资产摘要；`asset.get-data` 可以作为 asset-friendly wrapper，但底层复用 `object.get-data` serializer。 |
| `scriptable-object.info/modify` vs `object.get-data/modify` | ScriptableObject 也是 Unity Object。 | 现有 `scriptable-object.*` 保留为 convenience wrapper；长期底层复用 `object.*`，不重复 serializer/patch。 |
| `component.set` vs `component.modify` | 都写 Component。 | `component.set` 仅保留为简单单属性快捷入口；嵌套字段、数组、引用、patch 全部走 `component.modify`。 |
| `asset.modify` vs `object.modify` | 都可能改资产。 | 文件级 asset 内容/导入器操作保留在 `asset.modify`；Unity Object 序列化 patch 走 `object.modify`。 |
| `code.exec` vs `reflection.method.call` | 都能执行 C# 逻辑。 | `code.exec` 执行临时代码；`reflection.method.call` 调用已存在方法，不生成代码。 |
| `reflect type/member/search` vs `type.schema` / `reflection.method.find` | 都是反射相关。 | `reflect.*` 是离线文档/缓存查询；`type.schema` 和 `reflection.*` 是 live Unity 反射。 |
| `camera.screenshot` vs `screenshot.camera` | 语义完全重叠。 | 保留现有 `camera.screenshot` 作为主入口；不要新增重复主实现，最多提供 alias。 |
| `scene.screenshot` vs `screenshot.scene-view` | 名称相近但来源不同。 | 明确区分：`scene.screenshot` 面向 active scene/camera 输出；`screenshot.scene-view` 捕获 Editor SceneView。 |
| `graphics.screenshot` vs `screenshot.game-view` | 都可能输出 Game View 截图。 | 若 `graphics.screenshot` 只是带 diagnostics 的截图，应并入 `screenshot.game-view --include-diagnostics` 或保留兼容 alias。 |
| `profiler.export` vs `profiler.save` | 都写 profiler 数据。 | 二选一作为主语义；建议保留现有 `profiler.export`，新增 `profiler.load`，避免 `save/export` 双主入口。 |
| `scene.hierarchy` vs `scene.data` | 都读场景树。 | `scene.hierarchy` 是轻量层级；`scene.data` 是参数化深读，可包含 bounds/components/filter/maxDepth。 |
| `prefab.info` vs `prefab.hierarchy` | 都读 prefab。 | 保留：`info` 是摘要，`hierarchy` 是树；深度对象数据走 `object.get-data`。 |
| `editor.console` vs `console.get/clear` | 都操作 Console。 | 长期主入口应拆为 `console.get` / `console.clear`；`editor.console` 保留兼容 alias。 |
| `editor.selection` context vs `editor.selection.get` | 都读 selection。 | 不新增 selection get tool；读 selection 只用 `context read editor.selection`，只新增 `editor.selection.set`。 |

通用约束：

- Context 只观察，Tool 才执行。
- `*.info`、`*.inspect`、`*.hierarchy` 默认是摘要；深读统一走 `object.get-data` / `component.get`。
- 领域工具可以保留为 convenience wrapper，但 serializer、ObjectRef、patch 引擎只能有一套。
- 截图主实现应统一，旧命令可兼容 alias。
- 离线反射与 live 反射必须分层命名，不混用。
- 写入类 Object/Component/Asset deep modify 必须共享同一套 patch/audit 模型。

## 大域开工门禁

每开始一个大域实现前，必须先执行一次门禁，避免遗漏参考项目 action、误判当前状态或引入重复入口。门禁准源是 `docs/reference-projects-complete-alignment-plan.md` 的“大域开工门禁”。

门禁结论至少要覆盖：参考能力冻结、当前能力映射、入口与去重确认、core/extension 边界、安全契约、最小验收样例。未通过门禁时，不进入该大域实现。

## 必须新增或增强的功能点

### A. Object / Component / Reflection 核心能力

这些是吸收 IvanMurzak 的核心，不应仅通过 Context 表示。

| 目标功能 | Target tool/context | 说明 |
| --- | --- | --- |
| 统一引用模型 | internal infrastructure | ObjectRef/GameObjectRef/ComponentRef/AssetObjectRef/SceneRef，所有 deep object、method call、patch 共用；CLI schema 可提供 assetRef 友好别名。 |
| Deep object read | `tools run object.get-data` | 任意 `UnityEngine.Object`，支持 fields/props/value/path/truncation。 |
| Deep object modify | `tools run object.modify` | 支持 `objectDiff`、`pathPatches`、`jsonPatch`，修改后回读。 |
| Component deep get | `tools run component.get` | 对齐 Ivan `gameobject-component-get`，支持 includeFields/includeProperties/deepSerialization/paths/viewQuery。 |
| Component deep modify | `tools run component.modify` | 对齐 Ivan `gameobject-component-modify`，支持 componentDiff/pathPatches/jsonPatch。 |
| Component type discovery | `tools run component.list-types` | 枚举可添加/可用 Component 类型。 |
| SerializedMember model | internal + output schema | 统一 typeName/name/value/fields/props/path/readOnly/writable/error/truncated。 |
| Paths read | input option | 局部读取字段路径，避免全量 dump。 |
| View query | input option | path/namePattern/typeFilter/maxDepth。 |
| Type schema | `tools run type.schema` | 生成 C# / Unity 类型 JSON Schema。 |
| Live method find | `tools run reflection.method.find` | 任意 public/private/static/instance；只读。 |
| Live method call | `tools run reflection.method.call --yes` | 不做范围限制；高风险，要求 `--yes`，返回 target preview、audit、异常与结果序列化。 |
| ObjectRef 参数传递 | code exec / method call / patch | `{instanceId}`、asset path/guid、component index/type 可还原对象引用。 |

### B. Context 语义校正

| 现有 context | 保留原因 | 需要校正 |
| --- | --- | --- |
| `scene.gameobject.components` | 对应 CoplayDev Resource；适合第一步探索组件列表。 | 定义为轻量组件列表/summary/page，不承诺完整 fields/props。 |
| `scene.gameobject.component` | 对应 CoplayDev 单组件 Resource。 | 可作为轻量预览或跳转入口；深度读取交给 `component.get`。 |
| `scene.gameobject` | 对象基础状态。 | 保持 name/tag/layer/active/transform/parent/children/component types，不替代 `object.get-data`。 |

### C. Screenshot 强化

| 目标功能 | Target tool | 当前状态 |
| --- | --- | --- |
| Game View screenshot | `screenshot.game-view` | Missing/Partial。 |
| Scene View screenshot | `screenshot.scene-view` | Missing/Partial。 |
| Camera screenshot | 保留 `camera.screenshot` 主入口 | Existing。 |
| Isolated object screenshot | `screenshot.isolated-object` | Missing。 |
| Multiview | 保留 `camera.multiview` 主入口 | Partial。 |
| Orbit/surround batch | `screenshot.orbit` / `screenshot.surround` | Missing/Partial。 |
| Artifact metadata | all screenshot tools | 需统一返回 source/camera/path/size/format/diagnostics。 |

### D. Profiler 强化

| 目标功能 | Target tool | 当前状态 |
| --- | --- | --- |
| Module list | `profiler.modules` | Missing。 |
| Module enable/disable | `profiler.module-enable` | Missing。 |
| Clear data | `profiler.clear` | Missing。 |
| Export/load stats | `profiler.export` / `profiler.load` | Export 已有；补 load。`profiler.save` 不新增主入口。 |
| Profiler areas | `profiler.areas` | Missing。 |
| Object memory | `profiler.object-memory` | Missing。 |
| Rendering stats | `profiler.rendering-stats` | Partial；`profiler.*` 负责采样/性能，graphics/context 负责静态状态。 |
| Script stats | `profiler.script-stats` | Missing。 |
| Frame debugger | `profiler.frame-debugger.*` | Missing。 |
| Memory snapshot/list/compare | `profiler.memory-snapshot.*` | Missing，可依赖 Memory Profiler 包。 |

### E. Scene / Asset / Prefab / Script 补强

| 目标功能 | Target tool/context | 当前状态 |
| --- | --- | --- |
| scene list opened | `scene.list-opened` / `context read scene.opened` | Missing。 |
| scene unload | `scene.unload` | Missing。 |
| scene set active | `scene.set-active` | Missing。 |
| scene data parameterized | `scene.data` | Partial；作为 read-only Tool，不作为复杂 Context 主入口。 |
| asset built-in search | `asset.search-built-in` | Missing。 |
| asset deep data | `asset.get-data` | Missing/Partial。 |
| asset deep modify | enhanced `asset.modify` | Partial；文件级 asset 修改与 Unity Object patch 边界必须明确。 |
| asset preview | `asset.preview` 或 `asset.info/search --preview` | Missing。 |
| prefab instantiate | `prefab.instantiate` | Missing/Partial。 |
| prefab unpack | `prefab.unpack` | Missing。 |
| prefab deep modify | enhanced `prefab.modify` | Partial；覆盖 headless prefab contents 修改，与 `prefab.stage-*` 分开。 |
| shader list/get-data | `shader.list` / `shader.get-data` | Partial。 |
| texture pattern/gradient/noise | `texture.generate` | Missing。 |
| script update-or-create | `script.update-or-create` | Partial。 |
| script execute ObjectRef params | enhanced `code.exec` | Partial。 |
| script capabilities | `script.capabilities` 或 `tools describe script.*` 等价 | Missing/Adapted。 |
| legacy script router | 不新增主入口 | Adapted；由拆分后的 `script.*` / `asset.delete` / `script.find-in-file` 覆盖。 |
| find in file | `script.find-in-file` | Missing。 |

### F. Optional domains to keep as extension targets

这些功能点可纳入长期完全对齐，但不应污染 core package；应保留为物理可选 extension，core 不允许硬引用可选 Unity package。

- ProBuilder：完整 shape/poly/face/edge/vertex/material/uv/smoothing/repair。
- Cinemachine：brain/blend/body/aim/noise/extensions、Camera orbit/surround。
- VFX Graph extension ID 使用 `vfx-graph`；工具组为 `vfx.*`。
- ParticleSystem/LineRenderer/TrailRenderer 可归 visual effects extension，但不是 VFX Graph 的同义词。
- URP/HDRP：Renderer features、Volumes、Pipeline settings。
- Animation/Timeline：AnimationClip、AnimatorController、Timeline tracks/clips。
- UI Toolkit/uGUI/TMP/InputSystem。
- Navigation/NavMesh。
- Terrain。
- Tilemap。
- Splines。

## 风险与安全策略

| 功能 | 风险 | 对齐要求 |
| --- | --- | --- |
| delete/remove/destroy | 破坏性 | `destructiveHint: true`，支持 dry-run/preview/`--yes`。 |
| package add/remove/embed | 破坏性、domain reload | `destructiveHint: true`，返回 next_actions 和 audit。 |
| script/code exec | 任意代码执行 | 高风险，保留 explicit execution/audit。 |
| reflection.method.call | 任意方法调用 | 不做调用范围限制；必须标高风险，返回完整 audit。 |
| object/component modify | 可修改任意对象状态 | 高风险，支持 patch preview、changed paths、updated data、logs。 |
| prefab save/modify | 影响所有实例 | 高风险，明确 prefab mode / asset path / dirty state。 |
| editor.menu | 任意菜单副作用 | 高风险，记录 menu path 和结果。 |
| physics simulate/force | 改变场景/运行状态 | 高风险，记录 frame/time/targets。 |
| profiler clear/save/load | 影响诊断数据或写文件 | clear 高风险，save 写 artifact。 |
| screenshot | 写 artifact | 非破坏性，但要校验输出路径和 overwrite。 |

## 文档风格要求

后续文档可以学习 IvanMurzak 的清晰说明方式，但必须遵守本项目形态：

- 示例使用 `unity_cli tools describe <tool>`、`unity_cli tools run <tool> --input '<json>'`。
- 只读状态使用 `unity_cli context describe/read <name>`。
- 不写 MCP Client、MCP Server、SignalR、Prompt/Skill 作为用户主路径。
- 每个 tool 描述应说明：用途、何时使用、输入关键字段、输出形态、风险、常见失败与 next actions。
- 修改类工具应明确建议先读后改，例如先 `object.get-data` / `component.get`，再 `object.modify` / `component.modify`。

## 完全对齐目标清单

后续若执行“完全对齐”，应以本文档为功能范围基线。必须覆盖：

1. 当前已有功能保持不回退。
2. CoplayDev 的 Resource 语义在 `context` 中有等价读面。
3. CoplayDev 的 Tool 语义在 `tools run` 中有等价或明确 adaptation。
4. IvanMurzak 的 Object/Component/Reflection 智能层在 `tools run` 中有等价能力。
5. Screenshot 和 Profiler 强化纳入核心目标。
6. Optional domains 使用 extension 形态，不将可选包硬引用塞入 core。
7. 所有写入/高风险工具具备 metadata 风险标记、确认边界和结构化 audit。
