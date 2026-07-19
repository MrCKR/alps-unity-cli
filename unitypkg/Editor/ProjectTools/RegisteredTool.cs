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
    internal sealed class RegisteredTool
    {
        public ToolDescriptor Descriptor { get; set; }
        public string Name { get; set; }
        public string Summary { get; set; }
        public string Description { get; set; }
        public string Group { get; set; }
        public string Source { get; set; }
        public string Version { get; set; }
        public string InputSchema { get; set; }
        public string OutputSchema { get; set; }
        public string CapabilityType { get; set; }
        public bool DestructiveHint { get; set; }
        public string[] SideEffects { get; set; }
        public bool SupportsDryRun { get; set; }
        public string[] Permissions { get; set; }
        public string[] RequiresPackages { get; set; }
        public ToolExample[] Examples { get; set; }
        public string Target { get; set; }
        public string AvailabilityScope { get; set; }
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
        public string PinnedFingerprint { get; set; }
        public string FingerprintStatus { get; set; }
        public string FingerprintMode { get; set; }
        public string ExpectedFingerprint { get; set; }
        public string FingerprintMessage { get; set; }
        public string DescriptorSha256 { get; set; }
        public string HandlerSha256 { get; set; }
        public Type RequestType { get; set; }
        public Type ResultType { get; set; }
        public UnityCliToolCompletionContract CompletionContract { get; set; }
        public Func<object, UnityCliProjectToolContext, CancellationToken, ValueTask<object>> InvokeAsync { get; set; }
    }
}
#endif
