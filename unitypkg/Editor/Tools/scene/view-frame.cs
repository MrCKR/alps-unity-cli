#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// scene.view-frame — 将 Editor SceneView 对准目标 GameObject 或 frame all。
    /// </summary>
    public class SceneViewFrameTool : IUnityCliTool<SceneViewFrameTool.Request, SceneViewFrameTool.Result>
    {
        public sealed class Request
        {
            public string Target { get; set; }
            public string Path { get; set; }
            public bool? FrameAll { get; set; }
        }

        public sealed class Result
        {
            public string Target { get; set; }
            public bool Framed { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "scene.view-frame",
            Summary = "Frame the Editor SceneView on a target GameObject or frame all.",
            Description = "Frame the Editor SceneView on a target GameObject or frame all. Changes Editor selection and viewport state without modifying scene data. target resolves by name, hierarchy path or instance id.",
            Group = "scene",
            Version = "1.0.0",
            DestructiveHint = false,
            SideEffects = new[] { "editor-selection", "scene-view-state" },
            Target = "editor",
            PlayModeSupport = "editor-only",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Frame all objects", Input = new Request { FrameAll = true } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var target = request.Target ?? request.Path;
            var frameAll = request.FrameAll == true;
            var sceneView = SceneView.lastActiveSceneView;
            if (sceneView == null)
            {
                throw new UnityCliBridge.BridgeCommandException("E_SCENE_VIEW_NOT_AVAILABLE", "No active SceneView is open.", "Open a SceneView window in the Unity Editor.");
            }
            GameObject go = null;
            if (!string.IsNullOrEmpty(target))
            {
                go = SceneToolSupport.ResolveGameObjectByPath(target);
                if (go == null)
                    throw new UnityCliBridge.BridgeCommandException("E_GAME_OBJECT_NOT_FOUND", "SceneView frame target was not found: " + target, "Pass an existing hierarchy path, name, or instance id.");
            }
            else if (!frameAll)
            {
                throw new UnityCliBridge.BridgeCommandException("E_SCENE_VIEW_TARGET_REQUIRED", "scene.view-frame requires target/path or frameAll=true.", "Pass a GameObject target or set frameAll=true.");
            }

            ctx.CommitEffect();
            if (go != null)
            {
                Selection.activeGameObject = go;
                sceneView.Frame(new Bounds(go.transform.position, Vector3.one), false);
                if (Selection.activeGameObject != go)
                    throw new UnityCliBridge.BridgeCommandException("E_SCENE_VIEW_FRAME_STATE_MISMATCH", "SceneView selection did not match the frame target.", "Ensure the SceneView is active and retry.");
                throw new UnityCliBridge.BridgeCommandException("E_SCENE_VIEW_FRAME_PROOF_UNAVAILABLE", "SceneView selection was observed, but viewport framing cannot be attributed to this request with a stable business readback.", "Use the Editor SceneView manually or inspect selection with an editor read operation.");
            }
            sceneView.Frame(new Bounds(Vector3.zero, new Vector3(10, 10, 10)), true);
            if (SceneView.lastActiveSceneView != sceneView)
                throw new UnityCliBridge.BridgeCommandException("E_SCENE_VIEW_FRAME_STATE_MISMATCH", "SceneView changed before frame-all could be observed.", "Ensure the SceneView remains active and retry.");
            throw new UnityCliBridge.BridgeCommandException("E_SCENE_VIEW_FRAME_PROOF_UNAVAILABLE", "SceneView frame-all has no stable target-specific business readback.", "Use the Editor SceneView manually.");
        }
    }
}
#endif
