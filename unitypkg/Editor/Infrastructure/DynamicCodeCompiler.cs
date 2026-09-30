#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace UnityCli
{
    /// <summary>
    /// 共享动态编译请求。源码文本必须是完整翻译单元；固定文件名让同一时刻只存在一套临时产物。
    /// </summary>
    internal sealed class DynamicCodeCompileRequest
    {
        internal string SourceText;
        internal string SourceFileName = "source.cs";
        internal string ResponseFileName = "references.rsp";
        internal string AssemblyFileName = "assembly.dll";
        internal string PdbFileName = "assembly.pdb";
        internal string AssemblySimpleName;
        internal string Defines;
        internal List<string> ExtraReferences = new List<string>();
        /// <summary>源码 UTF-8 字节上限；0 表示不检查。</summary>
        internal int MaxSourceBytes;
        /// <summary>产物字节上限；0 表示不检查。</summary>
        internal long MaxArtifactBytes;
    }

    /// <summary>
    /// 共享动态编译结果。成功只携带内存 bytes；诊断保持既有结构化形状供各调用方自行映射错误码。
    /// </summary>
    internal sealed class DynamicCodeCompileResult
    {
        internal bool Success;
        internal bool TimedOut;
        internal bool Busy;
        internal string FailureCode;
        internal string FailureMessage;
        internal byte[] AssemblyBytes;
        internal List<Dictionary<string, object>> Diagnostics = new List<Dictionary<string, object>>();
    }

    /// <summary>
    /// 动态 C# 编译的唯一 owner：引用发现、固定临时文件、Unity 自带 mono/csc 子进程、有界诊断与 assembly bytes。
    ///
    /// code.execute、公开 hook mutation 与 hook 生命周期恢复都必须经由此处编译，因此同一时刻至多一个 active compiler：
    /// 调用方先 TryBeginCompilation 成功才允许进入，繁忙时必须自行延后或返回忙错误，不在此队列里无限堆积。
    /// 进程与等待都服从调用方传入的不可续期 deadline；超时、取消、reload、quitting 都会终止子进程。
    /// 输出文件固定命名并在读取 bytes 后 finally 删除，不按 hash 累积目录。
    /// </summary>
    internal static class DynamicCodeCompiler
    {
        internal const int MaxCompilerOutputBytes = 512 * 1024;
        internal const int MaxCompilerDiagnostics = 100;
        internal const int MaxCompileSeconds = 60;

        private static readonly object CompileGate = new object();
        private static bool compiling;

        /// <summary>当前是否有 active compiler；只用于诊断与延后判断。</summary>
        internal static bool IsCompiling { get { lock (CompileGate) return compiling; } }

        /// <summary>取得唯一 active compiler 所有权；失败表示必须延后本次编译。</summary>
        internal static bool TryBeginCompilation()
        {
            lock (CompileGate)
            {
                if (compiling) return false;
                compiling = true;
                return true;
            }
        }

        /// <summary>释放 active compiler 所有权；必须在 finally 中调用。</summary>
        internal static void EndCompilation()
        {
            lock (CompileGate) compiling = false;
        }

        /// <summary>项目内固定编译临时根；不按请求或版本建立新目录。</summary>
        internal static string TemporaryRoot()
        {
            return Path.Combine(UnityCliBridge.ProjectRoot(), "Library", "UnityCli", "code-exec-temp");
        }

        /// <summary>单次编译最多清理的残留文件数，避免异常目录把清理放大。</summary>
        private const int MaxStaleCleanupFiles = 64;

        /// <summary>编译临时目录只允许存在当前这一次编译的固定文件；开工前清掉残留。有界扫描，失败不阻断编译。</summary>
        private static void CleanupStaleCompilationFiles(string temporaryRoot)
        {
            try
            {
                var removed = 0;
                foreach (var file in Directory.GetFiles(temporaryRoot))
                {
                    if (removed >= MaxStaleCleanupFiles) break;
                    var extension = Path.GetExtension(file);
                    if (!string.Equals(extension, ".cs", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(extension, ".dll", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(extension, ".pdb", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(extension, ".rsp", StringComparison.OrdinalIgnoreCase)) continue;
                    // 带重试的删除，处理上一次编译的子进程尚未完全释放句柄的情况。
                    UnityCliBridge.DeleteTemporaryCodeFileWithRetry(file);
                    removed++;
                }
            }
            catch (Exception exception)
            {
                UnityCliBridge.WriteLog("dynamic_code_cleanup_failed " + exception.GetType().Name + ": " + exception.Message);
            }
        }

        /// <summary>
        /// 汇总 Unity Editor 编译器所需引用；全包含 AppDomain 已加载程序集，
        /// 不排除 mscorlib/System*/netstandard，避免项目程序集缺少核心库引用。
        /// deadlineOk 返回 false 时抛出 OperationCanceledException，由调用方映射为超时。
        /// </summary>
        internal static List<string> CollectReferences(string refsCsv, List<string> extraReferences, Func<bool> deadlineOk)
        {
            var references = new List<string>();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!deadlineOk()) throw new OperationCanceledException("Dynamic code reference discovery exceeded the immutable deadline.");
                if (assembly.IsDynamic) continue;
                var location = assembly.Location;
                if (string.IsNullOrEmpty(location)) continue;
                AddReference(references, location);
            }
            foreach (var reference in UnityCliBridge.SplitCsv(refsCsv)) AddReference(references, reference);
            if (extraReferences != null) foreach (var reference in extraReferences) AddReference(references, reference);
            if (!deadlineOk()) throw new OperationCanceledException("Dynamic code reference discovery exceeded the immutable deadline.");
            return references;
        }

        /// <summary>向引用列表添加非空且不重复的路径。</summary>
        private static void AddReference(List<string> references, string reference)
        {
            if (string.IsNullOrEmpty(reference)) return;
            if (!references.Contains(reference)) references.Add(reference);
        }

        /// <summary>
        /// 编译一段完整翻译单元。调用方负责先取得 active compiler 所有权，并在失败时映射自己的稳定错误码。
        /// </summary>
        internal static async ValueTask<DynamicCodeCompileResult> CompileAsync(
            DynamicCodeCompileRequest compile,
            List<string> references,
            Func<bool> deadlineOk,
            IUnityCliWaiter waiter,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            var result = new DynamicCodeCompileResult();
            if (compile == null || string.IsNullOrWhiteSpace(compile.SourceText))
            {
                result.FailureCode = "E_CODE_SOURCE_INVALID";
                result.FailureMessage = "Dynamic code source is empty.";
                return result;
            }
            if (compile.MaxSourceBytes > 0 && Encoding.UTF8.GetByteCount(compile.SourceText) > compile.MaxSourceBytes)
            {
                result.FailureCode = "E_HOOK_LIMIT_EXCEEDED";
                result.FailureMessage = "Dynamic code source exceeds the declared UTF-8 byte budget.";
                return result;
            }
            var editorDataPath = EditorDataPath();
            if (string.IsNullOrEmpty(editorDataPath))
            {
                result.Diagnostics.Add(Diagnostic("E_REFERENCE_RESOLUTION_FAILED", "Unity Editor Data directory was not found."));
                return result;
            }

            var temporaryRoot = TemporaryRoot();
            Directory.CreateDirectory(temporaryRoot);
            // 自愈：上一次编译在 Windows 句柄未释放时可能删除失败，这里在开工前清空固定临时目录，保证数量有界。
            CleanupStaleCompilationFiles(temporaryRoot);
            var sourceFilePath = Path.Combine(temporaryRoot, compile.SourceFileName);
            var responseFilePath = Path.Combine(temporaryRoot, compile.ResponseFileName);
            var assemblyFilePath = Path.Combine(temporaryRoot, compile.AssemblyFileName);
            var pdbFilePath = Path.Combine(temporaryRoot, compile.PdbFileName);
            try
            {
                File.WriteAllText(sourceFilePath, compile.SourceText, new UTF8Encoding(true));
                WriteReferenceResponseFile(responseFilePath, references);
                var compilation = await RunUnityCompilerAsync(editorDataPath, sourceFilePath, responseFilePath, assemblyFilePath, compile.Defines, deadlineOk, waiter, timeout, cancellationToken);
                if (compilation.TimedOut)
                {
                    result.TimedOut = true;
                    return result;
                }
                var diagnostics = RoslynDiagnostics(compilation.Output);
                if (compilation.ExitCode != 0 || diagnostics.Any(item => string.Equals((string)item["severity"], "error", StringComparison.Ordinal)))
                {
                    result.Diagnostics = diagnostics.Where(item => string.Equals((string)item["severity"], "error", StringComparison.Ordinal)).ToList();
                    return result;
                }
                var bytes = File.ReadAllBytes(assemblyFilePath);
                if (compile.MaxArtifactBytes > 0 && bytes.LongLength > compile.MaxArtifactBytes)
                {
                    result.FailureCode = "E_HOOK_LIMIT_EXCEEDED";
                    result.FailureMessage = "Compiled artifact exceeds the declared byte budget.";
                    return result;
                }
                result.AssemblyBytes = bytes;
                result.Success = true;
                return result;
            }
            finally
            {
                UnityCliBridge.DeleteTemporaryCodeFileWithRetry(sourceFilePath);
                UnityCliBridge.DeleteTemporaryCodeFileWithRetry(responseFilePath);
                UnityCliBridge.DeleteTemporaryCodeFileWithRetry(assemblyFilePath);
                UnityCliBridge.DeleteTemporaryCodeFileWithRetry(pdbFilePath);
            }
        }

        /// <summary>
        /// 使用 Unity 自带 mono/csc 编译；子进程等待通过框架 scheduler 推进，
        /// 因此不占用主线程，也不新增第二个 update 订阅。deadline 到期即终止编译器。
        /// </summary>
        private static async ValueTask<UnityCliBridge.RoslynCompilationHandle> RunUnityCompilerAsync(
            string editorDataPath,
            string sourceFilePath,
            string responseFilePath,
            string assemblyFilePath,
            string definesCsv,
            Func<bool> deadlineOk,
            IUnityCliWaiter waiter,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            var monoPath = Path.Combine(editorDataPath, "MonoBleedingEdge", "bin", "mono.exe");
            var cscPath = Path.Combine(editorDataPath, "MonoBleedingEdge", "lib", "mono", "4.5", "csc.exe");
            var arguments = UnityCliBridge.QuoteProcessArgument(cscPath) + " /nologo /noconfig /nostdlib /utf8output /target:library /langversion:latest /out:" + UnityCliBridge.QuoteProcessArgument(assemblyFilePath) + " @" + UnityCliBridge.QuoteProcessArgument(responseFilePath);
            foreach (var define in UnityCliBridge.SplitCsv(definesCsv)) arguments += " /define:" + define;
            arguments += " " + UnityCliBridge.QuoteProcessArgument(sourceFilePath);
            var startInfo = new ProcessStartInfo
            {
                FileName = monoPath,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                CreateNoWindow = true
            };
            var process = Process.Start(startInfo);
            if (process == null) throw new InvalidOperationException("Unity-bundled Roslyn compiler did not start.");
            var outputBudget = new UnityCliBridge.Utf8ByteBudget(MaxCompilerOutputBytes);
            var stdoutTask = UnityCliBridge.ReadBoundedToEndAsync(process.StandardOutput, outputBudget);
            var stderrTask = UnityCliBridge.ReadBoundedToEndAsync(process.StandardError, outputBudget);
            var compileLimit = TimeSpan.FromSeconds(MaxCompileSeconds);
            var waitBudget = timeout < compileLimit ? timeout : compileLimit;
            if (waitBudget <= TimeSpan.Zero) waitBudget = TimeSpan.FromMilliseconds(1);
            try
            {
                var exited = await waiter.Until(() => HasExited(process), waitBudget, cancellationToken);
                if (!exited || !deadlineOk())
                {
                    TerminateCompiler(process, stdoutTask, stderrTask);
                    return new UnityCliBridge.RoslynCompilationHandle { TimedOut = true, Output = string.Empty };
                }
                if (!Task.WaitAll(new Task[] { stdoutTask, stderrTask }, TimeSpan.FromSeconds(5)))
                {
                    TerminateCompiler(process, stdoutTask, stderrTask);
                    return new UnityCliBridge.RoslynCompilationHandle { TimedOut = true, Output = string.Empty };
                }
                return new UnityCliBridge.RoslynCompilationHandle
                {
                    ExitCode = process.ExitCode,
                    Output = stdoutTask.Result + Environment.NewLine + stderrTask.Result
                };
            }
            catch
            {
                TerminateCompiler(process, stdoutTask, stderrTask);
                throw;
            }
            finally
            {
                try { process.Dispose(); } catch { }
            }
        }

        /// <summary>进程退出判断必须是廉价且不抛异常的谓词，供主线程 tick 复用。</summary>
        private static bool HasExited(Process process)
        {
            try { return process.HasExited; }
            catch { return true; }
        }

        /// <summary>deadline 或取消到达后立即终止 compiler，并以短固定窗口回收进程与管道。</summary>
        private static void TerminateCompiler(Process process, params Task[] outputTasks)
        {
            try { if (process != null && !process.HasExited) process.Kill(); } catch { }
            try { if (process != null) process.WaitForExit(1000); } catch { }
            try { Task.WaitAll(outputTasks, 1000); } catch { }
        }

        /// <summary>写入 Roslyn 引用 response file，避免 Windows 命令行长度限制。</summary>
        private static void WriteReferenceResponseFile(string responseFilePath, IEnumerable<string> references)
        {
            using (var writer = new StreamWriter(responseFilePath, false, new UTF8Encoding(false)))
                foreach (var reference in references ?? Enumerable.Empty<string>()) writer.WriteLine("/r:\"" + reference + "\"");
        }

        /// <summary>解析 Roslyn 标准诊断行，无法解析的输出不作为协议诊断公开。</summary>
        internal static List<Dictionary<string, object>> RoslynDiagnostics(string output)
        {
            var diagnostics = new List<Dictionary<string, object>>();
            var pattern = new Regex("^(.*)\\((\\d+),(\\d+)\\):\\s*(error|warning)\\s+(CS\\d+):\\s*(.*)$", RegexOptions.IgnoreCase);
            foreach (var line in (output ?? string.Empty).Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                var match = pattern.Match(line);
                if (!match.Success) continue;
                if (diagnostics.Count >= MaxCompilerDiagnostics) continue;
                diagnostics.Add(new Dictionary<string, object>
                {
                    { "severity", match.Groups[4].Value.ToLowerInvariant() }, { "id", UnityCliBridge.BoundedText(match.Groups[5].Value, 64) },
                    { "message", UnityCliBridge.BoundedText(match.Groups[6].Value, 4096) }, { "file", UnityCliBridge.BoundedText(match.Groups[1].Value, 2048) },
                    { "line", long.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) },
                    { "column", long.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture) }, { "span", null }
                });
            }
            return diagnostics;
        }

        /// <summary>构造编译器侧的结构化诊断条目。</summary>
        internal static Dictionary<string, object> Diagnostic(string id, string message)
        {
            return new Dictionary<string, object>
            {
                { "severity", "error" }, { "id", id }, { "message", message },
                { "file", string.Empty }, { "line", 0L }, { "column", 0L }, { "span", null }
            };
        }

        /// <summary>解析当前 Editor 安装目录下的 Data 目录；不存在时返回 null。</summary>
        private static string EditorDataPath()
        {
            var applicationPath = UnityEditor.EditorApplication.applicationPath;
            var editorDirectory = string.IsNullOrEmpty(applicationPath) ? null : Path.GetDirectoryName(applicationPath);
            var dataPath = string.IsNullOrEmpty(editorDirectory) ? null : Path.Combine(editorDirectory, "Data");
            return string.IsNullOrEmpty(dataPath) ? null : dataPath;
        }
    }
}
#endif
