#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace UnityCli.Samples
{
    /// <summary>
    /// Minimal project-local Editor custom context example. Provides a read-only
    /// project summary: Unity version, active scene, render pipeline, and build target.
    /// The descriptor is derived entirely from the C# method attribute; there is no
    /// JSON, YAML, manifest or external script config to maintain by hand.
    ///
    /// Key constraints (enforced by the registry at registration):
    /// - Handler method name must NOT be Execute/Run.
    /// - Handler must NOT contain write-semantic tokens (see ProjectContextRegistry
    ///   ContextSourceGuardForbiddenTokens for the full list).
    /// - ReadOnlyHint is always true; no DestructiveHint/SupportsJob/ArtifactKinds.
    /// </summary>
    public static class SampleContext
    {
        /// <summary>
        /// Read handler for sample.project-summary context.
        /// Returns project configuration summary without writing files,
        /// changing scenes, modifying assets, or triggering Play Mode.
        /// Method name intentionally avoids Execute/Run (registry constraint).
        /// </summary>
        [UnityCliContext(
            "sample.project-summary",
            Title = "Sample Project Summary",
            Description = "Returns a read-only summary of the Unity project: version, active scene, render pipeline, and build target.",
            Category = "sample",
            InputSchema = "{\"type\":\"object\",\"properties\":{\"includeDisabled\":{\"type\":\"boolean\"}}}",
            OutputSchema = "{\"type\":\"object\",\"properties\":{\"unityVersion\":{\"type\":\"string\"},\"activeScene\":{\"type\":\"string\"},\"renderPipeline\":{\"type\":\"string\"},\"buildTarget\":{\"type\":\"string\"},\"includeDisabled\":{\"type\":\"boolean\"}}}",
            EquivalentUnityMcp = "get_project_info",
            EquivalentUnityMcpNote = "Provides similar info to unity-mcp get_project_info resource",
            RefreshBehavior = "onDemand")]
        public static string Provide(UnityCliContextRequest request)
        {
            var includeDisabled = TryParseIncludeDisabled(request.InputJson);
            var activeScene = SceneManager.GetActiveScene();
            var buildTarget = EditorUserBuildSettings.activeBuildTarget.ToString();
            var renderPipeline = GraphicsSettings.currentRenderPipeline != null
                ? GraphicsSettings.currentRenderPipeline.name
                : "Built-in";

            return new UnityCliJsonWriter().BeginObject()
                .Prop("unityVersion", Application.unityVersion)
                .Prop("activeScene", activeScene.path ?? string.Empty)
                .Prop("renderPipeline", renderPipeline)
                .Prop("buildTarget", buildTarget)
                .Prop("includeDisabled", includeDisabled)
                .EndObject()
                .ToString();
        }

        /// <summary>
        /// Minimal JSON boolean extraction for the includeDisabled field.
        /// Avoids dependency on external JSON libraries.
        /// </summary>
        private static bool TryParseIncludeDisabled(string inputJson)
        {
            if (string.IsNullOrEmpty(inputJson)) return false;
            var idx = inputJson.IndexOf("includeDisabled", System.StringComparison.Ordinal);
            if (idx < 0) return false;
            // Find the colon after the key
            var colonIdx = inputJson.IndexOf(':', idx);
            if (colonIdx < 0) return false;
            // Scan forward to the value
            for (var i = colonIdx + 1; i < inputJson.Length; i++)
            {
                if (inputJson[i] == 't') return true;   // true
                if (inputJson[i] == 'f') return false;  // false
            }
            return false;
        }
    }
}
#endif
