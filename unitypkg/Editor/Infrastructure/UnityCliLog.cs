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
        private static string LogPath()
        {
            return Path.Combine(ProjectRoot(), "Logs", "UnityCliBridge.log");
        }

        private static string RuntimeDiagnosticsPath()
        {
            return Path.Combine(ProjectRoot(), "Logs", "UnityCliBridgeRuntime.json");
        }

        private static void WriteRuntimeLoadDiagnostics(string phase)
        {
            try
            {
                var path = RuntimeDiagnosticsPath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, RuntimeDiagnosticsJson(phase));
                WriteLog("bridge_runtime_loaded phase=" + phase + " listenerId=" + ListenerId + " diagnosticsFingerprint=" + BridgeDiagnosticsFingerprint + " assemblyMvid=" + AssemblyMvid() + " packageName=" + BridgePackageName() + " packageAssetPath=" + BridgePackageAssetPath() + " packageResolvedPath=" + BridgePackageResolvedPath());
            }
            catch (Exception exception)
            {
                WriteLog("bridge_runtime_diagnostics_failed " + exception.GetType().Name + ": " + exception.Message);
            }
        }

        private static string RuntimeDiagnosticsJson(string phase)
        {
            return new UnityCliJsonWriter().BeginObject()
                .Prop("ok", true)
                .Prop("phase", phase)
                .Prop("diagnosticsFingerprint", BridgeDiagnosticsFingerprint)
                .Prop("listenerId", ListenerId)
                .Prop("loadedAt", BridgeLoadedAtUtc.ToString("O", CultureInfo.InvariantCulture))
                .Prop("bridgeLogPath", LogPath())
                .Prop("port", (long)port)
                .PropObject("assembly", w =>
                {
                    w.Prop("fullName", typeof(UnityCliBridge).Assembly.FullName)
                        .Prop("location", AssemblyLocation(typeof(UnityCliBridge).Assembly))
                        .Prop("moduleVersionId", AssemblyMvid());
                })
                .PropObject("package", w =>
                {
                    w.Prop("name", BridgePackageName())
                        .Prop("assetPath", BridgePackageAssetPath())
                        .Prop("resolvedPath", BridgePackageResolvedPath());
                })
                .EndObject().ToString();
        }

        private static void WriteLog(string line)
        {
            try
            {
                var path = LogPath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.AppendAllText(path, DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) + " " + line + Environment.NewLine);
            }
            catch
            {
            }
        }
    }
}
#endif
