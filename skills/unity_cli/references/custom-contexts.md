# 自定义 Context 引文

只在用户要创建、修改、审计或排查项目自定义 Context 时读取本文件。

自定义 Context 用于给 AI 提供项目侧只读信息。它不是动作入口，不能修改 Unity 状态。凡是需要改场景、资源、脚本、设置或 Play Mode 的需求，都应改写为自定义 Tool。

## 什么时候用 Context

选择 Context 的场景：

- 用户希望 AI 理解项目结构、团队约定、场景摘要、资源索引、配置状态。
- 信息应可重复读取、结构化输出、无副作用。
- 需要把项目特定知识暴露给 AI，但不希望 AI 执行动作。

如果用户说“执行、生成、修改、修复、删除、导入、设置”，通常应使用 Tool。

## 注册方式

在 Unity Editor 侧 C# 静态方法上使用 `[UnityCliContext]`。Context handler 方法名不要叫 `Execute` 或 `Run`，避免被误认为 Tool。`[UnityCliContext]` 定义在 `UnityCli.Editor` 程序集中（`autoReferenced: true`），项目内任何 asmdef 都可以直接引用。C# 文件放在名为 `UnityCLI` 的目录下（如 `Assets/UnityCLI/`）。

示例（放在 `Assets/UnityCLI/` 目录下）：

```csharp
#if UNITY_EDITOR
using UnityCli;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

public static class SampleContext
{
    [UnityCliContext(
        "sample.project-summary",
        Title = "Sample Project Summary",
        Description = "Returns a read-only summary of the Unity project.",
        Category = "sample",
        InputSchema = "{\"type\":\"object\",\"properties\":{\"includeDisabled\":{\"type\":\"boolean\"}}}",
        OutputSchema = "{\"type\":\"object\",\"properties\":{\"unityVersion\":{\"type\":\"string\"},\"activeScene\":{\"type\":\"string\"},\"renderPipeline\":{\"type\":\"string\"},\"buildTarget\":{\"type\":\"string\"}}}",
        RefreshBehavior = "onDemand")]
    public static string Provide(UnityCliContextRequest request)
    {
        var activeScene = SceneManager.GetActiveScene();
        var renderPipeline = GraphicsSettings.currentRenderPipeline != null
            ? GraphicsSettings.currentRenderPipeline.name
            : "Built-in";

        return new UnityCliJsonWriter().BeginObject()
            .Prop("unityVersion", Application.unityVersion)
            .Prop("activeScene", activeScene.path ?? string.Empty)
            .Prop("renderPipeline", renderPipeline)
            .Prop("buildTarget", EditorUserBuildSettings.activeBuildTarget.ToString())
            .EndObject()
            .ToString();
    }
}
#endif
```

## 关键属性

- `Name`：唯一 Context 名，建议 dotted/kebab 风格，例如 `sample.project-summary`。
- `Title` / `Description`：让 AI 能理解读取价值。
- `Category`：分类，例如 `sample`、`project`、`scene`、`design`。
- `InputSchema` / `OutputSchema`：JSON Schema；AI 读取前会通过 `context describe` 查看。
- `Target`：`editor`、`runtime` 或 `both`。
- `AvailabilityScope`：例如 `editorSession`、`playModeSession`。
- `RefreshBehavior`：通常用 `onDemand`。
- `RequiresPackages`：依赖 Unity Package 时声明。
- `ResourceUri` / `EquivalentUnityMcp`：有资源映射时再填。
- `Fingerprint`：可选固定指纹；缺省时 Bridge 会计算 handler + descriptor 指纹。

## 读取入口

AI 使用这些命令发现和读取自定义 Context：

```powershell
unity_cli context list --include-custom
unity_cli context describe <custom-context>
unity_cli context read <custom-context> --input '<json>'
```

如果想按分类浏览：

```powershell
unity_cli context list --category <category> --include-custom
```

## 只读硬边界

Context 必须满足：

- 不修改场景、GameObject、Component、Prefab、Asset、ProjectSettings、Package manifest。
- 不写文件、不导入资源、不触发编译。
- 不进入、暂停、停止 Play Mode。
- 不调用会产生副作用的 Editor API。
- 输出结构化 JSON，字段稳定，大小可控。
- 可重复读取；同一 Unity 状态下结果应稳定。

实现中避免使用带明显写语义的方法名或代码路径。注册器会拒绝部分写语义 token；不要试图绕过。

## AI 写 Context 时的最小步骤

1. 先确认用户要的是“只读信息”而不是“动作”。
2. 设计稳定 Context 名、Category、输入 schema、输出 schema。
3. 在 Editor C# 静态方法上加 `[UnityCliContext]`。
4. 确保 handler 名不叫 `Execute` 或 `Run`。
5. 让 Unity 编译后运行：
   ```powershell
   unity_cli context list --include-custom
   unity_cli context describe <context>
   unity_cli context read <context> --input '{}'
   ```
6. 检查输出是否结构化、精简、无副作用。
