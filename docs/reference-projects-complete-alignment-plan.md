# Reference Projects Complete Alignment Plan

本文档定义 `unity_cli` 对齐两个参考项目功能点的完整实施计划。它不是分阶段路线图，而是一个完整交付包的边界、实现点、去重规则与验收标准。

## 参考项目

- CoplayDev/unity-mcp: https://github.com/CoplayDev/unity-mcp
- IvanMurzak/Unity-MCP: https://github.com/IvanMurzak/Unity-MCP

## 目标

完整吸收两个参考项目中适合 `unity_cli` 的 Unity Editor 自动化、对象观察、对象修改、反射、截图、Profiler、资源管理和可选扩展能力，同时保持本项目的使用形态：

```text
unity_cli tools group/list/search/describe/run
unity_cli context list/describe/read
unity_cli extensions list/install/remove/doctor
unity_cli init
```

成功后的 `unity_cli` 应具备：

- AI 能按需发现工具、读取工具描述，然后调用工具。
- AI 能通过 Context 观察当前项目、Editor、Scene、Prefab、Tools、Tests 等状态。
- AI 能通过 Tools 执行只读查询、写入操作、高风险操作和 artifact 生成。
- AI 能深度读取和修改 Unity Object / Component / Asset。
- AI 能查找和调用任意 C# 方法。
- AI 能获得更完整的截图、Profiler、Scene、Prefab、Asset、Script、Physics、Graphics、UI、Animation、VFX、ProBuilder 能力。

## 非目标

- 不把项目改成 MCP Server。
- 不引入 SignalR/WebSocket 作为主架构。
- 不以 Prompt/Skill 自动生成为功能目标。
- 不实现 gameplay/in-game custom API 对齐。
- 不实现 AI 玩家输入模拟或 player-equivalent runtime driver。
- 不把 CoplayDev 的 tool activation/session 模型照搬进 `unity_cli`。
- 不实现 MCP request/session diagnostics，例如 `debug_request_context`。
- 不实现 `manage_tools.activate/deactivate/sync/reset` 的 MCP tool visibility 语义；`unity_cli` 由 AI 按需发现与描述工具。
- 不让 Context 承担写入或深度操作职责。
- 不新增或保留非白名单用户可见顶层命令；核心入口只允许 `tools` / `context` / `extensions` / `init`。

## 核心边界

### Top-Level CLI Baseline

用户可见顶层命令只允许：

- `tools`
- `context`
- `extensions`
- `init`

所有 Unity 操作、离线查询、构建、测试、包管理、诊断、文档、反射、代码执行、实例/目标选择等能力，都必须通过 Tool 或 Context 暴露。Bridge command、job runner、worker、registry 只能作为内部实现细节，不能定义用户可见顶层命令形态。

后续对齐实现不得新增或保留 domain 顶层入口；所有 `ToolMetadata.cliCommand`、examples、next_actions、README、Skill 和 docs 示例必须使用 `unity_cli tools ...`、`unity_cli context ...`、`unity_cli extensions ...` 或 `unity_cli init ...`。

### Context 与 Tool

| 类型 | 定义 | 示例 |
| --- | --- | --- |
| Context | 稳定只读状态面，用于观察事实，不执行写入，不承担复杂操作。 | `context read editor.state`、`context read scene.gameobject.components` |
| Read-only Tool | 可调用的只读动作，通常参数更复杂，可能走 Bridge、job 或生成 artifact 元数据。 | `tools run object.get-data`、`tools run component.get`、`tools run reflection.method.find` |
| Mutating Tool | 修改 Unity、项目文件、资产、场景或 Editor 状态。 | `tools run component.modify`、`tools run scene.save` |
| High-risk Tool | 删除、执行代码、反射调用、包安装、构建、测试、清理数据、Prefab/Scene 保存等。 | `tools run reflection.method.call --yes`、`tools run code.exec` |

Tools 不等于全部带操作。Tools 可以是只读，也可以写入。Context 只能只读。

### Tool discovery

`unity_cli` 不采用 MCP 的预注册/启用会话模型。AI 的工作流是：

```text
tools group/search
→ tools describe <name>
→ tools run <name> --input '<json>'
```

因此不实现 `manage_tools.activate/deactivate/sync/reset` 的 MCP 可见性/会话语义。已有 `tools group/list/search/describe/run` 就是主能力。项目自定义工具的执行也不需要单独 `execute_custom_tool` 顶层入口；发现后直接走 `tools run <custom-tool> --include-custom`。

### Unity Editor active tool

`editor.active-tool` 只表示 Unity Editor 当前 Move/Rotate/Scale 等编辑器工具，不代表 `unity_cli` 工具可用性，也不是 tool activation 机制。读取用 `context read editor.active-tool`；如果对齐 CoplayDev 的 `set_active_tool`，写入能力应是独立 Tool：`tools run editor.active-tool.set`。

## 去重规则

后续实现必须遵守以下合并边界，避免重复造工具或重复底层实现。

| 相似能力 | 规则 |
| --- | --- |
| `scene.gameobject.components` vs `component.get` | Context 只列组件 summary/page；完整 fields/props 只放 `component.get`。 |
| `scene.gameobject.component` vs `component.get` | Context 做 preview；深度读取只放 `component.get`。 |
| `go.inspect` vs `object.get-data` | `go.inspect` 是轻量 GameObject 摘要；`object.get-data` 是任意 Unity Object 深序列化。 |
| `asset.info` vs `asset.get-data` vs `object.get-data` | `asset.info` 是资产摘要；`asset.get-data` 是 asset-friendly wrapper，底层复用 `object.get-data`。 |
| `scriptable-object.*` vs `object.*` | ScriptableObject 领域工具保留为 convenience wrapper，底层复用 object serializer/patch。 |
| `component.set` vs `component.modify` | `component.set` 是简单单属性快捷入口；复杂、嵌套、数组、引用、patch 全部走 `component.modify`。 |
| `asset.modify` vs `object.modify` | 文件级 asset 修改走 `asset.modify`；Unity Object 序列化 patch 走 `object.modify`。 |
| `code.exec` vs `reflection.method.call` | `code.exec` 执行临时代码；`reflection.method.call` 调用现有方法，不生成代码。 |
| `reflect.*` vs `reflection.*` | `reflect.*` 是离线文档/缓存类 Tool；`reflection.*` 是 live Unity 反射类 Tool。二者都必须通过 `tools run` 暴露。 |
| `camera.screenshot` vs `screenshot.camera` | 保留现有 `camera.screenshot` 主入口，不新增重复主实现。 |
| `scene.screenshot` vs `screenshot.scene-view` | `scene.screenshot` 面向 active scene/camera；`screenshot.scene-view` 捕获 Editor SceneView。 |
| `graphics.screenshot` vs `screenshot.game-view` | 如只是带 diagnostics 的 Game View 截图，应合并到一个主 Tool，不保留双主实现。 |
| `profiler.export` vs `profiler.save` | 保留一个主语义，建议沿用 `profiler.export`，新增 `profiler.load`。 |
| `scene.hierarchy` vs `scene.data` | `scene.hierarchy` 是轻量层级；`scene.data` 是参数化深读。 |
| `editor.selection` context vs `editor.selection.get` | 读 selection 只用 Context；只新增 `editor.selection.set`。 |
| `editor.active-tool` context vs `editor.active-tool.set` | 读当前 Unity Editor Move/Rotate/Scale 工具走 Context；设置当前编辑器工具才是 Tool，且不得与 `unity_cli` tool activation 混淆。 |

底层能力也必须去重：ObjectRef、serializer、patch、audit 只能有一套，被 object/component/asset/scriptable-object 等领域工具复用。

## /goal 全量执行协议

本计划可以用一次 `/goal` 全量执行。执行时不得把目标缩小为单个大域，也不得在单个大域完成后调用 `goal_complete`。只有全部大域完成、全部验收标准逐项通过、最终验证命令通过后，才能调用 `goal_complete`。

推荐启动方式：

```text
/goal 按 docs/reference-projects-complete-alignment-plan.md 一次性完成全量对齐。必须按文档的大域顺序执行；每个大域开工前执行“大域开工门禁”，并重新对读 CoplayDev/unity-mcp 与 IvanMurzak/Unity-MCP 的相关源码/文档，记录参考能力、实现方式取舍和排除理由；每个大域完成后执行大域自测和方向审查，并更新 .multica-evidence/reference-alignment-progress.md。不要把目标拆小，不要在单个大域完成后调用 goal_complete。只有所有大域完成、所有验收标准逐项通过、build/test/contracts/pack 验证通过后，才能调用 goal_complete。
```

全量执行顺序固定如下，除非门禁发现依赖关系必须调整，并在进度账本中记录原因：

1. 修复已知 blocker 与基础契约：`script.apply-edits` security proof、Context descriptor/metadata 分裂、`tools run --input-file` 结构化输入、contract/test 基础。
2. Object / Component / Reflection 智能层。
3. Context 语义校正与增强。
4. Scene / Asset / Prefab / Script / Docs 补强。
5. Screenshot / Visual Observation。
6. Profiler / Diagnostics。
7. Build / Package / Console / Testing / Jobs。
8. Physics / Graphics / Camera 基础能力。
9. UI / Animation。
10. 物理可选 extensions：ProBuilder、Cinemachine、VFX Graph、URP、HDRP，以及 Navigation、Input System、Terrain、Tilemap、Splines 等后续 optional domains。
11. 文档、Skill、README、risk 文档与最终验收收口。

全量执行必须维护固定进度账本：

```text
.multica-evidence/reference-alignment-progress.md
```

每个大域至少记录：大域名称、参考项目对读证据、实现方式取舍、门禁结论、实现文件、contract/test 变更、大域自测命令与结果、方向审查结论、未实现项与原因、下一大域依赖。该账本是 `/goal` 自动续跑和上下文压缩后的恢复准源；不得只依赖对话记忆或本文档的二手摘要。

## 大域开工门禁

每开始一个大域实现前，必须先执行一次大域门禁，并把结论记录到 `.multica-evidence/reference-alignment-progress.md`。一个大域内只需要执行一次；如果参考项目发现新能力、用户边界变化、或实现范围跨到另一个大域，则必须重新执行。

大域包括但不限于：Object/Component/Reflection、Context、Screenshot、Profiler、Scene/Asset/Prefab/Script、Build/Package、Physics/Graphics、UI/Animation、VFX/ProBuilder/Cinemachine/URP/HDRP 等可选 extension。

门禁检查项：

1. **参考项目源码对读**：必须重新查看 CoplayDev/unity-mcp 与 IvanMurzak/Unity-MCP 中本大域相关的源码、文档、schema、README 或 package 说明，记录具体 URL、文件路径、commit/branch、功能入口和关键实现点。不得只依赖本计划或历史对话摘要。
2. **参考能力冻结**：基于参考项目对读结果，列出本大域来自 CoplayDev 与 IvanMurzak 的 action/resource/tool 清单，并标出明确排除项。
3. **实现方式取舍**：对每个能力给出取舍结论：采用 CoplayDev 方式、采用 IvanMurzak 方式、二者结合、保持 `unity_cli` native 方式、或明确排除；每项必须说明原因。
4. **当前能力映射**：列出本项目已有 Tool/Context/Bridge/Unity C# handler/extension 入口，区分 Existing、Partial、Missing。
5. **入口与去重确认**：确认每个能力最终落到 `tools run`、`context read`、`extensions` 或 `init`，并套用本计划的去重规则，不新增重复主入口或非白名单顶层命令。
6. **边界确认**：确认是否属于 core、physical extension、read-only Tool、Context、mutating Tool 或 high-risk Tool；可选 Unity package 不得进入 core 硬引用。
7. **安全与契约确认**：高风险能力必须定义 `destructiveHint`、`--yes`、dry-run/preview、fingerprint/security proof、audit、contract 和最小测试点。
8. **验收样例确认**：列出本大域至少一个 `tools describe`、一个成功 `tools run/context read`、一个失败/风险路径，以及需要落盘的验证命令。

未通过门禁时，不进入该大域实现；只能先补文档、contract 或用户决策。

## 大域完成门禁

每个大域实现完成后，必须先通过大域完成门禁，才能进入下一个大域。

完成门禁包括：

1. **大域自测**：运行与本大域最相关的最小测试、contract 校验或静态检查。能跑具体测试时先跑具体测试；无法直接验证 Unity Editor 行为时，至少补 source-level/contract 测试并记录 Unity smoke 待验证项。
2. **方向审查**：对照本计划的目标、非目标、核心边界、去重规则和大域门禁结论，确认实现没有偏向 MCP Server、Prompt/Skill 生成、gameplay/in-game API、AI 玩家输入模拟、重复入口、core 硬引用可选包等错误方向。
3. **风险审查**：确认新增/修改的 mutating 或 high-risk Tool 具备 `destructiveHint`、`--yes`、dry-run/preview、fingerprint/security proof、audit、contract 与失败路径。
4. **契约审查**：确认 ToolMetadata、Bridge registry、C# command registry、contracts、docs 和 tests 没有漂移；确认本大域没有新增或保留非白名单用户可见顶层命令。
5. **进度账本更新**：把自测结果、方向审查结论、风险/契约审查结论和遗留项写入 `.multica-evidence/reference-alignment-progress.md`。

方向审查不等于代码风格 review；它只回答两个问题：第一，本大域实现是否真的吸收了参考项目中值得吸收的能力与实现经验；第二，实现是否仍然沿着 `unity_cli tools/context/extensions/init` 的目标形态前进。如果方向审查发现没有实际对读参考项目、取舍理由不足、或实现偏向 MCP Server/transport/session 模型，必须先修正设计或实现，不得带着偏差进入下一个大域。

## 参考实现取舍规则

每个大域必须先回答“这个功能应该学谁、怎么学”。默认规则如下：

| 取舍类型 | 使用条件 | 输出要求 |
| --- | --- | --- |
| CoplayDev 方式 | 参考项目提供了更直接的 Unity Editor 操作、Resource 语义或 package workflow。 | 记录对应 handler/resource/action，并说明如何映射到 `tools/context`。 |
| IvanMurzak 方式 | 参考项目提供了更强的 ObjectRef、SerializedMember、Reflection、runtime/editor layer 或 serializer 思路。 | 记录对应 tool/package/source，并说明如何保留 `unity_cli` CLI-first 形态。 |
| Hybrid | 两边各有优势，例如 CoplayDev 的动作覆盖 + IvanMurzak 的 object/reflection 基础设施。 | 明确底层复用点、用户入口和安全边界。 |
| `unity_cli` native | 本项目已有更合适的 CLI-first 抽象，参考项目只提供能力确认。 | 说明为什么不照搬参考项目入口，并列出符合顶层白名单的 Tool/Context 映射。 |
| Excluded | 属于明确非目标或与本项目方向冲突。 | 说明排除原因，例如 MCP transport、Prompt/Skill 生成、gameplay/in-game API、AI 玩家输入模拟。 |

取舍结论必须写入 `.multica-evidence/reference-alignment-progress.md`。如果无法从参考项目确认某项能力，不能直接按猜测实现；应记录为待确认、继续查证，或请求用户决策。

## 必须实现的能力包

### 1. Object / Component / Reflection 智能层

这是吸收 IvanMurzak 的核心能力。

| 目标能力 | `unity_cli` 形态 | 说明 |
| --- | --- | --- |
| 统一引用模型 | 内部基础设施 | `ObjectRef`、`GameObjectRef`、`ComponentRef`、`AssetObjectRef`、`SceneRef`。CLI schema 可提供 `assetRef` 友好别名，但内部命名必须清晰对应 Unity Object/Asset 关系。支持 `instanceId`、path、name、assetPath、guid、component index/type/instanceId。 |
| SerializedMember | 内部模型 + 输出 schema | 输出 `typeName`、`name`、`value`、`fields`、`props`、`path`、`readOnly`、`writable`、`error`、`truncated`。 |
| Generic Object read | `tools run object.get-data` | 读取任意 `UnityEngine.Object`，支持 deep serialization、maxDepth、paths、viewQuery。 |
| Generic Object modify | `tools run object.modify` | 支持 `objectDiff`、`pathPatches`、`jsonPatch`，修改后返回 updated data、changed paths、logs、audit。 |
| Component deep get | `tools run component.get` | 对齐 Ivan `gameobject-component-get`，支持 includeFields/includeProperties/deepSerialization/paths/viewQuery。 |
| Component deep modify | `tools run component.modify` | 对齐 Ivan `gameobject-component-modify`，支持 `componentDiff`、`pathPatches`、`jsonPatch`。 |
| Component type discovery | `tools run component.list-types` | 枚举当前项目可用 Component 类型，用于 add/get/modify 前置发现。 |
| Type schema | `tools run type.schema` | live 生成 C# / Unity 类型 JSON schema。 |
| Method find | `tools run reflection.method.find` | 查找任意 public/private/static/instance 方法，只读。 |
| Method call | `tools run reflection.method.call --yes` | 调用任意 C# 方法，不做范围限制，标高风险，返回完整 audit。 |
| ObjectRef 参数传递 | code exec / method call / patch | `code.exec`、`reflection.method.call`、`object.modify`、`component.modify` 可传 Unity Object 引用。 |

`reflection.method.call` 的用户决策：不做 allowlist，不限制 public/private/static/instance，不限制 namespace/assembly，不限制 Editor/Runtime。工具仍必须在 metadata 上标记高风险，并记录 audit。

ObjectRef 解析必须定义：解析优先级、歧义处理、失效引用错误、Scene/Prefab scope、asset main object/sub-asset、本地文件 GUID、Unity fake-null 处理。ComponentRef 解析必须支持 component instanceId、GameObject + index、GameObject + typeName 三类路径。SerializedMember 必须定义数组/list/dictionary、Unity Object reference、循环引用、maxDepth、truncation budget、readOnly/writable 和异常字段输出。Patch 模型必须明确 `jsonPatch` 使用的语义是 JSON Merge Patch、RFC6902 JSON Patch，还是本项目扩展语法；同一语法必须被 `object.modify`、`component.modify`、`asset.modify` 复用。

### 2. Context 语义校正与增强

| Context | 目标 |
| --- | --- |
| `scene.gameobject` | 保持 GameObject 基础状态：name、tag、layer、active、transform、parent/children、component type list。 |
| `scene.gameobject.components` | 保持为轻量组件列表/summary/page，不承诺完整字段属性。 |
| `scene.gameobject.component` | 保持为单组件 preview/summary，不替代 `component.get`。 |
| `scene.opened` | 如新增，列出当前打开场景。 |
| `scene.gameobject` / component contexts 与 Ivan Resource 等价 | Ivan `gameobject://currentScene/{path}` 返回较深组件数据；本项目不让 Context 承担 deep serializer，等价路径是轻量 Context + `component.get` 组合。 |
| `editor.selection` | 保持只读，写入由 `editor.selection.set` Tool 承担。 |
| `tools.groups` / `tools.custom` | 保持工具发现状态面，不实现 MCP activation session。 |

### 3. Scene / GameObject / Prefab 补齐

| 目标能力 | `unity_cli` 形态 | 当前判断 |
| --- | --- | --- |
| 多 Scene 列表 | `tools run scene.list-opened` 或 `context read scene.opened` | 缺失。 |
| Scene unload | `tools run scene.unload` | 缺失。 |
| Scene set active | `tools run scene.set-active` | 缺失。 |
| 参数化 scene data | `tools run scene.data` | 现有 `scene.hierarchy` 不够。`scene.data` 是 read-only Tool，不做 Context 主入口，避免 Context 承担复杂深读。 |
| SceneView frame | `tools run scene.view-frame` | 缺失或不完整。 |
| GameObject set parent | `tools run go.set-parent` | 缺失或隐含在 modify。 |
| Prefab instantiate | `tools run prefab.instantiate` | 缺失或不清晰。 |
| Prefab unpack | `tools run prefab.unpack` | 对齐 CoplayDev prefab workflow；高风险或写入场景。 |
| Prefab deep modify | 增强 `tools run prefab.modify` | 需要复用 object/component patch；覆盖 headless prefab contents 修改，和 `prefab.stage-*` 的 Editor workflow 分开。 |

### 4. Asset / Material / Shader / Texture 补齐

| 目标能力 | `unity_cli` 形态 | 说明 |
| --- | --- | --- |
| Built-in asset search | `tools run asset.search-built-in` | 对齐 Ivan built-in resources 查找。 |
| Asset deep data | `tools run asset.get-data` | asset-friendly wrapper，底层复用 `object.get-data`。 |
| Asset deep modify | 增强 `tools run asset.modify` | 文件级和 Object patch 边界必须明确。 |
| Asset preview | `tools run asset.preview` 或 `asset.info/search --preview` | 对齐 CoplayDev asset preview；输出 artifact metadata。 |
| Shader list/data | `tools run shader.list` / `tools run shader.get-data` | 输出 properties、subshaders、passes、compile messages。 |
| Texture generate | `tools run texture.generate` | 支持 pattern、gradient、noise。 |
| Texture modify/delete | `tools run texture.modify/delete` 或复用 asset delete | 明确入口和风险。 |
| Material property model | 增强 `material.set/assign` | 补 shared/instance/unique/property block 等语义。 |

### 5. Script / Code / Docs/Reflect 补齐

| 目标能力 | `unity_cli` 形态 | 说明 |
| --- | --- | --- |
| Script update-or-create | `tools run script.update-or-create` 或增强 `script.create --overwrite` | 对齐 Ivan。 |
| Script delete | `tools run script.delete` 或保留 `asset.delete` | 若新增需避免重复。 |
| Find in file | `tools run script.find-in-file` | 对齐 CoplayDev `find_in_file`。 |
| Code exec ObjectRef args | 增强 `tools run code.exec` | 支持 GameObject/Component/ObjectRef 参数。 |
| Script execute compile feedback | 增强 `code.exec` / script execute 语义 | 返回编译、执行、异常、返回值、artifact。 |
| Script capabilities | `tools run script.capabilities` 或 `context read script.capabilities` | 对齐 CoplayDev script capability introspection；也可由 `tools describe script.*` 等价，但必须明确。 |
| Legacy script router | 不新增主入口 | CoplayDev `manage_script` 由拆分后的 `script.*` / `asset.delete` / `script.find-in-file` 等工具等价覆盖。 |
| Docs | `tools run docs.get/search/topic/list` 或等价 Tool | 不作为参考项目功能主目标扩张；CoplayDev `unity_docs.lookup` 的 asset fallback 若保留，应明确与 `asset.search` 的关系。不得保留顶层 `docs` 命令。 |
| Offline reflect | `tools run reflect.type/member/search` 或等价 Tool | 与 live `reflection.*` 分层。不得保留顶层 `reflect` 命令。 |

### 6. Screenshot 强化

| 目标能力 | `unity_cli` 形态 | 说明 |
| --- | --- | --- |
| Camera screenshot | 保留 `tools run camera.screenshot` | 不新增重复 `screenshot.camera` 主实现。 |
| Game View screenshot | `tools run screenshot.game-view` | 可合并 `graphics.screenshot` 诊断能力。 |
| Scene View screenshot | `tools run screenshot.scene-view` | 对齐 Ivan。 |
| Isolated object screenshot | `tools run screenshot.isolated-object` | 对齐 Ivan isolated render。 |
| Multiview | 保留 `tools run camera.multiview` 为主入口 | `screenshot.multiview` 不新增主实现。 |
| Orbit/surround batch | `tools run screenshot.orbit` / `screenshot.surround` | 对齐 CoplayDev。 |
| Artifact metadata | 所有截图工具 | 返回 source、camera、path、size、format、diagnostics、overwrite、artifact manifest。 |

### 7. Profiler 强化

| 目标能力 | `unity_cli` 形态 | 说明 |
| --- | --- | --- |
| Profiler status/start/stop/pause | 保留现有 `profiler.*` | 不回退。 |
| Counter/memory/frame/capture/export | 保留现有 `profiler.*` | 不回退。 |
| Modules | `tools run profiler.modules` | 对齐 Ivan module list。 |
| Module enable | `tools run profiler.module-enable` | 对齐 Ivan module toggle。 |
| Clear | `tools run profiler.clear` | 高风险。 |
| Load | `tools run profiler.load` | 与现有 export 配套。 |
| Profiler areas | `tools run profiler.areas` | 对齐 CoplayDev profiler area toggles。 |
| Object memory | `tools run profiler.object-memory` | 对齐 CoplayDev object memory 查询。 |
| Rendering stats | `tools run profiler.rendering-stats` | `profiler.*` 表示采样/性能数据；`graphics.*` / Context 表示静态场景和管线状态。 |
| Script stats | `tools run profiler.script-stats` | 对齐 Ivan。 |
| Frame debugger | `tools run profiler.frame-debugger.*` | 对齐 CoplayDev，可按可用性实现。 |
| Memory snapshots | `tools run profiler.memory-snapshot.*` | 依赖 Memory Profiler 包，可作为可选能力。 |

### 8. Console / Editor / Testing / Package / Build

| 目标能力 | `unity_cli` 形态 | 说明 |
| --- | --- | --- |
| Console get | `tools run console.get` | 支持 filter/page/stacktrace；`editor.console` 不作为长期主入口。 |
| Console clear | `tools run console.clear` | 写入 console state。 |
| Editor selection set | `tools run editor.selection.set` | 读取仍走 Context。 |
| Editor active tool set | `tools run editor.active-tool.set` | 设置 Unity Editor 当前 Move/Rotate/Scale 等编辑器工具；不是 `unity_cli` tool activation。 |
| Editor state/control | `tools run editor.play/pause/stop`；`context read editor.state` | 主观察入口是 Context；写入/控制入口是 Tool。不得保留顶层 `editor` 命令。 |
| Editor menu | 保留 `editor.menu` | 高风险 audit。 |
| Tests | 保留 `test.run/poll/logs/cancel`，增强 `tests.list` | 不回退。 |
| Package | 保留 package tools，补 `package.embed` | 高风险明确；`package.embed` 对齐 CoplayDev embed_package。 |
| Package deploy/restore | `tools run package.dev-deploy` / `package.dev-restore` 或明确排除 | CoplayDev package development helper；若不实现，必须在非目标中写清。 |
| Build | `tools run build.*` | 对齐 CoplayDev `manage_build`：BuildTarget、PlayerSettings、Build Settings scenes、Unity 6 Build Profiles、batch build。不得保留顶层 `build` 命令。 |
| Batch | `tools run batch.run/schema` 或删除用户可见入口 | 如保留必须是 Tool，不得保留顶层 `batch` 命令。 |

### 9. Physics / Graphics / Camera / Render Pipeline

| 目标能力 | `unity_cli` 形态 | 说明 |
| --- | --- | --- |
| Physics linecast/raycast-all/shapecast | `physics.linecast`、`physics.raycast-all`、`physics.shapecast` | 补 CoplayDev 查询能力。 |
| Physics validate | `tools run physics.validate` | 对齐 CoplayDev physics validate；主语义必须明确，不保留重复入口。 |
| Physics existing tools | 保留 settings/raycast/simulation/collision/material/joint/force/overlap/rigidbody | 不回退。 |
| URP renderer features | 增强 `urp.feature` | 覆盖 list/add/remove/configure/toggle/reorder。 |
| HDRP/URP volumes/settings | 增强 `hdrp.*` / `urp.*` | 对齐 CoplayDev。 |
| Cinemachine | 增强 `cinemachine.*` | 覆盖 brain/blend/body/aim/noise/extensions。 |
| Camera orbit/surround | 与 Screenshot 域合并 | 避免重复入口。 |

### 10. Optional extension domains

这些是完整对齐目标的一部分，但不应硬塞进 core Unity package。应作为物理可选 extension，只有目标项目具备对应 Unity package 时安装/启用；core 不允许硬引用可选包 asmdef。

- ProBuilder：shape、poly shape、face/edge/vertex、material/color/uv、smoothing、combine/merge/repair。
- Cinemachine：CinemachineCamera、Brain、blend、body、aim、noise、extensions。
- VFX Graph extension ID 使用 `vfx-graph`；工具组为 `vfx.*`。
- ParticleSystem / LineRenderer / TrailRenderer 可归 VFX/visual effects extension，但不是 VFX Graph 的同义词。
- URP / HDRP。
- UI Toolkit / uGUI / TMP / InputSystem。
- Animation / AnimatorController / Timeline。
- Navigation / NavMesh。
- Terrain。
- Tilemap。
- Splines。

## 实现落点

### TypeScript / CLI metadata

需要新增或修改：

- `src/tools/metadata/groups/object.ts`
- `src/tools/metadata/groups/reflection.ts`
- `src/tools/metadata/groups/type.ts` 或合并到 reflection group
- `src/tools/metadata/groups/component.ts` 或从 `game-object.ts` 拆出 component group
- `src/tools/metadata/groups/screenshot.ts`
- `src/tools/metadata/groups/profiler.ts`
- `src/tools/metadata/groups/scene.ts`
- `src/tools/metadata/groups/asset.ts`
- `src/tools/metadata/groups/shader.ts`
- `src/tools/metadata/groups/texture.ts`
- `src/tools/metadata/groups/editor.ts`
- `src/tools/metadata/index.ts`
- `src/bridge/domains/object.ts`
- `src/bridge/domains/reflection.ts`
- `src/bridge/domains/component.ts` 或增强 `game-object.ts`
- `src/bridge/domains/screenshot.ts`
- `src/bridge/domains/profiler.ts`
- `src/bridge/registry.ts`
- `contracts/command-contract.json`

### C# Unity Bridge

需要新增或修改：

- `unitypkg/Editor/Commands/ObjectCommands.cs`
- `unitypkg/Editor/Commands/ComponentCommands.cs`
- `unitypkg/Editor/Commands/ReflectionCommands.cs`
- `unitypkg/Editor/Commands/TypeSchemaCommands.cs` 或合并到 ReflectionCommands
- `unitypkg/Editor/Commands/ScreenshotCommands.cs`
- `unitypkg/Editor/Commands/ProfilerCommands.cs`
- `unitypkg/Editor/Commands/SceneCommands.cs`
- `unitypkg/Editor/Commands/GameObjectCommands.cs`
- `unitypkg/Editor/Commands/AssetCommands.cs`
- `unitypkg/Editor/Commands/PrefabCommands.cs`
- `unitypkg/Editor/Commands/EditorCommands.cs`
- `unitypkg/Editor/Commands/PhysicsCommands.cs`
- `unitypkg/Editor/Commands/GraphicsCommands.cs`
- `unitypkg/Editor/UnityCliBridge.cs`
- `unitypkg/Editor/Bridge/BridgeCommandRegistry.cs`
- `unitypkg/Editor/Bridge/BridgeRequest.cs`

### C# infrastructure

需要新增：

- `unitypkg/Editor/Infrastructure/UnityCliObjectRef.cs`
- `unitypkg/Editor/Infrastructure/UnityCliSerializedMember.cs`
- `unitypkg/Editor/Infrastructure/UnityCliObjectSerializer.cs`
- `unitypkg/Editor/Infrastructure/UnityCliObjectPatch.cs`
- `unitypkg/Editor/Infrastructure/UnityCliTypeSchema.cs`
- `unitypkg/Editor/Infrastructure/UnityCliReflectionInvoker.cs`
- `unitypkg/Editor/Infrastructure/UnityCliAudit.cs`

### Context

需要修改：

- `src/context/builtin.ts`
- `src/context/metadata/index.ts`
- `src/context/commands.ts`
- `unitypkg/Editor/Commands/ContextCommands.cs`

目标是校正语义、增加必要 context，而不是把 context 做成深操作工具。

### Documentation

需要修改：

- `README.md`
- `docs/reference-projects-functional-alignment.md`
- `docs/unity-mcp-functional-parity.md`
- `docs/custom-contexts.md`
- `docs/tool-discovery.md`
- `docs/tool-execution.md`
- `docs/risk-and-side-effects.md`

文档示例必须只使用 `unity_cli tools ...`、`unity_cli context ...`、`unity_cli extensions ...` 或 `unity_cli init ...`，不写 MCP Server 主路径，也不写任何非白名单顶层命令。

### Tests

需要新增或扩展：

- `tests/object-contract.test.ts`
- `tests/component-contract.test.ts`
- `tests/reflection-contract.test.ts`
- `tests/screenshot-contract.test.ts`
- `tests/profiler-contract.test.ts`
- `tests/context.test.ts`
- `tests/context-metadata.test.ts`
- `tests/contracts/command-contract.test.ts`
- `tests/tools.test.ts`
- `tests/camera-bridge-source.test.ts` 或拆分 bridge source tests

## 安全与风险边界

| 能力 | 要求 |
| --- | --- |
| 删除/移除/销毁 | `destructiveHint: true`，dry-run/preview/`--yes`。 |
| object/component/asset deep modify | `destructiveHint` 按实际风险标记，返回 changed paths、updated data、logs、audit。 |
| reflection.method.call | 不做调用范围限制；必须标高风险，必须有 `--yes`、target method preview、structured audit、异常隔离和 result serialization。 |
| code.exec/script execute | 高风险，返回编译诊断、执行结果、异常、artifact。 |
| package install/remove/embed/dev-deploy/dev-restore | 高风险，提示 domain reload、manifest 变更、文件复制/恢复范围。 |
| prefab save/modify | 高风险，明确 asset path、prefab mode、dirty state。 |
| scene save/load/unload/set-active | 写入或高风险，返回 scene state。 |
| editor.menu | 高风险，记录 menu path 和结果。 |
| profiler clear/export/load | clear 高风险；export/load 记录 artifact path。 |
| screenshot | 写 artifact，不破坏 Unity 状态；校验输出路径和 overwrite。 |
| physics simulate/force | 高风险，记录 frame/time/targets。 |

## 验收标准

完整交付后必须满足：

- `.multica-evidence/reference-alignment-progress.md` 记录了所有大域的参考项目对读证据、实现方式取舍、门禁结论、自测结果、方向审查结论、遗留项和最终收口审计。
- 每个大域均通过大域完成门禁，且没有未解释的 Missing/Partial 项。
- 每个大域的新增能力都能追溯到 CoplayDev、IvanMurzak、二者结合、`unity_cli` native 取舍或明确排除结论。
- 用户可见顶层命令只剩 `tools`、`context`、`extensions`、`init`；所有其他能力均已迁移为 Tool、Context、Extension 或内部实现。
- `tools group` 能发现新增 object/component/reflection/type/screenshot/profiler 等工具。
- `tools describe <name>` 为新增工具提供完整 schema、风险标记、示例和 next actions。
- `context list/describe/read` 能读取所有保留和新增 context，且 context 全部只读。
- `object.get-data` 能读取任意 Unity Object 的 fields/props/value。
- `component.get` 能读取单个 Component 的完整 fields/props，并支持 paths/viewQuery。
- `object.modify` / `component.modify` 能执行 diff/pathPatches/jsonPatch，并返回 updated data/audit。
- `reflection.method.find` 能查找 public/private/static/instance 方法。
- `reflection.method.call` 能调用 public/private/static/instance 方法，不做范围限制，并要求 `--yes`、返回 target method preview、audit、异常和结果序列化。
- `type.schema` 能为 Unity/C# 类型生成可用 JSON schema。
- `scene.gameobject.components` 保持轻量 context，不再声明为 deep serializer。
- Screenshot 工具覆盖 Camera/Game View/Scene View/Isolated/Multiview/Orbit 或有明确合并入口。
- Profiler 工具覆盖现有能力，并补 modules、areas、object-memory、clear、load、script/rendering stats 或明确 dependency 条件。
- Build 工具覆盖 platform/settings/scenes/profiles/batch，或对 Unity 版本不可用能力给出明确诊断。
- Package 工具覆盖 embed；package dev-deploy/restore 若不实现，必须有明确 non-goal 说明。
- Optional domains 不硬引用可选 Unity package；通过物理 extension 边界隔离。
- `npm run build` 通过。
- `npm test` 通过。
- `npm run contracts:validate` 通过。
- `npm pack --dry-run` 通过，且不包含测试构建产物。
- 有 Unity Editor 环境时，至少补一轮 smoke：init/check、context read、object/component get、reflection find/call、screenshot、profiler status。

调用 `goal_complete` 前必须逐条审计本节所有验收标准，并在进度账本中写入最终收口审计。若任一验收项失败、未验证或仅部分完成，不得调用 `goal_complete`。

## 与功能盘点文档的关系

- 功能全集与当前差距详见 `docs/reference-projects-functional-alignment.md`。
- 本文档是完整实施计划和边界约束。
- 如两者冲突，以本文档的边界、去重规则和 `unity_cli` 使用形态为准。
