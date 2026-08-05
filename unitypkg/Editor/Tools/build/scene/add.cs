#if UNITY_EDITOR
using System.Linq;
using UnityEditor;

namespace UnityCli
{
    public class BuildSceneAddTool : IUnityCliTool<BuildSceneAddTool.Request, BuildSceneMutationResult>
    {
        public sealed class Request { [UnityCliRequired] public string Path { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "build.scene.add", Summary = "Add an enabled scene to persistent Unity Build Settings.", Description = "Adds path to EditorBuildSettings as an enabled scene when absent. dryRun=true previews without writing. Available in Edit and Play mode.", Group = "build",DestructiveHint = true, SupportsDryRun = true, SideEffects = new[] { "project-settings-write" }, Target = "editor", PlayModeSupport = "editor-only", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Add main scene", Input = new Request { Path = "Assets/Scenes/Main.unity" }, Execution = new ToolExampleExecution { DryRun = true } } } };
        public System.Threading.Tasks.ValueTask<BuildSceneMutationResult> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var path = BuildToolSupport.NormalizeAssetPath(request.Path);
            if (string.IsNullOrEmpty(path))
                throw new UnityCliBridge.BridgeCommandException("E_BUILD_SCENE_PATH_REQUIRED", "build.scene.add requires path.", "Pass a scene path such as Assets/Scenes/Main.unity.");
            if (!path.StartsWith("Assets/", System.StringComparison.Ordinal) || !path.EndsWith(".unity", System.StringComparison.OrdinalIgnoreCase) || path.Contains(".."))
                throw new UnityCliBridge.BridgeCommandException("E_BUILD_SCENE_PATH_INVALID", "Build scene path must be a canonical Assets/*.unity path without traversal.", "Pass an existing path such as Assets/Scenes/Main.unity.");
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(path) == null)
                throw new UnityCliBridge.BridgeCommandException("E_BUILD_SCENE_NOT_FOUND", "Scene asset not found: " + path, "Pass an existing Assets/*.unity scene asset.");
            var scenes = BuildSceneStateTool.ReadScenes();
            var existingIndex = System.Array.FindIndex(scenes, scene => scene.path == path);
            var changed = existingIndex < 0 || !scenes[existingIndex].enabled;
            if (!ctx.DryRun && changed)
            {
                ctx.CommitEffect();
                if (existingIndex < 0)
                    scenes = scenes.Concat(new[] { new EditorBuildSettingsScene(path, true) }).ToArray();
                else
                    scenes[existingIndex] = new EditorBuildSettingsScene(path, true);
                EditorBuildSettings.scenes = scenes;
                scenes = BuildSceneStateTool.ReadScenes();
                if (!scenes.Any(scene => scene.path == path && scene.enabled))
                    throw new UnityCliBridge.BridgeCommandException("E_BUILD_SCENE_ADD_MISMATCH", "Build Settings did not add enabled scene: " + path, "Inspect build.scene.list and retry.");
            }
            return new(new BuildSceneMutationResult
            {
                Path = path,
                Count = scenes.LongLength,
                Changed = !ctx.DryRun && changed,
                NoOpReason = !ctx.DryRun && !changed ? "scene-already-enabled" : null,
                DryRun = ctx.DryRun
            });
        }
    }
}
#endif
