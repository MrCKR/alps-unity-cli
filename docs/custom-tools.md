# 自定义工具（Custom Tools）

项目侧通过 C# `[UnityCliTool]` 特性注册统一 Tool，并用 `target` / `availabilityScope` 描述运行约束；自定义 Tool 与内置工具共享同一发现链和执行入口。

## 概述

| `target` | 说明 |
|----------|------|
| `editor` | 编辑器主线程执行，适合资产/场景/配置修改 |
| `runtime` | gameplay/runtime layer 工具；通常需要 Play Mode 才可用 |

项目自定义工具：
- 通过 `tools group list custom --include-custom` 发现
- 通过 `tools describe <name>` 查看完整描述
- 通过 `tools run <name>` 执行
- 与内置工具使用相同的 descriptor schema 和 envelope 格式

## `target: editor` 注册

使用 `[UnityCliTool]` 特性标记静态类或静态方法：

### 类级别注册

```csharp
using UnityCli;

[UnityCliTool("sample.editor-tool",
    Title = "Sample Editor-targeted Tool",
    Group = "custom",
    Description = "A minimal editor-targeted tool.",
    DestructiveHint = false)]
public static class SampleEditorTool
{
    // 类级别注册需要恰好一个 static Execute 或 Run 方法
    public static object Execute(Dictionary<string, object> input)
    {
        var message = input.TryGetValue("message", out var msg) ? msg?.ToString() : "hello";
        return new { greeting = $"Editor says: {message}" };
    }
}
```

### 方法级别注册

```csharp
using UnityCli;

public static class MyTools
{
    [UnityCliTool("my.tool-a",
        Title = "Tool A",
        Group = "custom")]
    public static object ToolA(Dictionary<string, object> input)
    {
        return new { result = "tool-a output" };
    }

    [UnityCliTool("my.tool-b",
        Title = "Tool B",
        Group = "custom",
        DestructiveHint = true)]
    public static object ToolB(Dictionary<string, object> input)
    {
        return new { result = "tool-b output" };
    }
}
```

## `target: runtime` 注册

Runtime target 与 editor target 使用相同的注册方式，只是通过 descriptor 标记 `target` 和可用性范围：

```csharp
using UnityCli;

[UnityCliTool("sample.runtime-echo",
    Title = "Runtime Echo",
    Group = "custom",
    Description = "Echoes input during Play Mode.",
    Target = "runtime")]
public static class SampleRuntimeTool
{
    public static object Execute(Dictionary<string, object> input)
    {
        var message = input.TryGetValue("message", out var msg) ? msg?.ToString() : "ping";
        return new { echo = message, timestamp = DateTime.Now.ToString("o") };
    }
}
```

Runtime target 行为：
- 仅在 Play Mode 活跃时可用
- 非 Play Mode 时返回 `E_RUNTIME_NOT_READY`（transient error）
- 通过 `tools group list custom --include-custom` 发现（Play Mode 下自动标记 `available: true/false`）

## 特性参数

| 参数 | 类型 | 必填 | 说明 |
|------|------|------|------|
| `name` | string | ✅ | 工具唯一标识（如 `my.editor-tool`） |
| `Title` | string | ✅ | 人类可读标题 |
| `Group` | string | ✅ | 工具分组名 |
| `Description` | string | ❌ | 工具描述 |
| `DestructiveHint` | bool | ❌ | 是否有破坏性（默认 false） |
| `Target` | string | ❌ | 目标类型：`editor`（默认）/ `runtime` / `both` |

## Descriptor 自动生成

Bridge 扫描已加载的 Unity Editor 程序集，自动从 `[UnityCliTool]` 特性推导：
- `inputSchema` / `outputSchema` — 基于 Execute 方法签名
- `fingerprint` — `sha256(handler-code + descriptor-v1)`
- `handler` / `supportStatus` / `target` / `availabilityScope`

零手写配置，Class/Attribute-first 注册。

## 安全边界

### 必须遵守

- 每个工具必须有完整 descriptor（由特性自动生成）
- 所有输入输出必须通过统一 envelope 格式
- Fingerprint 校验保护，handler 代码变更时默认阻止执行
- 破坏性工具（`DestructiveHint: true`）执行前需要 `--yes` 确认

### 禁止项

| 禁止 | 说明 |
|------|------|
| 任意方法调用 | 只能通过 `tools run <registered-name>` 执行已注册工具 |
| 反射 escape hatch | 不可通过自定义工具绕过安全检查 |
| 绕过 registry 直接执行 | 不允许跳过 descriptor 校验和 fingerprint 检查 |
| 修改 Context 约定 | 有副作用的能力必须注册为 Tool，不得注册为 Context |

## 发现与执行

```powershell
# 发现所有自定义工具（需要 Bridge 连接）
unity_cli tools group list custom --include-custom

# 查看自定义工具完整描述
unity_cli tools describe my.editor-tool

# 执行自定义工具
unity_cli tools run my.editor-tool --input '{"message":"hello"}'

# target=runtime 的工具（通常需要 Play Mode）
unity_cli tools run editor.play
unity_cli tools run sample.runtime-echo --input '{"message":"ping"}'
```

## 当前状态与规划

| 能力 | 状态 |
|------|------|
| `target=editor` Tool 注册与执行 | ✅ 已实现 |
| `target=runtime` Tool 注册与执行 | ✅ 已实现 |
| `tools group list custom --include-custom` 发现 | ✅ 已实现 |
| Fingerprint 校验 | ✅ 已实现 |
| `--dry-run` 支持 | ✅ 已实现（descriptor 级别控制） |
| 独立 `extensions` 命令组 | ✅ 已实现 |
