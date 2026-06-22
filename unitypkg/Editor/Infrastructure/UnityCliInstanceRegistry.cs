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
        private static string ProjectRoot()
        {
            return Directory.GetParent(Application.dataPath).FullName;
        }

        private static string InstanceId()
        {
            return "unity-" + ProjectHash() + "-" + System.Diagnostics.Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture) + "-" + InstanceSuffix;
        }

        private static string ProjectHash()
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(ProjectRoot().ToLowerInvariant()));
                return BitConverter.ToString(bytes).Replace("-", string.Empty).Substring(0, 12).ToLowerInvariant();
            }
        }

        private static string DataDir()
        {
            var configured = Environment.GetEnvironmentVariable("UNITY_CLI_DATA_DIR");
            if (!string.IsNullOrEmpty(configured))
            {
                return configured;
            }
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return string.IsNullOrEmpty(localAppData) ? Path.Combine(ProjectRoot(), ".unity_cli") : Path.Combine(localAppData, "UnityCli");
        }

        private static string RegistryPath()
        {
            return Path.Combine(DataDir(), "instances.json");
        }

        private static string InstancesDir()
        {
            return Path.Combine(DataDir(), "instances");
        }

        private static string InstancePath()
        {
            return Path.Combine(InstancesDir(), InstanceId() + ".json");
        }

        private static void RegisterInstance(string status)
        {
            try
            {
                Directory.CreateDirectory(InstancesDir());
                var instanceJson = InstanceJson(status);
                File.WriteAllText(InstancePath(), instanceJson, Encoding.UTF8);
                WriteRegistryFile(instanceJson);
                WriteLog("instance_registered id=" + InstanceId() + " status=" + status + " endpoint=http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + " listenerId=" + ListenerId + " diagnosticsFingerprint=" + BridgeDiagnosticsFingerprint + " assemblyMvid=" + AssemblyMvid() + " packageName=" + BridgePackageName() + " packageAssetPath=" + BridgePackageAssetPath() + " packageResolvedPath=" + BridgePackageResolvedPath());
            }
            catch (Exception exception)
            {
                WriteLog("instance_register_failed " + exception.GetType().Name + ": " + exception.Message);
            }
        }

        private static void DeleteInstanceRegistration()
        {
            try
            {
                if (File.Exists(InstancePath()))
                {
                    File.Delete(InstancePath());
                }
                WriteRegistryFile(null);
                WriteLog("instance_unregistered id=" + InstanceId());
            }
            catch (Exception exception)
            {
                WriteLog("instance_unregister_failed " + exception.GetType().Name + ": " + exception.Message);
            }
        }

        private static void WriteRegistryFile(string instanceJson)
        {
            Directory.CreateDirectory(DataDir());
            var existing = File.Exists(RegistryPath()) ? File.ReadAllText(RegistryPath()) : string.Empty;
            var marker = "\"instanceId\":" + Quote(InstanceId());
            var items = new List<string>();
            foreach (Match match in Regex.Matches(existing, "\\{\\s*\\\"instanceId\\\"[\\s\\S]*?\\\"last(?:Heartbeat|Seen)At\\\"\\s*:\\s*\\\"(?:\\\\.|[^\\\"])*\\\"\\s*\\}"))
            {
                var item = match.Value;
                if (item.IndexOf(marker, StringComparison.Ordinal) < 0)
                {
                    items.Add(item);
                }
            }
            if (!string.IsNullOrEmpty(instanceJson))
            {
                items.Add(instanceJson);
            }
            var w = new UnityCliJsonWriter().BeginObject()
                .Prop("version", 1L)
                .PropArray("instances", arr =>
                {
                    foreach (var item in items)
                    {
                        arr.RawValue(item);
                    }
                })
                .EndObject();
            File.WriteAllText(RegistryPath(), w.ToString(), Encoding.UTF8);
        }

        private static string InstanceJson(string status)
        {
            var now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            var endpoint = "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture);
            return new UnityCliJsonWriter().BeginObject()
                .Prop("instanceId", InstanceId())
                .Prop("projectName", Path.GetFileName(ProjectRoot()))
                .Prop("projectPath", ProjectRoot())
                .Prop("projectHash", ProjectHash())
                .Prop("processId", (long)System.Diagnostics.Process.GetCurrentProcess().Id)
                .Prop("pid", (long)System.Diagnostics.Process.GetCurrentProcess().Id)
                .Prop("host", "127.0.0.1")
                .Prop("port", (long)port)
                .Prop("bridgeEndpoint", endpoint)
                .Prop("unityVersion", Application.unityVersion)
                .Prop("packageVersion", BridgeBuildStamp())
                .Prop("packageName", BridgePackageName())
                .Prop("packageAssetPath", BridgePackageAssetPath())
                .Prop("packageResolvedPath", BridgePackageResolvedPath())
                .Prop("capabilitiesHash", BridgeBuildStamp())
                .Prop("bridgeDiagnosticsFingerprint", BridgeDiagnosticsFingerprint)
                .Prop("bridgeListenerId", ListenerId)
                .Prop("bridgeAssemblyMvid", AssemblyMvid())
                .Prop("status", status == "healthy" ? "ready" : status)
                .PropRaw("editorState", EditorStateSummaryJson())
                .PropRaw("playModeState", PlayModeStateJson())
                .Prop("runtimeSessionId", currentPlayModeSessionId ?? string.Empty)
                .Prop("toolCatalogFingerprint", runtimeToolCatalogFingerprint ?? string.Empty)
                .Prop("registryVersion", runtimeRegistryVersion.ToString(CultureInfo.InvariantCulture))
                .Prop("registeredAt", now)
                .Prop("lastSeenAt", now)
                .Prop("lastHeartbeatAt", now)
                .EndObject().ToString();
        }

        private static string TargetContextJson()
        {
            return new UnityCliJsonWriter().BeginObject()
                .Prop("id", InstanceId())
                .PropNull("alias")
                .Prop("projectPath", ProjectRoot())
                .EndObject().ToString();
        }

        private static string EditorStateSummaryJson()
        {
            return new UnityCliJsonWriter().BeginObject()
                .Prop("busy", EditorApplication.isCompiling || EditorApplication.isUpdating || BuildPipeline.isBuildingPlayer || domainReloading)
                .Prop("compiling", EditorApplication.isCompiling)
                .Prop("updating", EditorApplication.isUpdating)
                .Prop("building", BuildPipeline.isBuildingPlayer)
                .Prop("domainReloading", domainReloading)
                .EndObject().ToString();
        }

        private static string PlayModeStateJson()
        {
            return new UnityCliJsonWriter().BeginObject()
                .Prop("isPlaying", EditorApplication.isPlaying)
                .Prop("isPaused", EditorApplication.isPaused)
                .Prop("isPlayingOrWillChangePlaymode", EditorApplication.isPlayingOrWillChangePlaymode)
                .EndObject().ToString();
        }
    }
}
#endif
