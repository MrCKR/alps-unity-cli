#if UNITY_EDITOR

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        // scene 域工具已全部迁移为 IUnityCliTool 自包含形态(unitypkg/Editor/Tools/scene/*.cs)。
        // 保留空注册方法以兼容 BridgeCommandRegistry.CreateCommandRegistry 的调用点;
        // 此处不再注册任何 RegisterCommand。
        private static void RegisterSceneCommands(BridgeCommandRegistry registry)
        {
        }
    }
}
#endif
