#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        // ── Cinemachine package detection ──────────────────────────────────
        // Critically references unity-mcp CameraHelpers.DetectCinemachine():
        //   _cmCameraType = UnityTypeResolver.ResolveComponent("CinemachineCamera");
        //   _cmBrainType  = UnityTypeResolver.ResolveComponent("CinemachineBrain");
        //   _hasCinemachine = _cmCameraType != null && _cmBrainType != null;
        //
        // Adapted for alps-unity-cli: uses FindType() (same reflection-based pattern
        // as ProBuilder) instead of UnityTypeResolver. No #if UNITY_CINEMACHINE
        // conditional compilation — purely runtime detection. Both CinemachineCamera
        // (CM3 new name) and CinemachineVirtualCamera (legacy CM2) are considered.

        private static Type _cmCameraType;
        private static Type _cmBrainType;
        private static Type _cmExtensionBaseType;
        private static Type _cmCoreType;
        private static bool? _hasCinemachine;

        private static bool IsCinemachineInstalled()
        {
            if (_hasCinemachine.HasValue) return _hasCinemachine.Value;
            _cmCameraType = FindType("Unity.Cinemachine.CinemachineCamera")
                         ?? FindType("Cinemachine.CinemachineVirtualCamera");
            _cmBrainType  = FindType("Unity.Cinemachine.CinemachineBrain")
                         ?? FindType("Cinemachine.CinemachineBrain");
            _cmExtensionBaseType = FindType("Unity.Cinemachine.CinemachineExtension")
                                ?? FindType("Cinemachine.CinemachineExtension");
            _cmCoreType = FindType("Unity.Cinemachine.CinemachineCore")
                       ?? FindType("Cinemachine.CinemachineCore");
            _hasCinemachine = _cmCameraType != null && _cmBrainType != null;
            return _hasCinemachine.Value;
        }

        private static string RequireCinemachinePackage()
        {
            if (!IsCinemachineInstalled())
            {
                throw new BridgeCommandException("E_CINEMACHINE_PACKAGE_NOT_FOUND",
                    "Cinemachine package (com.unity.cinemachine) is not installed.",
                    "Install com.unity.cinemachine via the Unity Package Manager or: unity_cli package install com.unity.cinemachine");
            }
            return null; // sentinel; actual guard is in each handler
        }

        // ── Registration ────────────────────────────────────────────────────

        private static void RegisterCinemachineCommands(BridgeCommandRegistry registry)
        {
            RegisterCommand(registry, "cinemachine.create",    "cinemachine", false, CinemachineCreate);
            RegisterCommand(registry, "cinemachine.target",    "cinemachine", false, CinemachineTarget);
            RegisterCommand(registry, "cinemachine.lens",      "cinemachine", false, CinemachineLens);
            RegisterCommand(registry, "cinemachine.priority",  "cinemachine", false, CinemachinePriority);
            RegisterCommand(registry, "cinemachine.blend",     "cinemachine", false, CinemachineBlend);
            RegisterCommand(registry, "cinemachine.body",      "cinemachine", false, CinemachineBody);
            RegisterCommand(registry, "cinemachine.aim",       "cinemachine", false, CinemachineAim);
            RegisterCommand(registry, "cinemachine.noise",     "cinemachine", false, CinemachineNoise);
            RegisterCommand(registry, "cinemachine.brain",     "cinemachine", false, CinemachineBrain);
            RegisterCommand(registry, "cinemachine.extension", "cinemachine", true,  CinemachineExtension);
        }

        // ── Preset → Body / Aim component mapping ──────────────────────────
        // Critically references unity-mcp CameraCreate preset map:
        //   ["follow"]       = ("CinemachineFollow",              "CinemachineRotationComposer")
        //   ["third_person"] = ("CinemachineThirdPersonFollow",  "CinemachineRotationComposer")
        //   ["freelook"]     = ("CinemachineOrbitalFollow",      "CinemachineRotationComposer")
        //   ["dolly"]        = ("CinemachineSplineDolly",        "CinemachineRotationComposer")
        //   ["static"]       = (null,                             "CinemachineHardLookAt")
        //   ["top_down"]     = ("CinemachineFollow",             null)
        //   ["side_scroller"]= ("CinemachinePositionComposer",   null)
        //
        // Adapted: kept the mapping but resolved component types at runtime
        // via FindType instead of direct type references (since we have no
        // compile-time dependency on Cinemachine).

        private static readonly Dictionary<string, string[]> CinemachinePresetMap = new Dictionary<string, string[]>
        {
            { "follow",        new[] { "CinemachineFollow",              "CinemachineRotationComposer" } },
            { "third_person",  new[] { "CinemachineThirdPersonFollow",   "CinemachineRotationComposer" } },
            { "freelook",      new[] { "CinemachineOrbitalFollow",       "CinemachineRotationComposer" } },
            { "dolly",         new[] { "CinemachineSplineDolly",         "CinemachineRotationComposer" } },
            { "static",        new[] { null,                              "CinemachineHardLookAt" } },
            { "top_down",      new[] { "CinemachineFollow",              null } },
            { "side_scroller", new[] { "CinemachinePositionComposer",    null } }
        };

        // ── CinemachineCreate ───────────────────────────────────────────────

        private static string CinemachineCreate(BridgeRequest request)
        {
            RequireCinemachinePackage();

            var preset = request.StringArg("preset") ?? "follow";
            var name   = request.StringArg("name") ?? ("UnityCli_CM_" + preset);

            // Create base GameObject with CinemachineCamera component
            var go = new GameObject(name);
            var cmCamera = go.AddComponent(_cmCameraType);
            Undo.RegisterCreatedObjectUndo(go, "UnityCli Cinemachine Create");

            // Position
            go.transform.position = new Vector3(
                request.FloatArg("x", go.transform.position.x),
                request.FloatArg("y", go.transform.position.y),
                request.FloatArg("z", go.transform.position.z));

            // Parent
            var parent = request.StringArg("parent");
            if (!string.IsNullOrEmpty(parent))
            {
                var parentGo = ResolveGameObject(parent);
                if (parentGo != null) go.transform.SetParent(parentGo.transform, false);
            }

            // Follow / LookAt targets
            var followName = request.StringArg("follow");
            if (!string.IsNullOrEmpty(followName))
            {
                var followGo = ResolveGameObject(followName);
                if (followGo != null) SetCinemachineFollowTarget(cmCamera, followGo.transform);
            }
            var lookAtName = request.StringArg("lookAt");
            if (!string.IsNullOrEmpty(lookAtName))
            {
                var lookAtGo = ResolveGameObject(lookAtName);
                if (lookAtGo != null) SetCinemachineLookAtTarget(cmCamera, lookAtGo.transform);
            }

            // Priority via SerializedObject (references unity-mcp's SerializedObject
            // approach for PrioritySettings.Enabled + m_Value)
            var priority = request.OptionalIntArg("priority");
            if (priority.HasValue)
            {
                SetCinemachinePriority(cmCamera, priority.Value);
            }

            // Apply preset → Body/Aim components
            string[] presetParts;
            if (CinemachinePresetMap.TryGetValue(preset.ToLowerInvariant(), out presetParts))
            {
                // Body component
                if (!string.IsNullOrEmpty(presetParts[0]))
                {
                    var bodyType = ResolveCinemachineComponentType(presetParts[0]);
                    if (bodyType != null)
                    {
                        SwapPipelineComponent(go, cmCamera, "Body", bodyType);
                    }
                }
                // Aim component
                if (!string.IsNullOrEmpty(presetParts[1]))
                {
                    var aimType = ResolveCinemachineComponentType(presetParts[1]);
                    if (aimType != null)
                    {
                        SwapPipelineComponent(go, cmCamera, "Aim", aimType);
                    }
                }
            }

            EditorSceneManager.MarkSceneDirty(go.scene);
            WriteLog("cinemachine_created preset=" + preset + " name=" + name);

            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("cinemachine", CinemachinePackageJson())
                .PropRaw("object", CinemachineObjectJson(go, preset))
                .PropArray("plannedEffects", arr => arr.BeginObject()
                    .Prop("type", "cinemachine.create")
                    .Prop("preset", preset)
                    .Prop("path", HierarchyPath(go))
                    .EndObject())
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        // ── CinemachineTarget ────────────────────────────────────────────────

        private static string CinemachineTarget(BridgeRequest request)
        {
            RequireCinemachinePackage();
            var go = ResolveUniqueCinemachineTarget(request.StringArg("target"));
            var cmCamera = go.GetComponent(_cmCameraType);
            if (cmCamera == null)
                return ErrorJson("E_CINEMACHINE_COMPONENT_NOT_FOUND",
                    "GameObject does not have a CinemachineCamera component: " + HierarchyPath(go),
                    "Pass a target with a CinemachineCamera component or create one with cinemachine create.");
            var effects = new List<string>();
            var followName = request.StringArg("follow");
            if (!string.IsNullOrEmpty(followName))
            {
                var followGo = ResolveGameObject(followName);
                if (followGo == null) return ErrorJson("E_TARGET_NOT_FOUND", "Follow target not found: " + followName, "Pass an existing GameObject name or path.");
                SetCinemachineFollowTarget(cmCamera, followGo.transform);
                effects.Add(EffectJson("cinemachine.target", "follow", followName));
            }
            var lookAtName = request.StringArg("lookAt");
            if (!string.IsNullOrEmpty(lookAtName))
            {
                var lookAtGo = ResolveGameObject(lookAtName);
                if (lookAtGo == null) return ErrorJson("E_TARGET_NOT_FOUND", "LookAt target not found: " + lookAtName, "Pass an existing GameObject name or path.");
                SetCinemachineLookAtTarget(cmCamera, lookAtGo.transform);
                effects.Add(EffectJson("cinemachine.target", "lookAt", lookAtName));
            }
            if (effects.Count > 0) EditorSceneManager.MarkSceneDirty(go.scene);
            WriteLog("cinemachine_target_set target=" + HierarchyPath(go));
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("cinemachine", CinemachinePackageJson())
                .PropRaw("object", CinemachineObjectJson(go, null))
                .PropArray("plannedEffects", arr => { foreach (var e in effects) arr.RawValue(e); })
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        // ── CinemachineLens ──────────────────────────────────────────────────
        // Critically references unity-mcp CameraConfigure.SetCinemachineLens:
        //   Uses SerializedObject to modify the Lens struct:
        //     so.FindProperty("Lens") / so.FindProperty("m_Lens")
        //     SetFloatSubProp(lensProp, "FieldOfView", ...)
        //     SetFloatSubProp(lensProp, "NearClipPlane", ...)
        //     SetFloatSubProp(lensProp, "FarClipPlane", ...)
        //     SetFloatSubProp(lensProp, "OrthographicSize", ...)
        //     SetFloatSubProp(lensProp, "Dutch", ...)
        //
        // Adapted: same SerializedObject pattern; tries both "Lens" and "m_Lens"
        // field paths for resilience.

        private static string CinemachineLens(BridgeRequest request)
        {
            RequireCinemachinePackage();
            var go = ResolveUniqueCinemachineTarget(request.StringArg("target"));
            var cmCamera = go.GetComponent(_cmCameraType);
            if (cmCamera == null)
                return ErrorJson("E_CINEMACHINE_COMPONENT_NOT_FOUND",
                    "GameObject does not have a CinemachineCamera component: " + HierarchyPath(go),
                    "Pass a target with a CinemachineCamera component.");
            var effects = new List<string>();
            using (var so = new SerializedObject(cmCamera))
            {
                var lensProp = so.FindProperty("Lens") ?? so.FindProperty("m_Lens");
                if (lensProp == null)
                    return ErrorJson("E_CINEMACHINE_LENS_PROPERTY_NOT_FOUND",
                        "Cinemachine Lens property not accessible via SerializedObject.",
                        "The Cinemachine package version may differ; check Console logs.");

                var fov = request.OptionalFloatArg("fieldOfView");
                if (fov.HasValue) { SetSubFloat(lensProp, "FieldOfView", fov.Value); effects.Add(EffectJson("cinemachine.lens", "fieldOfView", Number(fov.Value))); }
                var near = request.OptionalFloatArg("nearClipPlane");
                if (near.HasValue) { SetSubFloat(lensProp, "NearClipPlane", near.Value); effects.Add(EffectJson("cinemachine.lens", "nearClipPlane", Number(near.Value))); }
                var far = request.OptionalFloatArg("farClipPlane");
                if (far.HasValue) { SetSubFloat(lensProp, "FarClipPlane", far.Value); effects.Add(EffectJson("cinemachine.lens", "farClipPlane", Number(far.Value))); }
                var ortho = request.OptionalFloatArg("orthographicSize");
                if (ortho.HasValue) { SetSubFloat(lensProp, "OrthographicSize", ortho.Value); effects.Add(EffectJson("cinemachine.lens", "orthographicSize", Number(ortho.Value))); }
                var dutch = request.OptionalFloatArg("dutch");
                if (dutch.HasValue) { SetSubFloat(lensProp, "Dutch", dutch.Value); effects.Add(EffectJson("cinemachine.lens", "dutch", Number(dutch.Value))); }

                so.ApplyModifiedProperties();
            }
            if (effects.Count > 0) EditorSceneManager.MarkSceneDirty(go.scene);
            WriteLog("cinemachine_lens target=" + HierarchyPath(go));
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("cinemachine", CinemachinePackageJson())
                .PropRaw("object", CinemachineObjectJson(go, null))
                .PropArray("plannedEffects", arr => { foreach (var e in effects) arr.RawValue(e); })
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        // ── CinemachinePriority ──────────────────────────────────────────────
        // Critically references unity-mcp's SerializedObject approach for
        // PrioritySettings (Enabled + m_Value).

        private static string CinemachinePriority(BridgeRequest request)
        {
            RequireCinemachinePackage();
            var go = ResolveUniqueCinemachineTarget(request.StringArg("target"));
            var cmCamera = go.GetComponent(_cmCameraType);
            if (cmCamera == null)
                return ErrorJson("E_CINEMACHINE_COMPONENT_NOT_FOUND",
                    "GameObject does not have a CinemachineCamera component: " + HierarchyPath(go),
                    "Pass a target with a CinemachineCamera component.");
            var effects = new List<string>();
            var priority = request.OptionalIntArg("priority");
            if (priority.HasValue)
            {
                SetCinemachinePriority(cmCamera, priority.Value);
                effects.Add(EffectJson("cinemachine.priority", "priority", priority.Value.ToString(CultureInfo.InvariantCulture)));
            }
            var enabled = request.OptionalBoolArg("enabled");
            if (enabled.HasValue)
            {
                SetCinemachinePriorityEnabled(cmCamera, enabled.Value);
                effects.Add(EffectJson("cinemachine.priority", "enabled", Bool(enabled.Value)));
            }
            if (effects.Count > 0) EditorSceneManager.MarkSceneDirty(go.scene);
            WriteLog("cinemachine_priority target=" + HierarchyPath(go));
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("cinemachine", CinemachinePackageJson())
                .PropRaw("object", CinemachineObjectJson(go, null))
                .PropArray("plannedEffects", arr => { foreach (var e in effects) arr.RawValue(e); })
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        // ── CinemachineBlend ─────────────────────────────────────────────────
        // Critically references unity-mcp CameraControl.SetBlend:
        //   Uses SerializedObject for DefaultBlend (Style enum index + Time float).

        private static string CinemachineBlend(BridgeRequest request)
        {
            RequireCinemachinePackage();
            var brainTarget = request.StringArg("target");
            var brainGo = !string.IsNullOrEmpty(brainTarget) ? ResolveGameObject(brainTarget) : FindCinemachineBrainOwner();
            if (brainGo == null)
                return ErrorJson("E_CINEMACHINE_BRAIN_NOT_FOUND",
                    "No CinemachineBrain found in the scene.",
                    "Use cinemachine brain --action ensure to add one.");
            var brain = brainGo.GetComponent(_cmBrainType);
            if (brain == null)
                return ErrorJson("E_CINEMACHINE_BRAIN_NOT_FOUND",
                    "GameObject does not have CinemachineBrain: " + HierarchyPath(brainGo),
                    "Use cinemachine brain --action ensure to add one.");
            var effects = new List<string>();
            using (var so = new SerializedObject(brain))
            {
                var blendProp = so.FindProperty("DefaultBlend") ?? so.FindProperty("m_DefaultBlend");
                if (blendProp == null)
                    return ErrorJson("E_CINEMACHINE_BLEND_PROPERTY_NOT_FOUND",
                        "DefaultBlend property not accessible on CinemachineBrain.",
                        "The Cinemachine package version may differ; check Console logs.");
                var styleName = request.StringArg("style");
                if (!string.IsNullOrEmpty(styleName))
                {
                    var styleProp = blendProp.FindPropertyRelative("Style") ?? blendProp.FindPropertyRelative("m_Style");
                    if (styleProp != null)
                    {
                        var enumIdx = BlendStyleToEnumIndex(styleName);
                        if (enumIdx.HasValue) { styleProp.enumValueIndex = enumIdx.Value; effects.Add(EffectJson("cinemachine.blend", "style", styleName)); }
                    }
                }
                var duration = request.OptionalFloatArg("duration");
                if (duration.HasValue)
                {
                    var timeProp = blendProp.FindPropertyRelative("Time") ?? blendProp.FindPropertyRelative("m_Time");
                    if (timeProp != null) { timeProp.floatValue = duration.Value; effects.Add(EffectJson("cinemachine.blend", "duration", Number(duration.Value))); }
                }
                so.ApplyModifiedProperties();
            }
            if (effects.Count > 0) EditorSceneManager.MarkSceneDirty(brainGo.scene);
            WriteLog("cinemachine_blend target=" + HierarchyPath(brainGo));
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("cinemachine", CinemachinePackageJson())
                .PropRaw("object", CinemachineObjectJson(brainGo, null))
                .PropArray("plannedEffects", arr => { foreach (var e in effects) arr.RawValue(e); })
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        // ── CinemachineBody ──────────────────────────────────────────────────
        // Critically references unity-mcp CameraConfigure.SetBody:
        //   Gets existing pipeline component via CinemachineCamera.GetCinemachineComponent(CinemachineCore.Stage.Body)
        //   Destroys old component if type differs, adds new via Undo.AddComponent.

        private static string CinemachineBody(BridgeRequest request)
        {
            RequireCinemachinePackage();
            var go = ResolveUniqueCinemachineTarget(request.StringArg("target"));
            var cmCamera = go.GetComponent(_cmCameraType);
            if (cmCamera == null)
                return ErrorJson("E_CINEMACHINE_COMPONENT_NOT_FOUND",
                    "GameObject does not have a CinemachineCamera component: " + HierarchyPath(go),
                    "Pass a target with a CinemachineCamera component.");
            var bodyTypeName = request.StringArg("body");
            if (string.IsNullOrEmpty(bodyTypeName))
                return ErrorJson("E_CINEMACHINE_BODY_TYPE_REQUIRED",
                    "cinemachine body requires --body <type>.",
                    "Pass a Body component type such as CinemachineFollow, CinemachinePositionComposer, CinemachineOrbitalFollow, CinemachineSplineDolly, CinemachineThirdPersonFollow.");
            var bodyType = ResolveCinemachineComponentType(bodyTypeName);
            if (bodyType == null)
                return ErrorJson("E_CINEMACHINE_COMPONENT_TYPE_NOT_FOUND",
                    "Body component type not found in loaded assemblies: " + bodyTypeName,
                    "Check the Cinemachine package is fully imported and the type name is correct.");
            SwapPipelineComponent(go, cmCamera, "Body", bodyType);
            EditorSceneManager.MarkSceneDirty(go.scene);
            WriteLog("cinemachine_body target=" + HierarchyPath(go) + " body=" + bodyTypeName);
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("cinemachine", CinemachinePackageJson())
                .PropRaw("object", CinemachineObjectJson(go, null))
                .PropArray("plannedEffects", arr => arr.BeginObject()
                    .Prop("type", "cinemachine.body")
                    .Prop("bodyType", bodyTypeName)
                    .Prop("target", HierarchyPath(go))
                    .EndObject())
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        // ── CinemachineAim ───────────────────────────────────────────────────

        private static string CinemachineAim(BridgeRequest request)
        {
            RequireCinemachinePackage();
            var go = ResolveUniqueCinemachineTarget(request.StringArg("target"));
            var cmCamera = go.GetComponent(_cmCameraType);
            if (cmCamera == null)
                return ErrorJson("E_CINEMACHINE_COMPONENT_NOT_FOUND",
                    "GameObject does not have a CinemachineCamera component: " + HierarchyPath(go),
                    "Pass a target with a CinemachineCamera component.");
            var aimTypeName = request.StringArg("aim");
            if (string.IsNullOrEmpty(aimTypeName))
                return ErrorJson("E_CINEMACHINE_AIM_TYPE_REQUIRED",
                    "cinemachine aim requires --aim <type>.",
                    "Pass an Aim component type such as CinemachineRotationComposer or CinemachineHardLookAt.");
            var aimType = ResolveCinemachineComponentType(aimTypeName);
            if (aimType == null)
                return ErrorJson("E_CINEMACHINE_COMPONENT_TYPE_NOT_FOUND",
                    "Aim component type not found in loaded assemblies: " + aimTypeName,
                    "Check the Cinemachine package is fully imported and the type name is correct.");
            SwapPipelineComponent(go, cmCamera, "Aim", aimType);
            EditorSceneManager.MarkSceneDirty(go.scene);
            WriteLog("cinemachine_aim target=" + HierarchyPath(go) + " aim=" + aimTypeName);
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("cinemachine", CinemachinePackageJson())
                .PropRaw("object", CinemachineObjectJson(go, null))
                .PropArray("plannedEffects", arr => arr.BeginObject()
                    .Prop("type", "cinemachine.aim")
                    .Prop("aimType", aimTypeName)
                    .Prop("target", HierarchyPath(go))
                    .EndObject())
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        // ── CinemachineNoise ─────────────────────────────────────────────────
        // Critically references unity-mcp CameraConfigure.SetNoise:
        //   Adds CinemachineBasicMultiChannelPerlin component if missing.

        private static string CinemachineNoise(BridgeRequest request)
        {
            RequireCinemachinePackage();
            var go = ResolveUniqueCinemachineTarget(request.StringArg("target"));
            var cmCamera = go.GetComponent(_cmCameraType);
            if (cmCamera == null)
                return ErrorJson("E_CINEMACHINE_COMPONENT_NOT_FOUND",
                    "GameObject does not have a CinemachineCamera component: " + HierarchyPath(go),
                    "Pass a target with a CinemachineCamera component.");

            // Remove noise if requested
            if (request.BoolArg("remove", false))
            {
                var noiseType = ResolveCinemachineComponentType("CinemachineBasicMultiChannelPerlin");
                if (noiseType != null)
                {
                    var noiseComp = go.GetComponent(noiseType);
                    if (noiseComp != null)
                    {
                        Undo.DestroyObjectImmediate(noiseComp);
                        EditorSceneManager.MarkSceneDirty(go.scene);
                        WriteLog("cinemachine_noise_removed target=" + HierarchyPath(go));
                        return SuccessJson(new UnityCliJsonWriter().BeginObject()
                            .PropRaw("cinemachine", CinemachinePackageJson())
                            .PropRaw("object", CinemachineObjectJson(go, null))
                            .PropArray("plannedEffects", arr => arr.BeginObject()
                                .Prop("type", "cinemachine.noise.remove")
                                .Prop("target", HierarchyPath(go))
                                .EndObject())
                            .Prop("logPath", LogPath())
                            .EndObject().ToString());
                    }
                }
                return ErrorJson("E_CINEMACHINE_NOISE_NOT_FOUND",
                    "No CinemachineBasicMultiChannelPerlin component found on target: " + HierarchyPath(go),
                    "The noise component may have already been removed.");
            }

            var noiseType = ResolveCinemachineComponentType("CinemachineBasicMultiChannelPerlin");
            if (noiseType == null)
                return ErrorJson("E_CINEMACHINE_COMPONENT_TYPE_NOT_FOUND",
                    "CinemachineBasicMultiChannelPerlin type not found.",
                    "Ensure the Cinemachine package is fully imported.");
            var noiseComp = go.GetComponent(noiseType) ?? Undo.AddComponent(go, noiseType);
            var effects = new List<string>();

            // Noise profile assignment
            var profileName = request.StringArg("noiseProfile");
            if (!string.IsNullOrEmpty(profileName))
            {
                var profileAsset = FindNoiseProfile(profileName);
                if (profileAsset != null)
                {
                    var noiseProfileProp = noiseComp.GetType().GetProperty("NoiseProfile") ?? noiseComp.GetType().GetProperty("m_NoiseProfile");
                    if (noiseProfileProp != null && noiseProfileProp.CanWrite)
                    {
                        noiseProfileProp.SetValue(noiseComp, profileAsset, null);
                        effects.Add(EffectJson("cinemachine.noise", "noiseProfile", profileName));
                    }
                }
            }

            // Amplitude / Frequency via SerializedObject
            var amplitude = request.OptionalFloatArg("amplitude");
            var frequency = request.OptionalFloatArg("frequency");
            if (amplitude.HasValue || frequency.HasValue)
            {
                using (var so = new SerializedObject(noiseComp))
                {
                    if (amplitude.HasValue)
                    {
                        var ampProp = so.FindProperty("Amplitude") ?? so.FindProperty("m_Amplitude");
                        if (ampProp != null) { ampProp.floatValue = amplitude.Value; effects.Add(EffectJson("cinemachine.noise", "amplitude", Number(amplitude.Value))); }
                    }
                    if (frequency.HasValue)
                    {
                        var freqProp = so.FindProperty("Frequency") ?? so.FindProperty("m_Frequency");
                        if (freqProp != null) { freqProp.floatValue = frequency.Value; effects.Add(EffectJson("cinemachine.noise", "frequency", Number(frequency.Value))); }
                    }
                    so.ApplyModifiedProperties();
                }
            }

            EditorSceneManager.MarkSceneDirty(go.scene);
            WriteLog("cinemachine_noise target=" + HierarchyPath(go));
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("cinemachine", CinemachinePackageJson())
                .PropRaw("object", CinemachineObjectJson(go, null))
                .PropArray("plannedEffects", arr => { foreach (var e in effects) arr.RawValue(e); })
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        // ── CinemachineBrain ─────────────────────────────────────────────────
        // Critically references unity-mcp CameraControl: ensure_brain + get_brain_status.
        //   ensure_brain: adds CinemachineBrain component
        //   get_brain_status: reads ActiveVirtualCamera, IsBlending, ActiveBlend

        private static string CinemachineBrain(BridgeRequest request)
        {
            RequireCinemachinePackage();
            var targetName = request.StringArg("target");
            var action = request.StringArg("action") ?? "ensure";

            if (action == "ensure")
            {
                var go = !string.IsNullOrEmpty(targetName) ? ResolveGameObject(targetName) : FindOrCreateBrainOwner();
                if (go == null)
                {
                    go = new GameObject("CinemachineBrain");
                    Undo.RegisterCreatedObjectUndo(go, "UnityCli Cinemachine Brain Ensure");
                }
                var brain = go.GetComponent(_cmBrainType);
                if (brain == null)
                {
                    brain = Undo.AddComponent(go, _cmBrainType);
                    EditorSceneManager.MarkSceneDirty(go.scene);
                }
                WriteLog("cinemachine_brain_ensure target=" + HierarchyPath(go));
                return SuccessJson(new UnityCliJsonWriter().BeginObject()
                    .PropRaw("cinemachine", CinemachinePackageJson())
                    .PropRaw("brain", CinemachineBrainJson(brain))
                    .PropArray("plannedEffects", arr => arr.BeginObject()
                        .Prop("type", "cinemachine.brain.ensure")
                        .Prop("target", HierarchyPath(go))
                        .EndObject())
                    .Prop("logPath", LogPath())
                    .EndObject().ToString());
            }

            // action == "status"
            var brainGo = !string.IsNullOrEmpty(targetName) ? ResolveGameObject(targetName) : FindCinemachineBrainOwner();
            if (brainGo == null)
                return ErrorJson("E_CINEMACHINE_BRAIN_NOT_FOUND",
                    "No CinemachineBrain found in the scene.",
                    "Use cinemachine brain --action ensure to add one.");
            var brain = brainGo.GetComponent(_cmBrainType);
            if (brain == null)
                return ErrorJson("E_CINEMACHINE_BRAIN_NOT_FOUND",
                    "GameObject does not have CinemachineBrain: " + HierarchyPath(brainGo),
                    "Use cinemachine brain --action ensure to add one.");
            WriteLog("cinemachine_brain_status target=" + HierarchyPath(brainGo));
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("cinemachine", CinemachinePackageJson())
                .PropRaw("brain", CinemachineBrainJson(brain))
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        // ── CinemachineExtension ────────────────────────────────────────────
        // Critically references unity-mcp CameraConfigure add_extension / remove_extension:
        //   Resolves type via UnityTypeResolver.ResolveComponent()
        //   Checks CinemachineExtension base type for listing

        private static string CinemachineExtension(BridgeRequest request)
        {
            RequireCinemachinePackage();
            var go = ResolveUniqueCinemachineTarget(request.StringArg("target"));
            var cmCamera = go.GetComponent(_cmCameraType);
            if (cmCamera == null)
                return ErrorJson("E_CINEMACHINE_COMPONENT_NOT_FOUND",
                    "GameObject does not have a CinemachineCamera component: " + HierarchyPath(go),
                    "Pass a target with a CinemachineCamera component.");
            var action = request.StringArg("action") ?? "list";

            if (action == "list")
            {
                var extensions = new List<string>();
                if (_cmExtensionBaseType != null)
                {
                    foreach (var comp in go.GetComponents(_cmExtensionBaseType))
                    {
                        extensions.Add(comp.GetType().Name);
                    }
                }
                WriteLog("cinemachine_extension_list target=" + HierarchyPath(go));
                return SuccessJson(new UnityCliJsonWriter().BeginObject()
                    .PropRaw("cinemachine", CinemachinePackageJson())
                    .PropArray("extensions", arr => { foreach (var ext in extensions) arr.Value(ext); })
                    .Prop("count", (long)extensions.Count)
                    .Prop("logPath", LogPath())
                    .EndObject().ToString());
            }

            if (action == "add")
            {
                var extensionTypeName = request.StringArg("extensionType");
                if (string.IsNullOrEmpty(extensionTypeName))
                    return ErrorJson("E_CINEMACHINE_EXTENSION_TYPE_REQUIRED",
                        "Add extension requires --extension-type <type>.",
                        "Pass a CinemachineExtension-derived type name.");
                var extType = FindType(extensionTypeName) ?? ResolveCinemachineComponentType(extensionTypeName);
                if (extType == null)
                    return ErrorJson("E_CINEMACHINE_COMPONENT_TYPE_NOT_FOUND",
                        "Extension type not found: " + extensionTypeName,
                        "Ensure the Cinemachine package is fully imported and the type name is correct.");
                var existing = go.GetComponent(extType);
                if (existing != null)
                    return ErrorJson("E_CINEMACHINE_EXTENSION_EXISTS",
                        "Extension already present: " + extensionTypeName,
                        "Remove it first with --action remove --extension-type " + extensionTypeName);
                Undo.AddComponent(go, extType);
                EditorSceneManager.MarkSceneDirty(go.scene);
                WriteLog("cinemachine_extension_add target=" + HierarchyPath(go) + " type=" + extensionTypeName);
                return SuccessJson(new UnityCliJsonWriter().BeginObject()
                    .PropRaw("cinemachine", CinemachinePackageJson())
                    .PropArray("plannedEffects", arr => arr.BeginObject()
                        .Prop("type", "cinemachine.extension.add")
                        .Prop("extensionType", extensionTypeName)
                        .Prop("target", HierarchyPath(go))
                        .EndObject())
                    .Prop("logPath", LogPath())
                    .EndObject().ToString());
            }

            if (action == "remove")
            {
                var extensionTypeName = request.StringArg("extensionType");
                if (string.IsNullOrEmpty(extensionTypeName))
                    return ErrorJson("E_CINEMACHINE_EXTENSION_TYPE_REQUIRED",
                        "Remove extension requires --extension-type <type>.",
                        "Pass a CinemachineExtension-derived type name to remove.");
                var extType = FindType(extensionTypeName) ?? ResolveCinemachineComponentType(extensionTypeName);
                if (extType == null)
                    return ErrorJson("E_CINEMACHINE_COMPONENT_TYPE_NOT_FOUND",
                        "Extension type not found: " + extensionTypeName,
                        "Ensure the type name is correct.");
                var existing = go.GetComponent(extType);
                if (existing == null)
                    return ErrorJson("E_CINEMACHINE_EXTENSION_NOT_FOUND",
                        "Extension not found on target: " + extensionTypeName,
                        "Use --action list to inspect existing extensions.");
                Undo.DestroyObjectImmediate(existing);
                EditorSceneManager.MarkSceneDirty(go.scene);
                WriteLog("cinemachine_extension_remove target=" + HierarchyPath(go) + " type=" + extensionTypeName);
                return SuccessJson(new UnityCliJsonWriter().BeginObject()
                    .PropRaw("cinemachine", CinemachinePackageJson())
                    .PropArray("plannedEffects", arr => arr.BeginObject()
                        .Prop("type", "cinemachine.extension.remove")
                        .Prop("extensionType", extensionTypeName)
                        .Prop("target", HierarchyPath(go))
                        .EndObject())
                    .Prop("logPath", LogPath())
                    .EndObject().ToString());
            }

            return ErrorJson("E_CINEMACHINE_EXTENSION_ACTION_UNSUPPORTED",
                "Unsupported extension action: " + action,
                "Use list, add or remove.");
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private static GameObject ResolveUniqueCinemachineTarget(string target)
        {
            if (string.IsNullOrEmpty(target))
                throw new BridgeCommandException("E_TARGET_NOT_FOUND",
                    "Cinemachine command requires --target <go>.",
                    "Pass a GameObject name, hierarchy path or instance id.");
            var go = ResolveGameObject(target);
            if (go == null)
                throw new BridgeCommandException("E_TARGET_NOT_FOUND",
                    "Cinemachine target not found: " + target,
                    "Pass a GameObject name, hierarchy path or instance id.");
            return go;
        }

        private static Type ResolveCinemachineComponentType(string typeName)
        {
            // Search in Cinemachine assembly first, then all assemblies
            if (_cmCameraType != null)
            {
                var type = _cmCameraType.Assembly.GetType("Unity.Cinemachine." + typeName)
                        ?? _cmCameraType.Assembly.GetType("Cinemachine." + typeName);
                if (type != null) return type;
            }
            return FindType("Unity.Cinemachine." + typeName)
                ?? FindType("Cinemachine." + typeName);
        }

        private static void SetCinemachineFollowTarget(Component cmCamera, Transform target)
        {
            // References unity-mcp CameraHelpers.SetTransformTarget:
            //   Uses reflection PropertyInfo.SetValue for Follow/LookAt.
            var followProp = cmCamera.GetType().GetProperty("Follow", BindingFlags.Instance | BindingFlags.Public)
                          ?? cmCamera.GetType().GetProperty("m_Follow", BindingFlags.Instance | BindingFlags.NonPublic);
            if (followProp != null && followProp.CanWrite)
                followProp.SetValue(cmCamera, target, null);
        }

        private static void SetCinemachineLookAtTarget(Component cmCamera, Transform target)
        {
            var lookAtProp = cmCamera.GetType().GetProperty("LookAt", BindingFlags.Instance | BindingFlags.Public)
                          ?? cmCamera.GetType().GetProperty("m_LookAt", BindingFlags.Instance | BindingFlags.NonPublic);
            if (lookAtProp != null && lookAtProp.CanWrite)
                lookAtProp.SetValue(cmCamera, target, null);
        }

        private static void SetCinemachinePriority(Component cmCamera, int priority)
        {
            // References unity-mcp's SerializedObject approach for PrioritySettings struct
            using (var so = new SerializedObject(cmCamera))
            {
                var priorityProp = so.FindProperty("Priority") ?? so.FindProperty("m_Priority");
                if (priorityProp != null)
                {
                    var valueProp = priorityProp.FindPropertyRelative("Value") ?? priorityProp.FindPropertyRelative("m_Value");
                    if (valueProp != null) valueProp.intValue = priority;
                }
                else
                {
                    // Fallback: direct property
                    var directProp = cmCamera.GetType().GetProperty("Priority", BindingFlags.Instance | BindingFlags.Public);
                    if (directProp != null && directProp.CanWrite)
                        directProp.SetValue(cmCamera, priority, null);
                }
                so.ApplyModifiedProperties();
            }
        }

        private static void SetCinemachinePriorityEnabled(Component cmCamera, bool enabled)
        {
            using (var so = new SerializedObject(cmCamera))
            {
                var priorityProp = so.FindProperty("Priority") ?? so.FindProperty("m_Priority");
                if (priorityProp != null)
                {
                    var enabledProp = priorityProp.FindPropertyRelative("Enabled") ?? priorityProp.FindPropertyRelative("m_Enabled");
                    if (enabledProp != null) enabledProp.boolValue = enabled;
                }
                so.ApplyModifiedProperties();
            }
        }

        private static void SwapPipelineComponent(GameObject go, Component cmCamera, string stage, Type newType)
        {
            // References unity-mcp CameraConfigure: get existing via GetCinemachineComponent(Stage),
            // destroy if type differs, add new via Undo.AddComponent.
            var existing = GetPipelineComponent(cmCamera, stage);
            if (existing != null && existing.GetType() == newType) return; // same type, no swap needed
            if (existing != null) Undo.DestroyObjectImmediate(existing);
            Undo.AddComponent(go, newType);
        }

        private static Component GetPipelineComponent(Component cmCamera, string stage)
        {
            // References unity-mcp: CinemachineCamera.GetCinemachineComponent(CinemachineCore.Stage.Body/Aim)
            // Adapted: resolves Stage enum via reflection, calls GetCinemachineComponent method.
            if (_cmCoreType == null) return null;
            var stageEnumType = _cmCoreType.GetNestedType("Stage");
            if (stageEnumType == null) return null;
            var stageValue = Enum.Parse(stageEnumType, stage == "Body" ? "Body" : "Aim");
            var getMethod = _cmCameraType.GetMethod("GetCinemachineComponent", BindingFlags.Instance | BindingFlags.Public);
            if (getMethod == null) return null;
            return getMethod.Invoke(cmCamera, new object[] { stageValue }) as Component;
        }

        private static UnityEngine.Object FindNoiseProfile(string profileName)
        {
            // NoiseProfile assets are typically ScriptableObjects in the Cinemachine package
            var guids = AssetDatabase.FindAssets(profileName + " t:NoiseSettings");
            if (guids.Length == 0) guids = AssetDatabase.FindAssets(profileName + " t:ScriptableObject");
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
                if (asset != null) return asset;
            }
            return null;
        }

        private static GameObject FindCinemachineBrainOwner()
        {
            foreach (var comp in Resources.FindObjectsOfTypeAll(_cmBrainType))
            {
                var c = comp as Component;
                if (c != null && c.gameObject != null && !EditorUtility.IsPersistent(c.gameObject)) return c.gameObject;
            }
            return null;
        }

        private static GameObject FindOrCreateBrainOwner()
        {
            var existing = FindCinemachineBrainOwner();
            if (existing != null) return existing;
            // Default: attach brain to the main camera
            var mainCam = Camera.main;
            if (mainCam != null) return mainCam.gameObject;
            return null;
        }

        private static int? BlendStyleToEnumIndex(string styleName)
        {
            // Cinemachine blend style enum values
            var normalized = styleName.Replace("-", "_").ToLowerInvariant();
            var map = new Dictionary<string, int>
            {
                { "cut",           0 },
                { "ease_in_out",   1 },
                { "ease_in",       2 },
                { "ease_out",      3 },
                { "hard_in",       4 },
                { "hard_out",      5 },
                { "linear",        6 },
                { "soft_in",       7 },
                { "soft_out",      8 }
            };
            int idx;
            return map.TryGetValue(normalized, out idx) ? idx : null;
        }

        private static void SetSubFloat(SerializedProperty parent, string subPropName, float value)
        {
            var prop = parent.FindPropertyRelative(subPropName) ?? parent.FindPropertyRelative("m_" + subPropName);
            if (prop != null) prop.floatValue = value;
        }

        private static string CinemachinePackageJson()
        {
            return new UnityCliJsonWriter().BeginObject()
                .Prop("installed", IsCinemachineInstalled())
                .Prop("cmCameraTypeAvailable", _cmCameraType != null)
                .Prop("cmBrainTypeAvailable", _cmBrainType != null)
                .EndObject().ToString();
        }

        private static string CinemachineObjectJson(GameObject go, string preset)
        {
            var cmCamera = go.GetComponent(_cmCameraType);
            var hasCm = cmCamera != null;
            var extensions = new List<string>();
            if (hasCm && _cmExtensionBaseType != null)
            {
                foreach (var comp in go.GetComponents(_cmExtensionBaseType))
                    extensions.Add(comp.GetType().Name);
            }
            return new UnityCliJsonWriter().BeginObject()
                .Prop("id", (long)go.GetInstanceID())
                .Prop("name", go.name)
                .Prop("path", HierarchyPath(go))
                .Prop("preset", preset)
                .PropRaw("position", Vector3Json(go.transform.position))
                .Prop("hasCinemachineCamera", hasCm)
                .PropArray("extensions", arr => { foreach (var e in extensions) arr.Value(e); })
                .EndObject().ToString();
        }

        private static string CinemachineBrainJson(Component brain)
        {
            // References unity-mcp CameraControl: ActiveVirtualCamera, IsBlending, ActiveBlend
            var brainType = brain.GetType();
            var activeVcamProp = brainType.GetProperty("ActiveVirtualCamera", BindingFlags.Instance | BindingFlags.Public);
            var isBlendingProp = brainType.GetProperty("IsBlending", BindingFlags.Instance | BindingFlags.Public);
            var activeBlendProp = brainType.GetProperty("ActiveBlend", BindingFlags.Instance | BindingFlags.Public);

            var activeVcam = activeVcamProp != null ? activeVcamProp.GetValue(brain, null) : null;
            var activeVcamName = activeVcam != null ? (activeVcam is Component c ? c.gameObject.name : activeVcam.ToString()) : null;
            var isBlending = isBlendingProp != null && Convert.ToBoolean(isBlendingProp.GetValue(brain, null), CultureInfo.InvariantCulture);

            return new UnityCliJsonWriter().BeginObject()
                .Prop("enabled", ComponentEnabled(brain))
                .Prop("hasBrain", true)
                .Prop("activeVirtualCamera", activeVcamName)
                .Prop("isBlending", isBlending)
                .Prop("componentType", brainType.Name)
                .EndObject().ToString();
        }
    }
}
#endif
