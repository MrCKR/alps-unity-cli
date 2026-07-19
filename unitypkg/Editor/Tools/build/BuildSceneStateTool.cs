#if UNITY_EDITOR
using System.Linq;
using UnityEditor;

namespace UnityCli
{
    /// <summary>Build Settings 场景启用状态的共享读回校验。</summary>
    public static class BuildSceneStateTool
    {
        public static System.Threading.Tasks.ValueTask<BuildSceneMutationResult> Execute(string rawPath, bool enabled, string toolName, UnityCliProjectToolContext ctx)
        {
            var path = BuildToolSupport.NormalizeAssetPath(rawPath);
            if (string.IsNullOrEmpty(path))
                throw new UnityCliBridge.BridgeCommandException("E_BUILD_SCENE_PATH_REQUIRED", toolName + " requires path.", "Pass a scene path such as Assets/Scenes/Main.unity.");
            var scenes = ReadScenes();
            var index = scenes.ToList().FindIndex(scene => scene.path == path);
            if (index < 0)
                throw new UnityCliBridge.BridgeCommandException("E_BUILD_SCENE_NOT_FOUND", "Build Settings does not contain scene: " + path, "Run build.scene.list and pass a listed path.");
            if (!ctx.DryRun)
            {
                ctx.CommitEffect();
                scenes[index] = new EditorBuildSettingsScene(path, enabled);
                EditorBuildSettings.scenes = scenes;
                scenes = ReadScenes();
                if (!scenes.Any(scene => scene.path == path && scene.enabled == enabled))
                    throw new UnityCliBridge.BridgeCommandException("E_BUILD_SCENE_STATE_MISMATCH", "Build Settings did not apply the requested enabled state for " + path + ".", "Inspect build.scene.list and retry.");
            }
            return new(new BuildSceneMutationResult { Path = path, Count = scenes.LongLength, DryRun = ctx.DryRun });
        }

        public static EditorBuildSettingsScene[] ReadScenes()
        {
            return EditorBuildSettings.scenes ?? new EditorBuildSettingsScene[0];
        }
    }
}
#endif
