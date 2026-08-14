#if UNITY_EDITOR
using System.Collections.Generic;
namespace UnityCli
{
    public sealed class BuildPlatformResult { public string ActiveTarget { get; set; } public string ActiveTargetGroup { get; set; } public bool Switched { get; set; } public bool Changed { get; set; } public string NoOpReason { get; set; } public bool DryRun { get; set; } }
    public sealed class BuildProfileEntry { public string Path { get; set; } public string Name { get; set; } public bool Active { get; set; } }
    public sealed class BuildProfileListResult { public bool Available { get; set; } public string ActivePath { get; set; } public List<BuildProfileEntry> Profiles { get; set; } }
    public sealed class BuildProfileMutationResult { public bool Available { get; set; } public string Action { get; set; } public string ActivePath { get; set; } public bool DryRun { get; set; } }
    public sealed class BuildSceneEntry { public string Path { get; set; } public bool Enabled { get; set; } public long BuildIndex { get; set; } }
    public sealed class BuildSceneListResult { public long Count { get; set; } public List<BuildSceneEntry> Scenes { get; set; } }
    public sealed class BuildSceneMutationResult { public long Count { get; set; } public bool Changed { get; set; } public string NoOpReason { get; set; } public bool DryRun { get; set; } }
    public sealed class BuildSettingsResult { public string ProductName { get; set; } public string BundleVersion { get; set; } public string CompanyId { get; set; } public string ApplicationIdentifier { get; set; } public string ScriptingBackend { get; set; } public string ApiCompatibilityLevel { get; set; } public List<string> Changed { get; set; } public bool DryRun { get; set; } }
    public sealed class BuildSettingsMutationResult { public List<string> Changed { get; set; } public bool DryRun { get; set; } }
}
#endif
