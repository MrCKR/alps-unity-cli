#if UNITY_EDITOR
using System;

namespace UnityCli
{

    internal enum BridgeStartupPhase
    {
        None,
        Prime,
        DrainPipelineCallbacks,
        Complete,
        RegistryPublication
    }

    internal enum BridgeStartupTransition
    {
        None,
        Busy,
        PrimeSucceeded,
        PrimeFailed,
        DrainAdvanced,
        RegistryPublicationPending,
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

        /// <summary>handoff成功后由首次registry publication继续持有启动状态。</summary>
        internal void BeginRegistryPublication()
        {
            if (Phase != BridgeStartupPhase.None) throw new InvalidOperationException("Bridge startup cannot begin registry publication from the current state.");
            Phase = BridgeStartupPhase.RegistryPublication;
        }

        internal void CompleteRegistryPublication()
        {
            if (Phase == BridgeStartupPhase.RegistryPublication) Phase = BridgeStartupPhase.None;
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
            if (Phase == BridgeStartupPhase.RegistryPublication) return BridgeStartupTransition.RegistryPublicationPending;
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

    internal enum BridgeInitialPublicationAction
    {
        None,
        Wait,
        OpenQueueAndListen,
        StopListener
    }

    /// <summary>listener首次registry claim之前保持queue关闭；只有Written开放，stale或永久失败均停止。</summary>
    internal sealed class BridgeInitialPublicationGate
    {
        private bool waiting;
        private bool published;

        internal bool Published { get { return published; } }

        internal void Start()
        {
            if (waiting || published) throw new InvalidOperationException("Initial Bridge publication gate is already active.");
            waiting = true;
        }

        internal BridgeInitialPublicationAction Apply(InstancePublicationRetryOutcome outcome)
        {
            if (!waiting) return BridgeInitialPublicationAction.None;
            if (outcome == InstancePublicationRetryOutcome.Pending || outcome == InstancePublicationRetryOutcome.None)
                return BridgeInitialPublicationAction.Wait;
            waiting = false;
            if (outcome == InstancePublicationRetryOutcome.Written)
            {
                published = true;
                return BridgeInitialPublicationAction.OpenQueueAndListen;
            }
            return BridgeInitialPublicationAction.StopListener;
        }

        internal void Reset()
        {
            waiting = false;
            published = false;
        }
    }
}
#endif
