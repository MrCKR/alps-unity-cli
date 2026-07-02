#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace UnityCli.Samples
{
    /// <summary>
    /// 新形态 runtime-domain 项目工具样板(IUnityCliTool,Target=runtime)。
    /// 仅在 Play Mode 可用/可执行。设计依据:.plan/自定义工具定义规范.md。
    /// </summary>
    public class SampleRuntimeEchoTool : IUnityCliTool<SampleRuntimeEchoTool.Request, SampleRuntimeEchoTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired] public string Message { get; set; }
        }

        public sealed class Result
        {
            public string RequestId { get; set; }
            public string Message { get; set; }
            public bool IsPlaying { get; set; }
            public long FrameCount { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "sample.runtime-echo",
            Summary = "Echo a message payload from Play Mode with runtime frame state.",
            Description = "Echoes the supplied message from a Unity project runtime (Play Mode) C# tool. Only available in Play Mode. Returns the echoed message along with current Play Mode and frame state.",
            Group = "sample",
            Version = "1.0.0",
            DestructiveHint = false,
            Target = "runtime",
            PlayModeSupport = "runtime-only",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Play Mode 回显", Input = new Request { Message = "ping" } }
            }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            return new Result
            {
                RequestId = ctx.RequestId ?? string.Empty,
                Message = request.Message,
                IsPlaying = EditorApplication.isPlaying,
                FrameCount = Time.frameCount
            };
        }
    }
}
#endif
