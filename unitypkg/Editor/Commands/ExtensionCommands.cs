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
        /// <summary>
        /// Register extension.* internal bridge commands.
        ///
        /// extension.status provides read-only diagnostic of extension availability
        /// based on Unity packages installed in the project. Install/remove are
        /// handled by the CLI command layer and are not exposed as bridge commands.
        /// </summary>
        private static void RegisterExtensionCommands(BridgeCommandRegistry registry)
        {
            RegisterCommand(registry, "extension.status", "extension", false, ExtensionStatus);
        }

        private static string ExtensionStatus(BridgeRequest request)
        {
            // Return basic package availability status for CLI-side extension flows.
            var extensionId = request.StringArg("extensionId") ?? request.StringArg("id");
            var installed = new List<string>();
            var packageAvailability = new Dictionary<string, bool>();

            // Check known optional packages
            var optionalPackages = new[]
            {
                "com.unity.probuilder",
                "com.unity.cinemachine",
                "com.unity.visualeffectgraph",
                "com.unity.render-pipelines.universal",
                "com.unity.render-pipelines.high-definition"
            };

            foreach (var pkg in optionalPackages)
            {
                try
                {
                    var fileInfo = System.IO.Directory.GetFiles("Library/PackageCache", pkg + "@*", System.IO.SearchOption.TopDirectoryOnly);
                    packageAvailability[pkg] = fileInfo.Length > 0;
                }
                catch
                {
                    packageAvailability[pkg] = false;
                }
            }

            var pkgItems = new List<string>();
            foreach (var kv in packageAvailability.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                pkgItems.Add(new UnityCliJsonWriter().BeginObject()
                    .Prop("name", kv.Key)
                    .Prop("available", kv.Value)
                    .EndObject().ToString());
            }
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .Prop("ok", true)
                .PropArray("installed", arr => { foreach (var i in installed) arr.Value(i); })
                .PropArray("packages", arr => { foreach (var item in pkgItems) arr.RawValue(item); })
                .EndObject().ToString());
        }
    }
}
#endif
