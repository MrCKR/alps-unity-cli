#if UNITY_EDITOR
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        private sealed class PackageRefreshRequest
        {
            public PackageRefreshRequest() { }
        }

        // 注册 package 安装控制面使用的窄权限刷新命令；不依赖业务 Tool registry。
        private static void RegisterPackageRefreshCommand(BridgeCommandRegistry registry)
        {
            registry.Register<PackageRefreshRequest>(
                "bridge.package.refresh",
                "bridge",
                false,
                15000,
                RefreshPackageAssets);
        }

        // 触发 Unity package/import 刷新；可能引发 compile 与 domain reload，但不直接写入项目资产。
        private static ValueTask<object> RefreshPackageAssets(PackageRefreshRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<object>(new Dictionary<string, object>
            {
                { "command", "bridge.package.refresh" },
                { "status", "triggered" },
                { "domainReloadPossible", true }
            });
        }
    }
}
#endif
