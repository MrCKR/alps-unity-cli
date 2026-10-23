#if UNITY_EDITOR
using System.Collections.Generic; using UnityEditor;
namespace UnityCli
{
    public class BuildProfileListTool : IUnityCliTool<BuildProfileListTool.Request, BuildProfileListResult>
    {
        public sealed class Request { }
        public ToolDescriptor Describe() => new() { Name = "build.profile-list", Summary = "List Unity Build Profiles and identify the active profile.", Description = "Returns Build Profile assets and their active state. When this Unity Editor has no Build Profile API, returns available=false and an empty list. Read-only and available in Edit and Play mode.", Group = "build",Target = "editor", PlayModeSupport = "editor-only", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "List build profiles", Input = new Request() } } };
        public System.Threading.Tasks.ValueTask<BuildProfileListResult> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default) { var type = BuildToolSupport.BuildProfileType(); if (type == null) return new(new BuildProfileListResult { Available = false, Profiles = new List<BuildProfileEntry>() }); var active = BuildToolSupport.ActiveBuildProfile(type); var profiles = new List<BuildProfileEntry>(); foreach (var guid in AssetDatabase.FindAssets("t:" + type.Name)) { var path = AssetDatabase.GUIDToAssetPath(guid); var profile = AssetDatabase.LoadAssetAtPath(path, type); profiles.Add(new BuildProfileEntry { Path = path, Name = profile == null ? System.IO.Path.GetFileNameWithoutExtension(path) : profile.name, Active = profile == active }); } return new(new BuildProfileListResult { Available = true, ActivePath = active == null ? null : AssetDatabase.GetAssetPath(active), Profiles = profiles }); }
    }
}
#endif
