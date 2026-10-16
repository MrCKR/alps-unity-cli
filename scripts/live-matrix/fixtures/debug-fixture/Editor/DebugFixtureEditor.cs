// Dev 专用夹具的 Editor 侧装配入口（T-18）。本文件位于 Editor 子目录，因此编译进 Editor 程序集，
// 可以使用 UnityEditor。这里不做 [InitializeOnLoad] 自动启动，避免脚本重载产生不可控的隐式线程。
//
// 顺序约束：BindMainThread 必须在 Unity 主线程先执行一次，
// 之后 StartWorkers 与 RefreshShadow 才能成立（否则 DebugFixture 抛 InvalidOperationException）。
// MenuItem 与静态方法指向同一实现，保证调试会话与人工操作走同一条路径。

using UnityEditor;

namespace UnityCliDebugFixture
{
    /// <summary>夹具的 Editor 侧装配与卸载入口。</summary>
    public static class DebugFixtureEditor
    {
        /// <summary>装配入口：绑定主线程 -> 主线程刷新显示副本 -> 启动两个 worker。</summary>
        [MenuItem("Tools/UnityCLI Debug Fixture/Start")]
        public static void Start()
        {
            DebugFixture.BindMainThread();
            DebugFixture.RefreshShadow();
            DebugFixture.StartWorkers();
        }

        /// <summary>卸载入口：停止两个 worker；是否复位由调用方显式执行。</summary>
        [MenuItem("Tools/UnityCLI Debug Fixture/Stop")]
        public static void Stop()
        {
            DebugFixture.StopWorkers();
        }

        /// <summary>主线程刷新显示副本；非主线程调用会抛异常。</summary>
        [MenuItem("Tools/UnityCLI Debug Fixture/Refresh Shadow")]
        public static void RefreshShadow()
        {
            DebugFixture.RefreshShadow();
        }
    }
}
