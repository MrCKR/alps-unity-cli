# 自定义 Context 引文

只在用户要创建、修改、审计或排查项目自定义 Context 时读取本文件。

自定义 Context 用于给 AI 提供项目侧只读信息。它不是动作入口，不能修改 Unity 状态。凡是需要改场景、资源、脚本、设置、Package 或 Play Mode 的需求，都应改写为自定义 Tool。

## 何时写 Context

- 用户希望 AI 理解项目结构、团队约定、场景摘要、资源索引或配置状态。
- 信息应可重复读取、结构化输出、无副作用。
- 需要暴露项目特定知识，但不希望 AI 执行动作。

如果用户说“执行、生成、修改、修复、删除、导入、设置”，通常应使用 Tool。

## 注册方式

项目 C# Context 实现 `IUnityCliContext<TReq,TRes>`。C# 文件必须放在项目内名为 `UnityCLI` 的目录下，例如 `Assets/UnityCLI/` 或 `Assets/Editor/UnityCLI/`。

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

## 关键规则

- `Request` / `Result` 嵌套在 Context 类内，public 属性派生 `inputSchema` / `outputSchema`。
- `Describe()` 只写人工字段：`Name`、`Summary`、`Description`、`Category`、target、availability、Play Mode、refresh 等。
- `Read(Request, ctx)` 必须只读；不要命名为 `Execute` 或 `Run`。
- Context 不写 destructive、side effects、dry-run、artifact 或 job 语义。
- 输出要结构化、稳定、可分页/筛选，避免大对象默认全量返回。

## 读取入口

```powershell
unity_cli context list --include-custom
unity_cli context describe <custom-context> --include-custom
unity_cli context read <custom-context> --include-custom --input '{}'
```

按分类浏览：

```powershell
unity_cli context list --category <category> --include-custom
```

## 只读硬边界

- 不修改场景、GameObject、Component、Prefab、Asset、ProjectSettings、Package manifest。
- 不写文件、不导入资源、不触发编译或 domain reload。
- 不进入、暂停或停止 Play Mode。
- 不调用会产生副作用的 Editor API。
- 同一 Unity 状态下结果应稳定。

## 最小验证

1. Unity 编译无错误。
2. `context list --include-custom` 能发现 Context。
3. `context describe <context> --include-custom` 的只读边界、schema、target 和 Play Mode 语义足够明确。
4. `context read <context> --include-custom --input '{}'` 覆盖默认读取、代表性可选参数和错误路径。
