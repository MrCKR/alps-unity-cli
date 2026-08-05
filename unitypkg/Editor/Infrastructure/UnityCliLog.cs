#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        // Bridge 日志位于项目临时根 Library/UnityCli/，超上限滚动一次，只保留当前与上一段，大小有界。
        private const long BridgeLogMaxBytes = 10L * 1024 * 1024;

        private static string LogPath()
        {
            return Path.Combine(UnityCliTempRoot(), "UnityCliBridge.log");
        }

        private static string RuntimeDiagnosticsPath()
        {
            return Path.Combine(UnityCliTempRoot(), "UnityCliBridgeRuntime.json");
        }

        /// <summary>项目内临时根：Library/UnityCli/（AGENTS.md《临时文件与机制状态》约定的项目临时区）。</summary>
        internal static string UnityCliTempRoot()
        {
            return Path.Combine(ProjectRoot(), "Library", "UnityCli");
        }

        // 写入前超上限则把当前日志滚动为 .old（覆盖上一段），保持总量常数有界。
        private static void RollBridgeLogIfNeeded(string path)
        {
            try
            {
                if (!File.Exists(path) || new FileInfo(path).Length < BridgeLogMaxBytes) return;
                File.Copy(path, path + ".old", true);
                File.WriteAllText(path, string.Empty);
            }
            catch { }
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
                RollBridgeLogIfNeeded(path);
                File.AppendAllText(path, DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) + " " + line + Environment.NewLine);
            }
            catch { }
        }
    }
}
#endif
