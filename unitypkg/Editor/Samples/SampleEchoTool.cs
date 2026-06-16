#if UNITY_EDITOR
using UnityEditor;

namespace UnityCli.Samples
{
    /// <summary>
    /// Minimal project-local Editor custom tool example. The descriptor is derived
    /// entirely from the C# class attribute and method signature; there is no JSON,
    /// YAML, manifest or external script config to maintain by hand.
    /// </summary>
    [UnityCliTool(
        "sample.echo",
        Title = "Sample Echo",
        Description = "Echoes the supplied input JSON from a Unity project Editor C# tool.",
        Group = "sample",
        InputSchema = "{\"type\":\"object\",\"properties\":{\"message\":{\"type\":\"string\"}},\"required\":[\"message\"]}",
        OutputSchema = "{\"type\":\"object\",\"properties\":{\"requestId\":{\"type\":\"string\"},\"input\":{\"type\":\"object\"}}}",
        ReadOnlyHint = true,
        DestructiveHint = false,
        ArchitectureSensitive = true,
        FrozenDecision = "ALP-329-Q1",
        CoverageScope = "attribute-first,zero-config,unity-editor-csharp")]
    public static class SampleEchoTool
    {
        public static string Execute(UnityCliCustomToolContext context)
        {
            return "{\"requestId\":\"" + Escape(context.RequestId) + "\",\"input\":" + (string.IsNullOrEmpty(context.InputJson) ? "{}" : context.InputJson) + "}";
        }

        private static string Escape(string value)
        {
            return (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
        }
    }
}
#endif
