#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
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
        internal string FailureCode;
        internal string FailureMessage;
        internal byte[] AssemblyBytes;
        internal List<Dictionary<string, object>> Diagnostics = new List<Dictionary<string, object>>();
    }

    /// <summary>
    /// 一次动态编译的子进程、管道与固定临时文件状态。
    ///
    /// StartCompilation 只起进程与读取，不做任何等待；AdvanceCompilation 是可以由任一 lane 反复调用的有界推进；
    /// CompleteCompilation / AbortCompilation 发布终态、回收进程并删除固定临时文件。
    /// 因此同一次编译既可由主线程调度 tick 推进，也可由暂停期非主线程服务循环推进，不需要第二套 compiler。
    /// </summary>
    internal sealed class DynamicCodeCompileRun
    {
        internal Process Process;
        internal Task<string> StdoutTask;
        internal Task<string> StderrTask;
        internal Func<bool> DeadlineOk;
        internal CancellationToken CancellationToken;
        internal string TemporaryRoot;
        internal string SourceFilePath;
        internal string ResponseFilePath;
        internal string AssemblyFilePath;
        internal string PdbFilePath;
        internal long MaxArtifactBytes;
        internal bool Terminal;
        internal bool TimedOut;
        internal bool Failed;
        internal string FailureCode;
        internal string FailureMessage;
        internal int ExitCode;
        internal string Output = string.Empty;
        internal List<Dictionary<string, object>> StartupDiagnostics = new List<Dictionary<string, object>>();
        internal DateTime ExitObservedUtc;
    }

    /// <summary>
    /// 动态 C# 编译的唯一 mechanical worker：固定临时文件、Unity 自带 mono/csc 子进程、有界诊断与 assembly bytes。
    ///
    /// 所有权、引用快照与加载产物预算都不在这里：它们由唯一 owner <see cref="DebugCompilerOwner"/> 承担。
    /// 因此本类不持有编译队列，也不判断准入；调用方先经 owner 取得所有权后，才允许开始与推进一次编译。
    /// 进程遵守调用方传入的不可续期 deadline；超时、取消、不可收敛都会终止子进程并删除临时文件。
    /// 输出文件固定命名并在终态读取 bytes 后删除，不按 hash 累积目录。
    /// </summary>
    internal static class DynamicCodeCompiler
    {
        internal const int MaxCompilerOutputBytes = 512 * 1024;
        internal const int MaxCompilerDiagnostics = 100;
        internal const int MaxCompileSeconds = 60;

        /// <summary>子进程退出后给两个输出管道的固定收敛窗口；窗口内不阻塞推进 lane。</summary>
        internal static readonly TimeSpan PipeDrainWindow = TimeSpan.FromSeconds(5);

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
        /// 主线程 lane 的编译驱动：按同一有界推进入口等待子进程收敛。
        /// 调用方负责先取得 active compiler 所有权，并在失败时映射自己的稳定错误码。
        /// </summary>
        internal static async ValueTask<DynamicCodeCompileResult> CompileAsync(
            DynamicCodeCompileRequest compile,
            List<string> references,
            Func<bool> deadlineOk,
            IUnityCliWaiter waiter,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            DynamicCodeCompileRun run = null;
            try
            {
                run = StartCompilation(compile, references, deadlineOk, cancellationToken);
                if (run.Terminal) return CompleteCompilation(run);
                await RunUnityCompilerAsync(run, deadlineOk, waiter, timeout, cancellationToken);
                return CompleteCompilation(run);
            }
            catch
            {
                if (run != null) AbortCompilation(run);
                throw;
            }
        }

        /// <summary>
        /// 校验并启动一次编译：写固定临时文件、起 Unity 自带 mono/csc 子进程、开始有界管道读取。
        /// 不做任何等待；校验失败时直接返回已终态的 run，由 CompleteCompilation 统一投影为结果。
        /// </summary>
        internal static DynamicCodeCompileRun StartCompilation(
            DynamicCodeCompileRequest compile,
            List<string> references,
            Func<bool> deadlineOk,
            CancellationToken cancellationToken)
        {
            var run = new DynamicCodeCompileRun
            {
                DeadlineOk = deadlineOk ?? (() => true),
                CancellationToken = cancellationToken,
                MaxArtifactBytes = compile == null ? 0L : compile.MaxArtifactBytes
            };
            if (compile == null || string.IsNullOrWhiteSpace(compile.SourceText))
            {
                run.Terminal = true;
                run.Failed = true;
                run.FailureCode = "E_CODE_SOURCE_INVALID";
                run.FailureMessage = "Dynamic code source is empty.";
                return run;
            }
            if (compile.MaxSourceBytes > 0 && Encoding.UTF8.GetByteCount(compile.SourceText) > compile.MaxSourceBytes)
            {
                run.Terminal = true;
                run.Failed = true;
                run.FailureCode = "E_HOOK_LIMIT_EXCEEDED";
                run.FailureMessage = "Dynamic code source exceeds the declared UTF-8 byte budget.";
                return run;
            }
            var editorDataPath = EditorDataPath();
            if (string.IsNullOrEmpty(editorDataPath))
            {
                run.Terminal = true;
                run.StartupDiagnostics.Add(Diagnostic("E_REFERENCE_RESOLUTION_FAILED", "Unity Editor Data directory was not found."));
                return run;
            }

            var temporaryRoot = TemporaryRoot();
            Directory.CreateDirectory(temporaryRoot);
            // 自愈：上一次编译在 Windows 句柄未释放时可能删除失败，这里在开工前清空固定临时目录，保证数量有界。
            CleanupStaleCompilationFiles(temporaryRoot);
            run.TemporaryRoot = temporaryRoot;
            run.SourceFilePath = Path.Combine(temporaryRoot, compile.SourceFileName);
            run.ResponseFilePath = Path.Combine(temporaryRoot, compile.ResponseFileName);
            run.AssemblyFilePath = Path.Combine(temporaryRoot, compile.AssemblyFileName);
            run.PdbFilePath = Path.Combine(temporaryRoot, compile.PdbFileName);
            File.WriteAllText(run.SourceFilePath, compile.SourceText, new UTF8Encoding(true));
            WriteReferenceResponseFile(run.ResponseFilePath, references);
            run.Process = StartCompilerProcess(editorDataPath, run, compile.Defines);
            var outputBudget = new UnityCliBridge.Utf8ByteBudget(MaxCompilerOutputBytes);
            run.StdoutTask = UnityCliBridge.ReadBoundedToEndAsync(run.Process.StandardOutput, outputBudget);
            run.StderrTask = UnityCliBridge.ReadBoundedToEndAsync(run.Process.StandardError, outputBudget);
            return run;
        }

        /// <summary>
        /// 使用 Unity 自带 mono/csc 编译；子进程等待通过框架 scheduler 推进的有界推进入口，
        /// 因此不占用主线程，也不新增第二个 update 订阅。deadline 到期即终止编译器。
        /// </summary>
        private static async ValueTask<bool> RunUnityCompilerAsync(
            DynamicCodeCompileRun run,
            Func<bool> deadlineOk,
            IUnityCliWaiter waiter,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            var compileLimit = TimeSpan.FromSeconds(MaxCompileSeconds);
            var waitBudget = timeout < compileLimit ? timeout : compileLimit;
            if (waitBudget <= TimeSpan.Zero) waitBudget = TimeSpan.FromMilliseconds(1);
            // 调度器只负责反复调用唯一有界推进入口；推进本身与暂停期服务循环共用同一实现。
            var terminal = await waiter.Until(
                () => DebugCompilerOwner.AdvanceOwnedCompilation(run, DebugCompilerLane.MainThreadTick),
                waitBudget,
                cancellationToken);
            if (terminal && run.Terminal && !run.TimedOut && deadlineOk()) return true;
            // 等待窗口耗尽、deadline 到达或管道未收敛：给两个读取一个固定窗口后终止 compiler，绝不停留在旧 continuation。
            Task.WaitAll(new Task[] { run.StdoutTask, run.StderrTask }, PipeDrainWindow);
            TerminateCompiler(run);
            run.TimedOut = true;
            run.Terminal = true;
            return false;
        }

        /// <summary>
        /// 有界推进一步：轮询子进程退出、有限排空 stdout/stderr、应用取消与 deadline。
        /// 本入口不得阻塞或忙等，因为主线程 tick 与暂停期服务循环都会调用它。
        /// 返回 true 表示该次编译已有终态。
        /// </summary>
        internal static bool AdvanceCompilation(DynamicCodeCompileRun run)
        {
            if (run == null || run.Terminal) return true;
            if (run.CancellationToken.IsCancellationRequested || !run.DeadlineOk())
            {
                // 取消与 deadline 只在这个唯一入口应用，两条 lane 的终态语义才一致。
                TerminateCompiler(run);
                run.TimedOut = true;
                run.Terminal = true;
                return true;
            }
            if (!HasExited(run.Process)) return false;
            if (run.ExitObservedUtc == default) run.ExitObservedUtc = DateTime.UtcNow;
            if (!OutputDrained(run))
            {
                // 退出后管道仍未读完：在固定窗口内继续由后续推进检查，窗口用尽即视为不可收敛。
                if (DateTime.UtcNow - run.ExitObservedUtc < PipeDrainWindow) return false;
                TerminateCompiler(run);
                run.TimedOut = true;
                run.Terminal = true;
                return true;
            }
            run.ExitCode = run.Process.ExitCode;
            run.Output = run.StdoutTask.Result + Environment.NewLine + run.StderrTask.Result;
            run.Terminal = true;
            return true;
        }

        /// <summary>两个管道是否都已收敛；只做非阻塞判定，不在推进路径上等待。</summary>
        private static bool OutputDrained(DynamicCodeCompileRun run)
        {
            return run.StdoutTask != null && run.StderrTask != null && run.StdoutTask.IsCompleted && run.StderrTask.IsCompleted;
        }

        /// <summary>
        /// 发布终态：投影启动失败、超时、诊断或成功 bytes，并删除固定临时文件。
        /// 只读取推进阶段已经确定的事实，不接触进程对象，因此任一 lane 都能调用。
        /// </summary>
        internal static DynamicCodeCompileResult CompleteCompilation(DynamicCodeCompileRun run)
        {
            try
            {
                if (run == null)
                    return new DynamicCodeCompileResult { FailureCode = "E_CODE_SOURCE_INVALID", FailureMessage = "Dynamic code compilation was not started." };
                if (run.Failed)
                    return new DynamicCodeCompileResult { FailureCode = run.FailureCode, FailureMessage = run.FailureMessage };
                if (run.TimedOut) return new DynamicCodeCompileResult { TimedOut = true };
                if (run.StartupDiagnostics.Count > 0)
                    return new DynamicCodeCompileResult { Diagnostics = run.StartupDiagnostics };
                var result = new DynamicCodeCompileResult();
                var diagnostics = RoslynDiagnostics(run.Output);
                if (run.ExitCode != 0 || diagnostics.Any(item => string.Equals((string)item["severity"], "error", StringComparison.Ordinal)))
                {
                    result.Diagnostics = diagnostics.Where(item => string.Equals((string)item["severity"], "error", StringComparison.Ordinal)).ToList();
                    return result;
                }
                var bytes = File.ReadAllBytes(run.AssemblyFilePath);
                if (run.MaxArtifactBytes > 0 && bytes.LongLength > run.MaxArtifactBytes)
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
                DisposeCompilerProcess(run);
                DeleteCompilationTemporaryFiles(run);
            }
        }

        /// <summary>
        /// 终止本次编译并删除固定临时文件。已经加载产物的占用记账不由本方法扣减：
        /// Mono 不能逐个卸载，占用只由 Domain Reload 边界归零。
        /// </summary>
        internal static void AbortCompilation(DynamicCodeCompileRun run)
        {
            if (run == null) return;
            try
            {
                TerminateCompiler(run);
                run.TimedOut = true;
                run.Terminal = true;
            }
            finally
            {
                DisposeCompilerProcess(run);
                DeleteCompilationTemporaryFiles(run);
            }
        }

        /// <summary>删除本次编译的固定 4 个临时文件；成功、超时、取消都必须执行。</summary>
        private static void DeleteCompilationTemporaryFiles(DynamicCodeCompileRun run)
        {
            if (run == null) return;
            UnityCliBridge.DeleteTemporaryCodeFileWithRetry(run.SourceFilePath);
            UnityCliBridge.DeleteTemporaryCodeFileWithRetry(run.ResponseFilePath);
            UnityCliBridge.DeleteTemporaryCodeFileWithRetry(run.AssemblyFilePath);
            UnityCliBridge.DeleteTemporaryCodeFileWithRetry(run.PdbFilePath);
        }

        private static void DisposeCompilerProcess(DynamicCodeCompileRun run)
        {
            try { if (run != null && run.Process != null) run.Process.Dispose(); } catch { }
        }

        /// <summary>进程退出判断必须是廉价且不抛异常的谓词，供任一 lane 复用。</summary>
        private static bool HasExited(Process process)
        {
            try { return process.HasExited; }
            catch { return true; }
        }

        /// <summary>deadline、取消或不可收敛时终止 compiler，并以短固定窗口回收进程与管道。</summary>
        private static void TerminateCompiler(DynamicCodeCompileRun run)
        {
            if (run == null) return;
            TerminateCompiler(run.Process, run.StdoutTask, run.StderrTask);
        }

        /// <summary>有界终止：先 Kill，再用固定窗口回收进程与两个管道读取。</summary>
        private static void TerminateCompiler(Process process, params Task[] outputTasks)
        {
            try { if (process != null && !process.HasExited) process.Kill(); } catch { }
            try { if (process != null) process.WaitForExit(1000); } catch { }
            try { Task.WaitAll(outputTasks, 1000); } catch { }
        }

        /// <summary>构造 Unity mono/csc 子进程；输出重定向交给有界读取，控制台窗口不弹出。</summary>
        private static Process StartCompilerProcess(string editorDataPath, DynamicCodeCompileRun run, string definesCsv)
        {
            var monoPath = Path.Combine(editorDataPath, "MonoBleedingEdge", "bin", "mono.exe");
            var cscPath = Path.Combine(editorDataPath, "MonoBleedingEdge", "lib", "mono", "4.5", "csc.exe");
            var arguments = UnityCliBridge.QuoteProcessArgument(cscPath)
                + " /nologo /noconfig /nostdlib /utf8output /target:library /langversion:latest /out:"
                + UnityCliBridge.QuoteProcessArgument(run.AssemblyFilePath)
                + " @" + UnityCliBridge.QuoteProcessArgument(run.ResponseFilePath);
            foreach (var define in UnityCliBridge.SplitCsv(definesCsv)) arguments += " /define:" + define;
            arguments += " " + UnityCliBridge.QuoteProcessArgument(run.SourceFilePath);
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
            return process;
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
