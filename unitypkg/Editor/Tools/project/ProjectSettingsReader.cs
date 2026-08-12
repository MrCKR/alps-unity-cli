#if UNITY_EDITOR
namespace UnityCli
{
    /// <summary>
    /// 只读Tool的窄入口；只暴露四个无副作用snapshot方法，不提供mutation、保存或effect barrier。
    /// 具体字段与version surface仍由唯一ProjectSettingsToolSupport实现。
    /// </summary>
    internal static class ProjectSettingsReader
    {
        internal static ProjectAudioSettingsSnapshot ReadAudio(string toolName)
        {
            return ProjectSettingsToolSupport.ReadAudio(toolName);
        }

        internal static ProjectInputSettingsResult ReadInput(ProjectInputSettingsRequest request, string toolName)
        {
            return ProjectSettingsToolSupport.ReadInput(request, toolName);
        }

        internal static ProjectPlayerSettingsSnapshot ReadPlayer(string toolName)
        {
            return ProjectSettingsToolSupport.ReadPlayer(toolName);
        }

        internal static ProjectTimeSettingsSnapshot ReadTime(string toolName)
        {
            return ProjectSettingsToolSupport.ReadTime(toolName);
        }
    }
}
#endif
