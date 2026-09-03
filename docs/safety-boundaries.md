# 安全边界（Safety Boundaries）

alps-unity-cli的安全模型围绕三个核心原则：Tool副作用必须准确声明、读取型Tool必须纯只读、Extension和command入口不能绕过descriptor/envelope。

## 风险标记体系

每个工具 descriptor 包含以下安全相关字段：

| 字段 | 类型 | 说明 |
|------|------|------|
| `destructiveHint` | boolean | true = 可能有不可逆效果的描述信息，不构成执行门 |
| `supportsDryRun` | boolean | true = 支持可选的 `--dry-run` 预览 |
| `permissions` | string[] | 执行所需权限 |
| `target` | string | Tool 可执行的 Editor 目标 |
| `availabilityScope` | string | Tool 的可用性范围 |
| `playModeSupport` | string | Play Mode 行为：`supported` / `runtime-only` / `session-affecting` / `editor-only` / `unknown` |

## Tool 安全规则

### 破坏性工具

公开 Tool 与 CLI 控制面调用本身即授权。`destructiveHint`、`sideEffects` 与 `supportsDryRun` 准确描述风险、效果和可选预览，但不得触发额外确认、预定义按钮列表或意图复核，也不得阻止执行。

当前标记为破坏性的工具包括：

| 工具 | 说明 |
|------|------|
| `tools run scene.save` | 保存场景 |
| `tools run asset.delete` | 删除资产 |
| `tools run scriptable-object.create` | 创建 ScriptableObject 资产 |
| `tools run scriptable-object.modify` | 修改 ScriptableObject 资产 |
| `tools run texture.modify` | 修改 Texture 资产 |
| `tools run texture.delete` | 删除 Texture 资产 |
| `tools run shader.delete` | 删除 Shader 资产 |
| `tools run object.modify` | 修改场景对象或持久资产数据 |
| `tools run component.modify` | 修改场景组件或持久 Prefab/资产组件 |
| `tools run texture.import-settings` | 持久化 TextureImporter 设置 |
| `tools run texture.sprite` | 持久化 Sprite importer 与切片设置 |
| `tools run graphics.quality` | 持久化 QualitySettings |
| `tools run graphics.pipeline-settings` | 持久化默认 RenderPipelineAsset 并触发资产导入 |
| `tools run graphics.bake-settings` | 持久化 LightingSettings 与场景绑定 |
| `tools run build.platform.set` | 持久切换 active BuildTarget |
| `tools run build.profile.switch` | 持久切换 active Build Profile |
| `tools run editor.menu` | 执行编辑器菜单项 |
| `tools run reflection.method.call` | 调用精确同步 C# overload，并要求独立 completion proof |
| `tools run package.install` | 安装包 |
| `tools run package.remove` | 移除包 |
| `tools run package.deploy` | 复制本地 package 源到项目 Packages 目录 |
| `tools run package.restore` | 从 package.deploy 备份恢复或移除部署目录 |
| `tools run package.bridge-remove` | 移除 init 复制的 Bridge 包 |
| `tools run code.exec` | 在已打开的 Unity Editor 中执行代码 |

`code.exec`保留完整的同步C#执行能力，不按代码内容拒绝死循环、长时间sleep、阻塞等待、锁等待、同步I/O或长期占用Unity API。调用方在提交这类代码前必须先判断它对当前结果是否确有必要，并考虑有界写法或现有Tool能否达到同一结果；确有必要时仍可原样执行。代码一旦在Unity Editor主线程开始，`--timeout-ms`和取消只能停止CLI等待，不能强制中止正在运行的C#。Unity可能无响应，mutation owner可能无法收尾，业务状态可能部分改变或无法证明，恢复可能需要结束Editor进程。此时timeout不表示代码已经停止，不能据此直接重试。`--dry-run`只返回source hash和planned effect，不编译、不执行，也不验证代码能够结束。

### 可选预览

```text
tools describe <tool>        -> 了解 destructiveHint、sideEffects 和 supportsDryRun
       ↓
--dry-run（如支持且需要预览） -> 查看 plannedEffects
       ↓
tools run <tool>             -> 公开调用即授权并执行
```

未使用 dry-run 不会产生确认错误。

### Play Mode 支持

- `supported`：Play Mode 下可正常执行，语义基本不变。
- `runtime-only`：Play Mode 下可执行，但只影响当前运行会话，退出 Play Mode 后不持久。
- `session-affecting`：允许执行，但可能触发 asset import、compile、domain reload 或 Play Mode 状态变化。
- `editor-only`：没有合理运行态语义，Play Mode 下返回 `E_TOOL_PLAYMODE_UNAVAILABLE`。
- `unknown`：当前安装无法证明该状态，调用方必须先重新发现或检查可用性。

## 读取型Tool硬边界

读取型Tool不得：
- 修改Unity状态（场景、资产、GameObject、Component）
- 修改文件系统
- 修改Package依赖
- 触发import、compile、domain reload或PlayMode变化
- 产生任何副作用

`tools run`与`command`调用同一Tool时共享该风险分类、安全检查和completion contract。


## Extension 安全边界

项目自定义工具不能绕过 descriptor/envelope 安全模型：

| 规则 | 说明 |
|------|------|
| 必须有 descriptor | 通过 `IUnityCliTool<TReq,TRes>.Describe()` 提供人工字段，schema 由 Request/Result 派生，不可省略 |
| 必须通过 envelope | 输入输出必须遵循统一 JSON envelope 格式 |
| 内部完整性校验 | handler 代码变更时默认阻止执行 |
| 不可绕过 registry | 不允许直接调用未注册的方法 |
| 不可提供反射 escape hatch | 不允许通过自定义工具绕过安全检查 |

## 项目 Tool 完整性

项目自定义 Tool 在 `tools run` 前经过内部完整性校验。正常 descriptor 和成功 envelope 不公开校验材料；不匹配时返回结构化错误及恢复步骤。`unitycli doctor` 可用于诊断项目 Tool 状态。

## 正确性边界

schema、目标 instance、canonical project identity、PID/processStartedAt、session、deadline、stale identity、terminal proof、mutation lease 和审计用于确保调用落到正确对象并返回真实结果。它们是正确性约束，不是授权门。
