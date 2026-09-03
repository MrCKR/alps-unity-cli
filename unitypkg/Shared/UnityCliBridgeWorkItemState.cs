using System;

namespace UnityCli
{
    /// <summary>Bridge WorkItem准入、启动与HTTP取消的纯状态裁决。</summary>
    public sealed class UnityCliBridgeWorkItemState
    {
        public enum StartOutcome { Started, Cancelled, ListenerChanged, DeadlineElapsed }

        private bool cancelled;
        private bool accepted;
        private bool started;

        public bool Started { get { return started; } }
        public bool Accepted { get { return accepted; } }

        public StartOutcome TryStart(bool listenerMatches, int remainingMs)
        {
            if (cancelled) return StartOutcome.Cancelled;
            if (!listenerMatches) { cancelled = true; return StartOutcome.ListenerChanged; }
            if (remainingMs <= 0) { cancelled = true; return StartOutcome.DeadlineElapsed; }
            started = true;
            return StartOutcome.Started;
        }

        public bool TryAccept()
        {
            if (cancelled || accepted) return false;
            accepted = true;
            return true;
        }

        public bool CancelBeforeAcceptance()
        {
            if (accepted) return false;
            cancelled = true;
            return true;
        }

        public bool CancelBeforeStart()
        {
            if (started) return false;
            cancelled = true;
            return true;
        }
    }
}
