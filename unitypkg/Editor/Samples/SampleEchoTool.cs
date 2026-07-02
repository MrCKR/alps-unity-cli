#if UNITY_EDITOR
using System.Collections.Generic;

namespace UnityCli.Samples
{
    /// <summary>
    /// 新形态自定义工具样板(IUnityCliTool&lt;TReq,TRes&gt; 泛型接口自包含)。
    ///
    /// 设计依据:.plan/自定义工具定义规范.md
    /// - Request/Result 嵌套工具类内
    /// - Describe() 只声明人写字段,schema 由框架从 Request/Result 反射派生
    /// - Execute(Request, ctx): Result,结构体进结构体出,机制消灭手拼
    /// 这是首次真实使用 UnityCliSerializer.Serialize/Deserialize 往返(阶段1遗留验证)。
    /// </summary>
    public class SampleEchoTool : IUnityCliTool<SampleEchoTool.Request, SampleEchoTool.Result>
    {
        // ── 协议类型:嵌套 ──
        public readonly record struct Request
        {
            public required string Message { get; init; }   // required → schema 必填
            public string? Note { get; init; }              // 可选 → 无值不发(proto3)
        }

        public readonly record struct Result
        {
            public string RequestId { get; init; }
            public string Message { get; init; }
            public string? Note { get; init; }
        }

        // ── desc:Describe() 方法,只声明人写字段,不含 schema ──
        public ToolDescriptor Describe() => new()
        {
            Name = "sample.echo",
            Summary = "Echo a required message payload from a project-local Editor tool.",
            Description = "Echoes the supplied message from a Unity project Editor C# tool. Requires a non-empty message. Optional note is echoed back when present.",
            Group = "sample",
            Version = "1.0.0",
            DestructiveHint = false,
            Target = "editor",
            PlayModeSupport = "editor-only",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "回显消息", Input = new Request { Message = "hello" } },
                new ToolExample { Description = "带备注回显", Input = new Request { Message = "hi", Note = "with note" } }
            }
        };

        // ── handler:结构体进结构体出 ──
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
