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
        private sealed class TargetResolution
        {
            public readonly GameObject Target;
            public readonly List<GameObject> Matches;

            private TargetResolution(List<GameObject> matches)
            {
                Matches = matches;
                Target = matches.Count == 1 ? matches[0] : null;
            }

            public bool Ambiguous
            {
                get { return Matches.Count > 1; }
            }

            public static TargetResolution Single(GameObject target)
            {
                var matches = new List<GameObject>();
                if (target != null)
                {
                    matches.Add(target);
                }
                return new TargetResolution(matches);
            }

            public static TargetResolution FromMatches(List<GameObject> matches)
            {
                return new TargetResolution(matches);
            }
        }
    }
}
#endif
