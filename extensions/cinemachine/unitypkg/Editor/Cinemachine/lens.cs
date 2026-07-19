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

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            CinemachineToolSupport.EnsureEditorReady("cinemachine.lens");
            CinemachineToolSupport.EnsureCinemachineAvailable();

            var gameObject = CinemachineToolSupport.ResolveRequiredTarget(request.Target, "cinemachine lens requires --target <go>.");
            var camera = CinemachineToolSupport.RequireCameraComponent(gameObject);
            var effects = new List<CinemachinePlannedEffect>();
            if (!request.FieldOfView.HasValue && !request.NearClipPlane.HasValue && !request.FarClipPlane.HasValue && !request.OrthographicSize.HasValue && !request.Dutch.HasValue) throw new UnityCliBridge.BridgeCommandException("E_CINEMACHINE_LENS_INPUT_REQUIRED", "cinemachine.lens requires at least one lens value.", "Pass a supported lens field.");
            if (request.FieldOfView.HasValue) CinemachineToolSupport.RequireFinite(request.FieldOfView.Value, "fieldOfView");
            if (request.NearClipPlane.HasValue) CinemachineToolSupport.RequireFinite(request.NearClipPlane.Value, "nearClipPlane");
            if (request.FarClipPlane.HasValue) CinemachineToolSupport.RequireFinite(request.FarClipPlane.Value, "farClipPlane");
            if (request.OrthographicSize.HasValue) CinemachineToolSupport.RequireFinite(request.OrthographicSize.Value, "orthographicSize");
            if (request.Dutch.HasValue) CinemachineToolSupport.RequireFinite(request.Dutch.Value, "dutch");
            if (request.FieldOfView.HasValue && (request.FieldOfView.Value <= 0d || request.FieldOfView.Value >= 180d)) throw new UnityCliBridge.BridgeCommandException("E_CINEMACHINE_LENS_RANGE_INVALID", "fieldOfView must be between 0 and 180 degrees.", "Pass a fieldOfView greater than 0 and less than 180.");
            if (request.NearClipPlane.HasValue && request.NearClipPlane.Value <= 0d) throw new UnityCliBridge.BridgeCommandException("E_CINEMACHINE_LENS_RANGE_INVALID", "nearClipPlane must be greater than zero.", "Pass a positive nearClipPlane.");
            if (request.FarClipPlane.HasValue && request.FarClipPlane.Value <= 0d) throw new UnityCliBridge.BridgeCommandException("E_CINEMACHINE_LENS_RANGE_INVALID", "farClipPlane must be greater than zero.", "Pass a positive farClipPlane.");
            if (request.NearClipPlane.HasValue && request.FarClipPlane.HasValue && request.FarClipPlane.Value <= request.NearClipPlane.Value) throw new UnityCliBridge.BridgeCommandException("E_CINEMACHINE_LENS_RANGE_INVALID", "farClipPlane must be greater than nearClipPlane.", "Pass a farClipPlane greater than nearClipPlane.");
            if (request.OrthographicSize.HasValue && request.OrthographicSize.Value <= 0d) throw new UnityCliBridge.BridgeCommandException("E_CINEMACHINE_LENS_RANGE_INVALID", "orthographicSize must be greater than zero.", "Pass a positive orthographicSize.");
            using (var validate = new SerializedObject(camera))
            {
                var lens = validate.FindProperty("Lens") ?? validate.FindProperty("m_Lens");
                if (lens == null) throw new UnityCliBridge.BridgeCommandException("E_CINEMACHINE_LENS_PROPERTY_NOT_FOUND", "Cinemachine Lens property not accessible via SerializedObject.", "Use a supported Cinemachine package version.");
                if (request.FieldOfView.HasValue) CinemachineToolSupport.RequireLensFloat(lens, "FieldOfView");
                if (request.NearClipPlane.HasValue) CinemachineToolSupport.RequireLensFloat(lens, "NearClipPlane");
                if (request.FarClipPlane.HasValue) CinemachineToolSupport.RequireLensFloat(lens, "FarClipPlane");
                if (request.OrthographicSize.HasValue) CinemachineToolSupport.RequireLensFloat(lens, "OrthographicSize");
                if (request.Dutch.HasValue) CinemachineToolSupport.RequireLensFloat(lens, "Dutch");
            }
            ctx.CommitEffect();
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

            var observedCamera = CinemachineToolSupport.RequireCameraComponent(CinemachineToolSupport.ResolveRequiredTarget(gameObject.GetInstanceID().ToString(), "Cinemachine camera could not be resolved after setting lens."));
            using (var observed = new SerializedObject(observedCamera))
            {
                if (request.FieldOfView.HasValue) CinemachineToolSupport.VerifyLensFloat(observed, "FieldOfView", (float)request.FieldOfView.Value);
                if (request.NearClipPlane.HasValue) CinemachineToolSupport.VerifyLensFloat(observed, "NearClipPlane", (float)request.NearClipPlane.Value);
                if (request.FarClipPlane.HasValue) CinemachineToolSupport.VerifyLensFloat(observed, "FarClipPlane", (float)request.FarClipPlane.Value);
                if (request.OrthographicSize.HasValue) CinemachineToolSupport.VerifyLensFloat(observed, "OrthographicSize", (float)request.OrthographicSize.Value);
                if (request.Dutch.HasValue) CinemachineToolSupport.VerifyLensFloat(observed, "Dutch", (float)request.Dutch.Value);
            }

            if (effects.Count > 0)
            {
                CinemachineToolSupport.MarkDirty(gameObject);
            }

            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Cinemachine = CinemachineToolSupport.BuildPackageStatus(),
                Object = CinemachineToolSupport.BuildObjectInfo(gameObject),
                PlannedEffects = effects,
                LogPath = ctx.LogPath
            });
        }
    }
}
#endif
