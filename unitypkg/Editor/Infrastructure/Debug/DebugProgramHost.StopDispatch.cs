#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace UnityCli
{
    /// <summary>
    /// program 设施在 session 停止中的两个条目：
    ///
    /// 1. 关闭条目（stop-dispatch）：关闭该 session 每个 program 的未来入口，取消尚未到达执行点的投递。
    /// 2. 解除条目（cancel-and-resume）：对已经取得执行权、正在同步代码中的运行发出协作取消。
    ///
    /// 规则：
    /// - 两个条目都是每个 session 各一条，因此清理条目预算与 program 数量无关。
    /// - 关闭入口不删除定义、不改变 revision，也不撤除 detour（那是 program-detach 条目）。
    /// - 同步运行中的用户代码不能被抢占：解除条目只请求取消，运行状态由代码自己返回后收敛。
    /// </summary>
    internal static partial class DebugProgramHost
    {
        /// <summary>已经注册过 session 级关闭与解除条目的 session。</summary>
        private static readonly HashSet<string> SessionStopStepsRegistered = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// 首次为一个 session 安装 program 时登记关闭与解除条目；重复注册冲突视为已存在。
        /// </summary>
        private static void RegisterSessionStopSteps(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return;
            lock (Sync)
            {
                if (!SessionStopStepsRegistered.Add(sessionId)) return;
            }
            var dispatchFailure = DebugSessionHost.RegisterCleanupStep(
                sessionId,
                UnityCliDebugCleanupSteps.OrderOf(UnityCliDebugCleanupSteps.StopDispatch),
                "program-dispatch:" + sessionId,
                UnityCliDebugFacilityOwners.Program,
                report => CloseSessionProgramDispatch(sessionId, report));
            var cancelFailure = DebugSessionHost.RegisterCleanupStep(
                sessionId,
                UnityCliDebugCleanupSteps.OrderOf(UnityCliDebugCleanupSteps.CancelAndResume),
                "program-cancel:" + sessionId,
                UnityCliDebugFacilityOwners.Program,
                report => RequestSessionProgramCancellation(sessionId, report));
            if (dispatchFailure == null && cancelFailure == null) return;
            lock (Sync) SessionStopStepsRegistered.Remove(sessionId);
            UnityCliBridge.WriteLog("debug_program_stop_steps_reused session=" + sessionId
                + " dispatch=" + (dispatchFailure ?? "ok") + " cancel=" + (cancelFailure ?? "ok"));
        }

        /// <summary>关闭条目（stop-dispatch）：关闭每个 program 的未来入口并取消未到达执行点的投递。</summary>
        private static bool CloseSessionProgramDispatch(string sessionId, UnityCliDebugCleanupReport report)
        {
            var session = Registry();
            foreach (var program in session.ProgramsFor(sessionId))
            {
                // 有非终态运行表示该入口原本确实会继续执行：关闭它是一条可证明的事实。
                var pendingRuns = session.RunsFor(program.ProgramId).Count(run => !run.IsTerminal);
                if (report != null && pendingRuns > 0) report.StoppedFutureEntries++;
                if (report == null) continue;
                report.CancelledPendingOperations += DebugScheduler.DropPendingForProgram(program.ProgramId);
            }
            return true;
        }

        /// <summary>解除条目（cancel-and-resume）：对未终态运行发出协作取消；正在执行的同步代码不被抢占。</summary>
        private static bool RequestSessionProgramCancellation(string sessionId, UnityCliDebugCleanupReport report)
        {
            var session = Registry();
            foreach (var program in session.ProgramsFor(sessionId))
            {
                foreach (var run in session.RunsFor(program.ProgramId).Where(candidate => !candidate.IsTerminal).ToList())
                {
                    session.RequestCancellation(run.ProgramRunId, "The owning session stopped, so this run was asked to cancel.");
                }
            }
            if (report != null) report.UntrackedEffectsPossible = report.UntrackedEffectsPossible || session.ProgramsFor(sessionId).Count > 0;
            return true;
        }
    }
}
#endif
