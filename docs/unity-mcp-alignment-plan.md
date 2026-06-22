# unity-mcp 对齐执行计划

> 目标：在保持 `unity_cli` CLI-first 形态不变的前提下，批判性参考 `https://github.com/CoplayDev/unity-mcp`，补齐 Tools 功能点、Resources → Context 语义、Extensions 分类与实现三条等价线，并整理测试、双端文件结构、Skill 和文档。

## 执行上下文

### 项目定位

`unity_cli` 是给智能体操作 Unity 的 CLI 工具，不是 MCP server。

智能体使用方式：

- 写操作走 Tools：`tools group` → `tools group list <group>` → `tools describe <tool>` → `tools run <tool>`
- 读操作走 Context：`context list` → `context describe <name>` → `context read <name>`
- 扩展 Tools：Unity 项目通过 `[UnityCliTool]` 注册
- 扩展 Context：Unity 项目通过 `[UnityCliContext]` 注册
- Skill 只说明入口、覆盖面和发现流程；具体工具/Context 参数由 CLI 自描述命令现场获取

### 参考项目口径

参考项目：`CoplayDev/unity-mcp`

批判性参考：

- 参考 Unity 操作能力、Resource 只读语义、optional tool groups、C# API 使用经验
- 不照搬 FastMCP server、MCP transport、client auto-config、remote auth、Unity 配置 UI
- `unity-mcp` 的 `Tools` 对齐为 `unity_cli tools`
- `unity-mcp` 的 `Resources` 对齐为 `unity_cli context`
- `unity-mcp` 的 optional groups 对齐为 `unity_cli extensions`

### 关键不变量

- CLI 命令形态目前认可，优先补齐能力，不重做用户入口。
- `init` 行为不能破坏：Bridge 包 copy 安装、skill 安装、`init --check`、`--pkg-only`、`--skill-only`、`--agent` 语义保持。
- 改结构后必须同步 npm 构建、Unity package copy、测试路径和发布内容。
- 高风险写操作必须保留或补齐 `destructiveHint`、`--yes`、dry-run、fingerprint 等安全边界。

### 当前参考文档

- `docs/unity-mcp-functional-parity.md`：当前等价追踪表
- `docs/critical-references/README.md`：批判性参考记录
- `docs/coverage-matrix.md`：v1.0 archive，不作为当前唯一事实源
- `skills/unity_cli/SKILL.md`：agent-facing 入口说明

## 阶段 0：基线冻结

### 目标

建立改动前基线，避免补能力和重构后无法判断破坏来源。

### 工作项

- 运行 `npm run build`
- 运行 `npm test`，记录当前失败和 flaky，不先修无关问题
- 运行 `npm run contracts:validate`
- 运行 `npm pack --dry-run`，记录发布内容基线
- 记录 `init` 相关测试和行为

### 关键文件

- `package.json`
- `src/cli-commands/package.ts`
- `src/cli-commands/skill-installer.ts`
- `src/cli.test.ts`

### 验收

- 有基线记录：哪些命令通过、哪些失败、失败是否已知
- 后续每阶段能和基线对比

## 阶段 1：批判性学习参考项目

### 目标

把 `unity-mcp` 能力拆成三张表，不夹带 MCP 形态。

### 工作项

- 抽取 Tools 功能点：`manage_*` action、Unity Editor tools、server helper 中真实 Unity 操作
- 抽取 Resources 语义：只读状态、项目状态、实例、工具组、自定义工具
- 抽取 Extensions 分类：ProBuilder、Cinemachine、VFX、URP、HDRP 的 optional group、包依赖、可用性
- 更新 `docs/unity-mcp-functional-parity.md`

### 验收

- 所有缺口都有 `covered` / `covered-adapted` / `partial` / `not-covered` / `excluded`
- 每个 `not-covered` / `partial` 有优先级和下一步

## 阶段 2：补齐 Context 缺口

### 目标

让 `unity-mcp` Resources 的只读语义由 `unity_cli context` 等价覆盖。

### 优先补齐

- `scene.gameobject.components`
- `scene.gameobject.component`
- `prefab.info`
- `prefab.hierarchy`
- `instances.list`
- `tools.groups`
- `tools.custom`
- 可选：`scene.gameobject-api`、`prefab.api` 做成 docs topic 或 context

### 工作项

- 在 TS 侧补 context descriptor、schema、metadata
- 在 C# 侧补只读 handler，确保不修改 Unity 状态
- 补 `context list` / `context describe` / `context read` 测试
- 补 `equivalentUnityMcp` 映射测试

### 关键文件

- `src/context/builtin.ts`
- `src/context/metadata/index.ts`
- `src/context/commands.ts`
- `unitypkg/Editor/Commands/ContextCommands.cs`
- `src/context.test.ts`
- `src/context-metadata.test.ts`
- `src/context-bridge-source.test.ts`

### 验收

- `context list` 能看到新增 context
- `context describe <name>` 有 input/output schema
- `context read <name>` 返回结构化 JSON
- 新增 context 全部 `readOnlyHint=true`
- 新增 context 都有 `equivalentUnityMcp` 或明确说明

## 阶段 3：补齐 Tools 功能点缺口

### 目标

对齐智能体写 Unity 的操作能力。

### P0

- `script_apply_edits` 等价能力：text edits、anchor edits、class/method insert/replace/delete
- `script hash` / `preconditionSha256`：防止覆盖并发修改

### P1

- 通用 `ScriptableObject`：`scriptable-object.create` / `scriptable-object.info` / `scriptable-object.modify`
- Editor：`editor.undo`、`editor.redo`、tag/layer 管理、resolution 设置
- GameObject：`go.duplicate`、`go.move-relative`、`go.look-at`
- Package：`package status`，评估 `embed_package`
- Physics：collision matrix、physics material、joints、forces、shape/overlap queries、rigidbody validate/configure

### P2 / P3

- Texture pattern / gradient / noise / pixels
- Scene loaded / set-active / close / move / validate / templates / view-frame
- Build profile / settings / scenes 管理
- Audio convenience：AudioSource play / stop / volume
- UI / VFX 细项逐项核对后补齐

### 工作模式

每个功能点按同一链路实施：

1. tool metadata
2. bridge spec
3. C# handler 或 CLI handler
4. contract
5. tests
6. parity 文档更新

### 关键文件

- `src/bridge/domains/*.ts`
- `src/tools/metadata/groups/*.ts`
- `unitypkg/Editor/Commands/*.cs`
- `contracts/command-contract.json`
- `src/bridge-commands.test.ts`
- 对应 domain test

### 验收

- `tools group list <group>` 可发现
- `tools describe <tool>` 有 schema、风险、可用性
- `tools run <tool>` 能路由到正确 handler
- `npm run contracts:validate` 通过
- 新增功能有测试或 source-level contract test

## 阶段 4：修复 Extensions 分类与实现缺口

### 目标

让 optional capability pack 的分类和运行时实现一致。

### 运行时准源

- `src/extensions/contribution-loader.ts`
- `src/tools/metadata/groups/{probuilder,cinemachine,vfx,urp,hdrp}.ts`
- `src/bridge/domains/{probuilder,cinemachine,vfx,urp,hdrp}.ts`
- `unitypkg/Editor/Commands/*`
- `extensions/probuilder/unitypkg/Editor/ProBuilder/*`

### 工作项

- 更新 `docs/extensions.md`，不能再写 Cinemachine/VFX/URP/HDRP 是 backlog
- 同步 `src/extensions/metadata/index.ts` 的 `providesTools`
- 同步 `src/extensions/registry-data.ts` 的 contributions
- 处理 `extensions/*` 外置副本：同步，或明确标为非运行时准源
- 澄清 VFX 分类：VFX Graph 依赖 `com.unity.visualeffectgraph`，ParticleSystem / LineRenderer / TrailRenderer 是否继续放 `vfx` group 需要明确策略

### 关键文件

- `src/extensions/contribution-loader.ts`
- `src/extensions/metadata/index.ts`
- `src/extensions/registry-data.ts`
- `docs/extensions.md`
- `extensions/*`

### 验收

- `extensions list` 输出与实际工具组一致
- `tools group list vfx/cinemachine/urp/hdrp/probuilder` 与 extension metadata 一致
- 可选包缺失时返回 unavailable + next_actions
- 不再有“文档 backlog、代码已实现”的矛盾

## 阶段 5：测试整理

### 目标

测试文件不再混在 `src/**/*.test.ts` 中，避免源码结构和发布内容混乱。

### 建议结构

```text
tests/
  unit/
  integration/
  contracts/
  fixtures/
  contract-snapshots/
```

### 工作项

- 移动 `src/*.test.ts` 到 `tests/...`
- 调整 import 路径
- 新增或调整 `tsconfig.test.json`
- 让 build 输出不包含测试
- 让 test 编译并运行测试产物
- 确保 `npm pack --dry-run` 不包含 `*.test.js`

### 关键文件

- `tsconfig.json`
- `tsconfig.test.json`
- `package.json`
- `.gitignore`

### 验收

- `npm run build` 只构建发布代码
- `npm test` 能编译并运行 tests
- `npm pack --dry-run` 不包含 `*.test.js`
- 测试迁移不改变运行时行为

## 阶段 6：双端文件结构整理

### 目标

整理 TS CLI 端和 Unity C# 端结构，让 core、extension、context、command 边界更清晰。

### TS 端建议

```text
src/
  cli/
  commands/
  bridge/
  tools/
  context/
  extensions/
  runtime/
```

建议归类：

- `src/cli/`：CLI 入口、help、命令分发
- `src/commands/`：CLI-only 命令实现，如 package/test/build/job/init
- `src/bridge/`：bridge specs、execute、registry
- `src/tools/`：tool metadata、describe/run/discovery
- `src/context/`：context descriptor、commands、metadata
- `src/extensions/`：extension manifest、availability、contribution loader
- `src/runtime/`：job runner/store、platform、output、安全

### Unity C# 端建议

```text
unitypkg/Editor/
  Bridge/
  Commands/Core/
  Commands/Extensions/
  ProBuilder/
  ProjectTools/
  ProjectContexts/
  Infrastructure/
```

### 注意事项

- C# namespace 不要轻易改，避免 Unity 编译和 source-level tests 大面积破裂
- 文件移动后 source-level tests 要同步
- Bridge package copy 清单要同步

### 验收

- `npm run build` 通过
- `npm run contracts:validate` 通过
- C# source structure tests 通过
- `init` copy 后 Unity package 文件完整

## 阶段 7：保持 `init` 行为不变

### 目标

结构重整后，安装行为不变。

### 必须保持

- `unity_cli init --project <path>` copy `unitypkg` 到 `Packages/com.unity_cli`
- `init --check` 能检查安装状态
- `--pkg-only` 只装 Unity package
- `--skill-only --agent <...>` 只装 skill
- `--agent pi|claude|.agent` 安装对应 Skill
- 安装 metadata、file manifest、hash 行为保持
- open Editor live refresh 行为保持

### 关键文件

- `src/cli-commands/package.ts`
- `src/package-manager.ts`
- `src/cli-commands/skill-installer.ts`
- `unitypkg/package.json`

### 验收

- init 相关测试全部通过
- 临时 Unity 项目中存在：
  - `Packages/com.unity_cli/package.json`
  - `Packages/unity_cli.install.json`
  - `Packages/com.unity_cli/unity_cli.files.json`
- manifest 不应错误写入 `com.unity_cli` file dependency，除非设计明确改变

## 阶段 8：更新 Skill 和用户文档

### 目标

把最终形态呈现给 AI 和维护者。

### Skill 更新方向

`skills/unity_cli/SKILL.md` 只写入口和覆盖面，不列完整 tools 参数：

- 写：先 `tools describe` 再 `tools run`
- 读：先 `context describe` 再 `context read`
- destructive 需要 `--yes` 或 dry-run
- optional extension 先看 availability
- 自定义扩展用 `[UnityCliTool]` / `[UnityCliContext]`

### 文档更新范围

- `docs/tool-discovery.md`
- `docs/tool-execution.md`
- `docs/custom-tools.md`
- `docs/custom-contexts.md`
- `docs/extensions.md`
- `docs/unity-mcp-functional-parity.md`
- `docs/README.md`

### 验收

- Skill 简洁，但 AI 能知道怎么自发现
- 文档不再宣称过时状态
- parity 文档只作为维护者验收，不作为 AI 操作手册

## 阶段 9：最终验证与收口

### 必跑

```powershell
npm run build
npm test
npm run contracts:validate
npm pack --dry-run
```

### 建议补充

```powershell
node dist/cli.js --help
node dist/cli.js tools group
node dist/cli.js context list
```

并在临时项目验证：

```powershell
unity_cli init --project <temp-project>
unity_cli init --check --project <temp-project>
```

### 收口标准

- 无未解释的 failing tests
- `docs/unity-mcp-functional-parity.md` 中 P0/P1 已完成或有明确延期理由
- Skill 与 docs 不再和实际 CLI 行为冲突
- 发布包不含测试产物
- `.gitignore`、dist、test output 不污染工作区

## 建议执行顺序

1. 基线冻结
2. Context 缺口
3. Tools P0/P1 缺口
4. Extensions 漂移
5. 测试目录迁移
6. 双端结构整理
7. `init` 回归验证
8. Skill/docs 更新
9. 全量验证

原则：先补能力，再整理测试，再重构结构。每一步都要有独立验收点，避免一次性混改导致问题难以定位。
