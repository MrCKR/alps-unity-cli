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
            return ProjectHash();
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

        private static void RegisterInstance(string status)
        {
            try
            {
                Directory.CreateDirectory(DataDir());
                var existing = File.Exists(RegistryPath()) ? File.ReadAllText(RegistryPath()) : string.Empty;
                var instanceJson = InstanceJson(status);
                var marker = "\"instanceId\":" + Quote(InstanceId());
                var items = new List<string>();
                foreach (Match match in Regex.Matches(existing, "\\{\\s*\\\"instanceId\\\"[\\s\\S]*?\\\"lastHeartbeatAt\\\"\\s*:\\s*\\\"(?:\\\\.|[^\\\"])*\\\"\\s*\\}"))
                {
                    var item = match.Value;
                    if (item.IndexOf(marker, StringComparison.Ordinal) < 0)
                    {
                        items.Add(item);
                    }
                }
                items.Add(instanceJson);
                File.WriteAllText(RegistryPath(), "{\"version\":1,\"instances\":[" + string.Join(",", items.ToArray()) + "]}");
                WriteLog("instance_registered id=" + InstanceId() + " status=" + status + " endpoint=http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + " listenerId=" + ListenerId + " diagnosticsFingerprint=" + BridgeDiagnosticsFingerprint + " assemblyMvid=" + AssemblyMvid() + " packageName=" + BridgePackageName() + " packageAssetPath=" + BridgePackageAssetPath() + " packageResolvedPath=" + BridgePackageResolvedPath());
            }
            catch (Exception exception)
            {
                WriteLog("instance_register_failed " + exception.GetType().Name + ": " + exception.Message);
            }
        }

        private static string InstanceJson(string status)
        {
            var now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            return "{"
                + "\"instanceId\":" + Quote(InstanceId()) + ","
                + "\"projectPath\":" + Quote(ProjectRoot()) + ","
                + "\"projectHash\":" + Quote(ProjectHash()) + ","
                + "\"unityVersion\":" + Quote(Application.unityVersion) + ","
                + "\"packageVersion\":\"com.unity_cli\"," 
                + "\"packageName\":" + Quote(BridgePackageName()) + ","
                + "\"packageAssetPath\":" + Quote(BridgePackageAssetPath()) + ","
                + "\"packageResolvedPath\":" + Quote(BridgePackageResolvedPath()) + ","
                + "\"pid\":" + System.Diagnostics.Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture) + ","
                + "\"bridgeEndpoint\":" + Quote("http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture)) + ","
                + "\"capabilitiesHash\":" + Quote(BridgeBuildStamp()) + ","
                + "\"bridgeDiagnosticsFingerprint\":" + Quote(BridgeDiagnosticsFingerprint) + ","
                + "\"bridgeListenerId\":" + Quote(ListenerId) + ","
                + "\"bridgeAssemblyMvid\":" + Quote(AssemblyMvid()) + ","
                + "\"status\":" + Quote(status) + ","
                + "\"editorState\":{\"playMode\":" + Bool(EditorApplication.isPlaying) + ",\"compiling\":" + Bool(EditorApplication.isCompiling) + ",\"domainReloading\":" + Bool(domainReloading) + "},"
                + "\"registeredAt\":" + Quote(now) + ","
                + "\"lastHeartbeatAt\":" + Quote(now)
                + "}";
        }
    }
}
#endif
