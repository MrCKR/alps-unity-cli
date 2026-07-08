#if UNITY_EDITOR
using System;

namespace UnityCli
{
    /// <summary>
    /// Runtime definition for a project custom context. Created by
    /// ProjectContextRegistry during discovery from IUnityCliContext<TReq,TRes>
    /// implementations, and stored in the registry for lookup by context.list/describe/read.
    ///
    /// Custom contexts are always Source=project and must not declare
    /// DestructiveHint, SupportsJob, or artifact kinds.
    /// </summary>
    public sealed class UnityCliContextDefinition
    {
        // --- Core identity ---
        public string Name { get; set; }
        public string Summary { get; set; }
        public string Description { get; set; }
        public string Category { get; set; }
        public string Version { get; set; }

        // --- Schema ---
        public string InputSchema { get; set; }
        public string OutputSchema { get; set; }

        // --- Fixed context descriptor fields ---
        /// <summary>
        /// Always "project" for custom contexts.
        /// </summary>
        public string Source { get; set; }

        // --- Execution ---
        public string Target { get; set; }
        public string AvailabilityScope { get; set; }
        public string PlayModeSupport { get; set; }
        public string RefreshBehavior { get; set; }

        // --- Dependencies ---
        public string[] RequiresPackages { get; set; }

        // --- Registration metadata ---
        public string RegisteredBy { get; set; }
        public string DescriptorSource { get; set; }
        public string Assembly { get; set; }
        public string Method { get; set; }

        // --- Fingerprint ---
        public string Fingerprint { get; set; }
        public string PinnedFingerprint { get; set; }
        public string FingerprintStatus { get; set; }
        public string FingerprintMode { get; set; }
        public string ExpectedFingerprint { get; set; }
        public string FingerprintMessage { get; set; }
        public string DescriptorSha256 { get; set; }
        public string HandlerSha256 { get; set; }

        // --- Handler ---
        /// <summary>
        /// Read handler delegate. Takes the shared project context carrying
        /// requestId/projectPath/inputJson/logPath, returns a JSON string. The
        /// registry enforces that handlers do not perform writes.
        /// </summary>
        public Func<UnityCliProjectToolContext, string> Read { get; set; }
    }
}
#endif
