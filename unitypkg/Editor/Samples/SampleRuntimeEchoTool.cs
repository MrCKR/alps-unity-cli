#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace UnityCli.Samples
{
    /// <summary>
    /// Minimal runtime-domain project tool example. This tool is declared with
    /// Target="runtime" and is only available and executable when the Unity Editor
    /// is in Play Mode. It echoes the supplied input JSON and includes the current
    /// Play Mode session state to demonstrate runtime tool lifecycle awareness.
    /// </summary>
    [UnityCliTool(
        "sample.runtime-echo",
        Title = "Sample Runtime Echo",
        Description = "Echoes the supplied input JSON from a Unity project runtime (Play Mode) C# tool. Only available in Play Mode.",
        Group = "sample",
        Target = "runtime",
        InputSchema = "{\"type\":\"object\",\"properties\":{\"message\":{\"type\":\"string\"}},\"required\":[\"message\"]}",
        OutputSchema = "{\"type\":\"object\",\"properties\":{\"requestId\":{\"type\":\"string\"},\"input\":{\"type\":\"object\"},\"isPlaying\":{\"type\":\"boolean\"},\"frameCount\":{\"type\":\"number\"}}}",
        ReadOnlyHint = true,
        DestructiveHint = false
)]
    public static class SampleRuntimeEchoTool
    {
        public static string Execute(UnityCliProjectToolContext context)
        {
            var w = new UnityCliJsonWriter();
            w.BeginObject()
                .Prop("requestId", context.RequestId ?? string.Empty)
                .PropRaw("input", string.IsNullOrEmpty(context.InputJson) ? "{}" : context.InputJson)
                .Prop("isPlaying", EditorApplication.isPlaying)
                .Prop("frameCount", Time.frameCount)
                .EndObject();
            return w.ToString();
        }
    }
}
#endif