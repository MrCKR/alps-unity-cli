# 自定义 Editor Tool 示例

展示如何在 Unity 项目中注册 Editor Tool，并通过 alps-unity-cli 发现和执行。

## 1. C# 注册代码

```csharp
using UnityCli;
using System.Collections.Generic;

[UnityCliTool("sample.editor-tool",
    Title = "Sample Editor Tool",
    Group = "custom",
    Description = "A minimal example showing Editor Tool registration, descriptor, envelope and fingerprint.",
    ReadOnlyHint = false,
    DestructiveHint = false)]
public static class SampleEditorTool
{
    public static object Execute(Dictionary<string, object> input)
    {
        var message = input.TryGetValue("message", out var msg) ? msg?.ToString() : "hello";
        return new
        {
            greeting = $"Editor says: {message}",
            timestamp = System.DateTime.Now.ToString("o")
        };
    }
}
```

**要点**：
- `[UnityCliTool]` 特性必须指定 `name`、`Title`、`Group`
- 类级别注册需要恰好一个 `static Execute` 或 `Run` 方法
- Descriptor 和 fingerprint 由 Bridge 自动从特性推导
- 不需要手写 CLI 配置

## 2. CLI 发现与执行

```powershell
# 发现自定义工具
unity_cli tools group list custom --include-custom

# 查看完整描述（执行前必做）
unity_cli tools describe sample.editor-tool

# 执行
unity_cli tools run sample.editor-tool --input '{"message":"world"}'
```

## 3. Descriptor 字段

`tools describe sample.editor-tool` 输出包含：

| 字段 | 值 | 说明 |
|------|-----|------|
| `name` | `sample.editor-tool` | 工具唯一标识 |
| `group` | `custom` | 所属分组 |
| `readOnlyHint` | false | 可能修改状态 |
| `destructiveHint` | false | 无破坏性 |
| `handler` | bridge | 通过 Bridge 实时执行 |
| `target` | editor | 编辑器主线程 |
| `fingerprint` | `sha256-...` | handler 代码 + descriptor 指纹 |

## 4. 安全边界

- Descriptor 由 `[UnityCliTool]` 特性自动生成，不可省略
- 输入输出必须遵循统一 envelope 格式
- Fingerprint 校验保护，handler 代码变更时默认阻止执行
- 不可绕过 registry 直接调用未注册方法
- 不可通过自定义工具提供反射 escape hatch
