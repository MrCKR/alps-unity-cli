# 自定义 Tool 引文

只在用户要创建、修改、审计或排查项目自定义 Tool 时读取本文件。

自定义Tool用于让AI执行项目侧动作或观察项目状态。它可以只读，也可以修改Unity状态；因此必须准确声明风险、输入输出和PlayMode语义。只读能力同样实现为Tool，并声明空副作用。

## 何时写 Tool

- 用户要执行动作：生成资源、批量修改场景、修复对象、调用项目逻辑。
- 能力可能改变场景、资源、ProjectSettings、Package、脚本或 Editor/Play Mode 状态。
- 需要 dry-run、artifact、长任务或 runtime 执行语义。

## 注册方式

项目 C# Tool 实现 `IUnityCliTool<TReq,TRes>`。C# 文件必须放在项目内名为 `UnityCLI` 的目录下，例如 `Assets/UnityCLI/` 或 `Assets/Editor/UnityCLI/`。

```csharp
#if UNITY_EDITOR
using System.Threading;
using System.Threading.Tasks;
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
            public string Message { get; set; }
            public string Note { get; set; }
        }

        public ToolDescriptor Describe() => new ToolDescriptor
        {
            Name = "sample.echo",
            Summary = "Echo a required message from a project-local Editor tool.",
            Description = "Echoes a message from a Unity project Editor C# tool. Optional note is returned when present.",
            Group = "sample",
            DestructiveHint = false,
            Target = "editor",
            PlayModeSupport = "supported",
            Completion = new ToolCompletionDescriptor
            {
                ExecutionClass = "atomic",
                CompletionPredicate = "typed-business-terminal",
                ProofSource = "typed-result-and-effect-readback",
                ReloadPolicy = "same-session-proof-recovery",
                TimeoutError = "E_EXECUTION_TIMEOUT",
                OutcomeUnknownPolicy = "caller-decides-after-timeout",
                CompletionDriverId = "atomic-typed-result-v1"
            },
            Examples = new[]
            {
                new ToolExample { Description = "回显消息", Input = new Request { Message = "hello" } }
            }
        };

        /// <summary>
        /// 执行项目侧回显工具。Request/Result schema 由框架从 DTO 派生。
        /// </summary>
        public ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return new ValueTask<Result>(new Result
            {
                Message = request.Message,
                Note = request.Note
            });
        }
    }
}
#endif
```

## 关键规则

- `Request` / `Result` 嵌套在 Tool 类内，public 属性派生 `inputSchema` / `outputSchema`。
- `[UnityCliRequired]` 标记必填输入字段；不要手写 schema 字符串。
- `Describe()` 只写 `Name`、`Summary`、`Description`、`Group`、风险、target、Play Mode、completion 和 examples 等人工字段。
- 自定义 Tool 必须声明完整 `Completion`；driver 必须使用框架已注册的 execution class/driver 对。`OutcomeUnknownPolicy` 描述 timeout 后没有可信终态且后续由调用方决定，不构成调用限制。
- `ExecuteAsync(Request, ctx, ct)` 返回 `ValueTask<Result>`；失败交给框架转结构化错误 envelope，不要返回伪造的 `{ ok:false }`。
- mutation Tool 在全部输入、目标、权限和包前置校验后、紧邻首个真实 effect 前必须恰好调用一次 `ctx.CommitEffect()`。漏调而返回成功会被协调器拒绝为 `E_TOOL_COMPLETION_PROOF_INVALID`；重复调用稳定返回 `E_TOOL_EFFECT_BARRIER_DUPLICATE`。只读 Tool、dry-run 与 proof-only recovery 不得调用。
- `DestructiveHint = true` 准确描述可能不可逆效果，但公开 Tool 调用本身即授权；不得引入 CLI 确认、预定义按钮列表或意图门。能预览的写操作设置 `SupportsDryRun = true`。
- runtime Tool 设置 `Target = "runtime"` 和 `PlayModeSupport = "runtime-only"`；非 Play Mode 时让错误 envelope 提供 `transient`、`retryAfterMs`、`nextActions`。

## 执行入口

AI 使用当前实例的统一公开目录自发现和执行：

```powershell
unitycli tools group --instance <id|projectPath|alias>
unitycli tools group list <group> --instance <id|projectPath|alias>
unitycli tools describe <custom-tool> --instance <id|projectPath|alias>
unitycli tools run <custom-tool> --input '<json>' --instance <id|projectPath|alias>
```

`<group>` 必须来自当前实例 `tools group` 的真实输出；不要假设项目 Tool 位于 `custom` 组。`source` 只作描述/审计，不改变发现或执行路径。

## 最小验证

1. Unity 编译无错误。
2. `tools group` 与 `tools group list <group>` 能从同一实例完整目录发现 Tool。
3. `tools describe <tool>` 的 schema、风险、target、Play Mode 语义足以让陌生 AI 一次调用成功。
4. `tools run <tool> --input '<json>' --instance <id|projectPath|alias>` 覆盖默认正向、代表性可选参数和错误路径。
