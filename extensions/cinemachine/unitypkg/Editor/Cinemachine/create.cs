#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityCli.Cinemachine
{
    /// <summary>
    /// cinemachine.create — 创建虚拟相机，并按 preset 安装 Body/Aim 管线组件。
    /// </summary>
    public class CreateTool : IUnityCliTool<CreateTool.Request, CreateTool.Result>
    {
        public sealed class Request
        {
            public string Preset { get; set; }
            public string Name { get; set; }
            public string Parent { get; set; }
            public string Follow { get; set; }
            public string LookAt { get; set; }
            public double? X { get; set; }
            public double? Y { get; set; }
            public double? Z { get; set; }
            public int? Priority { get; set; }
        }

        public sealed class Result
        {
            public CinemachinePackageStatus Cinemachine { get; set; }
            public CinemachineObjectInfo Object { get; set; }
            public List<CinemachinePlannedEffect> PlannedEffects { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "cinemachine.create",
            Summary = "Create a Cinemachine virtual camera with an optional preset that configures Body and Aim pipeline components.",
            Description = "Create a Cinemachine virtual camera with an optional preset. preset supports follow, third_person, freelook, dolly, static, top_down, and side_scroller. The tool can also set parent, follow, lookAt, position, and priority during creation.",
            Group = "cinemachine",
            DestructiveHint = false,
            RequiresPackages = new[] { "com.unity.cinemachine" },
            SideEffects = new[] { "scene-modify", "undo-stack" },
            Target = "editor",
            PlayModeSupport = "session-affecting",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "创建跟随相机", Input = new Request { Preset = "follow", Name = "MatrixCreatedCamera", Follow = "Player" } },
                new ToolExample { Description = "创建 dolly 相机", Input = new Request { Preset = "dolly", Name = "DollyCam", LookAt = "Main Camera" } }
            }
        };

        /// <summary>先校验 preset，再创建并配置虚拟相机，保证非法输入不会留下场景对象。</summary>
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            CinemachineToolSupport.EnsureEditorReady("cinemachine.create");
            CinemachineToolSupport.EnsureCinemachineAvailable();

            request = request ?? new Request();
            var preset = string.IsNullOrWhiteSpace(request.Preset) ? "follow" : request.Preset.Trim();
            string[] presetParts;
            if (!CinemachineToolSupport.Presets.TryGetValue(preset, out presetParts))
            {
                throw new UnityCliBridge.BridgeCommandException(
                    "E_CINEMACHINE_PRESET_INVALID",
                    "Unsupported Cinemachine preset: " + preset,
                    "Pass one of: follow, third_person, freelook, dolly, static, top_down, side_scroller.");
            }

            var cameraName = string.IsNullOrWhiteSpace(request.Name) ? "UnityCli_CM_" + preset : request.Name.Trim();
            var parent = CinemachineToolSupport.ResolveOptionalTarget(request.Parent);
            var follow = CinemachineToolSupport.ResolveOptionalTarget(request.Follow);
            var lookAt = CinemachineToolSupport.ResolveOptionalTarget(request.LookAt);
            if (request.X.HasValue) CinemachineToolSupport.RequireFinite(request.X.Value, "x");
            if (request.Y.HasValue) CinemachineToolSupport.RequireFinite(request.Y.Value, "y");
            if (request.Z.HasValue) CinemachineToolSupport.RequireFinite(request.Z.Value, "z");
            var bodyType = string.IsNullOrWhiteSpace(presetParts[0]) ? null : CinemachineToolSupport.RequireComponentType(presetParts[0], "Body");
            var aimType = string.IsNullOrWhiteSpace(presetParts[1]) ? null : CinemachineToolSupport.RequireComponentType(presetParts[1], "Aim");
            if (bodyType != null) CinemachineToolSupport.RequirePipelineComponentStage(bodyType, "Body");
            if (aimType != null) CinemachineToolSupport.RequirePipelineComponentStage(aimType, "Aim");
            var followPlan = follow == null ? null : CinemachineToolSupport.PrepareFirstComponentObjectMember(CinemachineToolSupport.CameraType, follow.transform, "Follow", "m_Follow");
            var lookAtPlan = lookAt == null ? null : CinemachineToolSupport.PrepareFirstComponentObjectMember(CinemachineToolSupport.CameraType, lookAt.transform, "LookAt", "m_LookAt");
            if (request.Priority.HasValue) CinemachineToolSupport.RequirePriorityWritable(CinemachineToolSupport.CameraType);
            ctx.CommitEffect();
            var gameObject = new GameObject(cameraName);
            var camera = gameObject.AddComponent(CinemachineToolSupport.CameraType);
            UnityCliSceneMutation.RegisterCreatedObject(gameObject, "UnityCli Cinemachine Create");

            if (parent != null) gameObject.transform.SetParent(parent.transform, false);
            var expectedPosition = new Vector3((float)(request.X ?? 0d), (float)(request.Y ?? 0d), (float)(request.Z ?? 0d));
            gameObject.transform.position = expectedPosition;

            if (followPlan != null) CinemachineToolSupport.ApplyComponentMember(camera, followPlan);
            if (lookAtPlan != null) CinemachineToolSupport.ApplyComponentMember(camera, lookAtPlan);

            if (request.Priority.HasValue)
            {
                CinemachineToolSupport.SetPriority(camera, request.Priority.Value);
            }

            if (!string.IsNullOrWhiteSpace(presetParts[0]))
            {
                CinemachineToolSupport.SwapPipelineComponent(gameObject, camera, "Body", bodyType);
            }
            if (!string.IsNullOrWhiteSpace(presetParts[1]))
            {
                CinemachineToolSupport.SwapPipelineComponent(gameObject, camera, "Aim", aimType);
            }

            // 从场景重新解析并读取组件，避免以刚写入的引用作为完成证明。
            var observedObject = CinemachineToolSupport.ResolveRequiredTarget(gameObject.GetInstanceID().ToString(System.Globalization.CultureInfo.InvariantCulture), "Created Cinemachine camera could not be resolved.");
            var observedCamera = CinemachineToolSupport.RequireCameraComponent(observedObject);
            if (!string.Equals(observedObject.name, cameraName, System.StringComparison.Ordinal) || observedObject.transform.parent != (parent == null ? null : parent.transform) || (observedObject.transform.position - expectedPosition).sqrMagnitude > 0.0000001f) throw new UnityCliBridge.BridgeCommandException("E_CINEMACHINE_CREATE_VERIFY_FAILED", "Created camera identity, parent or position did not retain the request.", "Inspect the created object before retrying.");
            if (followPlan != null) CinemachineToolSupport.VerifyComponentMember(observedCamera, followPlan, "E_CINEMACHINE_CREATE_VERIFY_FAILED");
            if (lookAtPlan != null) CinemachineToolSupport.VerifyComponentMember(observedCamera, lookAtPlan, "E_CINEMACHINE_CREATE_VERIFY_FAILED");
            if (bodyType != null && CinemachineToolSupport.GetPipelineComponent(observedCamera, "Body")?.GetType() != bodyType) throw new UnityCliBridge.BridgeCommandException("E_CINEMACHINE_CREATE_VERIFY_FAILED", "Created camera Body did not retain the preset.", "Check the preset and retry.");
            if (aimType != null && CinemachineToolSupport.GetPipelineComponent(observedCamera, "Aim")?.GetType() != aimType) throw new UnityCliBridge.BridgeCommandException("E_CINEMACHINE_CREATE_VERIFY_FAILED", "Created camera Aim did not retain the preset.", "Check the preset and retry.");
            if (request.Priority.HasValue)
            {
                using (var observedSerialized = new UnityEditor.SerializedObject(observedCamera))
                {
                    var priorityRoot = observedSerialized.FindProperty("Priority") ?? observedSerialized.FindProperty("m_Priority");
                    var priorityValue = priorityRoot?.FindPropertyRelative("Value") ?? priorityRoot?.FindPropertyRelative("m_Value");
                    var directPriority = observedCamera.GetType().GetProperty("Priority", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
                    var actualPriority = priorityValue != null ? (int?)priorityValue.intValue : directPriority != null ? (int?)System.Convert.ToInt32(directPriority.GetValue(observedCamera, null)) : null;
                    if (!actualPriority.HasValue || actualPriority.Value != request.Priority.Value) throw new UnityCliBridge.BridgeCommandException("E_CINEMACHINE_CREATE_VERIFY_FAILED", "Created camera priority did not retain the request.", "Inspect the created object before retrying.");
                }
            }
            CinemachineToolSupport.MarkDirty(observedObject);
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Cinemachine = CinemachineToolSupport.BuildPackageStatus(),
                Object = CinemachineToolSupport.BuildObjectInfo(observedObject, preset),
                PlannedEffects = new List<CinemachinePlannedEffect>
                {
                    new CinemachinePlannedEffect
                    {
                        Type = "cinemachine.create",
                        Preset = preset,
                        Target = GameObjectToolSupport.HierarchyPath(observedObject)
                    }
                }
            });
        }
    }
}
#endif
