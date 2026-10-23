#if UNITY_EDITOR
using System.Linq;
using UnityEditor;

namespace UnityCli
{
    public class BuildSceneRemoveTool : IUnityCliTool<BuildSceneRemoveTool.Request, BuildSceneMutationResult>
    {
        public sealed class Request { [UnityCliRequired] public string Path { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "build.scene-remove", Summary = "Remove a scene from persistent Unity Build Settings.", Description = "Removes one exact scene path from EditorBuildSettings. Available in Edit and Play mode. dryRun previews the path; success returns the fresh remaining count, changed state, and no-op reason.", Group = "build",DestructiveHint = true, SupportsDryRun = true, SideEffects = new[] { "project-settings-write" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Remove main scene", Input = new Request { Path = "Assets/Scenes/Main.unity" }, Execution = new ToolExampleExecution { DryRun = true } } } };
        public System.Threading.Tasks.ValueTask<BuildSceneMutationResult> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var path = BuildToolSupport.NormalizeAssetPath(request.Path);
            if (string.IsNullOrEmpty(path))
                throw new UnityCliBridge.BridgeCommandException("E_BUILD_SCENE_PATH_REQUIRED", "build.scene-remove requires path.", "Pass a scene path such as Assets/Scenes/Main.unity.");
            var scenes = BuildSceneStateTool.ReadScenes();
            var existingIndex = System.Array.FindIndex(scenes, scene => scene.path == path);
            var changed = existingIndex >= 0;
            if (!ctx.DryRun && changed)
            {
                ctx.CommitEffect();
                EditorBuildSettings.scenes = scenes.Where(scene => scene.path != path).ToArray();
                scenes = BuildSceneStateTool.ReadScenes();
                if (scenes.Any(scene => scene.path == path))
                    throw new UnityCliBridge.BridgeCommandException("E_BUILD_SCENE_REMOVE_MISMATCH", "Build Settings did not remove scene: " + path, "Inspect build.scene-list and retry.");
            }
            return new(new BuildSceneMutationResult
            {
                Count = scenes.LongLength,
                Scene = ctx.DryRun && changed ? BuildSceneStateTool.BuildEntry(scenes[existingIndex], existingIndex) : null,
                Changed = !ctx.DryRun && changed,
                NoOpReason = !changed ? "scene-not-registered" : null,
                DryRun = ctx.DryRun,
                PlannedEffects = changed ? new System.Collections.Generic.List<BuildScenePlannedEffect> { new BuildScenePlannedEffect { Type = "build.scene-remove", Path = path } } : new System.Collections.Generic.List<BuildScenePlannedEffect>()
            });
        }
    }
}
#endif
