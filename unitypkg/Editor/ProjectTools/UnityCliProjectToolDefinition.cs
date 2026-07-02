#if UNITY_EDITOR
using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CSharp;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using UiButton = UnityEngine.UI.Button;
using UiCanvasScaler = UnityEngine.UI.CanvasScaler;
using UiGraphicRaycaster = UnityEngine.UI.GraphicRaycaster;
using UiImage = UnityEngine.UI.Image;
using UiText = UnityEngine.UI.Text;

namespace UnityCli
{
    public sealed class UnityCliProjectToolDefinition
    {
        public string Name { get; set; }
        public string Summary { get; set; }
        public string Description { get; set; }
        public string Group { get; set; }
        public string Version { get; set; }
        public string InputSchema { get; set; }
        public string OutputSchema { get; set; }
        public string CapabilityType { get; set; }
        public bool DestructiveHint { get; set; }
        /// <summary>
        /// Execution target: "editor", "runtime", or "both".
        /// Determines which invocation path is used: editor-domain
        /// (project-tool.run) or runtime-domain (runtime-tool.run).
        /// </summary>
        public string Target { get; set; }
        /// <summary>
        /// Availability scope for the tool. For Target=runtime tools,
        /// this is "playModeSession", meaning the tool is only available
        /// while a specific Play Mode session is active and becomes
        /// stale when the session changes.
        /// </summary>
        public string AvailabilityScope { get; set; }
        /// <summary>
        /// Play Mode execution semantics exposed to AI callers.
        /// Valid values: supported, runtime-only, session-affecting, editor-only, unknown.
        /// </summary>
        public string PlayModeSupport { get; set; }
        public int DefaultTimeoutMs { get; set; }
        public int MaxTimeoutMs { get; set; }
        public bool SupportsJob { get; set; }
        public string[] ArtifactKinds { get; set; }
        public string[] RequiredInput { get; set; }
        public string RegisteredBy { get; set; }
        public string DescriptorSource { get; set; }
        public string Assembly { get; set; }
        public string Method { get; set; }
        public string Fingerprint { get; set; }
        /// <summary>
        /// The fingerprint pinned by the developer via ToolDescriptor.Fingerprint.
        /// When non-null, this is enforced strictly. When null, the computed
        /// fingerprint is auto-accepted as the expected baseline.
        /// </summary>
        public string PinnedFingerprint { get; set; }
        public string FingerprintStatus { get; set; }
        public string FingerprintMode { get; set; }
        public string ExpectedFingerprint { get; set; }
        public string FingerprintMessage { get; set; }
        public string DescriptorSha256 { get; set; }
        public string HandlerSha256 { get; set; }
        public Func<UnityCliProjectToolContext, string> Execute { get; set; }
    }
}
#endif
