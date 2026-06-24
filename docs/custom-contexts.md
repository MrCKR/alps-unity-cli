# 自定义 Context（Custom Contexts）

项目侧只读 Context 注册规范。Context 是只读、结构化、target-scoped 的项目状态快照。

## 概述

Context 是 alps-unity-cli 中获取只读项目状态的核心机制。与 Tool（可有副作用）不同，Context 必须保证只读：

| 特性 | Context | Tool |
|------|---------|------|
| 副作用 | ❌ 绝对禁止 | ✅ 可有 |
| 只读保证 | ✅ 硬边界 | Context API 只允许观察语义 |
| 修改 Unity 状态 | ❌ 禁止 | ✅ 可有 |
| 执行前确认 | 不需要 | 破坏性需 `--yes` |

## 当前状态：已实现

`context list` / `context describe` / `context read` 命令已实现。可通过 CLI 直接获取只读项目状态。

当前可通过以下方式获取只读项目状态：

| 方式 | 命令 | 说明 |
|------|------|------|
| 内置 Context | `context list` / `context read <name>` | 23 个只读 Context，覆盖 editor、project、scene、prefab、tools 等状态 |
| 项目 Context | `[UnityCliContext]` + `context list --include-custom` | Unity 项目侧自定义只读状态快照 |
| 观察型能力 | `context list` / `context describe` / `context read` | project.info、editor.state、scene.cameras 等 |
| 离线文档 | `docs get/search` | Unity API 文档缓存 |
| 类型反射 | `reflect type/member/search` | C# 类型信息缓存 |
| 诊断预检 | `tools run diagnose.preflight` | 项目、编辑器、Bridge 状态快照 |

## 注册方式

Custom Context 通过 `[UnityCliContext]` 特性注册：

```csharp
using UnityCli;

// Custom Context 注册 API
[UnityCliContext("sample.project-summary",
    Title = "Project Summary",
    Description = "Read-only project overview.",
    Source = "project",
    Category = "project",
public static class SampleProjectSummaryContext
{
    // 只读读取，不得修改任何状态
    public static object Read(Dictionary<string, object> input)
    {
        return new
        {
            projectName = "...",
            unityVersion = "...",
            sceneCount = 0,
            packageCount = 0
        };
    }
}
```

## 只读硬边界

Custom Context 实现必须严格遵守：

### 不得修改

| 禁止修改项 | 说明 |
|------------|------|
| Unity 状态 | 场景、GameObject、Component |
| 文件系统 | 资产、脚本、配置文件 |
| Package 依赖 | manifest.json |
| Play Mode 状态 | 不得触发 play/pause/stop |
| Editor 设置 | 任何编辑器配置 |

### 必须保证

- 输出为结构化 JSON
- 可重复调用产生一致结果（幂等）
- 不产生副作用
- 不触发资源导入或编译

## 发现路径

Context 发现路径：

```
context list                    → 列出所有可用 Context
       ↓
context describe <name>         → 查看 Context schema 和来源
       ↓
context read <name>             → 读取 Context 数据
```

替代方案（也可通过只读工具获取）：

```
tools group list <read-only-group>   → 查看只读工具
       ↓
context describe <context>                → 确认 Context 描述
       ↓
tools run <tool>                        → 执行只读读取
```

## Built-in Context

当前内置 23 个只读 Context，与 unity-mcp 的 Resources 模型等价映射，并通过 `context describe <name>` 暴露 input/output schema 与 `equivalentUnityMcp` 信息。

| Context 概念 | Context 名称示例 | unity-mcp Resource |
|--------------|------------------|---------------------|
| 编辑器状态 | `editor.state` | `editor/state` |
| 项目信息 | `project.info` | `project/info` |
| 相机列表 | `scene.cameras` | `scene/cameras` |
| 已打开场景 | `scene.opened` | `scene/opened` |
| 场景对象与组件（轻量） | `scene.gameobject.components` / `scene.gameobject.component` | `scene/gameobjects/*` |
| Prefab 信息 | `prefab.info` / `prefab.hierarchy` | `prefabs/*` |
| 工具发现 | `tools.groups` / `tools.custom` | `tools/*` |

### 轻量 Context 与深读 Tool 的分层

`scene.gameobject.components` 与 `scene.gameobject.component` 是**轻量预览** Context：仅返回组件类型名、enabled 状态与短摘要，不承诺完整 fields/props。深度读取走 `tools run component.get`（对齐 IvanMurzak `gameobject-component-get`）。这是去重规则的要求：Context 只观察，Tool 才深读。

## 诊断与 doctor

`doctor` 命令检查 Context 相关健康状态：
- `project_tool_fingerprint` — 自定义工具指纹一致性
- `unified_tool_discovery` — 内置 + 项目工具发现

doctor 将额外检查 `customContexts.fingerprint`。

## 取证与依赖

本文档内容来源：
- 总架构：Context 只读硬边界
- 项目自定义 Context 设计
- 内置 Context 等价映射
- `doctor` 与 `customContexts.fingerprint` 诊断闭环
