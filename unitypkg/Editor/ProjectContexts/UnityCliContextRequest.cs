#if UNITY_EDITOR
using System;

namespace UnityCli
{
    /// <summary>
    /// Request context passed to a custom context read handler.
    /// Analogous to UnityCliProjectToolContext, but tailored for
    /// read-only context operations (no artifact dir, no timeout).
    /// </summary>
    public sealed class UnityCliContextRequest
    {
        /// <summary>
        /// Unique request identifier from the bridge.
        /// </summary>
        public string RequestId { get; set; }

        /// <summary>
        /// Absolute path of the Unity project root.
        /// </summary>
        public string ProjectPath { get; set; }

        /// <summary>
        /// Raw JSON string of the input arguments provided by the caller.
        /// Guaranteed to be a valid JSON object (at minimum "{}").
        /// </summary>
        public string InputJson { get; set; }

        /// <summary>
        /// Path to the bridge log file for diagnostics.
        /// </summary>
        public string LogPath { get; set; }
    }
}
#endif
