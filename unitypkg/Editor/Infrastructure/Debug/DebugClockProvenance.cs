#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using BridgeCommandException = UnityCli.UnityCliBridge.BridgeCommandException;

namespace UnityCli
{
    /// <summary>
    /// debug 域统一的时钟溯源 owner。
    ///
    /// 四种时钟种类由 UnityCliDebugClocks 冻结：单调耗时、Editor tick、真实 Unity frame、项目命名逻辑时钟。
    /// 规则：
    /// - Unity 真实帧号只有在可证明的主线程上才读取；off-main 只能报告主线程缓存的 lastObservedMainFrame
    ///   与本次采集的单调时间，绝不调用 ReadUnityFrame。
    /// - Editor tick 取本域中央 tick 计数；它只描述"中央 tick 推进到第几次"，不是帧号也不是墙钟。
    /// - 命名逻辑时钟由项目值发布（当前来源为被读取字段），它不必单调、不必每帧加一，也绝不从 Unity 帧号推断。
    /// - 每次捕获都记录来源、线程、采集时刻、有效区间与是否同现场采集；投影不执行任何用户代码。
    /// </summary>
    internal static class DebugClockProvenance
    {
        private const string MonotonicSource = "process-monotonic-stopwatch";
        private const string EditorTickSource = "debug-domain-central-editor-tick";
        private const string MainThreadFrameSource = "unity-frame-read-on-main-thread";
        private const string CachedFrameSource = "unity-frame-cached-by-main-thread";
        private const string SingleInstantValidity = "Valid for a single observed instant at capturedAtMonotonicMs; this is not continuous coverage of the interval.";

        /// <summary>
        /// 一次读取可用的四种时钟溯源。
        /// 单调、Editor tick 与 Unity frame 恒出现；命名逻辑时钟只在本次调用发布了该项目逻辑时钟时出现。
        /// </summary>
        internal static List<DebugClockView> CaptureReadClocks(DebugLogicalClockView publishedLogicalClock)
        {
            var clocks = new List<DebugClockView>
            {
                Monotonic(),
                EditorTick(),
                UnityFrame()
            };
            if (publishedLogicalClock != null) clocks.Add(ProjectLogical(publishedLogicalClock));
            return clocks;
        }

        /// <summary>单调耗时读法；任何线程都可用。</summary>
        internal static DebugClockView Monotonic()
        {
            var now = DebugEventClocks.MonotonicMilliseconds;
            return new DebugClockView
            {
                Kind = UnityCliDebugClocks.Monotonic,
                Value = now,
                Frame = 0L,
                LastObservedMainFrame = 0L,
                Phase = Phase(),
                ThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId,
                MainThread = DebugEventClocks.IsCurrentThreadMainThread(),
                Source = MonotonicSource,
                CapturedAtMonotonicMs = now,
                CapturedSameSite = true,
                ValidFromMonotonicMs = now,
                ValidToMonotonicMs = now,
                ValidityNote = SingleInstantValidity,
                IsUnityFrameCount = false
            };
        }

        /// <summary>Editor tick 读法；值为本域中央 tick 计数，0 表示本 domain generation 尚未推进过中央 tick。</summary>
        internal static DebugClockView EditorTick()
        {
            var now = DebugEventClocks.MonotonicMilliseconds;
            return new DebugClockView
            {
                Kind = UnityCliDebugClocks.EditorTick,
                Value = MethodHookHost.EditorTickValue,
                Frame = 0L,
                LastObservedMainFrame = 0L,
                Phase = Phase(),
                ThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId,
                MainThread = DebugEventClocks.IsCurrentThreadMainThread(),
                Source = EditorTickSource,
                CapturedAtMonotonicMs = now,
                CapturedSameSite = true,
                ValidFromMonotonicMs = now,
                ValidToMonotonicMs = now,
                ValidityNote = "An Editor tick counter, not a Unity frame and not wall time; 0 means the central Editor tick has not run in this domain generation yet.",
                IsUnityFrameCount = false
            };
        }

        /// <summary>
        /// Unity frame 读法。
        /// 可证明的主线程读取真实帧号；其它线程只报告主线程缓存的 lastObservedMainFrame。
        /// </summary>
        internal static DebugClockView UnityFrame()
        {
            var now = DebugEventClocks.MonotonicMilliseconds;
            var mainThread = DebugEventClocks.IsCurrentThreadMainThread();
            var frame = mainThread ? DebugEventClocks.ReadUnityFrame() : 0L;
            return new DebugClockView
            {
                Kind = UnityCliDebugClocks.UnityFrame,
                Value = mainThread ? frame : (double)DebugEventClocks.LastObservedMainFrame,
                Frame = frame,
                LastObservedMainFrame = mainThread ? 0L : DebugEventClocks.LastObservedMainFrame,
                Phase = Phase(),
                ThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId,
                MainThread = mainThread,
                Source = mainThread ? MainThreadFrameSource : CachedFrameSource,
                CapturedAtMonotonicMs = now,
                CapturedSameSite = true,
                ValidFromMonotonicMs = now,
                ValidToMonotonicMs = now,
                ValidityNote = mainThread
                    ? "The real Unity frame number read on the main thread, so this value is the current frame count of this call."
                    : "Off-main code must never read the Unity frame count; this value is the last frame the main thread published earlier, not the frame of this call.",
                IsUnityFrameCount = mainThread
            };
        }

        /// <summary>把命名逻辑时钟投影为时钟溯源；它明确不是 Unity 帧计数。</summary>
        internal static DebugClockView ProjectLogical(DebugLogicalClockView logical)
        {
            return new DebugClockView
            {
                Kind = UnityCliDebugClocks.ProjectLogical,
                Value = logical.Value,
                Frame = 0L,
                LastObservedMainFrame = 0L,
                Phase = Phase(),
                ThreadId = logical.ThreadId,
                MainThread = logical.MainThread,
                Source = logical.Source,
                CapturedAtMonotonicMs = logical.CapturedAtMonotonicMs,
                CapturedSameSite = logical.CapturedSameSite,
                ValidFromMonotonicMs = logical.ValidFromMonotonicMs,
                ValidToMonotonicMs = logical.ValidToMonotonicMs,
                ValidityNote = logical.ValidityNote,
                IsUnityFrameCount = false
            };
        }

        /// <summary>当前编辑模式的相位描述；它是执行域事实，不是帧号。</summary>
        private static string Phase()
        {
            return EditorApplication.isPlaying ? "play" : "edit";
        }
    }

    /// <summary>
    /// 命名项目逻辑时钟的唯一 owner。
    ///
    /// 规则：
    /// - 逻辑时钟值由项目值发布，当前来源是被读取的字段；不得从 Unity 帧号推断，也不声称单调或每帧加一。
    /// - 同名发布覆盖旧条目，条数由 DebugLimits 单点上限约束。
    /// - 只保存值、来源、线程与采集时刻；不保存任何活对象引用，也不触发用户代码。
    /// </summary>
    internal static class DebugLogicalClocks
    {
        private static readonly object Sync = new object();
        private static readonly List<DebugLogicalClockView> Entries = new List<DebugLogicalClockView>();

        /// <summary>发布或覆盖一个命名逻辑时钟并返回它的溯源。</summary>
        internal static DebugLogicalClockView Publish(string name, double value, string rawText, string source, string domainGeneration)
        {
            if (string.IsNullOrEmpty(name))
                throw new BridgeCommandException("E_SCHEMA_INVALID", "A named logical clock requires a non-empty name.", new Dictionary<string, object> { { "path", "$.logicalClockName" }, { "expected", "non-empty string" }, { "actual", name ?? "null" } }, new[] { "Pass the project-published clock name you want recorded." });
            var now = DebugEventClocks.MonotonicMilliseconds;
            var entry = new DebugLogicalClockView
            {
                Name = name,
                Value = value,
                RawText = rawText ?? string.Empty,
                Source = source ?? string.Empty,
                ThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId,
                MainThread = DebugEventClocks.IsCurrentThreadMainThread(),
                CapturedAtMonotonicMs = now,
                CapturedSameSite = true,
                ValidFromMonotonicMs = now,
                ValidToMonotonicMs = now,
                ValidityNote = "Recorded from the value read in this same call, so the clock and the read are the same observation site. " + DebugClockProvenanceNote + " A project logical clock is never the Unity frame count and is never inferred from one.",
                IsUnityFrameCount = false,
                DomainGeneration = domainGeneration ?? string.Empty
            };
            lock (Sync)
            {
                for (var index = 0; index < Entries.Count; index++)
                {
                    if (!string.Equals(Entries[index].Name, name, StringComparison.Ordinal)) continue;
                    Entries[index] = entry;
                    return entry;
                }
                if (Entries.Count >= DebugLimits.MaxNamedLogicalClocks) Entries.RemoveAt(0);
                Entries.Add(entry);
                return entry;
            }
        }

        /// <summary>当前已发布命名时钟的只读副本；按发布顺序返回，读取方不得修改。</summary>
        internal static List<DebugLogicalClockView> Snapshot()
        {
            lock (Sync) return new List<DebugLogicalClockView>(Entries);
        }

        /// <summary>
        /// 释放一个已发布的命名逻辑时钟，返回是否真的移除了条目。
        /// 发布者是程序或会话时，它们停止后必须走这里释放，否则一个已死发布者会永久占用
        /// 有限名额并继续出现在真实读取结果里；域重载会自然清空整个表，但同一代内不会自愈。
        /// </summary>
        internal static bool Release(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            lock (Sync)
            {
                for (var index = 0; index < Entries.Count; index++)
                {
                    if (!string.Equals(Entries[index].Name, name, StringComparison.Ordinal)) continue;
                    Entries.RemoveAt(index);
                    return true;
                }
                return false;
            }
        }

        /// <summary>释放当前代全部命名时钟；仅用于发布者整体消失（会话/程序清理）与测试恢复现场。</summary>
        internal static void Clear()
        {
            lock (Sync) Entries.Clear();
        }

        /// <summary>命名时钟有效性的固定说明；与每次发布的溯源文本共用同一来源。</summary>
        internal const string DebugClockProvenanceNote = "The named logical sequence is published by project data, need not be monotonic and need not advance once per frame.";
    }
}
#endif
