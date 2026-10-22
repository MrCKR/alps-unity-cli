#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace UnityCli
{
    /// <summary>保存 active scene，或显式保存全部 open dirty scenes 并逐项证明 clean terminal。</summary>
    public class SceneSaveTool : IUnityCliTool<SceneSaveTool.Request, SceneSaveTool.Result>
    {
        public sealed class Request
        {
            public string Path { get; set; }
            public string Name { get; set; }
            public bool All { get; set; }
        }

        public sealed class Result
        {
            public long SavedCount { get; set; }
            public SceneInfo Scene { get; set; }
            public List<SceneSaveReadback> Scenes { get; set; }
            public string RequestedPath { get; set; }
            public bool? DryRun { get; set; }
            public List<ScenePlannedEffect> PlannedEffects { get; set; }
        }

        public sealed class SceneSaveReadback
        {
            public SceneInfo Scene { get; set; }
            public string Guid { get; set; }
            public bool WasDirty { get; set; }
            public bool Saved { get; set; }
        }

        private sealed class SceneSavePlan
        {
            public int Handle;
            public string Name;
            public string Path;
            public bool WasDirty;
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "scene.save",
            Summary = "Save the active scene, or explicitly save every open dirty scene with per-scene clean readback.",
            Description = "Default mode saves the active scene and requires path for an untitled scene. all=true is mutually exclusive with path/name, saves only loaded dirty scenes, fails before any write if one is untitled, and returns the complete frozen dirty set after path/GUID/clean proof. Dry-run performs no scene or asset write.",
            Group = "scene",
            DestructiveHint = true,
            SupportsDryRun = true,
            SideEffects = new[] { "asset-write", "scene-modify" },
            Target = "editor",
            PlayModeSupport = "editor-only",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Save the active scene.", Input = new Request() },
                new ToolExample { Description = "Preview saving every open dirty scene.", Input = new Request { All = true }, Execution = new ToolExampleExecution { DryRun = true } },
                new ToolExample { Description = "Preview saving an untitled active scene to a path.", Input = new Request { Path = "Assets/Scenes/Main.unity" }, Execution = new ToolExampleExecution { DryRun = true } }
            }
        };

        /// <summary>按显式 mode 分派；all mode 在 effect 前冻结完整 dirty set，effect 后逐 handle fresh 证明。</summary>
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default(System.Threading.CancellationToken))
        {
            SceneToolSupport.EnsureEditorNotBusy("scene.save");
            request = request ?? new Request();
            ct.ThrowIfCancellationRequested();
            if (request.All)
                return new System.Threading.Tasks.ValueTask<Result>(SaveAll(request, ctx, ct));
            return new System.Threading.Tasks.ValueTask<Result>(SaveActive(request, ctx, ct));
        }

        /// <summary>保留 active scene 单场景契约，并补充统一 scenes collection 与 fresh GUID proof。</summary>
        private static Result SaveActive(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct)
        {
            var scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded)
                throw Error("E_SCENE_UNAVAILABLE", "scene.save requires one valid loaded active scene.", null);
            var path = SceneToolSupport.ResolveScenePath(request.Path ?? request.Name, false);
            if (string.IsNullOrEmpty(path)) path = SceneToolSupport.NormalizeAssetPath(scene.path);
            if (string.IsNullOrEmpty(path))
                throw new UnityCliBridge.BridgeCommandException("E_SCENE_PATH_REQUIRED", "Scene save requires path when the active scene has not been saved yet.", "Pass tools run scene.save --input '{\"path\":\"Assets/Scenes/Main.unity\"}'.");
            var plan = new SceneSavePlan { Handle = scene.handle, Name = scene.name, Path = path, WasDirty = scene.isDirty };
            var effects = Effects(new List<SceneSavePlan> { plan }, ctx.DryRun);
            if (ctx.DryRun)
            {
                var preview = Readback(scene, plan, false, false);
                return new Result { SavedCount = 0, Scene = preview.Scene, Scenes = new List<SceneSaveReadback> { preview }, RequestedPath = path, DryRun = true, PlannedEffects = effects };
            }

            ct.ThrowIfCancellationRequested();
            CommitEffect(ctx);
            SceneToolSupport.CreateAssetDirectory(path);
            if (!EditorSceneManager.SaveScene(scene, path))
                throw new UnityCliBridge.BridgeCommandException("E_SCENE_SAVE_FAILED", "Unity did not save the active scene.", "Inspect the active scene and project write permissions before retrying.");
            var observed = FindSceneByHandle(plan.Handle);
            var readback = Prove(observed, plan);
            return new Result
            {
                SavedCount = 1,
                Scene = readback.Scene,
                Scenes = new List<SceneSaveReadback> { readback },
                RequestedPath = path,
                DryRun = false,
                PlannedEffects = effects
            };
        }

        /// <summary>只冻结 loaded dirty scenes；untitled 或 mode 冲突在调用 SaveOpenScenes 前整体失败。</summary>
        private static Result SaveAll(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct)
        {
            if (request.Path != null || request.Name != null)
                throw Error("E_SCENE_SAVE_MODE_CONFLICT", "scene.save all=true cannot be combined with path or name.", null);
            var plans = CaptureDirtyScenes();
            var effects = Effects(plans, ctx.DryRun);
            var preview = new List<SceneSaveReadback>(plans.Count);
            foreach (var plan in plans)
            {
                var scene = FindSceneByHandle(plan.Handle);
                preview.Add(Readback(scene, plan, false, false));
            }
            if (ctx.DryRun)
                return new Result { SavedCount = 0, Scenes = preview, DryRun = true, PlannedEffects = effects };
            if (plans.Count == 0)
                return new Result { SavedCount = 0, Scenes = preview, DryRun = false, PlannedEffects = effects };

            ct.ThrowIfCancellationRequested();
            CommitEffect(ctx);
            if (!EditorSceneManager.SaveOpenScenes())
                throw new UnityCliBridge.BridgeCommandException("E_SCENE_SAVE_ALL_FAILED", "Unity did not save every frozen dirty scene.", "Inspect scene paths and project write permissions before deciding whether to retry.");

            var observed = new List<SceneSaveReadback>(plans.Count);
            foreach (var plan in plans)
                observed.Add(Prove(FindSceneByHandle(plan.Handle), plan));
            EnsureAllLoadedScenesClean();
            return new Result { SavedCount = observed.Count, Scenes = observed, DryRun = false, PlannedEffects = effects };
        }

        private static List<SceneSavePlan> CaptureDirtyScenes()
        {
            var result = new List<SceneSavePlan>();
            var paths = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < SceneManager.sceneCount; index++)
            {
                var scene = SceneManager.GetSceneAt(index);
                if (!scene.IsValid() || !scene.isLoaded || !scene.isDirty) continue;
                var path = SceneToolSupport.NormalizeAssetPath(scene.path);
                if (string.IsNullOrEmpty(path))
                    throw Error("E_SCENE_SAVE_ALL_UNTITLED", "Every dirty scene must have an asset path before scene.save all=true can run.", scene.name);
                if (!paths.Add(path))
                    throw Error("E_SCENE_SAVE_ALL_IDENTITY_CONFLICT", "Two dirty open scenes resolve to the same asset path.", path);
                result.Add(new SceneSavePlan { Handle = scene.handle, Name = scene.name, Path = path, WasDirty = true });
            }
            return result;
        }

        /// <summary>fresh scene handle/path/dirty 与 AssetDatabase GUID round-trip 构成单场景 terminal proof。</summary>
        private static SceneSaveReadback Prove(Scene scene, SceneSavePlan plan)
        {
            var path = scene.IsValid() ? SceneToolSupport.NormalizeAssetPath(scene.path) : null;
            var guid = string.IsNullOrEmpty(path) ? null : AssetDatabase.AssetPathToGUID(path);
            if (!scene.IsValid() || !scene.isLoaded || scene.handle != plan.Handle
                || !string.Equals(path, plan.Path, StringComparison.Ordinal) || scene.isDirty
                || string.IsNullOrEmpty(guid)
                || !string.Equals(AssetDatabase.GUIDToAssetPath(guid), plan.Path, StringComparison.Ordinal))
                throw Error("E_SCENE_SAVE_STATE_MISMATCH", "A saved scene did not retain its frozen identity or clean asset state.", plan.Path);
            return Readback(scene, plan, true, true);
        }

        private static SceneSaveReadback Readback(Scene scene, SceneSavePlan plan, bool saved, bool includeGuid)
        {
            return new SceneSaveReadback
            {
                Scene = SceneToolSupport.BuildSceneInfo(scene),
                Guid = includeGuid ? AssetDatabase.AssetPathToGUID(plan.Path) : null,
                WasDirty = plan.WasDirty,
                Saved = saved
            };
        }

        // Unity 2022 没有公开 GetSceneByHandle；按当前 loaded scene inventory 查找冻结 handle。
        private static Scene FindSceneByHandle(int handle)
        {
            for (var index = 0; index < SceneManager.sceneCount; index++)
            {
                var scene = SceneManager.GetSceneAt(index);
                if (scene.IsValid() && scene.handle == handle) return scene;
            }
            return default(Scene);
        }

        private static void EnsureAllLoadedScenesClean()
        {
            for (var index = 0; index < SceneManager.sceneCount; index++)
            {
                var scene = SceneManager.GetSceneAt(index);
                if (scene.IsValid() && scene.isLoaded && scene.isDirty)
                    throw Error("E_SCENE_SAVE_STATE_MISMATCH", "A loaded scene became dirty before save-all terminal readback completed.", scene.name);
            }
        }

        private static List<ScenePlannedEffect> Effects(List<SceneSavePlan> plans, bool dryRun)
        {
            var result = new List<ScenePlannedEffect>(plans.Count);
            foreach (var plan in plans)
                result.Add(new ScenePlannedEffect { Type = "scene.save", Path = plan.Path, SideEffects = dryRun ? "none" : null });
            return result;
        }

        // 两个互斥保存 mode 复用同一 barrier owner；每次调用只会进入其中一个分支。
        private static void CommitEffect(UnityCliProjectToolContext ctx)
        {
            ctx.CommitEffect();
        }

        private static UnityCliBridge.BridgeCommandException Error(string code, string message, string scene)
        {
            return new UnityCliBridge.BridgeCommandException(
                code,
                message,
                new Dictionary<string, object> { { "scene", scene } },
                new[] { "Run tools run scene.list-opened and save untitled scenes individually with scene.save path before retrying." });
        }
    }
}
#endif
