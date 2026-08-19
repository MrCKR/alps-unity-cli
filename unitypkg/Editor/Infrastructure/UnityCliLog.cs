#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        // Bridge 日志位于项目临时根 Library/UnityCli/，超上限滚动一次，只保留当前与上一段，大小有界。
        private const long BridgeLogMaxBytes = 10L * 1024 * 1024;
        private const int BridgeLogMaxEntryBytes = 64 * 1024;

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

        // current与old始终各不超过10MiB；incoming-aware滚动避免单条追加越过上限。
        private static void RollBridgeLogIfNeeded(string path, int incomingBytes)
        {
            try
            {
                var oldPath = path + ".old";
                TrimBridgeLogSegment(oldPath);
                TrimBridgeLogSegment(path);
                if (!File.Exists(path) || new FileInfo(path).Length + incomingBytes <= BridgeLogMaxBytes) return;
                File.Copy(path, oldPath, true);
                TrimBridgeLogSegment(oldPath);
                File.WriteAllText(path, string.Empty, new UTF8Encoding(false));
            }
            catch { }
        }

        // 对升级前或异常单条形成的oversize段保留最新UTF-8后缀，恢复固定绝对上界。
        private static void TrimBridgeLogSegment(string path)
        {
            if (!File.Exists(path) || new FileInfo(path).Length <= BridgeLogMaxBytes) return;
            var text = File.ReadAllText(path, Encoding.UTF8);
            var low = 0;
            var high = text.Length;
            while (low < high)
            {
                var middle = low + (high - low) / 2;
                if (Encoding.UTF8.GetByteCount(text.Substring(middle)) <= BridgeLogMaxBytes) high = middle;
                else low = middle + 1;
            }
            if (low < text.Length && char.IsLowSurrogate(text[low])) low++;
            File.WriteAllText(path, text.Substring(low), new UTF8Encoding(false));
        }

        // 单条日志使用固定UTF-8预算，禁止异常文本或输入派生消息制造无界段。
        private static string BoundedBridgeLogLine(string value, int maxBytes)
        {
            value = value ?? string.Empty;
            if (Encoding.UTF8.GetByteCount(value) <= maxBytes) return value;
            var low = 0;
            var high = Math.Min(value.Length, maxBytes);
            while (low < high)
            {
                var middle = low + (high - low + 1) / 2;
                if (Encoding.UTF8.GetByteCount(value.Substring(0, middle)) <= maxBytes) low = middle;
                else high = middle - 1;
            }
            if (low > 0 && char.IsHighSurrogate(value[low - 1])) low--;
            return value.Substring(0, low);
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
                var prefix = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) + " ";
                var suffix = Environment.NewLine;
                var lineBudget = BridgeLogMaxEntryBytes - Encoding.UTF8.GetByteCount(prefix + suffix);
                var entry = prefix + BoundedBridgeLogLine(line, lineBudget) + suffix;
                var incomingBytes = Encoding.UTF8.GetByteCount(entry);
                RollBridgeLogIfNeeded(path, incomingBytes);
                File.AppendAllText(path, entry, new UTF8Encoding(false));
            }
            catch { }
        }
    }
}
#endif
