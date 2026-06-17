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
        private static void RegisterDiagnoseCommands(BridgeCommandRegistry registry)
        {
            RegisterCommand(registry, "diagnose.preflight", "diagnose", false, delegate(BridgeRequest request) { return SuccessJson(DiagnosePreflightJson(request)); });
        }

        private static string DiagnosePreflightJson(BridgeRequest request)
        {
            DiscoverProjectTools();
            return new UnityCliJsonWriter().BeginObject()
                .Prop("command", "diagnose.preflight")
                .Prop("targetCommand", request.StringArg("command"))
                .PropObject("project", w =>
                {
                    w.Prop("path", ProjectRoot())
                        .Prop("unityVersion", Application.unityVersion)
                        .Prop("activeScene", SceneManager.GetActiveScene().path);
                })
                .PropRaw("editor", EditorStateJson())
                .PropObject("bridge", w =>
                {
                    w.Prop("port", (long)port)
                        .Prop("logPath", LogPath())
                        .PropRaw("capabilities", CapabilitiesJson());
                })
                .PropObject("customTools", w =>
                {
                    w.Prop("fingerprint", ProjectToolFingerprintSummaryJson());
                })
                .PropArray("checks", arr =>
                {
                    arr.RawValue(DiagnoseCheckJson("unityNotBusy", !(EditorApplication.isCompiling || EditorApplication.isUpdating || BuildPipeline.isBuildingPlayer)));
                    arr.RawValue(DiagnoseCheckJson("sceneLoaded", SceneManager.GetActiveScene().isLoaded));
                    arr.RawValue(DiagnoseCheckJson("projectToolFingerprint", !ProjectTools.Values.Any(definition => definition.FingerprintStatus == "blocked")));
                })
                .Prop("logPath", LogPath())
                .EndObject().ToString();
        }


        private static string DiagnoseCheckJson(string name, bool ok)
        {
            return new UnityCliJsonWriter().BeginObject()
                .Prop("name", name)
                .Prop("ok", ok)
                .EndObject().ToString();
        }
    }
}
#endif
