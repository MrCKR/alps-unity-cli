# 自定义 Tool 引文

只在用户要创建、修改、审计或排查项目自定义 Tool 时读取本文件。

自定义 Tool 用于让 AI 执行项目侧动作。它可以只读，也可以修改 Unity 状态；因此必须准确声明风险、输入输出和 Play Mode 语义。只读信息优先写自定义 Context。

## 何时写 Tool

- 用户要执行动作：生成资源、批量修改场景、修复对象、调用项目逻辑。
- 能力可能改变场景、资源、ProjectSettings、Package、脚本或 Editor/Play Mode 状态。
- 需要 dry-run、`--yes`、artifact、长任务或 runtime 执行语义。

## 注册方式

项目 C# Tool 实现 `IUnityCliTool<TReq,TRes>`。C# 文件必须放在项目内名为 `UnityCLI` 的目录下，例如 `Assets/UnityCLI/` 或 `Assets/Editor/UnityCLI/`。

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
            Examples = new[]
            {
                new ToolExample { Description = "回显消息", Input = new Request { Message = "hello" } }
            }
        };

        /// <summary>
        /// 执行项目侧回显工具。Request/Result schema 由框架从 DTO 派生。
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

## 关键规则

- `Request` / `Result` 嵌套在 Tool 类内，public 属性派生 `inputSchema` / `outputSchema`。
- `[UnityCliRequired]` 标记必填输入字段；不要手写 schema 字符串。
- `Describe()` 只写 `Name`、`Summary`、`Description`、`Group`、风险、target、Play Mode、examples 等人工字段。
- `Execute(Request, ctx)` 成功返回 Result DTO；失败交给框架转结构化错误 envelope，不要返回伪造的 `{ ok:false }`。
- `DestructiveHint = true` 的 Tool 必须通过 CLI `--yes` 门禁；能预览的写操作设置 `SupportsDryRun = true`。
- runtime Tool 设置 `Target = "runtime"` 和 `PlayModeSupport = "runtime-only"`；非 Play Mode 时让错误 envelope 提供 `transient`、`retryAfterMs`、`nextActions`。

## 执行入口

AI 使用公开 CLI 自发现和执行：

```powershell
unity_cli context read tools.custom
unity_cli tools group list <group> --include-custom
unity_cli tools describe <custom-tool> --include-custom
unity_cli tools run <custom-tool> --include-custom --input '<json>'
```

`<group>` 必须来自 `tools.custom` 或 `tools group list <group> --include-custom` 的真实输出；不要假设所有项目工具都在 `custom` 组。

## 最小验证

1. Unity 编译无错误。
2. `tools group list <group> --include-custom` 能发现 Tool。
3. `tools describe <tool> --include-custom` 的 schema、风险、target、Play Mode 语义足以让陌生 AI 一次调用成功。
4. `tools run <tool> --include-custom --input '<json>'` 覆盖默认正向、代表性可选参数和错误路径。
