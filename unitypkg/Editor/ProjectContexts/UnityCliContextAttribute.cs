#if UNITY_EDITOR
using System;

namespace UnityCli
{
    /// <summary>
    /// Attribute-first custom context declaration. Project Editor code marks a static C#
    /// method with this attribute; UnityCliBridge discovers it through loaded Editor
    /// assemblies and derives both the runtime registration and descriptor JSON from
    /// the method metadata. No hand-written config or manifest is required.
    ///
    /// Custom contexts are strictly read-only: they expose information about the Unity
    /// project state without modifying it. The registry enforces readOnlyHint=true,
    /// rejects destructive/write semantics, and applies a source guard at registration.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public sealed class UnityCliContextAttribute : Attribute
    {
        public UnityCliContextAttribute(string name)
        {
            Name = name;
            Version = "1.0.0";
            InputSchema = "{}";
            OutputSchema = "{}";
            Category = "custom";
            Target = "editor";
            AvailabilityScope = "editorSession";
            RefreshBehavior = "onDemand";
        }

        /// <summary>
        /// Required context name. Must be unique across built-in and project custom contexts.
        /// </summary>
        public string Name { get; private set; }

        /// <summary>
        /// Human-readable title shown in descriptor output.
        /// </summary>
        public string Title { get; set; }

        /// <summary>
        /// Longer description of what this context provides.
        /// </summary>
        public string Description { get; set; }

        /// <summary>
        /// Category grouping for organizational purposes (e.g. "scene", "asset", "custom").
        /// </summary>
        public string Category { get; set; }

        /// <summary>
        /// Semantic version of the context definition.
        /// </summary>
        public string Version { get; set; }

        /// <summary>
        /// JSON Schema for the context read input. Defaults to "{}" (no input).
        /// </summary>
        public string InputSchema { get; set; }

        /// <summary>
        /// JSON Schema for the context read output. Defaults to "{}".
        /// </summary>
        public string OutputSchema { get; set; }

        /// <summary>
        /// Execution target: "editor" (default), "runtime", or "both".
        /// </summary>
        public string Target { get; set; }

        /// <summary>
        /// Availability scope: "editorSession" (default), "playModeSession", etc.
        /// </summary>
        public string AvailabilityScope { get; set; }

        /// <summary>
        /// How the context refreshes: "onDemand" (default), "periodic", "onEvent".
        /// </summary>
        public string RefreshBehavior { get; set; }

        /// <summary>
        /// Optional resource URI pattern for MCP resource mapping.
        /// </summary>
        public string ResourceUri { get; set; }

        /// <summary>
        /// Comma-separated list of Unity package names this context requires.
        /// </summary>
        public string RequiresPackages { get; set; }

        /// <summary>
        /// Name of the equivalent built-in Unity MCP context, if one exists.
        /// </summary>
        public string EquivalentUnityMcp { get; set; }

        /// <summary>
        /// Note about the equivalent Unity MCP context relationship.
        /// </summary>
        public string EquivalentUnityMcpNote { get; set; }

        /// <summary>
        /// Optional pinned fingerprint for tamper detection. When omitted, the
        /// first computed handler-code + descriptor fingerprint is treated as
        /// the runtime baseline for this Editor session.
        /// </summary>
        public string Fingerprint { get; set; }
    }
}
#endif
