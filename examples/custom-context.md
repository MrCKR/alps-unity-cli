# 自定义 Context 示例

`[UnityCliContext]` 特性和 `context list / describe / read` 命令已实现。本示例展示注册和使用自定义 Context 的方式。

## 1. C# 注册代码

```csharp
using UnityCli;
using System.Collections.Generic;

[UnityCliContext("sample.project-summary",
    Title = "Project Summary",
    Description = "Read-only project overview including name, version, scene and package counts.",
    Source = "project",
    Category = "project",
    ReadOnlyHint = true)]
public static class SampleProjectSummaryContext
{
    // 只读读取，不得修改任何 Unity 状态
    public static object Read(Dictionary<string, object> input)
    {
        return new
        {
            projectName = "MyUnityProject",
            unityVersion = "2022.3.45f1",
            sceneCount = 3,
            packageCount = 12,
            targetPlatform = "StandaloneWindows64"
        };
    }
}
```

## 2. CLI 发现与执行

```powershell
# 列出所有可用 Context
unity_cli context list

# 查看 Context schema 和来源
unity_cli context describe sample.project-summary

# 读取 Context 数据
unity_cli context read sample.project-summary
```

## 3. 替代方案

也可通过只读工具获取类似信息：

```powershell
# 项目信息（通过诊断预检）
unity_cli diagnose preflight

# 编辑器状态
unity_cli tools run editor.state --input '{}'

# 场景层级
unity_cli tools run scene.hierarchy --input '{}'

# 包列表
unity_cli package list --project <path>
```

## 4. 只读硬边界

Context 实现必须严格遵守：

| 不得修改 | 说明 |
|----------|------|
| Unity 状态 | 场景、GameObject、Component |
| 文件系统 | 资产、脚本、配置文件 |
| Package 依赖 | manifest.json |
| Play Mode 状态 | 不得触发 play/pause/stop |
| Editor 设置 | 任何编辑器配置 |

必须保证：
- 输出为结构化 JSON
- 可重复调用产生一致结果（幂等）
- 不产生副作用
