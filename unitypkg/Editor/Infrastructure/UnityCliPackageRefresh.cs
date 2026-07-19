#if UNITY_EDITOR
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        // 注册 package 安装控制面使用的窄权限刷新命令；不依赖业务 Tool registry。
        private static void RegisterPackageRefreshCommand(BridgeCommandRegistry registry)
        {
            registry.Register<UnityCli.Protocol.BridgeRequest>(
                "bridge.package.refresh",
                "bridge",
                false,
                15000,
                (request, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return new ValueTask<object>(RefreshPackageAssets(request));
                });
        }

        /// <summary>在 durable effect barrier 后同步刷新，并仅以已完成 readback 登记 terminal proof。</summary>
        private static object RefreshPackageAssets(UnityCli.Protocol.BridgeRequest request)
        {
            BeginDirectEffect(request.RequestId);
            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
            var result = new Dictionary<string, object>
            {
                { "command", "bridge.package.refresh" },
                { "status", "completed" },
                { "refreshed", true },
                { "domainReloadPossible", true }
            };
            RecordDirectCompletionEvidence(request.RequestId, "bridge.package.refresh", ResolveRequestCompletionContract(request), result);
            return result;
        }
    }
}
#endif
