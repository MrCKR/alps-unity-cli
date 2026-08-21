#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace UnityCli
{
    /// <summary>只为Windows Development Player写入固定runtime sidecar，不修改Assets或ProjectSettings。</summary>
    internal sealed class UnityCliRuntimeBuildInjection : IPostprocessBuildWithReport
    {
        public int callbackOrder { get { return 10000; } }

        public void OnPostprocessBuild(BuildReport report)
        {
            if (report == null || report.summary.platform == BuildTarget.NoTarget) return;
            var output = report.summary.outputPath;
            var sidecar = RuntimeConfigPath(output, report.summary.platform);
            if (string.IsNullOrEmpty(sidecar)) return;
            if (!IsWindowsStandalone(report.summary.platform) || !IsDevelopmentBuild(report))
            {
                DeleteExactSidecar(sidecar);
                return;
            }

            var identity = UnityCliCanonicalProjectPath.Resolve(Application.dataPath);
            if (!identity.Success) throw new BuildFailedException(identity.ErrorCode + ": " + identity.ErrorMessage);
            var buildId = ReadBuildId(report);
            if (string.IsNullOrEmpty(buildId)) throw new BuildFailedException("UnityCli runtime sidecar requires a stable build GUID.");
            var settings = UnityCliProjectSettings.Current;
            var document = new Dictionary<string, object>
            {
                { "version", 2L },
                { "canonicalizationVersion", (long)UnityCliCanonicalProjectPath.CanonicalizationVersion },
                { "instanceId", identity.InstanceId },
                { "canonicalProjectPath", identity.CanonicalProjectPath },
                { "displayProjectPath", identity.DisplayProjectPath },
                { "buildId", buildId }, { "buildGuid", buildId },
                { "buildStamp", UnityCliBridgeBuildInfo.BuildStamp },
                { "taskPoolMaxActive", (long)settings.TaskPoolMaxActive }, { "taskPoolReopenAt", (long)settings.TaskPoolReopenAt },
                { "unityVersion", Application.unityVersion }, { "developmentBuild", true },
                { "generatedAt", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) }
            };
            WriteAtomically(sidecar, UnityCliSerializer.Serialize(document) + "\n");
        }

        private static bool IsWindowsStandalone(BuildTarget target)
        {
            return target == BuildTarget.StandaloneWindows || target == BuildTarget.StandaloneWindows64;
        }

        private static bool IsDevelopmentBuild(BuildReport report)
        {
            return report.summary.options.HasFlag(BuildOptions.Development);
        }

        /// <summary>只推导Unity给出的exe output下固定_Data目录，禁止接受任意调用方路径。</summary>
        private static string RuntimeConfigPath(string outputPath, BuildTarget target)
        {
            if (!IsWindowsStandalone(target) || string.IsNullOrWhiteSpace(outputPath)) return null;
            var fullOutput = Path.GetFullPath(outputPath);
            var directory = Path.GetDirectoryName(fullOutput);
            var executable = Path.GetFileNameWithoutExtension(fullOutput);
            if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(executable)) throw new BuildFailedException("UnityCli runtime sidecar could not derive the Windows Player data directory.");
            return Path.Combine(directory, executable + "_Data", "UnityCli", "runtime-config.json");
        }

        private static string ReadBuildId(BuildReport report)
        {
            try
            {
                var value = report.summary.guid.ToString();
                return IsHex(value, 32) ? value.ToLowerInvariant() : string.Empty;
            }
            catch { return string.Empty; }
        }

        private static bool IsHex(string value, int length)
        {
            if (string.IsNullOrEmpty(value) || value.Length != length) return false;
            foreach (var character in value)
                if (!((character >= '0' && character <= '9') || (character >= 'a' && character <= 'f') || (character >= 'A' && character <= 'F'))) return false;
            return true;
        }

        private static void DeleteExactSidecar(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
                var directory = Path.GetDirectoryName(path);
                if (Directory.Exists(directory) && Directory.GetFileSystemEntries(directory).Length == 0) Directory.Delete(directory);
            }
            catch (Exception exception) { throw new BuildFailedException("UnityCli runtime sidecar cleanup failed: " + exception.GetType().Name); }
        }

        /// <summary>sidecar使用固定临时名原子替换，失败时不留下请求级临时文件。</summary>
        private static void WriteAtomically(string path, string content)
        {
            var directory = Path.GetDirectoryName(path);
            Directory.CreateDirectory(directory);
            var temporary = path + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    writer.Write(content); writer.Flush(); stream.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
#endif
