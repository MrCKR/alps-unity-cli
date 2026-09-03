#if UNITY_EDITOR
using System.Collections.Generic;
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
            var changed = scenes[index].enabled != enabled;
            var plannedEffects = changed
                ? new List<BuildScenePlannedEffect> { new BuildScenePlannedEffect { Type = toolName, Path = path, Enabled = enabled } }
                : new List<BuildScenePlannedEffect>();
            if (!ctx.DryRun && changed)
            {
                ctx.CommitEffect();
                scenes[index] = new EditorBuildSettingsScene(path, enabled);
                EditorBuildSettings.scenes = scenes;
                scenes = ReadScenes();
                index = scenes.ToList().FindIndex(scene => scene.path == path);
                if (index < 0 || scenes[index].enabled != enabled)
                    throw new UnityCliBridge.BridgeCommandException("E_BUILD_SCENE_STATE_MISMATCH", "Build Settings did not apply the requested enabled state for " + path + ".", "Inspect build.scene.list and retry.");
            }
            return new(new BuildSceneMutationResult
            {
                Count = scenes.LongLength,
                Scene = BuildEntry(scenes[index], index),
                Changed = !ctx.DryRun && changed,
                NoOpReason = !changed ? "scene-already-" + (enabled ? "enabled" : "disabled") : null,
                DryRun = ctx.DryRun,
                PlannedEffects = plannedEffects
            });
        }

        public static EditorBuildSettingsScene[] ReadScenes()
        {
            return EditorBuildSettings.scenes ?? new EditorBuildSettingsScene[0];
        }

        // 将 fresh EditorBuildSettings 读回投影为公开条目。
        public static BuildSceneEntry BuildEntry(EditorBuildSettingsScene scene, int index)
        {
            return new BuildSceneEntry { Path = BuildToolSupport.NormalizeAssetPath(scene.path), Enabled = scene.enabled, BuildIndex = index };
        }
    }
}
#endif
