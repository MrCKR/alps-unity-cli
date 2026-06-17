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
        DestructiveHint = false,
        ArchitectureSensitive = true,
        FrozenDecision = "ALP-404-Q1",
        CoverageScope = "attribute-first,zero-config,unity-runtime-csharp,play-mode-session")]
    public static class SampleRuntimeEchoTool
    {
        public static string Execute(UnityCliProjectToolContext context)
        {
            return "{\"requestId\":\"" + Escape(context.RequestId) + "\",\"input\":" + (string.IsNullOrEmpty(context.InputJson) ? "{}" : context.InputJson) + ",\"isPlaying\":" + Bool(EditorApplication.isPlaying) + ",\"frameCount\":" + Time.frameCount.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}";
        }

        private static string Escape(string value)
        {
            return (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
        }

        private static string Bool(bool value)
        {
            return value ? "true" : "false";
        }
    }
}
#endif