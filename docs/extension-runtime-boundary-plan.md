# Extension 物理边界与统一 Tool 模型重整计划

> 目标：把 ProBuilder / Cinemachine / VFX Graph / URP / HDRP 从“只在 CLI metadata 上标为 extension”重整为真正的 Unity 侧可选代码包；同时把 Editor/Runtime 差异收敛为 Tool descriptor 的执行约束，避免用户心智分裂。

## 参考项目

参考项目：`https://github.com/CoplayDev/unity-mcp`

参考范围：

- 参考 Unity 操作能力、Resources 只读语义、optional tool groups、C# API 使用经验。
- `unity-mcp` 的 Tools 对齐为 `unity_cli tools`。
- `unity-mcp` 的 Resources 对齐为 `unity_cli context`。
- `unity-mcp` 的 optional groups 对齐为 `unity_cli extensions`。

不照搬范围：

- 不改成 MCP server。
- 不照搬 FastMCP transport、client auto-config、remote auth、Unity 配置 UI。
- 不新增 extension 专用执行命令；ProBuilder 等 extension 能力只通过 `tools group/list/describe/run` 暴露。

## 背景判断

当前实现已经把很多能力接入了 `tools` / `context` / `extensions`，但 extension 的形态仍不完全符合产品目标：

- 可选包相关 C# 文件仍可能随 core `unitypkg` 进入 Unity 项目。
- 这会让未安装对应 Unity package 的项目承担编译引用风险。
- `extensions list/install/remove/doctor` 更像状态与可用性管理，还不是 Unity 侧可选代码包安装器。
- 文档中 “Editor Tool / Runtime Tool” 容易让用户以为有两套工具体系；实际应只有统一 Tool，`target` 只是运行环境约束。
- `unity_cli probuilder ...` 这类 extension 专用命令形态会破坏统一 Tool 入口，也会让可选能力看起来像 core CLI 固定命令。

## 设计原则

### 1. CLI-first 不变

不引入 MCP server，用户执行能力的主入口统一为：

```text
tools group → tools group list <group> → tools describe <tool> → tools run <tool>
context list → context describe <name> → context read <name>
```

Extension 能力不再拥有 `unity_cli probuilder ...` 这类专用执行命令；生命周期管理仍走 `extensions list/install/remove/doctor`。

### 2. Core 必须零可选依赖

`unitypkg` core 只能包含所有 Unity 项目都能编译的代码。

禁止 core 直接引用：

- `UnityEngine.ProBuilder`
- `Cinemachine`
- `UnityEngine.VFX`
- `Unity.RenderPipelines.Universal`
- `Unity.RenderPipelines.HighDefinition`
- 其他 optional Unity package namespace

### 3. Extension 是物理可选代码包

Extension 不只是 CLI metadata 分组，而是可复制进目标 Unity 项目的独立代码包。

只有满足以下条件之一，extension C# 文件才进入项目：

- `init` 检测到目标项目已安装对应 Unity package。
- 用户显式运行 `extensions install <id>`，且目标项目已安装对应 Unity package。

如果依赖缺失，默认跳过，不自动安装 Unity package。

### 4. Tool 是统一用户概念

用户只需要理解一种对象：Tool。

`target: "editor" | "runtime" | "both"` 是 descriptor 的可用性与路由约束，不是用户需要预先选择的工具类型。

这里的 `runtime` 指 Unity 的 gameplay/runtime layer，不等同于“必须 Play Mode”。在本地 Editor 场景下，某些 runtime layer tool 可能需要 Play Mode 才能访问活跃游戏状态；但这是具体 tool 的 availability 条件，不是 runtime 的定义。

内部可以保留：

- `project-tool.*`
- `runtime-tool.*`
- `bridge-runtime`
- `availabilityScope`

但它们应作为实现细节，不作为主要用户文档入口。

### 5. Extension 能力只通过 tools 暴露

ProBuilder / Cinemachine / VFX Graph / URP / HDRP 等 extension 的操作能力，只通过统一 Tool 发现与执行链路暴露：

```text
unity_cli tools group list probuilder
unity_cli tools describe probuilder.create-shape
unity_cli tools run probuilder.create-shape --input <json>
```

需要删除或废弃：

```text
unity_cli probuilder ...
unity_cli cinemachine ...
unity_cli vfx ...
unity_cli urp ...
unity_cli hdrp ...
```

其中 `unity_cli probuilder ...` 是当前必须迁出的命令形态；其他 extension 域如果存在或后续新增，也不得以专用 CLI command family 作为用户执行入口。

保留：

```text
unity_cli extensions list
unity_cli extensions install <id>
unity_cli extensions remove <id>
unity_cli extensions doctor
```

这些命令只负责 extension 生命周期，不负责执行 extension tool。

## 目标目录结构

### 当前 core

```text
unitypkg/
  Editor/
    Bridge/
    Commands/
    Infrastructure/
    ProjectTools/
    ProjectContexts/
    Samples/
```

### 目标 extension bundles

```text
extensions/
  probuilder/
    extension.json
    unitypkg/
      Editor/
        Commands/ProBuilderCommands.cs
        ProBuilder/*.cs
  cinemachine/
    extension.json
    unitypkg/
      Editor/Commands/CinemachineCommands.cs
  vfx-graph/
    extension.json
    unitypkg/
      Editor/Commands/VfxCommands.cs
  urp/
    extension.json
    unitypkg/
      Editor/Commands/UrpCommands.cs
  hdrp/
    extension.json
    unitypkg/
      Editor/Commands/HdrpCommands.cs
```

`extension.json` 至少包含：

```json
{
  "id": "probuilder",
  "title": "ProBuilder",
  "requiresUnityPackages": ["com.unity.probuilder"],
  "providesTools": ["probuilder.create-shape"],
  "copyRoot": "unitypkg",
  "installMode": "copy-if-package-present"
}
```

## Init 行为

### `unity_cli init --project <path>`

执行顺序：

1. 安装 core `unitypkg` 到 `Packages/com.unity_cli`。
2. 读取 `Packages/manifest.json` 和 `Packages/packages-lock.json`。
3. 对每个内置 extension 判断依赖 Unity package 是否存在。
4. 依赖存在：复制对应 extension bundle 到 `Packages/com.unity_cli/Extensions/<id>` 或约定目录。
5. 依赖缺失：跳过，不复制 C# 文件。
6. 写入安装 manifest，记录 core 与 extension 文件清单、hash、依赖判断结果。

### `init --check`

必须报告：

- core 是否已安装、hash 是否一致。
- 每个 extension 的状态：
  - `installed`
  - `available_not_installed`
  - `missing_dependency`
  - `outdated`
  - `unknown_files`

### `init --pkg-only`

只安装 core 与符合条件的 extension，不安装 agent skill。

### `init --skill-only`

只安装 skill，不修改 Unity package 与 extension 文件。

## Extensions 命令语义

`extensions` 命令只管理可选代码包生命周期，不执行 ProBuilder / Cinemachine / VFX / URP / HDRP 具体能力。具体能力必须通过 `tools run <tool>` 执行。

### `extensions list`

展示 extension 定义与目标项目可用性：

- `dependencyInstalled`
- `installed`
- `providedTools`
- `installCommand`
- `next_actions`

### `extensions install <id>`

语义：安装 CLI 已实现的 extension C# bundle。

规则：

- 如果目标项目缺 Unity package，返回 `E_EXTENSION_DEPENDENCY_MISSING`。
- 不默认执行 `package install com.unity.probuilder`。
- 成功时复制 extension 文件，并更新 extension install manifest。

### `extensions remove <id>`

语义：只删除该 extension bundle 复制过的文件。

规则：

- 不删除 Unity package 依赖。
- 不删除 core。
- 必须依赖 file manifest，避免误删用户文件。

### `extensions doctor`

检查：

- 已安装 extension 文件是否完整。
- 目标 Unity package 是否仍存在。
- extension 文件 hash 是否匹配。
- core 是否引用了 optional namespace。

## C# 注册模型

Core Bridge 只能注册 core commands。

Extension commands 的注册代码随 extension bundle 一起复制。安装后 Unity 重新编译，Bridge command registry 才能发现并注册对应命令。

推荐约束：

- core 不静态引用 extension command class。
- extension command class 可以通过 `[InitializeOnLoad]` 或明确 registry hook 注册。
- 如果保留统一 `BridgeCommandRegistry.RegisterXxxCommands`，该调用必须位于 extension 文件内，而不是 core 文件内。

## Tool descriptor 模型

Tool descriptor 保持统一。Extension tool 也必须是普通 Tool descriptor，而不是专用 CLI command：

```json
{
  "name": "probuilder.create-shape",
  "group": "probuilder",
  "target": "editor",
  "handler": "bridge",
  "requiresPackages": ["com.unity.probuilder"],
  "available": false,
  "availabilityReason": "extension_not_installed"
}
```

用户文档统一描述为：

- 先发现 tool。
- 再 `tools describe` 查看 `target`、`available`、`requiresPackages`、`next_actions`。
- 再 `tools run <tool>` 执行。
- 不再引导用户调用 `unity_cli probuilder ...` 这类 extension 专用命令。
- `runtime` 表示作用于 Unity gameplay/runtime layer；是否需要 Play Mode 由具体 tool 的 `available`、`availabilityScope` 和 `next_actions` 说明。

## 需要修正的既有问题

这次重整应顺手修复以下审查发现：

1. `script.apply-edits` 必须纳入 destructive security policy 与 Unity Bridge proof 校验。
2. `context describe` 使用的 descriptor 必须包含 `resourceUri` / `equivalentUnityMcp`，避免与 `src/context/metadata` 分裂。
3. `tools run script.apply-edits --input-file` 必须能传递结构化 `edits` 数组，而不是只接受字符串 flag。
4. Skill 与文档把 “Editor Tool / Runtime Tool” 改为 “统一 Tool + target/availability”，并明确 `runtime` 是 gameplay/runtime layer，不是 Play Mode 的同义词。
5. 删除或废弃 `unity_cli probuilder ...` 专用命令形态；extension 能力只通过 `tools group/list/describe/run` 暴露。

## 分阶段计划

### 阶段 0：冻结与审计

- 记录当前 build/test/contracts/pack 基线。
- 列出所有 optional namespace 引用。
- 列出当前 `unitypkg` 中应迁出的 extension 文件。

验收：有审计清单，确认 core 需要迁出的文件集合。

### 阶段 1：定义 extension manifest 与安装状态模型

- 新增 extension manifest schema。
- 定义 core install manifest 与 extension install manifest。
- 明确 hash、文件清单、依赖状态字段。

验收：`extensions list` 能从 manifest/registry 输出一致状态。

### 阶段 2：迁移 Unity 侧 extension 文件

- 将 ProBuilder / Cinemachine / VFX / URP / HDRP C# 文件从 core 移到 `extensions/<id>/unitypkg`。
- 移除 core 对 extension command class 的静态引用。
- 保证 core `unitypkg` 在没有可选包时可编译。

验收：静态测试证明 core 不含 optional namespace。

### 阶段 3：重写 init extension 安装流程

- `init` 安装 core 后自动检测已安装 Unity package。
- 依赖存在时复制对应 extension。
- 依赖缺失时只报告 unavailable，不复制。
- `init --check` 报告 extension 状态。

验收：临时项目无 ProBuilder 依赖时，安装后没有 ProBuilder C#；添加依赖后再次 init 才安装 ProBuilder extension。

### 阶段 4：重写 extensions install/remove/doctor

- `install` 检查依赖后复制 extension bundle。
- `remove` 按 manifest 删除 extension 文件。
- `doctor` 检查 hash、缺依赖、孤儿文件。

验收：install/remove 不影响 core，不误删用户文件。

### 阶段 5：删除 extension 专用执行命令

- 删除或废弃 `unity_cli probuilder ...` 命令族。
- 确认 ProBuilder 能力只通过 `tools group list probuilder`、`tools describe probuilder.*`、`tools run probuilder.*` 暴露。
- 如果存在 Cinemachine / VFX / URP / HDRP 专用执行命令，同步删除或标记为迁移到 tools。
- CLI help 不再把 extension 能力列为顶层专用 command family。

验收：用户执行 extension tool 的唯一入口是 `tools run <tool>`；`extensions` 只负责生命周期。

### 阶段 6：统一 Tool 文档与 Skill 心智模型

- 更新 `docs/custom-tools.md`。
- 更新 `docs/tool-discovery.md` / `docs/tool-execution.md`。
- 更新 `docs/extensions.md`。
- 更新 `skills/unity_cli/SKILL.md`。

验收：文档不再把 Editor Tool / Runtime Tool 写成用户要选择的两套体系；`runtime` 被解释为 gameplay/runtime layer，Play Mode 只作为部分 tool 的可用性条件。

### 阶段 7：修复审查遗留问题

- 补 `script.apply-edits` destructive proof。
- 合并或同步 Context descriptor metadata。
- 修复结构化 `edits` 输入传递。

验收：新增或更新对应测试。

### 阶段 8：最终验证

必跑：

```powershell
npm run build
npm test
npm run contracts:validate
npm pack --dry-run
```

补充验证：

```powershell
node dist/cli.js extensions list --project <temp-project>
node dist/cli.js init --project <temp-project-without-optional-packages>
node dist/cli.js init --project <temp-project-with-probuilder>
node dist/cli.js init --check --project <temp-project>
```

## 成功标准

- 未安装可选 Unity package 的项目不会收到对应 extension C# 文件。
- 已安装可选 Unity package 的项目，`init` 会自动安装对应已实现 extension。
- 显式 `extensions install <id>` 不自动安装 Unity package，只安装 CLI extension bundle。
- Core `unitypkg` 不含 optional package namespace 引用。
- 用户仍通过统一 `tools` 入口发现和执行能力。
- `unity_cli probuilder ...` 等 extension 专用执行命令被删除或废弃；extension lifecycle 命令只负责安装、移除和诊断。
- `target` / `availabilityScope` 只作为 descriptor 约束和 `next_actions`，不成为用户入口分裂；`runtime` 表示 gameplay/runtime layer，不被写成 Play Mode 的同义词。
- `npm pack --dry-run` 不包含测试产物。
- 所有 destructive 写操作都有确认与 security proof。
