#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

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
                // 诊断文件是实际 artifact 边界，因此在此处进行唯一一次序列化。
                File.WriteAllText(path, UnityCliSerializer.Serialize(RuntimeDiagnosticsData(phase)));
                WriteLog("bridge_runtime_loaded phase=" + phase + " listenerId=" + ListenerId + " diagnosticsFingerprint=" + BridgeDiagnosticsFingerprint + " assemblyMvid=" + AssemblyMvid() + " packageName=" + BridgePackageName() + " packageAssetPath=" + BridgePackageAssetPath() + " packageResolvedPath=" + BridgePackageResolvedPath());
            }
            catch (Exception exception)
            {
                WriteLog("bridge_runtime_diagnostics_failed " + exception.GetType().Name + ": " + exception.Message);
            }
        }

        private static Dictionary<string, object> RuntimeDiagnosticsData(string phase)
        {
            return new Dictionary<string, object>
            {
                { "ok", true }, { "phase", phase }, { "diagnosticsFingerprint", BridgeDiagnosticsFingerprint }, { "listenerId", ListenerId },
                { "loadedAt", BridgeLoadedAtUtc.ToString("O", CultureInfo.InvariantCulture) }, { "bridgeLogPath", LogPath() }, { "port", (long)port },
                { "assembly", new Dictionary<string, object> { { "fullName", typeof(UnityCliBridge).Assembly.FullName }, { "location", AssemblyLocation(typeof(UnityCliBridge).Assembly) }, { "moduleVersionId", AssemblyMvid() } } },
                { "package", new Dictionary<string, object> { { "name", BridgePackageName() }, { "assetPath", BridgePackageAssetPath() }, { "resolvedPath", BridgePackageResolvedPath() } } }
            };
        }

        private static void WriteLog(string line)
        {
            try
            {
                var path = LogPath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.AppendAllText(path, DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) + " " + line + Environment.NewLine);
            }
            catch { }
        }
    }
}
#endif
