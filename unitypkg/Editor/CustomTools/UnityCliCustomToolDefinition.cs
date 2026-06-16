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
    public sealed class UnityCliCustomToolDefinition
    {
        public string Name { get; set; }
        public string DisplayName { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public string Group { get; set; }
        public string Version { get; set; }
        public string InputSchema { get; set; }
        public string OutputSchema { get; set; }
        public string CapabilityType { get; set; }
        public bool ReadOnlyHint { get; set; }
        public bool DestructiveHint { get; set; }
        public bool ArchitectureSensitive { get; set; }
        public string FrozenDecision { get; set; }
        public string[] CoverageScope { get; set; }
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
        public string FingerprintStatus { get; set; }
        public string FingerprintMode { get; set; }
        public string ExpectedFingerprint { get; set; }
        public string FingerprintMessage { get; set; }
        public string DescriptorSha256 { get; set; }
        public string HandlerSha256 { get; set; }
        public Func<UnityCliCustomToolContext, string> Execute { get; set; }
    }
}
#endif
