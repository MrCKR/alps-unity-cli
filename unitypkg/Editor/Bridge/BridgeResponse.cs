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
    public static partial class UnityCliBridge
    {
        private sealed class ScreenshotResult
        {
            public ScreenshotResult(string path, string format, int width, int height, bool transparent, int quality, string imageBase64, byte[] imageBytes = null, bool inlineBase64Requested = false)
            {
                Path = path;
                Format = format;
                Width = width;
                Height = height;
                Transparent = transparent;
                Quality = quality;
                ImageBase64 = imageBase64;
                ImageBytes = imageBytes;
                InlineBase64Requested = inlineBase64Requested;
            }

            public string Path { get; private set; }
            public string Format { get; private set; }
            public int Width { get; private set; }
            public int Height { get; private set; }
            public bool Transparent { get; private set; }
            public int Quality { get; private set; }
            public string ImageBase64 { get; private set; }
            public byte[] ImageBytes { get; private set; }
            public bool InlineBase64Requested { get; private set; }
        }
    }
}
#endif
