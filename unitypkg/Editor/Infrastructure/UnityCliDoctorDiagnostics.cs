#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        private const string DoctorBusinessCommand = "internal.doctor.business";
        private const string DoctorCatalogCommand = "internal.doctor.catalog";
        private const string DoctorCompileCommand = "internal.doctor.compile";

        private static void RegisterDoctorDiagnosticCommands(BridgeCommandRegistry registry)
        {
            registry.Register<UnityCli.Protocol.BridgeRequest>("internal.doctor.business", "internal", false, 30000,
                (request, cancellationToken) => new ValueTask<object>(DoctorBusinessDiagnostic(cancellationToken)));
            registry.Register<UnityCli.Protocol.BridgeRequest>("internal.doctor.catalog", "internal", false, 30000,
                (request, cancellationToken) => new ValueTask<object>(DoctorCatalogDiagnostic(cancellationToken)));
            registry.Register<UnityCli.Protocol.BridgeRequest>("internal.doctor.compile", "internal", false, 30000,
                (request, cancellationToken) => new ValueTask<object>(DoctorCompileDiagnostic(cancellationToken)));
        }

        /// <summary>业务诊断必须通过正常 admission 和主线程队列，不能替代 transport 或 pulse 结论。</summary>
        private static object DoctorBusinessDiagnostic(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var scene = SceneManager.GetActiveScene();
            return new Dictionary<string, object>
            {
                { "projectPath", ProjectRoot() },
                { "sceneLoaded", scene.IsValid() && scene.isLoaded },
                { "activeScenePath", scene.path ?? string.Empty },
                { "playMode", EditorApplication.isPlaying },
                { "accepted", true }
            };
        }

        /// <summary>只复制 registration 时冻结的 catalog 摘要；缺失时立即 fail closed。</summary>
        private static object DoctorCatalogDiagnostic(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return CatalogSnapshotReader.Read();
        }

        /// <summary>编译诊断独立于 catalog 和业务 admission，并保持条目、时间与字节上限。</summary>
        private static object DoctorCompileDiagnostic(CancellationToken cancellationToken)
        {
            const int maxScannedEntries = 5000;
            const int maxScanMilliseconds = 50;
            const int maxCompileErrorBytes = 64 * 1024;
            const int maxCompileErrorLineBytes = 4096;
            var scanTimer = System.Diagnostics.Stopwatch.StartNew();
            var compiling = EditorApplication.isCompiling;
            var scanComplete = true;
            var compileErrorCount = 0L;
            var compileErrorsTruncated = false;
            var compileErrorBytes = 0;
            var errors = new List<string>();

            try
            {
                var logEntriesType = FindType("UnityEditor.LogEntries") ?? FindType("UnityEditorInternal.LogEntries");
                if (logEntriesType == null) scanComplete = false;
                else
                {
                    var flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                    var start = logEntriesType.GetMethod("StartGettingEntries", flags);
                    var end = logEntriesType.GetMethod("EndGettingEntries", flags);
                    var count = logEntriesType.GetMethod("GetCount", flags);
                    var getEntry = logEntriesType.GetMethod("GetEntryInternal", flags);
                    var getLines = logEntriesType.GetMethod("GetLinesAndModeFromEntryInternal", flags)
                        ?? logEntriesType.GetMethod("GetLinesAndModeInternal", flags);
                    if ((start == null && count == null) || (getEntry == null && getLines == null)) scanComplete = false;
                    var total = start != null
                        ? Convert.ToInt32(start.Invoke(null, Type.EmptyTypes), CultureInfo.InvariantCulture)
                        : count == null ? 0 : Convert.ToInt32(count.Invoke(null, Type.EmptyTypes), CultureInfo.InvariantCulture);
                    try
                    {
                        var scannedEntries = 0;
                        for (var index = total - 1; index >= 0; index--)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (scannedEntries >= maxScannedEntries || scanTimer.ElapsedMilliseconds >= maxScanMilliseconds)
                            {
                                scanComplete = false;
                                compileErrorsTruncated = true;
                                break;
                            }
                            scannedEntries++;
                            string line = null;
                            var mode = 0;
                            try
                            {
                                if (getLines != null && getLines.GetParameters().Length == 4)
                                {
                                    var args = new object[] { index, 1, 0, null };
                                    getLines.Invoke(null, args);
                                    mode = Convert.ToInt32(args[2], CultureInfo.InvariantCulture);
                                    line = args[3] as string;
                                }
                                else if (getEntry != null && getEntry.GetParameters().Length == 4)
                                {
                                    var args = new object[] { index, null, null, null };
                                    getEntry.Invoke(null, args);
                                    line = args[1] as string;
                                    mode = args[2] == null ? 0 : Convert.ToInt32(args[2], CultureInfo.InvariantCulture);
                                }
                                else if (getLines != null && getLines.GetParameters().Length == 2)
                                {
                                    var args = new object[] { index, null };
                                    getLines.Invoke(null, args);
                                    line = args[1] as string;
                                }
                            }
                            catch { scanComplete = false; }
                            if (string.IsNullOrEmpty(line)) { scanComplete = false; continue; }
                            var compilerText = line.IndexOf("CS", StringComparison.OrdinalIgnoreCase) >= 0
                                && line.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0;
                            if (!compilerText && ((mode & 1) == 0 || line.IndexOf("Compile", StringComparison.OrdinalIgnoreCase) < 0)) continue;

                            compileErrorCount++;
                            var boundedLine = DoctorBoundedUtf8(line, maxCompileErrorLineBytes);
                            var boundedBytes = Encoding.UTF8.GetByteCount(boundedLine);
                            if (errors.Count < 20 && compileErrorBytes + boundedBytes <= maxCompileErrorBytes)
                            {
                                errors.Add(boundedLine);
                                compileErrorBytes += boundedBytes;
                                if (boundedLine.Length != line.Length) compileErrorsTruncated = true;
                            }
                            else compileErrorsTruncated = true;
                        }
                    }
                    finally
                    {
                        if (start != null && end != null) end.Invoke(null, Type.EmptyTypes);
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                scanComplete = false;
                var readFailure = "Failed to read compile errors: " + exception.GetType().Name;
                if (errors.Count < 20 && compileErrorBytes + Encoding.UTF8.GetByteCount(readFailure) <= maxCompileErrorBytes) errors.Insert(0, readFailure);
                else compileErrorsTruncated = true;
            }

            if (!scanComplete) compileErrorsTruncated = true;
            var nextActions = new List<string>();
            if (!scanComplete) nextActions.Add("Inspect the Unity Console because compile diagnostics could not be scanned completely.");
            if (compileErrorCount > 0) nextActions.Add("Fix the listed compiler errors, then let Unity finish recompiling.");
            if (compiling) nextActions.Add("Wait for Unity to finish compiling, then run doctor again.");
            return new Dictionary<string, object>
            {
                { "compiling", compiling },
                { "scanComplete", scanComplete },
                { "hasCompileErrors", compileErrorCount > 0 },
                { "compileErrorCount", compileErrorCount },
                { "compileErrorsTruncated", compileErrorsTruncated },
                { "compileErrors", errors },
                { "nextActions", nextActions }
            };
        }

        private static string DoctorBoundedUtf8(string value, int maxBytes)
        {
            if (string.IsNullOrEmpty(value) || Encoding.UTF8.GetByteCount(value) <= maxBytes) return value ?? string.Empty;
            var length = Math.Min(value.Length, maxBytes);
            while (length > 0 && Encoding.UTF8.GetByteCount(value.Substring(0, length)) > maxBytes) length--;
            if (length > 0 && char.IsHighSurrogate(value[length - 1])) length--;
            return value.Substring(0, length);
        }

        private static bool IsDoctorDiagnosticCommand(string command)
        {
            return string.Equals(command, DoctorBusinessCommand, StringComparison.Ordinal)
                || string.Equals(command, DoctorCatalogCommand, StringComparison.Ordinal)
                || string.Equals(command, DoctorCompileCommand, StringComparison.Ordinal);
        }

        private static bool IsDoctorAdmissionBypassCommand(string command)
        {
            return string.Equals(command, DoctorCatalogCommand, StringComparison.Ordinal)
                || string.Equals(command, DoctorCompileCommand, StringComparison.Ordinal);
        }
    }
}
#endif
