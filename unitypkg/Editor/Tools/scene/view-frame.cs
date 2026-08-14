#if UNITY_EDITOR
using System;
using System.Collections.Generic;
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

        public sealed class ViewReadback
        {
            public Vector3Data Pivot { get; set; }
            public Vector3Data Rotation { get; set; }
            public float Size { get; set; }
            public bool Orthographic { get; set; }
            public Vector3Data BoundsCenter { get; set; }
            public Vector3Data BoundsSize { get; set; }
            public bool BoundsContained { get; set; }
        }

        public sealed class Result
        {
            public bool Framed { get; set; }
            public bool Changed { get; set; }
            public string NoOpReason { get; set; }
            public ViewReadback View { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "scene.view-frame",
            Summary = "Frame the Editor SceneView on a target GameObject or frame all.",
            Description = "Frame the Editor SceneView on a target GameObject or frame all. Changes Editor selection and viewport state without modifying scene data. target resolves by name, hierarchy path or instance id.",
            Group = "scene",
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

        /// <summary>执行一次 SceneView.Frame，等待 repaint 稳定后用 camera frustum 与完整 viewport 状态证明终态。</summary>
        public async System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var target = request.Target ?? request.Path;
            var frameAll = request.FrameAll == true;
            var sceneView = SceneView.lastActiveSceneView;
            if (sceneView == null || sceneView.camera == null)
                throw new UnityCliBridge.BridgeCommandException("E_SCENE_VIEW_NOT_AVAILABLE", "No active SceneView with a camera is open.", "Open a SceneView window in the Unity Editor.");
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

            var instanceId = go == null ? 0 : go.GetInstanceID();
            var globalId = go == null ? null : GlobalObjectId.GetGlobalObjectIdSlow(go).ToString();
            var sceneHandle = go == null ? 0 : go.scene.handle;
            var bounds = go == null ? ReadAllBounds() : ReadTargetBounds(go);
            var before = ReadView(sceneView, bounds);
            if (before != null && before.BoundsContained)
                return new Result { Framed = true, Changed = false, NoOpReason = "target-bounds-already-contained", View = before };

            ctx.CommitEffect();
            try
            {
                if (go != null) Selection.activeGameObject = go;
                if (!sceneView.Frame(bounds, true))
                    throw new InvalidOperationException("SceneView rejected the resolved finite bounds.");
                SynchronizeSceneCamera(sceneView);
                sceneView.Repaint();
            }
            catch (System.Exception exception)
            {
                throw Unknown("invoke", exception.Message);
            }

            await ctx.Wait.NextFrame(1, ct);
            var stable = await ctx.Wait.UntilStable(() => SceneView.lastActiveSceneView == sceneView && ReadView(sceneView, bounds)?.BoundsContained == true,
                System.TimeSpan.FromMilliseconds(ctx.TimeoutMs), minimumFrames: 2, ct: ct);
            if (!stable) throw Unknown("postcondition", "SceneView did not stably contain the requested bounds before the deadline.");

            if (instanceId != 0)
            {
                var fresh = EditorUtility.InstanceIDToObject(instanceId) as GameObject;
                if (fresh == null || fresh.scene.handle != sceneHandle
                    || !string.Equals(GlobalObjectId.GetGlobalObjectIdSlow(fresh).ToString(), globalId, StringComparison.Ordinal))
                    throw Unknown("readback", "SceneView target identity changed while framing.");
                var freshBounds = ReadTargetBounds(fresh);
                if (!SameBounds(bounds, freshBounds)) throw Unknown("readback", "SceneView target bounds changed while framing.");
            }
            var readback = ReadView(sceneView, bounds);
            if (readback == null || !readback.BoundsContained) throw Unknown("postcondition", "Fresh SceneView readback does not contain the requested bounds.");
            return new Result { Framed = true, Changed = true, View = readback };
        }

        /// <summary>合并目标及子层级 Renderer bounds；无 Renderer 时回退到 Transform 原点。</summary>
        private static Bounds ReadTargetBounds(GameObject go)
        {
            var renderers = go.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0) return new Bounds(go.transform.position, Vector3.one);
            var bounds = renderers[0].bounds;
            for (var index = 1; index < renderers.Length; index++) bounds.Encapsulate(renderers[index].bounds);
            return bounds;
        }

        /// <summary>frameAll 使用当前已加载 scene 的真实 Renderer 集合，不使用固定虚构范围。</summary>
        private static Bounds ReadAllBounds()
        {
            var renderers = Resources.FindObjectsOfTypeAll<Renderer>();
            var found = false;
            var bounds = new Bounds(Vector3.zero, Vector3.one);
            foreach (var renderer in renderers)
            {
                if (renderer == null || !renderer.gameObject.scene.IsValid() || !renderer.gameObject.scene.isLoaded) continue;
                if (!found) { bounds = renderer.bounds; found = true; }
                else bounds.Encapsulate(renderer.bounds);
            }
            return bounds;
        }

        /// <summary>读取完整 viewport 并用 camera frustum 检查 AABB 八角范围。</summary>
        private static ViewReadback ReadView(SceneView sceneView, Bounds bounds)
        {
            if (sceneView == null || sceneView.camera == null || !Finite(sceneView.pivot) || !Finite(sceneView.rotation.eulerAngles)
                || sceneView.size <= 0f || float.IsNaN(sceneView.size) || float.IsInfinity(sceneView.size)) return null;
            var contained = GeometryUtility.TestPlanesAABB(GeometryUtility.CalculateFrustumPlanes(sceneView.camera), bounds);
            return new ViewReadback {
                Pivot = Vector(sceneView.pivot), Rotation = Vector(sceneView.rotation.eulerAngles), Size = sceneView.size,
                Orthographic = sceneView.orthographic, BoundsCenter = Vector(bounds.center), BoundsSize = Vector(bounds.size), BoundsContained = contained
            };
        }

        /// <summary>后台或被遮挡的 SceneView 不执行 OnGUI；按 Unity 2022.3 SetupCamera 公式同步其隐藏 camera，确保读回不依赖窗口可见性。</summary>
        private static void SynchronizeSceneCamera(SceneView sceneView)
        {
            var camera = sceneView.camera;
            var settings = sceneView.cameraSettings;
            if (camera == null || settings == null)
                throw new InvalidOperationException("SceneView camera settings are unavailable.");

            var size = Mathf.Abs(sceneView.size);
            var aspect = camera.aspect;
            if (float.IsNaN(aspect) || float.IsInfinity(aspect) || aspect <= 0f) aspect = 1f;

            var rotation = sceneView.rotation;
            float distance;
            if (sceneView.orthographic)
            {
                camera.orthographic = true;
                camera.orthographicSize = aspect < 1f ? size / aspect : size;
                distance = size * 2f;
            }
            else
            {
                var aspectNeutralFov = settings.fieldOfView;
                var halfFovRadians = aspectNeutralFov * 0.5f * Mathf.Deg2Rad;
                var halfFovSin = Mathf.Sin(halfFovRadians);
                if (float.IsNaN(aspectNeutralFov) || float.IsInfinity(aspectNeutralFov) || halfFovSin <= Mathf.Epsilon)
                    throw new InvalidOperationException("SceneView field of view is not finite and positive.");

                var verticalMultiplier = aspect < 1f ? 1f / aspect : 1f;
                var verticalHalfFov = Mathf.Atan(Mathf.Tan(halfFovRadians) * verticalMultiplier);
                camera.orthographic = false;
                camera.fieldOfView = verticalHalfFov * 2f * Mathf.Rad2Deg;
                distance = size / halfFovSin;
            }

            camera.transform.rotation = rotation;
            camera.transform.position = sceneView.pivot + rotation * new Vector3(0f, 0f, -distance);
            if (settings.dynamicClip)
            {
                var farClip = Mathf.Clamp(2000f * size, 1000f, 1.844674E+19f);
                camera.nearClipPlane = Mathf.Max(0.00001f, farClip * 0.000005f);
                camera.farClipPlane = farClip;
            }
            else
            {
                camera.nearClipPlane = settings.nearClip;
                camera.farClipPlane = settings.farClip;
            }
            camera.ResetWorldToCameraMatrix();
            camera.ResetProjectionMatrix();
        }

        private static bool SameBounds(Bounds left, Bounds right) { return (left.center - right.center).sqrMagnitude <= 0.000001f && (left.size - right.size).sqrMagnitude <= 0.000001f; }
        private static bool Finite(Vector3 value) { return !(float.IsNaN(value.x) || float.IsInfinity(value.x) || float.IsNaN(value.y) || float.IsInfinity(value.y) || float.IsNaN(value.z) || float.IsInfinity(value.z)); }
        private static Vector3Data Vector(Vector3 value) { return new Vector3Data { X = value.x, Y = value.y, Z = value.z }; }
        private static UnityCliBridge.BridgeCommandException Unknown(string stage, string cause)
        {
            return new UnityCliBridge.BridgeCommandException("E_EXECUTION_TIMEOUT", "SceneView framing result cannot be confirmed; inspect the business state before deciding whether to retry.",
                new Dictionary<string, object> { { "stage", stage }, { "cause", cause } }, new[] { "Inspect the SceneView before submitting a new request." });
        }
    }
}
#endif
