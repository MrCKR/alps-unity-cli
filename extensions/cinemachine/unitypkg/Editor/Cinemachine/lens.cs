#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;

namespace UnityCli.Cinemachine
{
    /// <summary>
    /// cinemachine.lens — 修改虚拟相机 Lens 结构中的常用镜头字段。
    /// </summary>
    public class LensTool : IUnityCliTool<LensTool.Request, LensTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired] public string Target { get; set; }
            public double? FieldOfView { get; set; }
            public double? NearClipPlane { get; set; }
            public double? FarClipPlane { get; set; }
            public double? OrthographicSize { get; set; }
            public double? Dutch { get; set; }
        }

        public sealed class Result
        {
            public CinemachinePackageStatus Cinemachine { get; set; }
            public CinemachineObjectInfo Object { get; set; }
            public List<CinemachinePlannedEffect> PlannedEffects { get; set; }
            public string LogPath { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "cinemachine.lens",
            Summary = "Get or set Cinemachine virtual camera lens properties via SerializedObject-backed fields.",
            Description = "Set Cinemachine virtual camera lens properties through SerializedObject so the tool remains resilient across internal field renames. Supported fields are fieldOfView, nearClipPlane, farClipPlane, orthographicSize, and dutch.",
            Group = "cinemachine",
            Version = "1.0.0",
            DestructiveHint = false,
            RequiresPackages = new[] { "com.unity.cinemachine" },
            SideEffects = new[] { "scene-modify" },
            Target = "editor",
            PlayModeSupport = "session-affecting",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 60000,
            Examples = new[]
            {
                new ToolExample { Description = "设置 60 度视野", Input = new Request { Target = "PlayerCam", FieldOfView = 60 } }
            }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            CinemachineToolSupport.EnsureEditorReady("cinemachine.lens");
            CinemachineToolSupport.EnsureCinemachineAvailable();

            var gameObject = CinemachineToolSupport.ResolveRequiredTarget(request.Target, "cinemachine lens requires --target <go>.");
            var camera = CinemachineToolSupport.RequireCameraComponent(gameObject);
            var effects = new List<CinemachinePlannedEffect>();

            using (var serializedObject = new SerializedObject(camera))
            {
                var lensProperty = serializedObject.FindProperty("Lens") ?? serializedObject.FindProperty("m_Lens");
                if (lensProperty == null)
                {
                    throw new UnityCliBridge.BridgeCommandException(
                        "E_CINEMACHINE_LENS_PROPERTY_NOT_FOUND",
                        "Cinemachine Lens property not accessible via SerializedObject.",
                        "The Cinemachine package version may differ; inspect the Unity Console for details.");
                }

                if (request.FieldOfView.HasValue)
                {
                    CinemachineToolSupport.SetLensFloat(lensProperty, "FieldOfView", (float)request.FieldOfView.Value);
                    effects.Add(new CinemachinePlannedEffect { Type = "cinemachine.lens", Target = GameObjectToolSupport.HierarchyPath(gameObject), Field = "fieldOfView", Value = request.FieldOfView.Value.ToString() });
                }
                if (request.NearClipPlane.HasValue)
                {
                    CinemachineToolSupport.SetLensFloat(lensProperty, "NearClipPlane", (float)request.NearClipPlane.Value);
                    effects.Add(new CinemachinePlannedEffect { Type = "cinemachine.lens", Target = GameObjectToolSupport.HierarchyPath(gameObject), Field = "nearClipPlane", Value = request.NearClipPlane.Value.ToString() });
                }
                if (request.FarClipPlane.HasValue)
                {
                    CinemachineToolSupport.SetLensFloat(lensProperty, "FarClipPlane", (float)request.FarClipPlane.Value);
                    effects.Add(new CinemachinePlannedEffect { Type = "cinemachine.lens", Target = GameObjectToolSupport.HierarchyPath(gameObject), Field = "farClipPlane", Value = request.FarClipPlane.Value.ToString() });
                }
                if (request.OrthographicSize.HasValue)
                {
                    CinemachineToolSupport.SetLensFloat(lensProperty, "OrthographicSize", (float)request.OrthographicSize.Value);
                    effects.Add(new CinemachinePlannedEffect { Type = "cinemachine.lens", Target = GameObjectToolSupport.HierarchyPath(gameObject), Field = "orthographicSize", Value = request.OrthographicSize.Value.ToString() });
                }
                if (request.Dutch.HasValue)
                {
                    CinemachineToolSupport.SetLensFloat(lensProperty, "Dutch", (float)request.Dutch.Value);
                    effects.Add(new CinemachinePlannedEffect { Type = "cinemachine.lens", Target = GameObjectToolSupport.HierarchyPath(gameObject), Field = "dutch", Value = request.Dutch.Value.ToString() });
                }

                serializedObject.ApplyModifiedProperties();
            }

            if (effects.Count > 0)
            {
                CinemachineToolSupport.MarkDirty(gameObject);
            }

            return new Result
            {
                Cinemachine = CinemachineToolSupport.BuildPackageStatus(),
                Object = CinemachineToolSupport.BuildObjectInfo(gameObject),
                PlannedEffects = effects,
                LogPath = ctx.LogPath
            };
        }
    }
}
#endif
