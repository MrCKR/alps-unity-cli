# 自定义 Tool 引文

只在用户要创建、修改、审计或排查项目自定义 Tool 时读取本文件。

自定义 Tool 用于让 AI 执行项目侧动作。它可以只读，也可以修改 Unity 状态；因此必须准确声明风险和输入输出 schema。

## 什么时候用 Tool

选择 Tool 的场景：

- 用户要让 AI 执行动作，例如生成资源、批量修改场景、检查并修复对象、运行项目特定逻辑。
- 操作可能改变场景、资源、ProjectSettings、Package、脚本或 Editor 状态。
- 需要运行在 Editor 或 Play Mode runtime 中。

如果只是给 AI 提供只读信息，优先写自定义 Context，而不是 Tool。

## 注册方式

在 Unity Editor 侧 C# 中使用 `[UnityCliTool]`。可以标在静态类或静态方法上。

`[UnityCliTool]` 定义在 `UnityCli.Editor` 程序集中（`autoReferenced: true`），项目内任何 asmdef 都可以直接引用，无需手动添加程序集引用。C# 文件放在名为 `UnityCLI` 的目录下（如 `Assets/UnityCLI/`）。

类级别示例：

```csharp
#if UNITY_EDITOR
using UnityCli;

[UnityCliTool(
    "sample.echo",
    Summary = "Echo the supplied input JSON from a Unity project Editor C# tool.",
    Description = "Echoes the supplied input JSON from a Unity project Editor C# tool.",
    Group = "sample",
    InputSchema = "{\"type\":\"object\",\"properties\":{\"message\":{\"type\":\"string\"}},\"required\":[\"message\"]}",
    OutputSchema = "{\"type\":\"object\",\"properties\":{\"requestId\":{\"type\":\"string\"},\"input\":{\"type\":\"object\"}}}",
    DestructiveHint = false,
    PlayModeSupport = "editor-only")]
public static class SampleEchoTool
{
    public static string Execute(UnityCliProjectToolContext context)
    {
        return new UnityCliJsonWriter().BeginObject()
            .Prop("requestId", context.RequestId ?? string.Empty)
            .PropRaw("input", string.IsNullOrEmpty(context.InputJson) ? "{}" : context.InputJson)
            .EndObject()
            .ToString();
    }
}
#endif
```

方法级别示例（放在 `Assets/UnityCLI/` 目录下）：

```csharp
#if UNITY_EDITOR
using System.Collections.Generic;
using UnityCli;

public static class MyProjectTools
{
    [UnityCliTool(
        "my.asset-summary",
        Summary = "Read a project-specific asset summary for one asset path.",
        Description = "Returns a project-specific asset summary.",
        Group = "custom",
        InputSchema = "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"}},\"required\":[\"path\"]}",
        OutputSchema = "{\"type\":\"object\"}")]
    public static object AssetSummary(Dictionary<string, object> input)
    {
        return new { ok = true, path = input["path"] };
    }
}
#endif
```

## 关键属性

- `Name`：唯一 Tool 名，建议 dotted/kebab 风格，例如 `my.asset-summary`。
- `Summary`：必填短说明，用于 `tools group list` / `tools search` / `tools describe` 的快速判断。
- `Description`：完整说明工具用途、边界和关键副作用。
- `Group`：分组名；项目专用工具常用 `custom` 或项目域名或功能域名。
- `InputSchema` / `OutputSchema`：JSON Schema；AI 执行前会通过 `tools describe` 读取。
- `DestructiveHint`：删除、覆盖、不可逆修改必须设为 `true`。
- `Target`：`editor`、`runtime` 或 `both`；runtime 通常需要 Play Mode。
- `PlayModeSupport`：`supported`、`runtime-only`、`session-affecting`、`editor-only` 或 `unknown`；editor-only 在 Play Mode 下返回 `E_TOOL_PLAYMODE_UNAVAILABLE`。
- `DefaultTimeoutMs` / `MaxTimeoutMs`：长任务按需设置。
- `Fingerprint`：可选固定指纹；缺省时 Bridge 会计算 handler + descriptor 指纹。

## 执行入口

AI 使用这些命令发现和执行自定义 Tool：

```powershell
unity_cli context read tools.custom
unity_cli tools group list <group> --include-custom
unity_cli tools describe <custom-tool> --include-custom
unity_cli tools run <custom-tool> --include-custom --input '<json>'
```

`<group>` 必须来自 `tools.custom` 或 `tools group list <group> --include-custom` 的真实输出；不要假设所有项目自定义 Tool 都在 `custom` 组。

runtime Tool 通常先进入 Play Mode，并声明 `PlayModeSupport = "runtime-only"`：

```powershell
unity_cli tools run editor.play
unity_cli tools run <runtime-tool> --input '<json>'
```

## 安全约束

- 不要绕过 descriptor、fingerprint、统一 envelope。
- `DestructiveHint: true` 的 Tool 必须要求 `--yes`；如可行，支持 dry-run。
- 修改文件/资产/场景时，返回结构化结果和可验证字段。
- 如果 Tool 是只读的，确保代码确实不写文件、不改 Unity 状态、不触发导入/编译。

## AI 写 Tool 时的最小步骤

1. 先确认用户要的是“动作”而不是“只读上下文”。
2. 设计稳定 Tool 名、Group、输入 schema、输出 schema、风险标记。
3. 在 Editor C# 代码中加 `[UnityCliTool]` 和 handler。
4. 让 Unity 编译后运行：
   ```powershell
   unity_cli tools group list <group> --include-custom
   unity_cli tools describe <tool>
   ```
5. 先用安全输入或 dry-run 验证，再执行真实修改。
