#if UNITY_EDITOR
using System.Collections.Generic;

namespace UnityCli
{
    /// <summary>urp.volume.create：创建仅存在于当前会话的 URP Volume。</summary>
    public sealed class UrpVolumeCreateTool : IUnityCliTool<UrpVolumeCreateTool.Request, UrpVolumeCreateTool.Result>
    {
        public sealed class Request { public string Name { get; set; } public bool? IsGlobal { get; set; } public double? Priority { get; set; } public double? Weight { get; set; } }
        public sealed class Result { public string Command { get; set; } public GameObjectRef Target { get; set; } public bool IsGlobal { get; set; } public List<UrpPlannedEffect> PlannedEffects { get; set; } public string LogPath { get; set; } }

        public ToolDescriptor Describe() => new()
        {
            Name = "urp.volume.create",
            Summary = "Create a scene-session URP Volume with a new in-memory profile.",
            Description = "Creates a URP Volume GameObject and assigns a new in-memory VolumeProfile. name defaults to URPVolume; isGlobal defaults to true. The scene and profile are not saved as assets. Requires com.unity.render-pipelines.universal and an active UniversalRenderPipelineAsset.",
            Group = "urp", Version = "1.0.0", DestructiveHint = false,
            RequiresPackages = new[] { "com.unity.render-pipelines.universal" }, SideEffects = new[] { "scene-modify", "volume-profile-edit" }, Target = "editor", PlayModeSupport = "session-affecting", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Create a global URP volume", Input = new Request { Name = "GlobalVolume", IsGlobal = true } }, new ToolExample { Description = "Create a weighted URP volume", Input = new Request { Name = "OptionalURPVolume", IsGlobal = true, Priority = 1, Weight = 0.5 } } }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            GameObjectToolSupport.EnsureEditorNotBusy("urp.volume.create");
            UrpToolSupport.EnsureUrpAvailable();
            var plan = UrpVolumeOperations.ValidateCreate(request.Priority, request.Weight, "E_URP");
            ctx.CommitEffect();
            var created = UrpVolumeOperations.Create(request.Name, request.IsGlobal, plan, "URP", "E_URP");
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Command = "urp.volume.create", Target = GameObjectToolSupport.BuildGameObjectRef(created.GameObject), IsGlobal = created.IsGlobal, PlannedEffects = new List<UrpPlannedEffect> { new UrpPlannedEffect { Type = "urp.volume.create", Name = created.GameObject.name } }, LogPath = ctx.LogPath });
        }
    }
}
#endif
