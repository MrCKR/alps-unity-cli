#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Threading;

namespace UnityCli
{
    public sealed class UnityCliProjectToolContext
    {
        public string RequestId { get; set; }
        public string ProjectPath { get; set; }
        public string ArtifactDir { get; set; }
        public string LogPath { get; set; }

        /// <summary>当前框架请求是否只预览计划副作用。</summary>
        public bool DryRun { get; set; }

        internal Action CommitEffectAction { get; set; }
        private int commitEffectState;

        /// <summary>只有授权action成功返回才算跨过barrier；校验中或异常回滚均属于effect前。</summary>
        internal bool EffectCommitted { get { return Volatile.Read(ref commitEffectState) == 1; } }

        /// <summary>
        /// 所有输入、目标、包和权限前置校验完成后，紧邻首个真实 mutation 调用。
        /// 重复、dry-run 或 read-only 调用均 fail closed。
        /// </summary>
        public void CommitEffect()
        {
            if (DryRun)
                throw new UnityCliBridge.BridgeCommandException("E_TOOL_EFFECT_BARRIER_INVALID", "dry-run must not cross the mutation commit barrier.", "Return planned effects without calling ctx.CommitEffect().");
            if (CommitEffectAction == null)
                throw new UnityCliBridge.BridgeCommandException("E_TOOL_EFFECT_BARRIER_INVALID", "This request has no mutating commit boundary.", "Call ctx.CommitEffect() only for a Tool invocation classified as a mutation.");
            if (Interlocked.CompareExchange(ref commitEffectState, -1, 0) != 0)
                throw new UnityCliBridge.BridgeCommandException("E_TOOL_EFFECT_BARRIER_DUPLICATE", "The Tool crossed its mutation commit barrier more than once.", "Use one commit boundary immediately before the first business effect.");
            try
            {
                CommitEffectAction();
                Volatile.Write(ref commitEffectState, 1);
            }
            catch
            {
                Volatile.Write(ref commitEffectState, 0);
                throw;
            }
        }

        /// <summary>
        /// 对高风险同步动作执行声明式 before/effect/fresh-after proof。调用方不得在 action 内 yield；
        /// 方法内部负责唯一 CommitEffect，Tool 不得再次手动提交。
        /// </summary>
        public UnityCliCompletionProbeReadback RunCompletionProbe(UnityCliCompletionProbe probe, Action action)
        {
            return UnityCliBridge.ExecuteCompletionProbe(this, probe, action);
        }

        /// <summary>当前工具执行的超时上限（毫秒）。</summary>
        public int TimeoutMs { get; set; }

        /// <summary>框架级 wait 能力。工具只能通过这里申请跨帧/回调等待。</summary>
        public IUnityCliWaiter Wait { get; set; }

        /// <summary>当前请求的取消令牌。异步工具应把它继续传给 ctx.Wait.*。</summary>
        public CancellationToken CancellationToken { get; set; }

    }
}
#endif
