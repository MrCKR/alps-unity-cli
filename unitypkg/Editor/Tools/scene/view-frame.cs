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
            Description = "Frame the Editor SceneView on a target GameObject or frame all. Does not modify scene data, only the viewport. target resolves by name, hierarchy path or instance id.",
            Group = "scene",
            Version = "1.0.0",
            DestructiveHint = false,
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
            if (!string.IsNullOrEmpty(target))
            {
                var go = SceneToolSupport.ResolveGameObjectByPath(target);
                if (go != null)
                {
                    Selection.activeGameObject = go;
                    sceneView.Frame(new Bounds(go.transform.position, Vector3.one), false);
                    return new System.Threading.Tasks.ValueTask<Result>(new Result { Target = target, Framed = true });
                }
            }
            if (frameAll)
            {
                sceneView.Frame(new Bounds(Vector3.zero, new Vector3(10, 10, 10)), true);
            }
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Framed = true });
        }
    }
}
#endif
