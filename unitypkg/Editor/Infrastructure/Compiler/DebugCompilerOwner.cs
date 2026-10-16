#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace UnityCli
{
    /// <summary>
    /// 编译推进的调用来源。两种 lane 只能互斥持有同一个编译所有权：
    /// 正常路径由主线程调度 tick 推进，暂停期间由非主线程服务现场循环推进。
    /// </summary>
    internal enum DebugCompilerLane
    {
        MainThreadTick,
        PauseService
    }

    /// <summary>当前域编译产物的真实占用；只反映实际加载结果，不反映已经释放的路由。</summary>
    internal sealed class DebugCompilerBudgetReport
    {
        internal int Count;
        internal long Bytes;

        /// <summary>还可以加载的不同产物数量；已耗尽时为 0。</summary>
        internal int RemainingCount { get { return Math.Max(0, DebugLimits.MaxCompiledArtifacts - Count); } }

        /// <summary>还可以加载的累计字节；已耗尽时为 0。</summary>
        internal long RemainingBytes { get { return Math.Max(0L, DebugLimits.MaxCompiledArtifactBytes - Bytes); } }

        /// <summary>结构化占用：当前数量与字节、剩余额度与三个已声明上限都来自同一份记账。</summary>
        internal Dictionary<string, object> ToData()
        {
            return new Dictionary<string, object>
            {
                { "compiledArtifacts", Count },
                { "compiledArtifactBytes", Bytes },
                { "remainingArtifacts", RemainingCount },
                { "remainingBytes", RemainingBytes },
                { "maxCompiledArtifacts", DebugLimits.MaxCompiledArtifacts },
                { "maxCompiledArtifactBytes", DebugLimits.MaxCompiledArtifactBytes },
                { "maxSingleArtifactBytes", DebugLimits.MaxSingleArtifactBytes }
            };
        }
    }

    /// <summary>加载前准入判定结果；拒绝时必然携带当前占用与 reload 建议，且不改变任何状态。</summary>
    internal sealed class DebugCompilerAdmission
    {
        internal bool Admitted;
        /// <summary>拒绝原因：single-artifact-bytes、artifact-count 或 artifact-bytes。</summary>
        internal string Reason = string.Empty;
        internal DebugCompilerBudgetReport Report;

        internal static DebugCompilerAdmission Admit(DebugCompilerBudgetReport report)
        {
            return new DebugCompilerAdmission { Admitted = true, Report = report };
        }

        internal static DebugCompilerAdmission Refuse(string reason, DebugCompilerBudgetReport report)
        {
            return new DebugCompilerAdmission { Admitted = false, Reason = reason, Report = report };
        }
    }

    /// <summary>
    /// debug 域动态编译的唯一 owner：编译所有权、每 domain generation 的引用快照、已加载产物预算与准入判定。
    ///
    /// 四个提交形状（debug.code.execute、hook 回调源码、program 源码、pause evaluate）只能经由本类：
    /// 先用 TryBeginOwnership 取得唯一所有权，再用 References 取同一 generation 的引用快照，用 TryAdmitCandidate
    /// 判定准入，加载成功后用 CommitLoadedArtifact 记账。形状侧不得另建计数器、队列、引用快照或临时区。
    /// 预算常量只在 DebugLimits 声明，本类不重复声明任何数值。
    ///
    /// 记账语义：Mono 不能逐个卸载已加载程序集，因此只有 Domain Reload 边界的 ResetDomain 可以归零，
    /// remove/stop 不扣减占用。失败候选按真实加载结果计数：没有加载就不占数量，已经加载但无法卸载的字节继续计入。
    ///
    /// program 源码与 pause evaluate 在后面接入时的接缝就是本类已有的四项：TryBeginOwnership(PauseService) 或
    /// TryBeginOwnershipWithin 取得所有权、References 取同一 generation 快照、TryAdmitCandidate 判定准入、
    /// CommitLoadedArtifact 记账，并用 StartCompilation / AdvanceOwnedCompilation / CompleteCompilation / AbortCompilation
    /// 在非主线程服务循环里有界推进。它们不得再建预算、队列、快照或临时区。
    /// 本类不持有也不释放暂停现场：取不到编译资源时只返回忙，现场由调用方继续持有，仍可 inspect/resume。
    /// </summary>
    internal static class DebugCompilerOwner
    {
        /// <summary>已发布的“当前域编译产物预算耗尽”稳定错误码；四个形状共用，不按形状另造码。</summary>
        internal const string BudgetExhaustedCode = "E_HOOK_COMPILE_BUDGET_EXHAUSTED";

        /// <summary>拒绝加载时给出的 reload 建议；owner 只报告，不自动 reload。</summary>
        internal const string ReloadSuggestion = "Reload the Unity domain to reclaim the loaded assemblies; removing definitions or stopping a session does not unload them.";

        private static readonly object Gate = new object();
        private static readonly HashSet<string> LoadedArtifactIdentities = new HashSet<string>(StringComparer.Ordinal);
        private static readonly List<string> GeneratedAssemblyNames = new List<string>();
        private static DebugCompilerLane? ownerLane;
        private static long loadedArtifactBytes;
        private static List<string> referenceSnapshot;
        private static string referenceFingerprint;

        // ------------------------------------------------------------------
        // 所有权
        // ------------------------------------------------------------------

        /// <summary>唯一所有权入口；非阻塞，已被任一 lane 持有时立即拒绝，调用方据此返回忙结果。</summary>
        internal static bool TryBeginOwnership(DebugCompilerLane lane)
        {
            return TryBeginOwnershipWithin(lane, TimeSpan.Zero);
        }

        /// <summary>
        /// 在给定预算内等待所有权。只有暂停期的非主线程服务循环可以使用有界等待；
        /// 主线程 tick 必须使用非阻塞重载，避免把暂停现场继续占住。
        /// </summary>
        internal static bool TryBeginOwnershipWithin(DebugCompilerLane lane, TimeSpan budget)
        {
            var wait = budget < TimeSpan.Zero ? TimeSpan.Zero : budget;
            if (!Monitor.TryEnter(Gate, wait)) return false;
            try
            {
                if (ownerLane.HasValue) return false;
                ownerLane = lane;
                return true;
            }
            finally
            {
                Monitor.Exit(Gate);
            }
        }

        /// <summary>释放所有权；必须由持有它的 lane 在 finally 中调用，lane 不匹配时不释放。</summary>
        internal static bool EndOwnership(DebugCompilerLane lane)
        {
            lock (Gate)
            {
                if (!ownerLane.HasValue || ownerLane.Value != lane) return false;
                ownerLane = null;
                return true;
            }
        }

        /// <summary>当前是否由任一 lane 持有编译所有权；只用于诊断与延后判断。</summary>
        internal static bool IsOwned { get { lock (Gate) return ownerLane.HasValue; } }

        /// <summary>指定 lane 是否持有所有权。</summary>
        internal static bool HoldsOwnership(DebugCompilerLane lane)
        {
            lock (Gate) return ownerLane.HasValue && ownerLane.Value == lane;
        }

        /// <summary>
        /// 有界推进入口：只有持有所有权的 lane 可以推进，因此主线程 tick 与暂停期服务循环互斥，
        /// 不会出现第二个编译 pump。单次调用只做有界工作：轮询进程退出、有限排空 stdout/stderr、应用取消与 deadline。
        /// 非 owner 调用返回 false（不推进、不阻塞），调用方据此结束推进而不是等待旧 continuation。
        /// </summary>
        internal static bool AdvanceOwnedCompilation(DynamicCodeCompileRun run, DebugCompilerLane lane)
        {
            if (!HoldsOwnership(lane)) return false;
            return DynamicCodeCompiler.AdvanceCompilation(run);
        }

        // ------------------------------------------------------------------
        // 引用快照
        // ------------------------------------------------------------------

        /// <summary>
        /// 当前 generation 的引用快照加上本次请求的额外引用。
        ///
        /// 快照每个 generation 只建立一次：同一 generation 内的后续加载（本形状产物、其他形状产物、其他功能惰性加载）
        /// 不得改变已发布快照，否则同域同源码无法复用。本功能自己加载的产物是输出而不是输入，必须排除。
        /// deadline 到期时抛出 OperationCanceledException，由调用方映射为超时。
        /// </summary>
        internal static List<string> References(string refsCsv, List<string> extraReferences, Func<bool> deadlineOk)
        {
            var deadline = deadlineOk ?? (() => true);
            if (!deadline()) throw new OperationCanceledException("Dynamic code reference discovery exceeded the immutable deadline.");
            var references = new List<string>(Snapshot(deadline));
            foreach (var reference in UnityCliBridge.SplitCsv(refsCsv)) AddReference(references, reference);
            if (extraReferences != null) foreach (var reference in extraReferences) AddReference(references, reference);
            if (!deadline()) throw new OperationCanceledException("Dynamic code reference discovery exceeded the immutable deadline.");
            return references;
        }

        /// <summary>当前 generation 引用快照的稳定摘要；用于候选 identity，同域内不随后续加载变化。</summary>
        internal static string ReferenceFingerprint()
        {
            Snapshot(() => true);
            lock (Gate) return referenceFingerprint ?? string.Empty;
        }

        /// <summary>指纹内容的有界诊断：参与计数的程序集数量与排除掉的自家产物数量。</summary>
        internal static string ReferenceDiagnostics()
        {
            var count = 0;
            var excluded = 0;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic || IsOwnGeneratedAssembly(assembly)) { excluded++; continue; }
                count++;
            }
            return "refCount=" + count.ToString(CultureInfo.InvariantCulture)
                + " excluded=" + excluded.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 候选 identity：形状、源码、编译参数与同一 generation 的引用摘要共同决定一个候选。
        /// 重编译相同候选不重复占用数量预算，真正不同的产物才占用新的数量。
        /// </summary>
        internal static string CandidateIdentity(string shape, string sourceText, string defines)
        {
            return UnityCliSerializer.Sha256Canonical(new Dictionary<string, object>
            {
                { "shape", shape ?? string.Empty },
                { "source", sourceText ?? string.Empty },
                { "defines", defines ?? string.Empty },
                { "references", ReferenceFingerprint() }
            });
        }

        private static List<string> Snapshot(Func<bool> deadlineOk)
        {
            lock (Gate)
            {
                if (referenceSnapshot != null) return referenceSnapshot;
            }
            // 枚举在锁外完成；首次发布取胜，后续调用直接读已发布快照。
            var references = new List<string>();
            var identities = new List<string>();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!deadlineOk()) throw new OperationCanceledException("Dynamic code reference discovery exceeded the immutable deadline.");
                if (assembly.IsDynamic || IsOwnGeneratedAssembly(assembly)) continue;
                try
                {
                    var name = assembly.GetName();
                    var mvid = assembly.ManifestModule == null ? string.Empty : assembly.ManifestModule.ModuleVersionId.ToString("D");
                    identities.Add((name.FullName ?? name.Name) + "#" + mvid);
                }
                catch
                {
                    // 无法读取身份的程序集不参与指纹；引用仍按 Location 收集。
                }
                AddReference(references, assembly.Location);
            }
            identities.Sort(StringComparer.Ordinal);
            var fingerprint = UnityCliSerializer.Sha256Canonical(identities.Cast<object>().ToList());
            lock (Gate)
            {
                if (referenceSnapshot == null)
                {
                    referenceSnapshot = references;
                    referenceFingerprint = fingerprint;
                }
                return referenceSnapshot;
            }
        }

        /// <summary>本功能自己加载的产物程序集不参与引用快照与指纹：它们是输出而不是输入。</summary>
        private static bool IsOwnGeneratedAssembly(Assembly assembly)
        {
            var simpleName = assembly == null ? string.Empty : assembly.GetName().Name ?? string.Empty;
            // Adapter 容器由 hook 后端加载，不是本 owner 的产物，但同样属于本功能生成的代码。
            if (string.Equals(simpleName, "UnityCli.HookAdapters.Generated", StringComparison.Ordinal)) return true;
            lock (Gate)
            {
                foreach (var name in GeneratedAssemblyNames)
                    if (string.Equals(simpleName, name, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        private static void AddReference(List<string> references, string reference)
        {
            if (string.IsNullOrEmpty(reference)) return;
            if (!references.Contains(reference)) references.Add(reference);
        }

        // ------------------------------------------------------------------
        // 预算与准入
        // ------------------------------------------------------------------

        /// <summary>当前域的真实占用；remove/stop 之后仍然随后返回同一事实。</summary>
        internal static DebugCompilerBudgetReport Budget()
        {
            lock (Gate)
            {
                return new DebugCompilerBudgetReport { Count = LoadedArtifactIdentities.Count, Bytes = loadedArtifactBytes };
            }
        }

        /// <summary>该候选是否已经计入过占用；重复编译相同候选时用于避免重复计数。</summary>
        internal static bool IsLoaded(string identity)
        {
            lock (Gate) return !string.IsNullOrEmpty(identity) && LoadedArtifactIdentities.Contains(identity);
        }

        /// <summary>
        /// 加载前的准入判定。artifactBytes 为 0 表示编译前的粗判，为真实长度表示加载前的精确判定。
        /// 拒绝时返回当前占用（数量、字节、剩余）与 reload 建议，且不改变任何状态；owner 不自动 reload。
        /// </summary>
        internal static DebugCompilerAdmission TryAdmitCandidate(string identity, long artifactBytes)
        {
            if (string.IsNullOrEmpty(identity)) return DebugCompilerAdmission.Refuse("identity-missing", Budget());
            if (artifactBytes < 0L) artifactBytes = 0L;
            lock (Gate)
            {
                var report = new DebugCompilerBudgetReport { Count = LoadedArtifactIdentities.Count, Bytes = loadedArtifactBytes };
                if (artifactBytes > DebugLimits.MaxSingleArtifactBytes) return DebugCompilerAdmission.Refuse("single-artifact-bytes", report);
                var alreadyLoaded = LoadedArtifactIdentities.Contains(identity);
                if (!alreadyLoaded && report.Count >= DebugLimits.MaxCompiledArtifacts) return DebugCompilerAdmission.Refuse("artifact-count", report);
                // 同一候选的数量与字节都已在账上，重编译相同候选不重复占用。
                if (alreadyLoaded) return DebugCompilerAdmission.Admit(report);
                if (report.Bytes + artifactBytes > DebugLimits.MaxCompiledArtifactBytes) return DebugCompilerAdmission.Refuse("artifact-bytes", report);
                return DebugCompilerAdmission.Admit(report);
            }
        }

        /// <summary>
        /// 加载成功后的记账入口。同一 identity 只记一次；字节按真实加载结果累计。
        /// 形状侧只允许在 Assembly.Load 成功之后调用，加载失败或编译失败不得调用。
        /// </summary>
        internal static void CommitLoadedArtifact(string identity, long artifactBytes, string assemblySimpleName)
        {
            if (string.IsNullOrEmpty(identity)) return;
            lock (Gate)
            {
                if (!LoadedArtifactIdentities.Add(identity)) return;
                loadedArtifactBytes += artifactBytes < 0L ? 0L : artifactBytes;
                if (!string.IsNullOrEmpty(assemblySimpleName)) GeneratedAssemblyNames.Add(assemblySimpleName);
            }
        }

        /// <summary>
        /// Domain Reload / 新 generation 边界：归零已加载产物占用与引用快照。
        /// 这是唯一可以扣减占用的路径；remove/stop 不得调用它。
        /// </summary>
        internal static void ResetDomain()
        {
            lock (Gate)
            {
                ownerLane = null;
                LoadedArtifactIdentities.Clear();
                GeneratedAssemblyNames.Clear();
                loadedArtifactBytes = 0L;
                referenceSnapshot = null;
                referenceFingerprint = null;
            }
        }
    }
}
#endif
