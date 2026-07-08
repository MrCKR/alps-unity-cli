# 自定义 Tool

项目侧通过 C# `IUnityCliTool<TReq,TRes>` 泛型接口声明自定义 Tool。Tool 是动作入口：可以读取，也可以修改 Unity Editor、Play Mode 会话、场景或资产；任何可能有副作用的能力都应设计为 Tool，而不是 Context。

自定义 Tool 与内置工具共享同一公开链路：

- `tools group` / `tools group list <group> --include-custom` 发现
- `tools describe <name> --include-custom` 学习输入、输出、风险和状态
- `tools run <name> --include-custom --input '<json>'` 执行
- 成功/失败都返回统一 JSON envelope

C# 文件必须放在项目内名为 `UnityCLI` 的目录下，例如 `Assets/UnityCLI/` 或 `Assets/Editor/UnityCLI/`。

## 最小示例

```csharp
#if UNITY_EDITOR
using UnityCli;

namespace MyProject.UnityCLI
{
    public sealed class SampleEchoTool : IUnityCliTool<SampleEchoTool.Request, SampleEchoTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired]
            public string Message { get; set; }
            public string Note { get; set; }
        }

        public sealed class Result
        {
            public string RequestId { get; set; }
            public string Message { get; set; }
            public string Note { get; set; }
        }

        public ToolDescriptor Describe() => new ToolDescriptor
        {
            Name = "sample.echo",
            Summary = "Echo a required message from a project-local Editor tool.",
            Description = "Echoes a message from a Unity project Editor C# tool. Optional note is returned when present.",
            Group = "sample",
            Version = "1.0.0",
            DestructiveHint = false,
            Target = "editor",
            PlayModeSupport = "editor-only",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample
                {
                    Description = "回显消息",
                    Input = new Request { Message = "hello" }
                }
            }
        };

        /// <summary>
        /// 执行项目侧回显工具。输入和输出均为强类型 DTO，schema 由框架从类型派生。
        /// </summary>
        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            return new Result
            {
                RequestId = ctx.RequestId ?? string.Empty,
                Message = request.Message,
                Note = request.Note
            };
        }
    }
}
#endif
```

## 结构要求

每个 Tool 类自包含三部分：

- `Request`：输入 DTO。public 属性会派生为 `inputSchema`。
- `Result`：成功输出 DTO。public 属性会派生为 `outputSchema`。
- `Describe()`：人工编写 descriptor 字段。不要在这里手写 schema。
- `Execute(Request, ctx)`：真正执行逻辑，结构体进、结构体出。

`[UnityCliRequired]` 标记必填输入字段。未标记的引用类型字段可省略；无值字段按 proto3 风格不发送。Unity 2022.3 默认 C# 版本不支持 C# 11 `required`，因此必填语义使用 `[UnityCliRequired]`。

## Descriptor 字段

`Describe()` 只写 AI 需要理解的稳定语义：

| 字段 | 说明 |
|---|---|
| `Name` | 唯一 Tool 名，使用 dotted/kebab 风格，例如 `sample.echo`。 |
| `Summary` | 发现阶段路由字段，短句说明动作对象和结果。 |
| `Description` | 调用阶段说明边界、默认行为、副作用、Play Mode 语义和错误恢复。 |
| `Group` | 发现分组；项目工具可使用项目域名或功能域名，不要求都叫 `custom`。 |
| `DestructiveHint` | 真实持久化写入、删除、ProjectSettings/manifest 修改或任意代码执行时为 `true`。 |
| `Target` | `editor`、`runtime` 或 `both`。 |
| `PlayModeSupport` | `supported`、`runtime-only`、`session-affecting`、`editor-only` 或 `unknown`。 |
| `SupportsDryRun` | 支持预览时设为 `true`。 |
| `DefaultTimeoutMs` / `MaxTimeoutMs` | 控制默认与最大执行时间。 |
| `RequiresPackages` | 依赖 Unity package 时声明，例如 `com.unity.probuilder`。 |
| `Examples` | 用强类型 `Request` 实例给出可照抄的典型输入。 |

`inputSchema` / `outputSchema` 由框架从 `Request` / `Result` 派生并注入 describe 输出；不要手写 JSON Schema 字符串。

## Editor 与 Runtime

`Target = "editor"` 的工具运行在 Editor 侧，适合场景、资产、项目设置或 Editor 会话操作。

`Target = "runtime"` 的工具通过 runtime bridge 在 Play Mode 会话中运行。非 Play Mode 时应返回稳定 runtime 错误，AI 根据 `error.transient`、`error.retryAfterMs` 和 `error.nextActions` 决定下一步。

`Target = "both"` 只在同一个能力确实有 Editor/Runtime 双态语义时使用；descriptor 必须解释两种状态下的结果差异。

## 安全规则

- 真实持久化写入或删除必须设置 `DestructiveHint = true`，CLI 执行时需要 `--yes`。
- 能 dry-run 的写操作应设置 `SupportsDryRun = true`，并在 `Result` 中返回 planned effects。
- 只改场景内存或会话状态通常不算 destructive，但仍要在 `SideEffects` / `Description` 里写清楚。
- 只读能力必须保持只读；如果未来可能写入，应拆成单独 Tool。
- 失败应让框架返回结构化错误 envelope；不要把 `{ ok:false }` 作为成功 Result 返回。

## 发现与验证

```powershell
unity_cli tools group list <group> --include-custom
unity_cli tools describe <tool-name> --include-custom
unity_cli tools run <tool-name> --include-custom --input '{"message":"hello"}'
```

`<group>` 必须来自真实 discovery 输出；不要假设项目工具一定在 `custom` 组。执行前始终先看 `tools describe`，确认 schema、risk、target、availabilityScope 和 playModeSupport。
