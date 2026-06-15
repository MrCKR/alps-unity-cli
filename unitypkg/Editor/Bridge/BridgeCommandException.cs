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
        private sealed class BridgeCommandException : Exception
        {
            public BridgeCommandException(string code, string message, string nextAction = null, bool transient = false, int retryAfterMs = 0) : base(message)
            {
                Code = code;
                NextAction = nextAction;
                Transient = transient;
                RetryAfterMs = retryAfterMs;
            }

            public string Code { get; private set; }
            public string NextAction { get; private set; }
            public bool Transient { get; private set; }
            public int RetryAfterMs { get; private set; }
        }
    }
}
#endif
