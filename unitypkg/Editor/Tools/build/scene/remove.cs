#if UNITY_EDITOR
using System.Linq;
using UnityEditor;

namespace UnityCli
{
    public class BuildSceneRemoveTool : IUnityCliTool<BuildSceneRemoveTool.Request, BuildSceneMutationResult>
    {
        public sealed class Request { [UnityCliRequired] public string Path { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "build.scene.remove", Summary = "Remove a scene from persistent Unity Build Settings.", Description = "Removes path from EditorBuildSettings. dryRun=true previews without writing. Available in Edit and Play mode.", Group = "build",DestructiveHint = true, SupportsDryRun = true, SideEffects = new[] { "project-settings-write" }, Target = "editor", PlayModeSupport = "editor-only", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Remove main scene", Input = new Request { Path = "Assets/Scenes/Main.unity" }, Execution = new ToolExampleExecution { DryRun = true } } } };
        public System.Threading.Tasks.ValueTask<BuildSceneMutationResult> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var path = BuildToolSupport.NormalizeAssetPath(request.Path);
            if (string.IsNullOrEmpty(path))
                throw new UnityCliBridge.BridgeCommandException("E_BUILD_SCENE_PATH_REQUIRED", "build.scene.remove requires path.", "Pass a scene path such as Assets/Scenes/Main.unity.");
            var scenes = BuildSceneStateTool.ReadScenes();
            if (!ctx.DryRun && scenes.Any(scene => scene.path == path))
            {
                ctx.CommitEffect();
                EditorBuildSettings.scenes = scenes.Where(scene => scene.path != path).ToArray();
                scenes = BuildSceneStateTool.ReadScenes();
                if (scenes.Any(scene => scene.path == path))
                    throw new UnityCliBridge.BridgeCommandException("E_BUILD_SCENE_REMOVE_MISMATCH", "Build Settings did not remove scene: " + path, "Inspect build.scene.list and retry.");
            }
            return new(new BuildSceneMutationResult { Count = scenes.LongLength, DryRun = ctx.DryRun });
        }
    }
}
#endif
