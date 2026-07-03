#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace UnityCli
{
    /// <summary>
    /// scene.unload — 卸载一个 additive 加载场景。不能卸载唯一打开的场景。支持 dryRun。
    /// 对齐旧 SceneUnload handler;session-affecting。
    /// 设计依据:.plan/工具统一形态.md(C# Bridge 工具 → C# 自包含)。
    /// </summary>
    public class SceneUnloadTool : IUnityCliTool<SceneUnloadTool.Request, SceneUnloadTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired]
            public string Path { get; set; }
            public string Name { get; set; }
            public bool? RemoveAll { get; set; }
            public bool? Save { get; set; }
            public bool? DryRun { get; set; }
        }

        public sealed class Result
        {
            public SceneInfo Scene { get; set; }
            public string RequestedPath { get; set; }
            public bool? DryRun { get; set; }
            public string SceneName { get; set; }
            public bool? IsDirty { get; set; }
            public bool? WillSave { get; set; }
            public List<PlannedEffect> PlannedEffects { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "scene.unload",
            Summary = "Unload an additively-loaded scene from the Editor.",
            Description = "Unload an additively-loaded scene from the Editor. Cannot unload the only open scene. Destructive: removes the scene from the Editor session. Supports --dry-run.",
            Group = "scene",
            Version = "1.0.0",
            DestructiveHint = false,
            SupportsDryRun = true,
            SideEffects = new[] { "scene-modify" },
            Target = "editor",
            PlayModeSupport = "session-affecting",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Preview unloading a scene", Input = new Request { Path = "Assets/Scenes/Level2.unity", DryRun = true } }
            }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            SceneToolSupport.EnsureEditorNotBusy("scene.unload");
            var path = SceneToolSupport.ResolveScenePath(request.Path ?? request.Name, true);
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (string.Equals(SceneToolSupport.NormalizeAssetPath(scene.path), path, System.StringComparison.OrdinalIgnoreCase))
                {
                    if (scene == SceneManager.GetActiveScene() && SceneManager.sceneCount == 1)
                    {
                        throw new UnityCliBridge.BridgeCommandException("E_SCENE_UNLOAD_LAST", "Cannot unload the only open scene.", "Open another scene first.");
                    }
                    var removeAll = request.RemoveAll == true;
                    var opts = removeAll ? UnloadSceneOptions.UnloadAllEmbeddedSceneObjects : UnloadSceneOptions.None;
                    var saveDirty = request.Save == true;
                    if (request.DryRun == true)
                    {
                        return new Result
                        {
                            Scene = SceneToolSupport.BuildSceneInfo(SceneManager.GetActiveScene()),
                            RequestedPath = path,
                            DryRun = true,
                            SceneName = scene.name,
                            IsDirty = scene.isDirty,
                            WillSave = saveDirty && scene.isDirty,
                            PlannedEffects = new List<PlannedEffect>
                            {
                                new PlannedEffect { Type = "scene.unload", Path = path }
                            }
                        };
                    }
                    // dirty 保存策略:save=true 且场景有未保存改动时先保存再卸载,避免丢失改动。
                    if (saveDirty && scene.isDirty && !string.IsNullOrEmpty(scene.path))
                    {
                        EditorSceneManager.SaveScene(scene);
                    }
                    EditorSceneManager.CloseScene(scene, opts == UnloadSceneOptions.UnloadAllEmbeddedSceneObjects);
                    return new Result
                    {
                        Scene = SceneToolSupport.BuildSceneInfo(SceneManager.GetActiveScene()),
                        RequestedPath = path,
                        PlannedEffects = new List<PlannedEffect>
                        {
                            new PlannedEffect { Type = "scene.unload", Path = path }
                        }
                    };
                }
            }
            throw new UnityCliBridge.BridgeCommandException("E_SCENE_NOT_OPEN", "Scene is not currently open: " + path, "Run context read scene.opened to list open scenes.");
        }
    }
}
#endif
