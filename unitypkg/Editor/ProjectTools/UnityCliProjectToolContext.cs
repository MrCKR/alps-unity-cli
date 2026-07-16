#if UNITY_EDITOR
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

        /// <summary>当前工具执行的超时上限（毫秒）。</summary>
        public int TimeoutMs { get; set; }

        /// <summary>框架级 wait 能力。工具只能通过这里申请跨帧/回调等待。</summary>
        public IUnityCliWaiter Wait { get; set; }

        /// <summary>当前请求的取消令牌。异步工具应把它继续传给 ctx.Wait.*。</summary>
        public CancellationToken CancellationToken { get; set; }

    }
}
#endif
