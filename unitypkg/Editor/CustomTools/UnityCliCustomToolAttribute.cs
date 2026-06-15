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
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class UnityCliCustomToolAttribute : Attribute
    {
        public UnityCliCustomToolAttribute(string name)
        {
            Name = name;
            Version = "1.0.0";
            InputSchema = "{}";
            OutputSchema = "{}";
            CapabilityType = "project-write";
            DefaultTimeoutMs = 30000;
            MaxTimeoutMs = 600000;
            SupportsJob = true;
            ArtifactKinds = "json,log,file,image";
        }

        public string Name { get; private set; }
        public string DisplayName { get; set; }
        public string Description { get; set; }
        public string Version { get; set; }
        public string InputSchema { get; set; }
        public string OutputSchema { get; set; }
        public string CapabilityType { get; set; }
        public int DefaultTimeoutMs { get; set; }
        public int MaxTimeoutMs { get; set; }
        public bool SupportsJob { get; set; }
        public string ArtifactKinds { get; set; }
        public string RequiredInput { get; set; }
    }
}
#endif
