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
            DiscoverCustomTools();
            return "{\"command\":\"diagnose.preflight\",\"targetCommand\":" + Quote(request.StringArg("command")) + ",\"project\":{\"path\":" + Quote(ProjectRoot()) + ",\"unityVersion\":" + Quote(Application.unityVersion) + ",\"activeScene\":" + Quote(SceneManager.GetActiveScene().path) + "},\"editor\":" + EditorStateJson() + ",\"bridge\":{\"port\":" + port.ToString(CultureInfo.InvariantCulture) + ",\"logPath\":" + Quote(LogPath()) + ",\"capabilities\":" + CapabilitiesJson() + "},\"customTools\":{\"fingerprint\":" + CustomToolFingerprintSummaryJson() + "},\"checks\":[" + DiagnoseCheckJson("unityNotBusy", !(EditorApplication.isCompiling || EditorApplication.isUpdating || BuildPipeline.isBuildingPlayer)) + "," + DiagnoseCheckJson("sceneLoaded", SceneManager.GetActiveScene().isLoaded) + "," + DiagnoseCheckJson("customToolFingerprint", !CustomTools.Values.Any(definition => definition.FingerprintStatus == "blocked")) + "],\"logPath\":" + Quote(LogPath()) + "}";
        }


        private static string DiagnoseCheckJson(string name, bool ok)
        {
            return "{\"name\":" + Quote(name) + ",\"ok\":" + Bool(ok) + "}";
        }
    }
}
#endif
