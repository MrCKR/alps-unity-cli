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

        // 同目录原子替换 registry 文件，避免 heartbeat 原地截断时让并发 CLI 短暂丢失 target。
        private static void WriteRegistryTextAtomically(string path, string content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temporaryPath = path + "." + System.Diagnostics.Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture) + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporaryPath, content, Encoding.UTF8);
                for (var attempt = 0; ; attempt++)
                {
                    try
                    {
                        if (File.Exists(path)) File.Replace(temporaryPath, path, null);
                        else File.Move(temporaryPath, path);
                        return;
                    }
                    catch (IOException) when (attempt < 4)
                    {
                        Thread.Sleep(10 * (attempt + 1));
                    }
                }
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }

        private static void RegisterInstance(string status)
        {
            try
            {
                Directory.CreateDirectory(InstancesDir());
                DeleteSupersededInstanceFiles();
                var instanceData = InstanceData(status);
                WriteRegistryTextAtomically(InstancePath(), UnityCliSerializer.Serialize(instanceData));
                WriteRegistryFile(instanceData);
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

        private static void WriteRegistryFile(Dictionary<string, object> instanceData)
        {
            Directory.CreateDirectory(DataDir());
            var items = new List<object>();
            if (File.Exists(RegistryPath()))
            {
                var registry = UnityCliSerializer.Parse(File.ReadAllText(RegistryPath())) as Dictionary<string, object>;
                object existingInstances;
                var existing = registry != null && registry.TryGetValue("instances", out existingInstances) ? existingInstances as List<object> : null;
                foreach (var item in existing ?? new List<object>())
                {
                    var registration = item as Dictionary<string, object>;
                    if (registration != null && !ShouldRemoveRegistration(registration)) items.Add(registration);
                }
            }
            if (instanceData != null)
            {
                items.Add(instanceData);
            }
            var registryData = new Dictionary<string, object>
            {
                { "version", 1L },
                { "instances", items }
            };
            WriteRegistryTextAtomically(RegistryPath(), UnityCliSerializer.Serialize(registryData));
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

        // 判断结构化注册项是否属于当前项目且已被当前注册取代。
        private static bool ShouldRemoveRegistration(string json)
        {
            return ShouldRemoveRegistration(UnityCliSerializer.Parse(json) as Dictionary<string, object>);
        }

        private static bool ShouldRemoveRegistration(Dictionary<string, object> registration)
        {
            object projectHash;
            if (registration == null || !registration.TryGetValue("projectHash", out projectHash)
                || !string.Equals(projectHash as string, ProjectHash(), StringComparison.Ordinal)) return false;

            var pid = RegistrationPid(registration);
            if (pid.HasValue && pid.Value == System.Diagnostics.Process.GetCurrentProcess().Id) return true;

            var timestamp = RegistrationTimestamp(registration);
            return timestamp.HasValue && DateTime.UtcNow - timestamp.Value.ToUniversalTime() > TimeSpan.FromSeconds(StaleRegistrationSeconds);
        }

        // 从结构化注册项读取 Unity 进程 ID。
        private static int? RegistrationPid(Dictionary<string, object> registration)
        {
            object value;
            if (!registration.TryGetValue("processId", out value)) registration.TryGetValue("pid", out value);
            try { return value == null ? (int?)null : Convert.ToInt32(value, CultureInfo.InvariantCulture); }
            catch (Exception) { return null; }
        }

        // 从结构化注册项读取最近心跳时间。
        private static DateTime? RegistrationTimestamp(Dictionary<string, object> registration)
        {
            object value;
            if (!registration.TryGetValue("lastSeenAt", out value)) registration.TryGetValue("lastHeartbeatAt", out value);
            DateTime timestamp;
            return value is string text && DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out timestamp) ? (DateTime?)timestamp : null;
        }

        private static Dictionary<string, object> InstanceData(string status)
        {
            var now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            var endpoint = "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture);
            return new Dictionary<string, object> {
                { "instanceId", InstanceId() }, { "projectName", Path.GetFileName(ProjectRoot()) }, { "projectPath", ProjectRoot() }, { "projectHash", ProjectHash() },
                { "processId", (long)System.Diagnostics.Process.GetCurrentProcess().Id }, { "pid", (long)System.Diagnostics.Process.GetCurrentProcess().Id }, { "host", "127.0.0.1" }, { "port", (long)port }, { "bridgeEndpoint", endpoint },
                { "unityVersion", Application.unityVersion }, { "packageVersion", BridgeBuildStamp() }, { "packageName", BridgePackageName() }, { "packageAssetPath", BridgePackageAssetPath() }, { "packageResolvedPath", BridgePackageResolvedPath() },
                { "capabilitiesHash", BridgeBuildStamp() }, { "bridgeDiagnosticsFingerprint", BridgeDiagnosticsFingerprint }, { "bridgeListenerId", ListenerId }, { "bridgeAssemblyMvid", AssemblyMvid() }, { "status", status == "healthy" ? "ready" : status },
                { "editorState", EditorStateSummaryData() }, { "playModeState", PlayModeStateData() }, { "runtimeSessionId", currentPlayModeSessionId ?? string.Empty }, { "toolCatalogFingerprint", runtimeToolCatalogFingerprint ?? string.Empty },
                { "registryVersion", runtimeRegistryVersion.ToString(CultureInfo.InvariantCulture) }, { "registeredAt", now }, { "lastSeenAt", now }, { "lastHeartbeatAt", now }
            };
        }

        private static Dictionary<string, object> TargetContextData()
        {
            return new Dictionary<string, object> { { "id", InstanceId() }, { "alias", null }, { "projectPath", ProjectRoot() } };
        }

        private static Dictionary<string, object> EditorStateSummaryData()
        {
            return new Dictionary<string, object> { { "busy", EditorApplication.isCompiling || EditorApplication.isUpdating || BuildPipeline.isBuildingPlayer || domainReloading }, { "compiling", EditorApplication.isCompiling }, { "updating", EditorApplication.isUpdating }, { "building", BuildPipeline.isBuildingPlayer }, { "domainReloading", domainReloading } };
        }

        private static Dictionary<string, object> PlayModeStateData()
        {
            return new Dictionary<string, object> { { "isPlaying", EditorApplication.isPlaying }, { "isPaused", EditorApplication.isPaused }, { "isPlayingOrWillChangePlaymode", EditorApplication.isPlayingOrWillChangePlaymode } };
        }
    }
}
#endif
