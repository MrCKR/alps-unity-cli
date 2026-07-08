# 自定义 Context

自定义 Context 是项目侧只读数据源，用于让 AI 观察 Unity Editor、场景、资源索引或项目约定。Context 不是动作入口，不能修改 Unity 状态、资产、场景、Package、ProjectSettings，也不能触发导入、编译或 Play Mode 切换。

如果能力需要执行、生成、修改、修复、删除、导入或设置，应设计为自定义 Tool。

C# 文件必须放在项目内名为 `UnityCLI` 的目录下，例如 `Assets/UnityCLI/` 或 `Assets/Editor/UnityCLI/`。

## 发现与读取

```powershell
unity_cli context list --include-custom
unity_cli context describe <context-name> --include-custom
unity_cli context read <context-name> --include-custom --input '{}'
```

按分类浏览：

```powershell
unity_cli context list --category <category> --include-custom
```

## 最小示例

```csharp
#if UNITY_EDITOR
using UnityCli;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace MyProject.UnityCLI
{
    public sealed class ProjectSummaryContext : IUnityCliContext<ProjectSummaryContext.Request, ProjectSummaryContext.Result>
    {
        public sealed class Request
        {
            public bool IncludeDisabled { get; set; }
        }

        public sealed class Result
        {
            public string UnityVersion { get; set; }
            public string ActiveScene { get; set; }
            public string RenderPipeline { get; set; }
            public string BuildTarget { get; set; }
            public bool IncludeDisabled { get; set; }
        }

        public ContextDescriptor Describe() => new ContextDescriptor
        {
            Name = "sample.project-summary",
            Summary = "Read project version, active scene, render pipeline, and build target.",
            Description = "Returns a read-only project summary without modifying scenes, assets, packages, ProjectSettings, imports, compilation, or Play Mode.",
            Category = "sample",
            Version = "1.0.0",
            Target = "editor",
            AvailabilityScope = "editorSession",
            PlayModeSupport = "supported",
            RefreshBehavior = "onDemand"
        };

        /// <summary>
        /// 读取项目状态摘要。只读访问 Editor 状态，不写文件、不改场景、不触发导入或 Play Mode。
        /// </summary>
        public Result Read(Request request, UnityCliProjectToolContext ctx)
        {
            var activeScene = SceneManager.GetActiveScene();
            var renderPipeline = GraphicsSettings.currentRenderPipeline != null
                ? GraphicsSettings.currentRenderPipeline.name
                : "Built-in";

            return new Result
            {
                UnityVersion = Application.unityVersion,
                ActiveScene = activeScene.path ?? string.Empty,
                RenderPipeline = renderPipeline,
                BuildTarget = EditorUserBuildSettings.activeBuildTarget.ToString(),
                IncludeDisabled = request != null && request.IncludeDisabled
            };
        }
    }
}
#endif
```

## 结构要求

每个 Context 类自包含三部分：

- `Request`：读取参数 DTO。public 属性会派生为 `inputSchema`。
- `Result`：读取输出 DTO。public 属性会派生为 `outputSchema`。
- `Describe()`：人工编写 name、summary、description、category、target、availability、refresh 等字段。
- `Read(Request, ctx)`：只读 handler，返回 Result DTO。

Context 不写 `DestructiveHint`、`SideEffects`、`SupportsDryRun`、artifact 或 job 字段。schema 由框架派生，`Describe()` 不手写 JSON Schema。

## Descriptor 字段

| 字段 | 说明 |
|---|---|
| `Name` | 唯一 Context 名，例如 `sample.project-summary`。 |
| `Summary` | 发现阶段路由字段，短句说明读取对象和主要结果。 |
| `Description` | 说明只读范围、输出边界、分页/筛选、Play Mode 语义和不可用条件。 |
| `Category` | 分类，例如 `project`、`scene`、`sample` 或项目域名。 |
| `Target` | `editor`、`runtime` 或 `both`。 |
| `AvailabilityScope` | `editorSession`、`playModeSession` 或 `always`。 |
| `PlayModeSupport` | `supported`、`runtime-only`、`session-affecting`、`editor-only` 或 `unknown`。 |
| `RefreshBehavior` | `static`、`onDemand` 或 `live`。 |
| `RequiresPackages` | 依赖 Unity package 时声明。 |

## 只读硬边界

Context 必须满足：

- 不创建、修改、删除 GameObject、Component、Prefab、Asset、ProjectSettings 或 Package manifest。
- 不写文件、不调用 `AssetDatabase.Refresh()`、不触发导入、编译或 domain reload。
- 不进入、暂停或退出 Play Mode。
- 不调用会产生副作用的 Editor API。
- 输出结构化 JSON，字段稳定，必要时提供分页、筛选或截断标记。

如果读取逻辑需要缓存或生成 artifact，这通常已经越过 Context 边界，应改为 Tool。
