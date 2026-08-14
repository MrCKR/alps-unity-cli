#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityCli
{
    /// <summary>
    /// scene.move-object — 将 GameObject 移动到另一个已加载场景。
    /// </summary>
    public class SceneMoveObjectTool : IUnityCliTool<SceneMoveObjectTool.Request, SceneMoveObjectTool.Result>
    {
        public sealed class Request
        {
            public string Target { get; set; }
            public string Path { get; set; }
            public int? InstanceId { get; set; }
            [UnityCliRequired]
            public string Scene { get; set; }
        }

        public sealed class Result
        {
            public string FromScene { get; set; }
            public string ToScene { get; set; }
            public bool? Moved { get; set; }
            public string Reason { get; set; }
            public bool? DryRun { get; set; }
            public List<ScenePlannedEffect> PlannedEffects { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "scene.move-object",
            Summary = "Move a GameObject from its current scene into another already-loaded scene.",
            Description = "Move a GameObject (resolved by target path or instanceId) into another already-loaded destination scene (--scene path). Useful for multi-scene editing. Requires the destination scene to be loaded (scene.load); returns moved=false if the object is already in the destination.",
            Group = "scene",
            DestructiveHint = false,
            SupportsDryRun = true,
            SideEffects = new[] { "scene-modify" },
            Target = "editor",
            PlayModeSupport = "session-affecting",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Move an object to another scene", Input = new Request { Target = "Player", Scene = "Assets/Scenes/Gameplay.unity" } }
            }
        };

        // 移动对象后读取场景标识，确保返回的是已生效的场景归属。
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            SceneToolSupport.EnsureEditorNotBusy("scene.move-object");
            var target = request.Target ?? request.Path;
            var instanceId = request.InstanceId;
            var destScenePath = request.Scene;
            if (string.IsNullOrEmpty(destScenePath))
            {
                throw new UnityCliBridge.BridgeCommandException("E_SCENE_DESTINATION_REQUIRED", "scene.move-object requires --scene (destination scene path).", "Run tools run scene.list-opened to list loaded scenes.");
            }
            GameObject go = null;
            if (instanceId.HasValue && instanceId.Value != 0)
            {
                go = EditorUtility.InstanceIDToObject(instanceId.Value) as GameObject;
            }
            if (go == null && !string.IsNullOrEmpty(target))
            {
                go = SceneToolSupport.ResolveGameObjectByPath(target);
            }
            if (go == null)
            {
                throw new UnityCliBridge.BridgeCommandException("E_TARGET_NOT_FOUND", "GameObject not found: " + (target ?? instanceId?.ToString()), "Pass a valid target path or instanceId.");
            }
            var destScene = SceneManager.GetSceneByPath(destScenePath);
            if (!destScene.IsValid() || !destScene.isLoaded)
            {
                throw new UnityCliBridge.BridgeCommandException("E_SCENE_NOT_OPEN", "Destination scene is not loaded: " + destScenePath, "Load the destination scene first via scene.load, or run tools run scene.list-opened.");
            }
            if (go.scene == destScene)
            {
                return new System.Threading.Tasks.ValueTask<Result>(new Result
                {
                    ToScene = destScenePath,
                    Moved = false,
                    Reason = "already in destination scene"
                });
            }
            if (ctx.DryRun)
            {
                return new System.Threading.Tasks.ValueTask<Result>(new Result
                {
                    FromScene = go.scene.path,
                    ToScene = destScenePath,
                    DryRun = true,
                    PlannedEffects = new List<ScenePlannedEffect>
                    {
                        new ScenePlannedEffect { Type = "scene.move-object", Target = go.name }
                    }
                });
            }
            var fromScene = go.scene.path;
            var gameObjectId = go.GetInstanceID();
            ctx.CommitEffect();
            SceneManager.MoveGameObjectToScene(go, destScene);
            // Resolve the same object afresh; the call itself is not completion proof.
            var observed = EditorUtility.InstanceIDToObject(gameObjectId) as GameObject;
            var observedScenePath = observed == null ? null : observed.scene.path;
            var observedSceneHandle = observed == null ? 0 : observed.scene.handle;
            if (observedScenePath != destScene.path || observedSceneHandle != destScene.handle)
                throw new UnityCliBridge.BridgeCommandException("E_SCENE_MOVE_STATE_MISMATCH", "GameObject did not move to the requested destination scene.", "Inspect the object scene and destination scene load state before retrying.");
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                FromScene = fromScene,
                ToScene = observedScenePath,
                Moved = true
            });
        }
    }
}
#endif
