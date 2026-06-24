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

        // 实例 ID 对同一 Unity 进程保持稳定，domain reload 只通过 bridgeListenerId 区分。
        private static string InstanceId()
        {
            return "unity-" + ProjectHash() + "-" + System.Diagnostics.Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture);
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
                DeleteSupersededInstanceFiles();
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
            var items = new List<string>();
            foreach (Match match in Regex.Matches(existing, "\\{\\s*\\\"instanceId\\\"[\\s\\S]*?\\\"last(?:Heartbeat|Seen)At\\\"\\s*:\\s*\\\"(?:\\\\.|[^\\\"])*\\\"\\s*\\}"))
            {
                var item = match.Value;
                if (!ShouldRemoveRegistration(item))
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

        private const double StaleRegistrationSeconds = 30.0;

        // 清理同项目的旧注册：当前进程旧随机文件直接移除，心跳过期的历史文件也移除。
        private static void DeleteSupersededInstanceFiles()
        {
            if (!Directory.Exists(InstancesDir()))
            {
                return;
            }

            foreach (var path in Directory.GetFiles(InstancesDir(), "*.json"))
            {
                if (string.Equals(path, InstancePath(), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    var json = File.ReadAllText(path);
                    if (ShouldRemoveRegistration(json))
                    {
                        File.Delete(path);
                    }
                }
                catch (Exception exception)
                {
                    WriteLog("instance_cleanup_failed " + Path.GetFileName(path) + " " + exception.GetType().Name + ": " + exception.Message);
                }
            }
        }

        // 判断注册 JSON 是否属于当前项目且已被当前注册取代。
        private static bool ShouldRemoveRegistration(string json)
        {
            if (!Regex.IsMatch(json, "\\\"projectHash\\\"\\s*:\\s*" + Regex.Escape(Quote(ProjectHash()))))
            {
                return false;
            }

            var pid = RegistrationPid(json);
            if (pid.HasValue && pid.Value == System.Diagnostics.Process.GetCurrentProcess().Id)
            {
                return true;
            }

            var timestamp = RegistrationTimestamp(json);
            return timestamp.HasValue && DateTime.UtcNow - timestamp.Value.ToUniversalTime() > TimeSpan.FromSeconds(StaleRegistrationSeconds);
        }

        // 从注册 JSON 中解析 Unity 进程 ID，兼容 processId 与旧 pid 字段。
        private static int? RegistrationPid(string json)
        {
            var match = Regex.Match(json, "\\\"(?:processId|pid)\\\"\\s*:\\s*(\\d+)(?=\\s*[,}])");
            int value;
            return match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ? (int?)value : null;
        }

        // 从注册 JSON 中解析最近心跳时间，优先 lastSeenAt，兼容 lastHeartbeatAt。
        private static DateTime? RegistrationTimestamp(string json)
        {
            var match = Regex.Match(json, "\\\"last(?:Seen|Heartbeat)At\\\"\\s*:\\s*\\\"((?:\\\\.|[^\\\"])*)\\\"");
            DateTime value;
            return match.Success && DateTime.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out value) ? (DateTime?)value : null;
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
