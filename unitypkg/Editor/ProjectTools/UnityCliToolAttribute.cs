#if UNITY_EDITOR
using System;

namespace UnityCli
{
    /// <summary>
    /// Attribute-first custom tool declaration. Project Editor code marks a static C#
    /// method with this attribute; UnityCliBridge discovers it through loaded Editor
    /// assemblies and derives both the runtime registration and descriptor JSON from
    /// the class/method metadata. No hand-written config or manifest is required.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public sealed class UnityCliToolAttribute : Attribute
    {
        public UnityCliToolAttribute(string name)
        {
            Name = name;
            Version = "1.0.0";
            InputSchema = "{}";
            OutputSchema = "{}";
            Group = "custom";
            CapabilityType = "project-write";
            Target = "editor";
            DefaultTimeoutMs = 30000;
            MaxTimeoutMs = 600000;
            SupportsJob = true;
            ArtifactKinds = "json,log,file,image";
        }

        public string Name { get; private set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public string Group { get; set; }
        public string Version { get; set; }
        public string InputSchema { get; set; }
        public string OutputSchema { get; set; }
        public string CapabilityType { get; set; }
        public bool ReadOnlyHint { get; set; }
        public bool DestructiveHint { get; set; }
        public int DefaultTimeoutMs { get; set; }
        public int MaxTimeoutMs { get; set; }
        public bool SupportsJob { get; set; }
        public string ArtifactKinds { get; set; }
        public string RequiredInput { get; set; }
        /// <summary>
        /// Execution target: "editor" (default, runs in Editor domain),
        /// "runtime" (runs only in Play Mode), or "both" (available in both).
        /// Tools with Target="runtime" require an active Play Mode session
        /// and are routed through the RuntimeExecutor.
        /// </summary>
        public string Target { get; set; }

        /// <summary>
        /// Optional pinned fingerprint for tamper detection. When omitted, the
        /// first computed handler-code + descriptor fingerprint is treated as
        /// the runtime baseline for this Editor session.
        /// </summary>
        public string Fingerprint { get; set; }
    }
}
#endif
