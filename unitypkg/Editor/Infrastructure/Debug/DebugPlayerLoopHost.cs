#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine.LowLevel;
using BridgeCommandException = UnityCli.UnityCliBridge.BridgeCommandException;

namespace UnityCli
{
    /// <summary>
    /// 可声明的 PlayerLoop phase 封闭集合。
    ///
    /// 名字是公开契约：调用方只能声明这里列出的取值，非法名称在 install 时被拒绝。
    /// 每个名字对应顶层 PlayerLoop 上的一个固定位置，因此“Update 前/后、FixedUpdate、LateUpdate”
    /// 是四个可区分的位置，而不是同一个中央 tick 的别名。
    /// </summary>
    internal static class DebugPlayerLoopPhases
    {
        /// <summary>顶层 Update 阶段之前。</summary>
        internal const string BeforeUpdate = "before-update";

        /// <summary>顶层 Update 阶段之后。</summary>
        internal const string AfterUpdate = "after-update";

        /// <summary>顶层 FixedUpdate 阶段之后（固定步已经跑完）。</summary>
        internal const string FixedUpdate = "fixed-update";

        /// <summary>LateUpdate 脚本阶段之后（PostLateUpdate 之前）。</summary>
        internal const string LateUpdate = "late-update";

        internal static bool IsDefined(string value)
        {
            return string.Equals(value, BeforeUpdate, StringComparison.Ordinal)
                || string.Equals(value, AfterUpdate, StringComparison.Ordinal)
                || string.Equals(value, FixedUpdate, StringComparison.Ordinal)
                || string.Equals(value, LateUpdate, StringComparison.Ordinal);
        }

        internal static string[] All()
        {
            return new[] { BeforeUpdate, AfterUpdate, FixedUpdate, LateUpdate };
        }

        /// <summary>一种可执行的解释说明；用于 describe 文本与错误 nextActions，避免两处各写一份。</summary>
        internal static string PlacementSummary()
        {
            return "before-update inserts before the top-level Update phase, after-update after it, fixed-update after the FixedUpdate phase, and late-update after the LateUpdate scripts.";
        }
    }

    /// <summary>
    /// phase 在顶层 PlayerLoop 里的插入位置解析。
    ///
    /// 这里只做纯名字匹配，不缓存任何 PlayerLoop 副本：调用方每次都用
    /// <c>PlayerLoop.GetCurrentPlayerLoop()</c> 重新读取当前循环，因此域重载或 Play 段切换后
    /// 不会把一个旧域的位置当成仍然有效。
    /// </summary>
    internal static class DebugPlayerLoopLayout
    {
        internal const string UpdateRoot = "Update";
        internal const string FixedUpdateRoot = "FixedUpdate";
        internal const string PostLateUpdateRoot = "PostLateUpdate";

        /// <summary>该 phase 的锚点顶层阶段名；未定义的 phase 返回空串。</summary>
        internal static string AnchorOf(string phase)
        {
            if (string.Equals(phase, DebugPlayerLoopPhases.BeforeUpdate, StringComparison.Ordinal)
                || string.Equals(phase, DebugPlayerLoopPhases.AfterUpdate, StringComparison.Ordinal)) return UpdateRoot;
            if (string.Equals(phase, DebugPlayerLoopPhases.FixedUpdate, StringComparison.Ordinal)) return FixedUpdateRoot;
            if (string.Equals(phase, DebugPlayerLoopPhases.LateUpdate, StringComparison.Ordinal)) return PostLateUpdateRoot;
            return string.Empty;
        }

        /// <summary>true 表示插在锚点之后，false 表示插在锚点之前。</summary>
        internal static bool InsertAfterAnchor(string phase)
        {
            return string.Equals(phase, DebugPlayerLoopPhases.AfterUpdate, StringComparison.Ordinal)
                || string.Equals(phase, DebugPlayerLoopPhases.FixedUpdate, StringComparison.Ordinal);
        }

        /// <summary>
        /// 计算插入下标。锚点不存在时返回 false：调用方必须拒绝安装并给出稳定原因，
        /// 而不是把子系统放到一个语义不同的位置。
        /// </summary>
        internal static bool TryResolve(string phase, IList<string> rootNames, out int index)
        {
            index = -1;
            var anchor = AnchorOf(phase);
            if (string.IsNullOrEmpty(anchor) || rootNames == null) return false;
            for (var position = 0; position < rootNames.Count; position++)
            {
                if (!string.Equals(rootNames[position], anchor, StringComparison.Ordinal)) continue;
                index = InsertAfterAnchor(phase) ? position + 1 : position;
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Editor Play 下 PlayerLoop phase 现场的唯一 owner。
    ///
    /// 规则：
    /// - 只有中央框架在这里插入/移除具名 PlayerLoop 子系统；Tool、program、listener 与 handler
    ///   都不得自行注册 PlayerLoopSystem、调用 PlayerLoop.SetPlayerLoop 或注册 EditorApplication.update。
    /// - 子系统在自己的真实 phase 上推进中央 scheduler：只推进声明了同一 phase 的 driver 与投递，
    ///   因此事件可以被证明来自该 phase，而不是被 Editor tick 冒充。
    /// - 现场按 owner 记账：同一 owner 重复安装是幂等的；同一 phase 只在第一个 owner 安装时插入一次子系统。
    /// - 机制状态只对本次 Unity 启动有效：Play 退出、域重载、Editor 关停与 session stop 都必须移除子系统，
    ///   移除只删除本框架自己的标记条目，不重建或改写其他子系统。
    /// </summary>
    internal static class DebugPlayerLoopHost
    {
        // 标记类型即身份：移除时只匹配这些类型，绝不按名字猜测其他子系统。
        private struct BeforeUpdateSubsystem { }
        private struct AfterUpdateSubsystem { }
        private struct FixedUpdateSubsystem { }
        private struct LateUpdateSubsystem { }

        private static readonly object Sync = new object();
        private static readonly Dictionary<string, string> Owners = new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly List<string> PendingRemovals = new List<string>();
        private static long insertedSubsystems;
        private static long removedSubsystems;
        private static long idempotentInstalls;
        private static string lastFailure = string.Empty;

        [ThreadStatic] private static int phaseAdvanceDepth;

        /// <summary>当前真实安装在 PlayerLoop 里的本框架子系统数量。</summary>
        internal static int InstalledCount { get { lock (Sync) return InstalledLocked().Count; } }

        /// <summary>当前真实安装的 phase 名（按封闭集合顺序，便于报告比较基线）。</summary>
        internal static string[] InstalledPhases()
        {
            lock (Sync)
            {
                var installed = InstalledLocked();
                return DebugPlayerLoopPhases.All().Where(installed.Contains).ToArray();
            }
        }

        internal static long InsertedSubsystems { get { lock (Sync) return insertedSubsystems; } }

        internal static long RemovedSubsystems { get { lock (Sync) return removedSubsystems; } }

        internal static long IdempotentInstalls { get { lock (Sync) return idempotentInstalls; } }

        internal static string LastFailure { get { lock (Sync) return lastFailure; } }

        /// <summary>非法 phase 名称的稳定错误；携带封闭集合与位置说明。</summary>
        internal static BridgeCommandException PhaseUnknown(string path, string phase)
        {
            return new BridgeCommandException(
                "E_DEBUG_PLAYER_LOOP_PHASE_UNKNOWN",
                "Field " + path + " is not a declarable PlayerLoop phase, so no phase site was installed.",
                new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "path", path },
                    { "expected", string.Join(", ", DebugPlayerLoopPhases.All()) },
                    { "actual", phase ?? "null" },
                    { "availablePhases", DebugPlayerLoopPhases.All() }
                },
                new[]
                {
                    "Pass one of availablePhases as the phase field.",
                    DebugPlayerLoopPhases.PlacementSummary()
                });
        }

        /// <summary>安装失败的稳定错误；原因是环境事实（锚点缺失或 SetPlayerLoop 失败），不是调用方输入问题。</summary>
        internal static BridgeCommandException Unavailable(string phase, string reason)
        {
            return new BridgeCommandException(
                "E_DEBUG_PLAYER_LOOP_UNAVAILABLE",
                "The Editor PlayerLoop could not host a phase site, so the requested phase execution point was refused before any effect.",
                new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "phase", phase ?? string.Empty },
                    { "reason", reason ?? string.Empty },
                    { "executionDomain", "editor-play" },
                    { "installedPhases", InstalledPhases() }
                },
                new[]
                {
                    "Keep running in Editor Play: a PlayerLoop phase exists only while the Editor is playing.",
                    "Restart Editor Play if this Editor session no longer exposes the default PlayerLoop anchors."
                });
        }

        /// <summary>
        /// Edit 模式下的不可用拒绝：PlayerLoop phase 只存在于 Editor Play，绝不用中央 editor tick 冒充。
        /// 恢复路径与 program 执行点、listener 触发源共用同一实现。
        /// </summary>
        internal static void RequirePlayDomain(string phase, Dictionary<string, object> details)
        {
            if (UnityEngine.Application.isPlaying) return;
            var payload = details == null
                ? new Dictionary<string, object>(StringComparer.Ordinal)
                : new Dictionary<string, object>(details, StringComparer.Ordinal);
            payload["phase"] = phase ?? string.Empty;
            payload["availablePhases"] = DebugPlayerLoopPhases.All();
            payload["executionDomain"] = "editor-edit";
            throw DebugTriggerSources.PlayerLoopPhaseUnavailable(
                payload,
                new[]
                {
                    "Enter Editor Play mode: a PlayerLoop phase only exists while the Editor is playing.",
                    DebugPlayerLoopPhases.PlacementSummary(),
                    "Use executionSite editor-tick only when a central editor tick is what you actually want; it is never reported as a PlayerLoop phase."
                });
        }

        /// <summary>
        /// 为一个 owner 安装（或移动）phase 现场。返回 null 表示成功，否则返回稳定原因字符串。
        /// 同一 owner 同一 phase 的重复安装是幂等的；同一 phase 只插入一个子系统。
        /// </summary>
        internal static string Install(string phase, string ownerId)
        {
            if (!DebugPlayerLoopPhases.IsDefined(phase)) return "phase-unknown";
            if (string.IsNullOrEmpty(ownerId)) return "owner-required";
            lock (Sync)
            {
                string previous;
                if (Owners.TryGetValue(ownerId, out previous) && string.Equals(previous, phase, StringComparison.Ordinal))
                {
                    idempotentInstalls++;
                    return null;
                }
                var loop = PlayerLoop.GetCurrentPlayerLoop();
                var subsystems = loop.subSystemList ?? new PlayerLoopSystem[0];
                var marker = MarkerOf(phase);
                var changed = false;
                if (!ContainsMarker(subsystems, marker))
                {
                    var names = new string[subsystems.Length];
                    for (var position = 0; position < subsystems.Length; position++)
                        names[position] = subsystems[position].type == null ? string.Empty : subsystems[position].type.Name;
                    int index;
                    if (!DebugPlayerLoopLayout.TryResolve(phase, names, out index))
                    {
                        lastFailure = "phase-anchor-missing:" + phase;
                        return "phase-anchor-missing";
                    }
                    subsystems = InsertAt(subsystems, index, new PlayerLoopSystem { type = marker, updateDelegate = DelegateOf(phase) });
                    changed = true;
                }
                if (changed)
                {
                    loop.subSystemList = subsystems;
                    try
                    {
                        PlayerLoop.SetPlayerLoop(loop);
                    }
                    catch (Exception exception)
                    {
                        lastFailure = exception.GetType().Name;
                        UnityCliBridge.WriteLog("debug_player_loop_install_failed phase=" + phase + " error=" + exception.GetType().Name);
                        return "player-loop-unavailable";
                    }
                    insertedSubsystems++;
                }
                Owners[ownerId] = phase;
                // 同一个 owner 改声明另一个 phase 时，旧 phase 若不再有 owner 就立即移除，不留孤儿子系统。
                if (!string.IsNullOrEmpty(previous) && !string.Equals(previous, phase, StringComparison.Ordinal) && !Owners.Values.Any(value => string.Equals(value, previous, StringComparison.Ordinal)))
                    RemoveMarkerLocked(previous);
                return null;
            }
        }

        /// <summary>释放一个 owner 的现场；未登记时是幂等 no-op。返回是否真的释放了登记。</summary>
        internal static bool Release(string ownerId)
        {
            if (string.IsNullOrEmpty(ownerId)) return false;
            lock (Sync)
            {
                string phase;
                if (!Owners.TryGetValue(ownerId, out phase)) return false;
                Owners.Remove(ownerId);
                if (!Owners.Values.Any(value => string.Equals(value, phase, StringComparison.Ordinal))) RemoveMarkerLocked(phase);
                return true;
            }
        }

        /// <summary>
        /// 移除全部现场并清空 owner 记账；Play 退出、域重载与 Editor 关停共用这里。
        /// 只要还有任何 phase 被声明过，就必须真正移除对应子系统，而不是只清账。
        /// </summary>
        internal static bool ReleaseAll(string reason)
        {
            List<string> phases;
            lock (Sync)
            {
                phases = Owners.Values.Distinct().ToList();
                Owners.Clear();
                if (phases.Count == 0) return false;
                foreach (var phase in phases) RemoveMarkerLocked(phase);
            }
            UnityCliBridge.WriteLog("debug_player_loop_release_all reason=" + (reason ?? string.Empty) + " phases=" + phases.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return true;
        }

        /// <summary>phase 现场的结构化事实；安装计数是真实子系统数量，不是 owner 数量。</summary>
        internal static Dictionary<string, object> Describe()
        {
            lock (Sync)
            {
                return new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "installedPhases", InstalledLocked().ToArray() },
                    { "installedCount", (long)InstalledLocked().Count },
                    { "phaseSet", DebugPlayerLoopPhases.All() },
                    { "ownerCount", (long)Owners.Count },
                    { "insertedSubsystems", insertedSubsystems },
                    { "removedSubsystems", removedSubsystems },
                    { "idempotentInstalls", idempotentInstalls },
                    { "lastFailure", lastFailure }
                };
            }
        }

        // ------------------------------------------------------------------
        // PlayerLoop 写入
        // ------------------------------------------------------------------

        private static void AdvanceBeforeUpdate() { RunPhase(DebugPlayerLoopPhases.BeforeUpdate); }
        private static void AdvanceAfterUpdate() { RunPhase(DebugPlayerLoopPhases.AfterUpdate); }
        private static void AdvanceFixedUpdate() { RunPhase(DebugPlayerLoopPhases.FixedUpdate); }
        private static void AdvanceLateUpdate() { RunPhase(DebugPlayerLoopPhases.LateUpdate); }

        /// <summary>子系统入口：在真实 phase 上推进中央 scheduler，并在此后清掉本帧被请求的移除。</summary>
        private static void RunPhase(string phase)
        {
            phaseAdvanceDepth++;
            try
            {
                DebugScheduler.AdvancePhase(phase);
            }
            catch (Exception exception)
            {
                UnityCliBridge.WriteLog("debug_player_loop_phase_failed phase=" + phase + " error=" + exception.GetType().Name + ": " + UnityCliBridge.BoundedText(exception.Message, 256));
            }
            finally
            {
                phaseAdvanceDepth--;
                FlushPendingRemovals();
            }
        }

        private static PlayerLoopSystem.UpdateFunction DelegateOf(string phase)
        {
            if (string.Equals(phase, DebugPlayerLoopPhases.BeforeUpdate, StringComparison.Ordinal)) return AdvanceBeforeUpdate;
            if (string.Equals(phase, DebugPlayerLoopPhases.AfterUpdate, StringComparison.Ordinal)) return AdvanceAfterUpdate;
            if (string.Equals(phase, DebugPlayerLoopPhases.FixedUpdate, StringComparison.Ordinal)) return AdvanceFixedUpdate;
            return AdvanceLateUpdate;
        }

        private static Type MarkerOf(string phase)
        {
            if (string.Equals(phase, DebugPlayerLoopPhases.BeforeUpdate, StringComparison.Ordinal)) return typeof(BeforeUpdateSubsystem);
            if (string.Equals(phase, DebugPlayerLoopPhases.AfterUpdate, StringComparison.Ordinal)) return typeof(AfterUpdateSubsystem);
            if (string.Equals(phase, DebugPlayerLoopPhases.FixedUpdate, StringComparison.Ordinal)) return typeof(FixedUpdateSubsystem);
            return typeof(LateUpdateSubsystem);
        }

        private static bool ContainsMarker(PlayerLoopSystem[] subsystems, Type marker)
        {
            foreach (var subsystem in subsystems)
            {
                if (subsystem.type == marker) return true;
            }
            return false;
        }

        private static PlayerLoopSystem[] InsertAt(PlayerLoopSystem[] subsystems, int index, PlayerLoopSystem inserted)
        {
            var result = new PlayerLoopSystem[subsystems.Length + 1];
            for (var position = 0; position < index; position++) result[position] = subsystems[position];
            result[index] = inserted;
            for (var position = index; position < subsystems.Length; position++) result[position + 1] = subsystems[position];
            return result;
        }

        /// <summary>
        /// 从当前循环里删除本框架的一个标记子系统。正在自己的 phase 回调里时只登记，
        /// 由该回调结束时统一写入，避免在派发过程中改写正在被遍历的循环。
        /// </summary>
        private static void RemoveMarkerLocked(string phase)
        {
            if (phaseAdvanceDepth > 0)
            {
                if (!PendingRemovals.Contains(phase)) PendingRemovals.Add(phase);
                return;
            }
            ApplyRemoveLocked(phase);
        }

        private static void ApplyRemoveLocked(string phase)
        {
            var loop = PlayerLoop.GetCurrentPlayerLoop();
            var subsystems = loop.subSystemList ?? new PlayerLoopSystem[0];
            var marker = MarkerOf(phase);
            var kept = new List<PlayerLoopSystem>(subsystems.Length);
            foreach (var subsystem in subsystems)
            {
                if (subsystem.type == marker) continue;
                kept.Add(subsystem);
            }
            if (kept.Count == subsystems.Length) return;
            loop.subSystemList = kept.ToArray();
            try
            {
                PlayerLoop.SetPlayerLoop(loop);
                removedSubsystems++;
            }
            catch (Exception exception)
            {
                lastFailure = exception.GetType().Name;
                UnityCliBridge.WriteLog("debug_player_loop_remove_failed phase=" + phase + " error=" + exception.GetType().Name);
            }
        }

        private static void FlushPendingRemovals()
        {
            List<string> pending;
            lock (Sync)
            {
                if (PendingRemovals.Count == 0) return;
                pending = new List<string>(PendingRemovals);
                PendingRemovals.Clear();
            }
            foreach (var phase in pending)
            {
                lock (Sync) ApplyRemoveLocked(phase);
            }
        }

        private static List<string> InstalledLocked()
        {
            var installed = new List<string>();
            foreach (var phase in DebugPlayerLoopPhases.All())
            {
                if (Owners.Values.Any(value => string.Equals(value, phase, StringComparison.Ordinal))) installed.Add(phase);
            }
            return installed;
        }
    }
}
#endif
