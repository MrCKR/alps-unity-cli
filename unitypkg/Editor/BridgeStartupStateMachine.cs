#if UNITY_EDITOR
using System;

namespace UnityCli
{

    internal enum BridgeStartupPhase
    {
        None,
        Prime,
        DrainPipelineCallbacks,
        Complete
    }

    internal enum BridgeStartupTransition
    {
        None,
        Busy,
        PrimeSucceeded,
        PrimeFailed,
        DrainAdvanced,
        CompleteSucceeded,
        CompleteFailed
    }

    /// <summary>
    /// 管理当前domain内唯一Bridge启动链。busy只暂停当前phase；prime或handoff失败后立即归零，
    /// 后续tick不得重放已经失败或可能已产生效果的attempt。
    /// </summary>
    internal sealed class BridgeStartupStateMachine
    {
        internal BridgeStartupPhase Phase { get; private set; }

        /// <summary>仅在空闲状态创建一条启动链；重复调度不改变phase。</summary>
        internal bool Schedule()
        {
            if (Phase != BridgeStartupPhase.None) return false;
            Phase = BridgeStartupPhase.Prime;
            return true;
        }

        /// <summary>domain reload或退出时清除当前domain的机制状态。</summary>
        internal void Reset()
        {
            Phase = BridgeStartupPhase.None;
        }

        /// <summary>
        /// 每次稳定update最多推进一个phase。attempt由Bridge注入，便于验证调用次数与失败不重放约束。
        /// </summary>
        internal BridgeStartupTransition Tick(bool busy, Func<bool> prime, Func<bool> complete)
        {
            if (Phase == BridgeStartupPhase.None) return BridgeStartupTransition.None;
            if (busy) return BridgeStartupTransition.Busy;

            if (Phase == BridgeStartupPhase.Prime)
            {
                if (prime == null) throw new ArgumentNullException("prime");
                // prime可能在读取项目状态时抛出；调用前先归零，任何失败都不得由后续tick重放。
                Phase = BridgeStartupPhase.None;
                if (!prime()) return BridgeStartupTransition.PrimeFailed;
                Phase = BridgeStartupPhase.DrainPipelineCallbacks;
                return BridgeStartupTransition.PrimeSucceeded;
            }

            if (Phase == BridgeStartupPhase.DrainPipelineCallbacks)
            {
                Phase = BridgeStartupPhase.Complete;
                return BridgeStartupTransition.DrainAdvanced;
            }

            if (complete == null) throw new ArgumentNullException("complete");
            // complete可能跨越handoff effect；调用前先归零，任何结果都不得由后续tick重放。
            Phase = BridgeStartupPhase.None;
            return complete()
                ? BridgeStartupTransition.CompleteSucceeded
                : BridgeStartupTransition.CompleteFailed;
        }
    }
}
#endif
