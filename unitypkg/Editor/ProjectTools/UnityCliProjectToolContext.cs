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

        /// <summary>
        /// true 时本次 redelivery 只能重观察原 effect，不得再次执行 mutation。
        /// 仅 sealed completion driver 可设置；普通 Tool 首次执行始终为 false。
        /// </summary>
        public bool ProofOnly { get; set; }

        /// <summary>proof-only redelivery 的 durable、非敏感 continuation；首次执行恒为 null。</summary>
        public IDictionary<string, object> ProofContinuation { get; set; }

        /// <summary>当前私有 listener generation，仅供 sealed completion driver 对比 lifecycle replacement。</summary>
        public long ListenerGeneration { get; set; }

        /// <summary>读取并校验 sealed driver continuation，kind 不匹配时 fail closed。</summary>
        public IDictionary<string, object> RequireProofContinuation(string kind)
        {
            object actual;
            if (!ProofOnly || ProofContinuation == null || !ProofContinuation.TryGetValue("kind", out actual) || !string.Equals(actual as string, kind, System.StringComparison.Ordinal))
                throw new UnityCliBridge.BridgeCommandException("E_REQUEST_JOURNAL_CORRUPT", "Proof recovery continuation is missing or has the wrong kind.", "Inspect the request journal before submitting a new request.");
            return ProofContinuation;
        }

        internal Action CommitEffectAction { get; set; }
        private int commitEffectCalled;

        /// <summary>
        /// 所有输入、目标、包和权限前置校验完成后，紧邻首个真实 mutation 调用。
        /// 框架以此写入 durable effect_started；重复、dry-run、proof-only 或 read-only 调用均 fail closed。
        /// </summary>
        public void CommitEffect()
        {
            if (DryRun)
                throw new UnityCliBridge.BridgeCommandException("E_TOOL_EFFECT_BARRIER_INVALID", "dry-run must not cross the mutation commit barrier.", "Return planned effects without calling ctx.CommitEffect().");
            if (ProofOnly)
                throw new UnityCliBridge.BridgeCommandException("E_TOOL_EFFECT_BARRIER_INVALID", "proof-only recovery must not replay the mutation commit.", "Reobserve the durable continuation without calling ctx.CommitEffect().");
            if (CommitEffectAction == null)
                throw new UnityCliBridge.BridgeCommandException("E_TOOL_EFFECT_BARRIER_INVALID", "This request has no mutating commit boundary.", "Call ctx.CommitEffect() only for a Tool invocation classified as a mutation.");
            if (Interlocked.Exchange(ref commitEffectCalled, 1) != 0)
                throw new UnityCliBridge.BridgeCommandException("E_TOOL_EFFECT_BARRIER_DUPLICATE", "The Tool crossed its mutation commit barrier more than once.", "Use one commit boundary immediately before the first business effect.");
            CommitEffectAction();
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
