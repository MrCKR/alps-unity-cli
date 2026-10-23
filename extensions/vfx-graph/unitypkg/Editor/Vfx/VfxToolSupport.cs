#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// VFX Graph / ParticleSystem / LineRenderer / TrailRenderer 工具共享支持层。
    /// 负责包探测、目标解析、公共 DTO 构造与渲染组件字段应用，不参与工具发现。
    /// </summary>
    internal static class VfxToolSupport
    {
        public static void EnsureEditorReady(string toolName)
        {
            GameObjectToolSupport.EnsureEditorNotBusy(toolName);
        }

        public static void RequireVfxPackageInstalled()
        {
            if (PackageManifestContains("com.unity.visualeffectgraph") || PackageLockContains("com.unity.visualeffectgraph"))
            {
                return;
            }

            throw new UnityCliBridge.BridgeCommandException(
                "E_VFX_PACKAGE_MISSING",
                "Visual Effect Graph package is not installed in this Unity project.",
                "Install com.unity.visualeffectgraph manually via Unity Package Manager or Packages/manifest.json, then run unitycli extensions install vfx-graph --project <path>.");
        }

        public static Type RequireVfxAssetType()
        {
            RequireVfxPackageInstalled();
            var type = GameObjectToolSupport.FindType("UnityEngine.VFX.VisualEffectAsset");
            if (type == null)
            {
                throw new UnityCliBridge.BridgeCommandException(
                    "E_VFX_PACKAGE_MISSING",
                    "Visual Effect Graph package is installed in the manifest but its editor/runtime assemblies are not loaded.",
                    "Wait for Unity PackageManager resolve/import to finish, then retry.");
            }
            return type;
        }

        public static Type RequireVisualEffectType()
        {
            RequireVfxPackageInstalled();
            var type = GameObjectToolSupport.FindType("UnityEngine.VFX.VisualEffect");
            if (type == null)
            {
                throw new UnityCliBridge.BridgeCommandException(
                    "E_VFX_PACKAGE_MISSING",
                    "Visual Effect Graph package is installed in the manifest but its runtime assembly is not loaded.",
                    "Wait for Unity PackageManager resolve/import to finish, then retry.");
            }
            return type;
        }

        public static string RequireVfxGraphPath(string rawPath)
        {
            var path = AssetToolSupport.NormalizeAssetPath(rawPath);
            if (string.IsNullOrEmpty(path))
            {
                throw new UnityCliBridge.BridgeCommandException(
                    "E_VFX_GRAPH_PATH_REQUIRED",
                    "Pass an Assets/*.vfx path.",
                    "Use unitycli tools run vfx.graph-info --input '{\"path\":\"Assets/VFX/Effect.vfx\"}'.");
            }

            if (!path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) || !path.EndsWith(".vfx", StringComparison.OrdinalIgnoreCase))
            {
                throw new UnityCliBridge.BridgeCommandException(
                    "E_VFX_GRAPH_PATH_INVALID",
                    "VFX Graph path must be under Assets/ and end with .vfx: " + path,
                    "Pass a Unity asset path such as Assets/VFX/Explosion.vfx.");
            }

            return path;
        }

        public static VfxGraphInfo BuildGraphInfo(string path, UnityEngine.Object asset)
        {
            return new VfxGraphInfo
            {
                Path = path,
                Guid = AssetDatabase.AssetPathToGUID(path),
                Name = asset == null ? null : asset.name,
                Type = asset == null ? null : asset.GetType().FullName,
                Hash = HashAsset(path)
            };
        }

        public static VfxEffectInfo BuildEffectInfo(Component component)
        {
            var property = component.GetType().GetProperty("visualEffectAsset", BindingFlags.Instance | BindingFlags.Public);
            var graph = property == null ? null : property.GetValue(component, null) as UnityEngine.Object;
            return new VfxEffectInfo
            {
                ComponentType = component.GetType().FullName,
                Enabled = GameObjectToolSupport.ComponentEnabled(component),
                Graph = BuildAssetRef(graph)
            };
        }

        public static ParticleSystem ResolveParticleSystem(string target)
        {
            if (string.IsNullOrWhiteSpace(target))
            {
                var all = UnityEngine.Object.FindObjectsOfType<ParticleSystem>(true);
                return all.Length == 0 ? null : all[0];
            }

            var gameObject = FindGameObjectByTarget(target);
            return gameObject == null ? null : gameObject.GetComponent<ParticleSystem>();
        }

        public static LineRenderer ResolveLineRenderer(string target)
        {
            if (string.IsNullOrWhiteSpace(target))
            {
                var all = UnityEngine.Object.FindObjectsOfType<LineRenderer>(true);
                return all.Length == 0 ? null : all[0];
            }

            var gameObject = FindGameObjectByTarget(target);
            return gameObject == null ? null : gameObject.GetComponent<LineRenderer>();
        }

        public static TrailRenderer ResolveTrailRenderer(string target)
        {
            if (string.IsNullOrWhiteSpace(target))
            {
                var all = UnityEngine.Object.FindObjectsOfType<TrailRenderer>(true);
                return all.Length == 0 ? null : all[0];
            }

            var gameObject = FindGameObjectByTarget(target);
            return gameObject == null ? null : gameObject.GetComponent<TrailRenderer>();
        }

        public static GameObject FindGameObjectByTarget(string target)
        {
            foreach (var gameObject in UnityEngine.Object.FindObjectsOfType<GameObject>(true))
            {
                if (gameObject.name == target
                    || gameObject.GetInstanceID().ToString(CultureInfo.InvariantCulture) == target
                    || GameObjectToolSupport.HierarchyPath(gameObject) == target)
                {
                    return gameObject;
                }
            }

            return null;
        }

        public static GameObject CreateSceneObject(string name)
        {
            return new GameObject(string.IsNullOrWhiteSpace(name) ? "GameObject" : name);
        }

        // Commit 后只能通过稳定 instance ID 重新取得场景对象，不能把提交前的组件引用当作完成证明。
        public static T ResolveObservedComponent<T>(int gameObjectId, string toolName) where T : Component
        {
            var gameObject = FindGameObjectByTarget(gameObjectId.ToString(CultureInfo.InvariantCulture));
            var component = gameObject == null ? null : gameObject.GetComponent<T>();
            if (component == null)
                throw new UnityCliBridge.BridgeCommandException("E_VFX_OBSERVED_TARGET_MISSING", toolName + " target/component disappeared after commit.", "Retry after checking the target still exists.");
            return component;
        }

        public static void RequireFinite(double? value, string field, double? minimum = null, double? maximum = null)
        {
            if (!value.HasValue) return;
            if (double.IsNaN(value.Value) || double.IsInfinity(value.Value) || value.Value < -float.MaxValue || value.Value > float.MaxValue || (minimum.HasValue && value.Value < minimum.Value) || (maximum.HasValue && value.Value > maximum.Value))
                throw new UnityCliBridge.BridgeCommandException("E_VFX_INPUT_RANGE_INVALID", "Invalid " + field + " value.", "Pass a finite value within the supported range.");
        }

        public static void PrepareParticleBursts(List<ParticleBurstInput> bursts)
        {
            if (bursts == null) return;
            foreach (var burst in bursts)
            {
                if (burst == null) throw new UnityCliBridge.BridgeCommandException("E_VFX_INPUT_RANGE_INVALID", "bursts cannot contain null entries.", "Pass a complete burst object for every entry.");
                RequireFinite(burst.Time, "bursts.time", 0);
                RequireFinite(burst.RepeatInterval, "bursts.repeatInterval", 0);
                if (burst.Count.HasValue && (burst.Count.Value < 0 || burst.Count.Value > short.MaxValue)) throw new UnityCliBridge.BridgeCommandException("E_VFX_INPUT_RANGE_INVALID", "bursts.count must be a non-negative Int16.", "Pass a count between 0 and 32767.");
                if (burst.Cycles.HasValue && (burst.Cycles.Value < 1 || burst.Cycles.Value > short.MaxValue)) throw new UnityCliBridge.BridgeCommandException("E_VFX_INPUT_RANGE_INVALID", "bursts.cycles must fit positive Int16.", "Pass cycles between 1 and 32767.");
            }
        }

        public static void RequireMutation(bool hasMutation, string toolName)
        {
            if (!hasMutation)
                throw new UnityCliBridge.BridgeCommandException("E_VFX_INPUT_REQUIRED", toolName + " requires at least one field to modify.", "Provide one or more supported mutation fields.");
        }

        public static void PrepareLineRendererFields(LineRendererFields request)
        {
            RequireFinite(request.StartWidth, "startWidth", 0); RequireFinite(request.EndWidth, "endWidth", 0); RequireFinite(request.WidthMultiplier, "widthMultiplier", 0);
            RequireFinite(request.PositionCount, "positionCount", 0); RequireFinite(request.NumCornerVertices, "numCornerVertices", 0); RequireFinite(request.NumCapVertices, "numCapVertices", 0);
            if (request.WidthCurve != null) BuildCurve(request.WidthCurve);
            if (request.ColorGradient != null) BuildGradient(request.ColorGradient);
            if (!string.IsNullOrWhiteSpace(request.StartColor)) GraphicsToolSupport.ParseColor(request.StartColor);
            if (!string.IsNullOrWhiteSpace(request.EndColor)) GraphicsToolSupport.ParseColor(request.EndColor);
            if (!string.IsNullOrWhiteSpace(request.Material)) ResolveOptionalMaterial(request.Material);
            if (!string.IsNullOrWhiteSpace(request.Alignment)) ParseEnum<LineAlignment>(request.Alignment, "E_LINE_ALIGNMENT_INVALID", "Use View or TransformZ.");
            if (!string.IsNullOrWhiteSpace(request.TextureMode)) ParseEnum<LineTextureMode>(request.TextureMode, "E_LINE_TEXTURE_MODE_INVALID", "Use a valid LineTextureMode.");
            if (request.Positions != null) foreach (var position in request.Positions) { RequireFinite(position.X, "positions.x"); RequireFinite(position.Y, "positions.y"); RequireFinite(position.Z, "positions.z"); }
        }

        public static void PrepareTrailRendererFields(TrailRendererFields request)
        {
            RequireFinite(request.Time, "time", 0); RequireFinite(request.StartWidth, "startWidth", 0); RequireFinite(request.EndWidth, "endWidth", 0); RequireFinite(request.WidthMultiplier, "widthMultiplier", 0); RequireFinite(request.MinVertexDistance, "minVertexDistance", 0);
            RequireFinite(request.NumCornerVertices, "numCornerVertices", 0); RequireFinite(request.NumCapVertices, "numCapVertices", 0);
            if (request.WidthCurve != null) BuildCurve(request.WidthCurve);
            if (request.ColorGradient != null) BuildGradient(request.ColorGradient);
            if (!string.IsNullOrWhiteSpace(request.StartColor)) GraphicsToolSupport.ParseColor(request.StartColor);
            if (!string.IsNullOrWhiteSpace(request.EndColor)) GraphicsToolSupport.ParseColor(request.EndColor);
            if (!string.IsNullOrWhiteSpace(request.Material)) ResolveOptionalMaterial(request.Material);
            if (!string.IsNullOrWhiteSpace(request.Alignment)) ParseEnum<LineAlignment>(request.Alignment, "E_TRAIL_ALIGNMENT_INVALID", "Use View or TransformZ.");
            if (!string.IsNullOrWhiteSpace(request.TextureMode)) ParseEnum<LineTextureMode>(request.TextureMode, "E_TRAIL_TEXTURE_MODE_INVALID", "Use a valid LineTextureMode.");
        }

        public static void VerifyLineRendererFields(LineRenderer line, LineRendererFields request)
        {
            if ((request.StartWidth.HasValue && !Mathf.Approximately((float)request.StartWidth.Value, line.startWidth)) ||
                (request.EndWidth.HasValue && !Mathf.Approximately((float)request.EndWidth.Value, line.endWidth)) ||
                (request.WidthMultiplier.HasValue && !Mathf.Approximately((float)request.WidthMultiplier.Value, line.widthMultiplier)) ||
                (request.WidthCurve != null && request.WidthCurve.Count > 0 && !CurveEquals(line.widthCurve, request.WidthCurve)) ||
                (!string.IsNullOrWhiteSpace(request.StartColor) && line.startColor != GraphicsToolSupport.ParseColor(request.StartColor)) ||
                (!string.IsNullOrWhiteSpace(request.EndColor) && line.endColor != GraphicsToolSupport.ParseColor(request.EndColor)) ||
                (request.ColorGradient != null && request.ColorGradient.Count > 0 && !GradientEquals(line.colorGradient, request.ColorGradient)) ||
                (request.PositionCount.HasValue && line.positionCount != request.PositionCount.Value) ||
                (request.Positions != null && request.Positions.Count > 0 && !PositionsEqual(line, ToUnityVector3Array(request.Positions))) ||
                (request.Loop.HasValue && line.loop != request.Loop.Value) || (request.UseWorldSpace.HasValue && line.useWorldSpace != request.UseWorldSpace.Value) ||
                (!string.IsNullOrWhiteSpace(request.Alignment) && line.alignment != ParseEnum<LineAlignment>(request.Alignment, "E_LINE_ALIGNMENT_INVALID", "Use View or TransformZ.")) ||
                (!string.IsNullOrWhiteSpace(request.TextureMode) && line.textureMode != ParseEnum<LineTextureMode>(request.TextureMode, "E_LINE_TEXTURE_MODE_INVALID", "Use a valid LineTextureMode.")) ||
                (request.NumCornerVertices.HasValue && line.numCornerVertices != request.NumCornerVertices.Value) || (request.NumCapVertices.HasValue && line.numCapVertices != request.NumCapVertices.Value) ||
                (!string.IsNullOrWhiteSpace(request.Material) && !SameAssetPathOrGuid(line.sharedMaterial, ResolveOptionalMaterial(request.Material))))
                throw new UnityCliBridge.BridgeCommandException("E_LINE_VERIFY_FAILED", "LineRenderer did not retain every requested field.", "Check the LineRenderer state and retry.");
        }

        public static void VerifyTrailRendererFields(TrailRenderer trail, TrailRendererFields request)
        {
            if ((request.Time.HasValue && !Mathf.Approximately((float)request.Time.Value, trail.time)) ||
                (request.StartWidth.HasValue && !Mathf.Approximately((float)request.StartWidth.Value, trail.startWidth)) ||
                (request.EndWidth.HasValue && !Mathf.Approximately((float)request.EndWidth.Value, trail.endWidth)) ||
                (request.WidthMultiplier.HasValue && !Mathf.Approximately((float)request.WidthMultiplier.Value, trail.widthMultiplier)) ||
                (request.MinVertexDistance.HasValue && !Mathf.Approximately((float)request.MinVertexDistance.Value, trail.minVertexDistance)) ||
                (request.WidthCurve != null && request.WidthCurve.Count > 0 && !CurveEquals(trail.widthCurve, request.WidthCurve)) ||
                (!string.IsNullOrWhiteSpace(request.StartColor) && trail.startColor != GraphicsToolSupport.ParseColor(request.StartColor)) ||
                (!string.IsNullOrWhiteSpace(request.EndColor) && trail.endColor != GraphicsToolSupport.ParseColor(request.EndColor)) ||
                (request.ColorGradient != null && request.ColorGradient.Count > 0 && !GradientEquals(trail.colorGradient, request.ColorGradient)) ||
                (request.Autodestruct.HasValue && trail.autodestruct != request.Autodestruct.Value) || (request.Emitting.HasValue && trail.emitting != request.Emitting.Value) ||
                (!string.IsNullOrWhiteSpace(request.Alignment) && trail.alignment != ParseEnum<LineAlignment>(request.Alignment, "E_TRAIL_ALIGNMENT_INVALID", "Use View or TransformZ.")) ||
                (!string.IsNullOrWhiteSpace(request.TextureMode) && trail.textureMode != ParseEnum<LineTextureMode>(request.TextureMode, "E_TRAIL_TEXTURE_MODE_INVALID", "Use a valid LineTextureMode.")) ||
                (request.NumCornerVertices.HasValue && trail.numCornerVertices != request.NumCornerVertices.Value) || (request.NumCapVertices.HasValue && trail.numCapVertices != request.NumCapVertices.Value) ||
                (!string.IsNullOrWhiteSpace(request.Material) && !SameAssetPathOrGuid(trail.sharedMaterial, ResolveOptionalMaterial(request.Material))))
                throw new UnityCliBridge.BridgeCommandException("E_TRAIL_VERIFY_FAILED", "TrailRenderer did not retain every requested field.", "Check the TrailRenderer state and retry.");
        }

        private static bool PositionsEqual(LineRenderer line, Vector3[] expected)
        {
            if (line.positionCount != expected.Length) return false;
            var actual = new Vector3[expected.Length]; line.GetPositions(actual);
            for (var index = 0; index < actual.Length; index++) if (actual[index] != expected[index]) return false;
            return true;
        }

        private static bool CurveEquals(AnimationCurve actual, List<VfxCurveKeyInput> expected)
        {
            var keys = actual == null ? new Keyframe[0] : actual.keys;
            if (keys.Length != expected.Count) return false;
            for (var index = 0; index < keys.Length; index++) if (!Mathf.Approximately(keys[index].time, (float)expected[index].Time) || !Mathf.Approximately(keys[index].value, (float)expected[index].Value)) return false;
            return true;
        }

        private static bool GradientEquals(Gradient actual, List<VfxGradientStopInput> expected)
        {
            var colors = actual == null ? new GradientColorKey[0] : actual.colorKeys;
            var alphas = actual == null ? new GradientAlphaKey[0] : actual.alphaKeys;
            if (colors.Length != expected.Count || alphas.Length != expected.Count) return false;
            for (var index = 0; index < expected.Count; index++)
            {
                var color = GraphicsToolSupport.ParseColor(expected[index].Color);
                if (!Mathf.Approximately(colors[index].time, (float)expected[index].Time) || colors[index].color != color || !Mathf.Approximately(alphas[index].time, (float)expected[index].Time) || !Mathf.Approximately(alphas[index].alpha, color.a)) return false;
            }
            return true;
        }

        private static bool SameAssetPathOrGuid(UnityEngine.Object actual, UnityEngine.Object expected)
        {
            if (actual == expected) return true;
            if (actual == null || expected == null) return false;
            var actualPath = AssetDatabase.GetAssetPath(actual); var expectedPath = AssetDatabase.GetAssetPath(expected);
            return string.Equals(actualPath, expectedPath, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(AssetDatabase.AssetPathToGUID(actualPath), AssetDatabase.AssetPathToGUID(expectedPath), StringComparison.OrdinalIgnoreCase);
        }

        public static void MarkSceneDirty(GameObject gameObject)
        {
            if (gameObject != null && gameObject.scene.IsValid())
            {
                UnityCliSceneMutation.MarkSceneDirty(gameObject.scene);
            }
        }

        public static UnityEngine.Object CreateVfxGraphAsset(string path, Type assetType)
        {
            try
            {
                var created = CreateVfxGraphAssetWithUnityUtility(path, assetType);
                if (created == null)
                    throw new UnityCliBridge.BridgeCommandException("E_VFX_GRAPH_FACTORY_UNAVAILABLE", "VisualEffectAssetEditorUtility did not create a readable VisualEffectAsset.", "Use a Visual Effect Graph package exposing UnityEditor.VisualEffectAssetEditorUtility.CreateNewAsset(string).");
                return created;
            }
            catch (UnityCliBridge.BridgeCommandException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new UnityCliBridge.BridgeCommandException(
                    "E_VFX_GRAPH_CREATE_FAILED",
                    "Failed to create VFX Graph asset: " + exception.GetType().Name + ": " + exception.Message,
                    "Check Unity Console and UnityCliBridge.log; the Visual Effect Graph package import pipeline may have changed.");
            }
        }

        public static List<string> DiscoverVfxTemplates()
        {
            var results = new List<string>();
            var packageInfo = UnityEditor.PackageManager.PackageInfo.FindForAssetPath("Packages/com.unity.visualeffectgraph");
            if (packageInfo == null)
            {
                return results;
            }

            foreach (var searchPath in GetTemplateSearchPaths(packageInfo.resolvedPath))
            {
                if (!Directory.Exists(searchPath))
                {
                    continue;
                }

                foreach (var file in Directory.GetFiles(searchPath, "*.vfx", SearchOption.AllDirectories))
                {
                    var relative = file.Substring(searchPath.Length).TrimStart(Path.DirectorySeparatorChar, '/');
                    var name = Path.GetFileNameWithoutExtension(relative);
                    if (!results.Contains(name))
                    {
                        results.Add(name);
                    }
                }
            }

            results.Sort(StringComparer.OrdinalIgnoreCase);
            return results;
        }

        public static string ResolveVfxTemplatePath(string templateName)
        {
            var packageInfo = UnityEditor.PackageManager.PackageInfo.FindForAssetPath("Packages/com.unity.visualeffectgraph");
            if (packageInfo == null)
            {
                return null;
            }

            foreach (var searchPath in GetTemplateSearchPaths(packageInfo.resolvedPath))
            {
                if (!Directory.Exists(searchPath))
                {
                    continue;
                }

                var exactFile = Path.Combine(searchPath, templateName + ".vfx");
                if (File.Exists(exactFile))
                {
                    return ToPackageAssetPath(exactFile, packageInfo.resolvedPath, packageInfo.name);
                }

                foreach (var file in Directory.GetFiles(searchPath, "*.vfx", SearchOption.AllDirectories))
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    if (string.Equals(name, templateName, StringComparison.OrdinalIgnoreCase))
                    {
                        return ToPackageAssetPath(file, packageInfo.resolvedPath, packageInfo.name);
                    }

                    var relative = file.Substring(searchPath.Length).TrimStart(Path.DirectorySeparatorChar, '/');
                    if (string.Equals(relative, templateName + ".vfx", StringComparison.OrdinalIgnoreCase))
                    {
                        return ToPackageAssetPath(file, packageInfo.resolvedPath, packageInfo.name);
                    }
                }
            }

            return null;
        }

        public static void PrepareParticleMainFields(ParticleMainFields request)
        {
            RequireFinite(request.Duration, "duration", 0); RequireFinite(request.StartLifetime, "startLifetime", 0); RequireFinite(request.StartSpeed, "startSpeed"); RequireFinite(request.StartSize, "startSize", 0); RequireFinite(request.StartRotation, "startRotation"); RequireFinite(request.GravityModifier, "gravityModifier");
            if (request.MaxParticles.HasValue && request.MaxParticles.Value < 1) throw new UnityCliBridge.BridgeCommandException("E_VFX_INPUT_RANGE_INVALID", "maxParticles must be positive.", "Pass maxParticles greater than zero.");
            if (!string.IsNullOrWhiteSpace(request.StartColor)) GraphicsToolSupport.ParseColor(request.StartColor);
            if (!string.IsNullOrWhiteSpace(request.SimulationSpace)) ParseEnum<ParticleSystemSimulationSpace>(request.SimulationSpace, "E_PARTICLE_SIMULATION_SPACE_INVALID", "Use Local, World, or Custom.");
            if (!string.IsNullOrWhiteSpace(request.ScalingMode)) ParseEnum<ParticleSystemScalingMode>(request.ScalingMode, "E_PARTICLE_SCALING_MODE_INVALID", "Use Hierarchy, Local, or Shape.");
        }

        public static void ApplyParticleMainFields(ParticleSystem particleSystem, ParticleMainFields request, string effectType, List<VfxPlannedEffect> effects)
        {
            var main = particleSystem.main;
            if (request.Duration.HasValue)
            {
                main.duration = (float)request.Duration.Value;
                effects.Add(Effect(effectType, "duration", Number(main.duration)));
            }
            if (request.StartLifetime.HasValue)
            {
                main.startLifetime = (float)request.StartLifetime.Value;
                effects.Add(Effect(effectType, "startLifetime", Number(main.startLifetime.constant)));
            }
            if (request.StartSpeed.HasValue)
            {
                main.startSpeed = (float)request.StartSpeed.Value;
                effects.Add(Effect(effectType, "startSpeed", Number(main.startSpeed.constant)));
            }
            if (request.StartSize.HasValue)
            {
                main.startSize = (float)request.StartSize.Value;
                effects.Add(Effect(effectType, "startSize", Number(main.startSize.constant)));
            }
            if (!string.IsNullOrWhiteSpace(request.StartColor))
            {
                main.startColor = GraphicsToolSupport.ParseColor(request.StartColor);
                effects.Add(Effect(effectType, "startColor", request.StartColor));
            }
            if (request.StartRotation.HasValue)
            {
                main.startRotation = (float)request.StartRotation.Value * Mathf.Deg2Rad;
                effects.Add(Effect(effectType, "startRotation", Number(main.startRotation.constant * Mathf.Rad2Deg)));
            }
            if (request.GravityModifier.HasValue)
            {
                main.gravityModifier = (float)request.GravityModifier.Value;
                effects.Add(Effect(effectType, "gravityModifier", Number(main.gravityModifier.constant)));
            }
            if (request.MaxParticles.HasValue)
            {
                main.maxParticles = request.MaxParticles.Value;
                effects.Add(Effect(effectType, "maxParticles", main.maxParticles.ToString(CultureInfo.InvariantCulture)));
            }
            if (request.Loop.HasValue)
            {
                main.loop = request.Loop.Value;
                effects.Add(Effect(effectType, "loop", Bool(main.loop)));
            }
            if (request.PlayOnAwake.HasValue)
            {
                main.playOnAwake = request.PlayOnAwake.Value;
                effects.Add(Effect(effectType, "playOnAwake", Bool(main.playOnAwake)));
            }
            if (!string.IsNullOrWhiteSpace(request.SimulationSpace))
            {
                main.simulationSpace = ParseEnum<ParticleSystemSimulationSpace>(request.SimulationSpace, "E_PARTICLE_SIMULATION_SPACE_INVALID", "Use Local, World, or Custom.");
                effects.Add(Effect(effectType, "simulationSpace", main.simulationSpace.ToString()));
            }
            if (!string.IsNullOrWhiteSpace(request.ScalingMode))
            {
                main.scalingMode = ParseEnum<ParticleSystemScalingMode>(request.ScalingMode, "E_PARTICLE_SCALING_MODE_INVALID", "Use Hierarchy, Local, or Shape.");
                effects.Add(Effect(effectType, "scalingMode", main.scalingMode.ToString()));
            }
        }

        // 每个请求字段都从模块 getter 重新读取，避免把写入时的局部模块缓存当成完成证明。
        public static void VerifyParticleMainFields(ParticleSystem particleSystem, ParticleMainFields request)
        {
            var main = particleSystem.main;
            if ((request.Duration.HasValue && !Mathf.Approximately(main.duration, (float)request.Duration.Value)) ||
                (request.StartLifetime.HasValue && !Mathf.Approximately(main.startLifetime.constant, (float)request.StartLifetime.Value)) ||
                (request.StartSpeed.HasValue && !Mathf.Approximately(main.startSpeed.constant, (float)request.StartSpeed.Value)) ||
                (request.StartSize.HasValue && !Mathf.Approximately(main.startSize.constant, (float)request.StartSize.Value)) ||
                (!string.IsNullOrWhiteSpace(request.StartColor) && main.startColor.color != GraphicsToolSupport.ParseColor(request.StartColor)) ||
                (request.StartRotation.HasValue && !Mathf.Approximately(main.startRotation.constant * Mathf.Rad2Deg, (float)request.StartRotation.Value)) ||
                (request.GravityModifier.HasValue && !Mathf.Approximately(main.gravityModifier.constant, (float)request.GravityModifier.Value)) ||
                (request.MaxParticles.HasValue && main.maxParticles != request.MaxParticles.Value) ||
                (request.Loop.HasValue && main.loop != request.Loop.Value) ||
                (request.PlayOnAwake.HasValue && main.playOnAwake != request.PlayOnAwake.Value) ||
                (!string.IsNullOrWhiteSpace(request.SimulationSpace) && !string.Equals(main.simulationSpace.ToString(), request.SimulationSpace, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(request.ScalingMode) && !string.Equals(main.scalingMode.ToString(), request.ScalingMode, StringComparison.OrdinalIgnoreCase)))
                throw new UnityCliBridge.BridgeCommandException("E_PARTICLE_VERIFY_FAILED", "ParticleSystem main module did not retain every requested field.", "Check the ParticleSystem main module and retry.");
        }

        public static void VerifyParticleEmissionFields(ParticleSystem particleSystem, bool? enabled, double? rateOverTime, double? rateOverDistance, int? burstCount, List<ParticleBurstInput> bursts)
        {
            var emission = particleSystem.emission;
            if ((enabled.HasValue && emission.enabled != enabled.Value) || (rateOverTime.HasValue && !Mathf.Approximately(emission.rateOverTime.constant, (float)rateOverTime.Value)) || (rateOverDistance.HasValue && !Mathf.Approximately(emission.rateOverDistance.constant, (float)rateOverDistance.Value)) || (burstCount.HasValue && emission.burstCount != burstCount.Value))
                throw new UnityCliBridge.BridgeCommandException("E_PARTICLE_VERIFY_FAILED", "ParticleSystem emission module did not retain every requested field.", "Check the ParticleSystem emission module and retry.");
            if (burstCount.HasValue)
            {
                var actual = new ParticleSystem.Burst[emission.burstCount]; emission.GetBursts(actual);
                for (var index = 0; index < actual.Length; index++)
                {
                    var expected = bursts != null && index < bursts.Count ? bursts[index] : null;
                    if (!Mathf.Approximately(actual[index].time, (float)(expected?.Time ?? 0d)) || actual[index].minCount != (short)(expected?.Count ?? 10) || actual[index].maxCount != (short)(expected?.Count ?? 10) || actual[index].cycleCount != (short)(expected?.Cycles ?? 1) || !Mathf.Approximately(actual[index].repeatInterval, (float)(expected?.RepeatInterval ?? 0.01d)))
                        throw new UnityCliBridge.BridgeCommandException("E_PARTICLE_VERIFY_FAILED", "ParticleSystem burst did not retain every requested field.", "Check burst time, count, cycles, and repeatInterval.");
                }
            }
        }

        public static void VerifyParticleShapeFields(ParticleSystem particleSystem, bool? enabled, string shapeType, double? radius, double? angle, double? arc, double? length, double? boxX, double? boxY, double? boxZ, bool? randomDirection)
        {
            var shape = particleSystem.shape;
            if ((enabled.HasValue && shape.enabled != enabled.Value) || (!string.IsNullOrWhiteSpace(shapeType) && !string.Equals(shape.shapeType.ToString(), shapeType, StringComparison.OrdinalIgnoreCase)) || (radius.HasValue && !Mathf.Approximately(shape.radius, (float)radius.Value)) || (angle.HasValue && !Mathf.Approximately(shape.angle, (float)angle.Value)) || (arc.HasValue && !Mathf.Approximately(shape.arc, (float)arc.Value)) || (length.HasValue && !Mathf.Approximately(shape.length, (float)length.Value)) || (boxX.HasValue && !Mathf.Approximately(shape.scale.x, (float)boxX.Value)) || (boxY.HasValue && !Mathf.Approximately(shape.scale.y, (float)boxY.Value)) || (boxZ.HasValue && !Mathf.Approximately(shape.scale.z, (float)boxZ.Value)) || (randomDirection.HasValue && !Mathf.Approximately(shape.randomDirectionAmount, randomDirection.Value ? 1f : 0f)))
                throw new UnityCliBridge.BridgeCommandException("E_PARTICLE_VERIFY_FAILED", "ParticleSystem shape module did not retain every requested field.", "Check the ParticleSystem shape module and retry.");
        }

        public static void ApplyLineRendererFields(LineRenderer line, LineRendererFields request, string effectType, List<VfxPlannedEffect> effects)
        {
            if (request.StartWidth.HasValue)
            {
                line.startWidth = (float)request.StartWidth.Value;
                effects.Add(Effect(effectType, "startWidth", Number(line.startWidth)));
            }
            if (request.EndWidth.HasValue)
            {
                line.endWidth = (float)request.EndWidth.Value;
                effects.Add(Effect(effectType, "endWidth", Number(line.endWidth)));
            }
            if (request.WidthMultiplier.HasValue)
            {
                line.widthMultiplier = (float)request.WidthMultiplier.Value;
                effects.Add(Effect(effectType, "widthMultiplier", Number(line.widthMultiplier)));
            }
            if (request.WidthCurve != null && request.WidthCurve.Count > 0)
            {
                line.widthCurve = BuildCurve(request.WidthCurve);
                effects.Add(Effect(effectType, "widthCurve", "keys=" + request.WidthCurve.Count.ToString(CultureInfo.InvariantCulture)));
            }
            if (!string.IsNullOrWhiteSpace(request.StartColor))
            {
                line.startColor = GraphicsToolSupport.ParseColor(request.StartColor);
                effects.Add(Effect(effectType, "startColor", request.StartColor));
            }
            if (!string.IsNullOrWhiteSpace(request.EndColor))
            {
                line.endColor = GraphicsToolSupport.ParseColor(request.EndColor);
                effects.Add(Effect(effectType, "endColor", request.EndColor));
            }
            if (request.ColorGradient != null && request.ColorGradient.Count > 0)
            {
                line.colorGradient = BuildGradient(request.ColorGradient);
                effects.Add(Effect(effectType, "colorGradient", "keys=" + request.ColorGradient.Count.ToString(CultureInfo.InvariantCulture)));
            }
            if (!string.IsNullOrWhiteSpace(request.Material))
            {
                line.sharedMaterial = ResolveOptionalMaterial(request.Material);
                effects.Add(Effect(effectType, "material", line.sharedMaterial == null ? "null" : AssetDatabase.GetAssetPath(line.sharedMaterial)));
            }
            if (request.PositionCount.HasValue)
            {
                line.positionCount = request.PositionCount.Value;
                effects.Add(Effect(effectType, "positionCount", line.positionCount.ToString(CultureInfo.InvariantCulture)));
            }
            if (request.Positions != null && request.Positions.Count > 0)
            {
                var positions = ToUnityVector3Array(request.Positions);
                line.positionCount = positions.Length;
                line.SetPositions(positions);
                effects.Add(Effect(effectType, "positions", positions.Length.ToString(CultureInfo.InvariantCulture)));
            }
            if (request.Loop.HasValue)
            {
                line.loop = request.Loop.Value;
                effects.Add(Effect(effectType, "loop", Bool(line.loop)));
            }
            if (request.UseWorldSpace.HasValue)
            {
                line.useWorldSpace = request.UseWorldSpace.Value;
                effects.Add(Effect(effectType, "useWorldSpace", Bool(line.useWorldSpace)));
            }
            if (!string.IsNullOrWhiteSpace(request.Alignment))
            {
                line.alignment = ParseEnum<LineAlignment>(request.Alignment, "E_LINE_ALIGNMENT_INVALID", "Use View or TransformZ.");
                effects.Add(Effect(effectType, "alignment", line.alignment.ToString()));
            }
            if (!string.IsNullOrWhiteSpace(request.TextureMode))
            {
                line.textureMode = ParseEnum<LineTextureMode>(request.TextureMode, "E_LINE_TEXTURE_MODE_INVALID", "Use Stretch, Tile, DistributePerSegment, or RepeatPerSegment.");
                effects.Add(Effect(effectType, "textureMode", line.textureMode.ToString()));
            }
            if (request.NumCornerVertices.HasValue)
            {
                line.numCornerVertices = request.NumCornerVertices.Value;
                effects.Add(Effect(effectType, "numCornerVertices", line.numCornerVertices.ToString(CultureInfo.InvariantCulture)));
            }
            if (request.NumCapVertices.HasValue)
            {
                line.numCapVertices = request.NumCapVertices.Value;
                effects.Add(Effect(effectType, "numCapVertices", line.numCapVertices.ToString(CultureInfo.InvariantCulture)));
            }
        }

        public static void ApplyTrailRendererFields(TrailRenderer trail, TrailRendererFields request, List<VfxPlannedEffect> effects, string toolName)
        {
            if (request.Time.HasValue)
            {
                trail.time = (float)request.Time.Value;
                effects.Add(Effect(toolName, "time", Number(trail.time)));
            }
            if (request.StartWidth.HasValue)
            {
                trail.startWidth = (float)request.StartWidth.Value;
                effects.Add(Effect(toolName, "startWidth", Number(trail.startWidth)));
            }
            if (request.EndWidth.HasValue)
            {
                trail.endWidth = (float)request.EndWidth.Value;
                effects.Add(Effect(toolName, "endWidth", Number(trail.endWidth)));
            }
            if (request.WidthMultiplier.HasValue)
            {
                trail.widthMultiplier = (float)request.WidthMultiplier.Value;
                effects.Add(Effect(toolName, "widthMultiplier", Number(trail.widthMultiplier)));
            }
            if (request.WidthCurve != null && request.WidthCurve.Count > 0)
            {
                trail.widthCurve = BuildCurve(request.WidthCurve);
                effects.Add(Effect(toolName, "widthCurve", "keys=" + request.WidthCurve.Count.ToString(CultureInfo.InvariantCulture)));
            }
            if (!string.IsNullOrWhiteSpace(request.StartColor))
            {
                trail.startColor = GraphicsToolSupport.ParseColor(request.StartColor);
                effects.Add(Effect(toolName, "startColor", request.StartColor));
            }
            if (!string.IsNullOrWhiteSpace(request.EndColor))
            {
                trail.endColor = GraphicsToolSupport.ParseColor(request.EndColor);
                effects.Add(Effect(toolName, "endColor", request.EndColor));
            }
            if (request.ColorGradient != null && request.ColorGradient.Count > 0)
            {
                trail.colorGradient = BuildGradient(request.ColorGradient);
                effects.Add(Effect(toolName, "colorGradient", "keys=" + request.ColorGradient.Count.ToString(CultureInfo.InvariantCulture)));
            }
            if (!string.IsNullOrWhiteSpace(request.Material))
            {
                trail.sharedMaterial = ResolveOptionalMaterial(request.Material);
                effects.Add(Effect(toolName, "material", trail.sharedMaterial == null ? "null" : AssetDatabase.GetAssetPath(trail.sharedMaterial)));
            }
            if (request.MinVertexDistance.HasValue)
            {
                trail.minVertexDistance = (float)request.MinVertexDistance.Value;
                effects.Add(Effect(toolName, "minVertexDistance", Number(trail.minVertexDistance)));
            }
            if (request.Autodestruct.HasValue)
            {
                trail.autodestruct = request.Autodestruct.Value;
                effects.Add(Effect(toolName, "autodestruct", Bool(trail.autodestruct)));
            }
            if (request.Emitting.HasValue)
            {
                trail.emitting = request.Emitting.Value;
                effects.Add(Effect(toolName, "emitting", Bool(trail.emitting)));
            }
            if (!string.IsNullOrWhiteSpace(request.Alignment))
            {
                trail.alignment = ParseEnum<LineAlignment>(request.Alignment, "E_TRAIL_ALIGNMENT_INVALID", "Use View or TransformZ.");
                effects.Add(Effect(toolName, "alignment", trail.alignment.ToString()));
            }
            if (!string.IsNullOrWhiteSpace(request.TextureMode))
            {
                trail.textureMode = ParseEnum<LineTextureMode>(request.TextureMode, "E_TRAIL_TEXTURE_MODE_INVALID", "Use Stretch, Tile, DistributePerSegment, or RepeatPerSegment.");
                effects.Add(Effect(toolName, "textureMode", trail.textureMode.ToString()));
            }
            if (request.NumCornerVertices.HasValue)
            {
                trail.numCornerVertices = request.NumCornerVertices.Value;
                effects.Add(Effect(toolName, "numCornerVertices", trail.numCornerVertices.ToString(CultureInfo.InvariantCulture)));
            }
            if (request.NumCapVertices.HasValue)
            {
                trail.numCapVertices = request.NumCapVertices.Value;
                effects.Add(Effect(toolName, "numCapVertices", trail.numCapVertices.ToString(CultureInfo.InvariantCulture)));
            }
        }


        public static VfxParticleSystemInfo BuildParticleSystemInfo(ParticleSystem particleSystem)
        {
            var main = particleSystem.main;
            var emission = particleSystem.emission;
            var shape = particleSystem.shape;
            var color = particleSystem.colorOverLifetime;
            var size = particleSystem.sizeOverLifetime;
            var rotation = particleSystem.rotationOverLifetime;
            var velocity = particleSystem.velocityOverLifetime;
            var noise = particleSystem.noise;
            var collision = particleSystem.collision;
            var sub = particleSystem.subEmitters;
            var trails = particleSystem.trails;
            return new VfxParticleSystemInfo
            {
                IsPlaying = particleSystem.isPlaying,
                IsPaused = particleSystem.isPaused,
                IsEmitting = particleSystem.isEmitting,
                ParticleCount = particleSystem.particleCount,
                Main = new VfxParticleMainInfo
                {
                    Duration = main.duration,
                    StartLifetime = main.startLifetime.constant,
                    StartSpeed = main.startSpeed.constant,
                    StartSize = main.startSize.constant,
                    StartRotationDegrees = main.startRotation.constant * Mathf.Rad2Deg,
                    MaxParticles = main.maxParticles,
                    Loop = main.loop,
                    PlayOnAwake = main.playOnAwake,
                    SimulationSpace = main.simulationSpace.ToString(),
                    ScalingMode = main.scalingMode.ToString(),
                    GravityModifier = main.gravityModifier.constant
                },
                Emission = new VfxParticleEmissionInfo
                {
                    Enabled = emission.enabled,
                    RateOverTime = emission.rateOverTime.constant,
                    RateOverDistance = emission.rateOverDistance.constant,
                    BurstCount = emission.burstCount
                },
                Shape = new VfxParticleShapeInfo
                {
                    Enabled = shape.enabled,
                    ShapeType = shape.shapeType.ToString(),
                    Radius = shape.radius,
                    Angle = shape.angle,
                    Arc = shape.arc,
                    Length = shape.length,
                    Box = new Vector3Data(shape.scale),
                    RandomDirection = shape.randomDirectionAmount > 0f
                },
                Modules = new VfxParticleModulesInfo
                {
                    ColorOverLifetime = color.enabled,
                    SizeOverLifetime = size.enabled,
                    RotationOverLifetime = rotation.enabled,
                    VelocityOverLifetime = velocity.enabled,
                    Noise = noise.enabled,
                    Collision = collision.enabled,
                    SubEmitters = sub.enabled,
                    SubEmittersCount = sub.subEmittersCount,
                    Trails = trails.enabled
                }
            };
        }

        public static VfxLineRendererInfo BuildLineRendererInfo(LineRenderer line)
        {
            var positions = new Vector3[line.positionCount];
            line.GetPositions(positions);
            var items = new List<Vector3Data>();
            foreach (var position in positions)
            {
                items.Add(new Vector3Data(position));
            }

            return new VfxLineRendererInfo
            {
                StartWidth = line.startWidth,
                EndWidth = line.endWidth,
                WidthMultiplier = line.widthMultiplier,
                WidthCurve = BuildCurveData(line.widthCurve),
                StartColor = GraphicsToolSupport.ColorHtml(line.startColor),
                EndColor = GraphicsToolSupport.ColorHtml(line.endColor),
                ColorGradient = BuildGradientData(line.colorGradient),
                Material = line.sharedMaterial == null ? null : AssetToolSupport.NormalizeAssetPath(AssetDatabase.GetAssetPath(line.sharedMaterial)),
                PositionCount = line.positionCount,
                Positions = items,
                Loop = line.loop,
                UseWorldSpace = line.useWorldSpace,
                Alignment = line.alignment.ToString(),
                TextureMode = line.textureMode.ToString(),
                NumCornerVertices = line.numCornerVertices,
                NumCapVertices = line.numCapVertices
            };
        }

        public static VfxTrailRendererInfo BuildTrailRendererInfo(TrailRenderer trail)
        {
            return new VfxTrailRendererInfo
            {
                Time = trail.time,
                StartWidth = trail.startWidth,
                EndWidth = trail.endWidth,
                WidthMultiplier = trail.widthMultiplier,
                WidthCurve = BuildCurveData(trail.widthCurve),
                StartColor = GraphicsToolSupport.ColorHtml(trail.startColor),
                EndColor = GraphicsToolSupport.ColorHtml(trail.endColor),
                ColorGradient = BuildGradientData(trail.colorGradient),
                Material = trail.sharedMaterial == null ? null : AssetToolSupport.NormalizeAssetPath(AssetDatabase.GetAssetPath(trail.sharedMaterial)),
                MinVertexDistance = trail.minVertexDistance,
                Emitting = trail.emitting,
                Autodestruct = trail.autodestruct,
                Alignment = trail.alignment.ToString(),
                TextureMode = trail.textureMode.ToString(),
                NumCornerVertices = trail.numCornerVertices,
                NumCapVertices = trail.numCapVertices
            };
        }

        public static AnimationCurve BuildCurve(List<VfxCurveKeyInput> keys)
        {
            var items = new List<Keyframe>();
            double? previousTime = null;
            foreach (var key in keys)
            {
                if (key == null) throw new UnityCliBridge.BridgeCommandException("E_VFX_INPUT_RANGE_INVALID", "widthCurve cannot contain null keys.", "Pass finite time/value curve keys.");
                RequireFinite(key.Time, "widthCurve.time"); RequireFinite(key.Value, "widthCurve.value");
                if (previousTime.HasValue && key.Time <= previousTime.Value) throw new UnityCliBridge.BridgeCommandException("E_VFX_INPUT_RANGE_INVALID", "widthCurve key times must be strictly increasing.", "Sort keys by time and do not repeat a time value.");
                items.Add(new Keyframe((float)key.Time, (float)key.Value));
                previousTime = key.Time;
            }
            return new AnimationCurve(items.ToArray());
        }

        public static Gradient BuildGradient(List<VfxGradientStopInput> stops)
        {
            var colorKeys = new List<GradientColorKey>();
            var alphaKeys = new List<GradientAlphaKey>();
            double? previousTime = null;
            foreach (var stop in stops)
            {
                if (stop == null) throw new UnityCliBridge.BridgeCommandException("E_VFX_INPUT_RANGE_INVALID", "colorGradient cannot contain null stops.", "Pass color stops with times from zero through one.");
                RequireFinite(stop.Time, "colorGradient.time", 0, 1);
                if (previousTime.HasValue && stop.Time <= previousTime.Value) throw new UnityCliBridge.BridgeCommandException("E_VFX_INPUT_RANGE_INVALID", "colorGradient stop times must be strictly increasing.", "Sort stops by time and do not repeat a time value.");
                var color = GraphicsToolSupport.ParseColor(stop.Color);
                var time = (float)stop.Time;
                colorKeys.Add(new GradientColorKey(color, time));
                alphaKeys.Add(new GradientAlphaKey(color.a, time));
                previousTime = stop.Time;
            }
            if (colorKeys.Count < 2) throw new UnityCliBridge.BridgeCommandException("E_VFX_INPUT_RANGE_INVALID", "colorGradient requires at least two stops.", "Provide at least two strictly time-ordered color stops.");
            var gradient = new Gradient();
            gradient.SetKeys(colorKeys.ToArray(), alphaKeys.ToArray());
            return gradient;
        }

        public static Vector3 ToUnityVector3(Vector3Input value)
        {
            if (value == null)
            {
                return Vector3.zero;
            }
            return new Vector3((float)value.X, (float)value.Y, (float)value.Z);
        }

        public static Vector3[] ToUnityVector3Array(List<Vector3Input> values)
        {
            var results = new List<Vector3>();
            if (values != null)
            {
                foreach (var value in values)
                {
                    results.Add(ToUnityVector3(value));
                }
            }
            return results.ToArray();
        }

        public static Vector4 ToUnityVector4(Vector4Input value)
        {
            if (value == null)
            {
                return Vector4.zero;
            }
            return new Vector4((float)value.X, (float)value.Y, (float)value.Z, (float)value.W);
        }

        public static Color ToUnityColor(ColorInput value)
        {
            if (value == null)
            {
                return Color.white;
            }
            return new Color((float)value.R, (float)value.G, (float)value.B, (float)(value.A ?? 1d));
        }

        public static T ParseEnum<T>(string value, string code, string nextAction) where T : struct
        {
            T parsed;
            if (Enum.TryParse<T>(value, true, out parsed) && Enum.IsDefined(typeof(T), parsed))
            {
                return parsed;
            }
            throw new UnityCliBridge.BridgeCommandException(code, "Invalid " + typeof(T).Name + " value: " + value, nextAction);
        }

        public static VfxPlannedEffect Effect(string type, string property, string value)
        {
            return new VfxPlannedEffect
            {
                Type = type,
                Property = property,
                Value = value
            };
        }

        public static string Bool(bool value)
        {
            return value ? "true" : "false";
        }

        public static string Number(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static UnityEngine.Object CreateVfxGraphAssetWithUnityUtility(string path, Type assetType)
        {
            var created = RequireVfxGraphFactory().Invoke(null, new object[] { path }) as UnityEngine.Object;
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            return created == null || !assetType.IsInstanceOfType(created)
                ? AssetDatabase.LoadAssetAtPath(path, assetType)
                : created;
        }

        public static void PrepareVfxGraphFactory()
        {
            RequireVfxGraphFactory();
        }

        private static MethodInfo RequireVfxGraphFactory()
        {
            var utilityType = GameObjectToolSupport.FindType("UnityEditor.VisualEffectAssetEditorUtility");
            var createMethod = utilityType == null ? null : utilityType.GetMethod("CreateNewAsset", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(string) }, null);
            if (createMethod == null)
                throw new UnityCliBridge.BridgeCommandException("E_VFX_GRAPH_FACTORY_UNAVAILABLE", "VisualEffectAssetEditorUtility.CreateNewAsset(string) is unavailable.", "Use a Visual Effect Graph package exposing UnityEditor.VisualEffectAssetEditorUtility.CreateNewAsset(string).");
            return createMethod;
        }

        private static string[] GetTemplateSearchPaths(string resolvedPath)
        {
            return new[]
            {
                Path.Combine(resolvedPath, "Editor", "Templates"),
                Path.Combine(resolvedPath, "Samples"),
                Path.Combine(resolvedPath, "Samples~")
            };
        }

        /// <summary>把 PackageCache/embedded package 的物理路径转换为 AssetDatabase 可识别的 Packages 路径。</summary>
        private static string ToPackageAssetPath(string fullPath, string resolvedPath, string packageName)
        {
            if (string.IsNullOrEmpty(fullPath) || string.IsNullOrEmpty(resolvedPath) || string.IsNullOrEmpty(packageName)) return null;
            var normalizedRoot = resolvedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!fullPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !fullPath.StartsWith(normalizedRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
            var relative = fullPath.Substring(normalizedRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace('\\', '/');
            return "Packages/" + packageName + "/" + relative;
        }

        // 创建类工具返回工件内容哈希，调用方可证明路径上的资产确实是本次可读取结果。
        private static string HashAsset(string path)
        {
            try
            {
                var fullPath = Path.Combine(AssetToolSupport.ProjectRoot(), path.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(fullPath)) return null;
                using (var stream = File.OpenRead(fullPath))
                using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
            }
            catch { return null; }
        }

        private static bool PackageManifestContains(string packageName)
        {
            return FileContains(Path.Combine(AssetToolSupport.ProjectRoot(), "Packages", "manifest.json"), "\"" + packageName + "\"");
        }

        private static bool PackageLockContains(string packageName)
        {
            return FileContains(Path.Combine(AssetToolSupport.ProjectRoot(), "Packages", "packages-lock.json"), "\"" + packageName + "\"");
        }

        private static bool FileContains(string path, string value)
        {
            try
            {
                return File.Exists(path) && File.ReadAllText(path).IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch
            {
                return false;
            }
        }

        private static Material ResolveOptionalMaterial(string rawPath)
        {
            if (string.IsNullOrWhiteSpace(rawPath)
                || string.Equals(rawPath, "null", StringComparison.OrdinalIgnoreCase)
                || string.Equals(rawPath, "none", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var path = AssetToolSupport.NormalizeAssetPath(rawPath);
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                throw new UnityCliBridge.BridgeCommandException(
                    "E_VFX_MATERIAL_NOT_FOUND",
                    "Material not found: " + rawPath,
                    "Pass an existing Assets/*.mat path or omit material.");
            }
            return material;
        }

        private static AssetRef BuildAssetRef(UnityEngine.Object asset)
        {
            if (asset == null)
            {
                return null;
            }
            var path = AssetToolSupport.NormalizeAssetPath(AssetDatabase.GetAssetPath(asset));
            return new AssetRef
            {
                Name = asset.name,
                Path = path,
                Guid = string.IsNullOrEmpty(path) ? null : AssetDatabase.AssetPathToGUID(path)
            };
        }


        private static VfxCurveData BuildCurveData(AnimationCurve curve)
        {
            var keys = new List<VfxCurveKeyData>();
            foreach (var key in curve == null ? new Keyframe[0] : curve.keys)
            {
                keys.Add(new VfxCurveKeyData { Time = key.time, Value = key.value });
            }
            return new VfxCurveData { Keys = keys };
        }

        private static VfxGradientData BuildGradientData(Gradient gradient)
        {
            var colors = new List<VfxGradientColorStopData>();
            var alphas = new List<VfxGradientAlphaStopData>();
            foreach (var key in gradient == null ? new GradientColorKey[0] : gradient.colorKeys)
            {
                colors.Add(new VfxGradientColorStopData { Time = key.time, Color = GraphicsToolSupport.ColorHtml(key.color) });
            }
            foreach (var key in gradient == null ? new GradientAlphaKey[0] : gradient.alphaKeys)
            {
                alphas.Add(new VfxGradientAlphaStopData { Time = key.time, Alpha = key.alpha });
            }
            return new VfxGradientData { Colors = colors, Alphas = alphas };
        }
    }

    /// <summary>粒子系统主模块通用输入字段。</summary>
    public class ParticleMainFields
    {
        public double? Duration { get; set; }
        public double? StartLifetime { get; set; }
        public double? StartSpeed { get; set; }
        public double? StartSize { get; set; }
        public string StartColor { get; set; }
        public double? StartRotation { get; set; }
        public double? GravityModifier { get; set; }
        public int? MaxParticles { get; set; }
        public bool? Loop { get; set; }
        public bool? PlayOnAwake { get; set; }
        public string SimulationSpace { get; set; }
        public string ScalingMode { get; set; }
    }

    /// <summary>LineRenderer 通用输入字段。</summary>
    public class LineRendererFields
    {
        public double? StartWidth { get; set; }
        public double? EndWidth { get; set; }
        public double? WidthMultiplier { get; set; }
        public List<VfxCurveKeyInput> WidthCurve { get; set; }
        public string StartColor { get; set; }
        public string EndColor { get; set; }
        public List<VfxGradientStopInput> ColorGradient { get; set; }
        public string Material { get; set; }
        public int? PositionCount { get; set; }
        public List<Vector3Input> Positions { get; set; }
        public bool? Loop { get; set; }
        public bool? UseWorldSpace { get; set; }
        public string Alignment { get; set; }
        public string TextureMode { get; set; }
        public int? NumCornerVertices { get; set; }
        public int? NumCapVertices { get; set; }
    }

    /// <summary>TrailRenderer 通用输入字段。</summary>
    public class TrailRendererFields
    {
        public double? Time { get; set; }
        public double? StartWidth { get; set; }
        public double? EndWidth { get; set; }
        public double? WidthMultiplier { get; set; }
        public List<VfxCurveKeyInput> WidthCurve { get; set; }
        public string StartColor { get; set; }
        public string EndColor { get; set; }
        public List<VfxGradientStopInput> ColorGradient { get; set; }
        public string Material { get; set; }
        public double? MinVertexDistance { get; set; }
        public bool? Autodestruct { get; set; }
        public bool? Emitting { get; set; }
        public string Alignment { get; set; }
        public string TextureMode { get; set; }
        public int? NumCornerVertices { get; set; }
        public int? NumCapVertices { get; set; }
    }

    public sealed class VfxCurveKeyInput
    {
        public double Time { get; set; }
        public double Value { get; set; }
    }

    public sealed class VfxGradientStopInput
    {
        public double Time { get; set; }
        public string Color { get; set; }
    }

    public sealed class Vector3Input
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
    }

    public sealed class Vector4Input
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
        public double W { get; set; }
    }

    public sealed class ColorInput
    {
        public double R { get; set; }
        public double G { get; set; }
        public double B { get; set; }
        public double? A { get; set; }
    }

    public sealed class ParticleBurstInput
    {
        public double? Time { get; set; }
        public int? Count { get; set; }
        public int? Cycles { get; set; }
        public double? RepeatInterval { get; set; }
    }

    public sealed class VfxPlannedEffect
    {
        public string Type { get; set; }
        public string Target { get; set; }
        public string Path { get; set; }
        public string Property { get; set; }
        public string Value { get; set; }
        public string Module { get; set; }
        public string EventName { get; set; }
        public string Template { get; set; }
        public string Graph { get; set; }
    }

    public sealed class VfxGraphInfo
    {
        public string Path { get; set; }
        public string Guid { get; set; }
        public string Name { get; set; }
        public string Type { get; set; }
        public string Hash { get; set; }
    }

    public sealed class VfxEffectInfo
    {
        public string ComponentType { get; set; }
        public bool Enabled { get; set; }
        public AssetRef Graph { get; set; }
    }

    public sealed class VfxParticleSystemInfo
    {
        public bool IsPlaying { get; set; }
        public bool IsPaused { get; set; }
        public bool IsEmitting { get; set; }
        public long ParticleCount { get; set; }
        public VfxParticleMainInfo Main { get; set; }
        public VfxParticleEmissionInfo Emission { get; set; }
        public VfxParticleShapeInfo Shape { get; set; }
        public VfxParticleModulesInfo Modules { get; set; }
    }

    public sealed class VfxParticleMainInfo
    {
        public double Duration { get; set; }
        public double StartLifetime { get; set; }
        public double StartSpeed { get; set; }
        public double StartSize { get; set; }
        public double StartRotationDegrees { get; set; }
        public long MaxParticles { get; set; }
        public bool Loop { get; set; }
        public bool PlayOnAwake { get; set; }
        public string SimulationSpace { get; set; }
        public string ScalingMode { get; set; }
        public double GravityModifier { get; set; }
    }

    public sealed class VfxParticleEmissionInfo
    {
        public bool Enabled { get; set; }
        public double RateOverTime { get; set; }
        public double RateOverDistance { get; set; }
        public long BurstCount { get; set; }
    }

    public sealed class VfxParticleShapeInfo
    {
        public bool Enabled { get; set; }
        public string ShapeType { get; set; }
        public double Radius { get; set; }
        public double Angle { get; set; }
        public double Arc { get; set; }
        public double Length { get; set; }
        public Vector3Data Box { get; set; }
        public bool RandomDirection { get; set; }
    }

    public sealed class VfxParticleModulesInfo
    {
        public bool ColorOverLifetime { get; set; }
        public bool SizeOverLifetime { get; set; }
        public bool RotationOverLifetime { get; set; }
        public bool VelocityOverLifetime { get; set; }
        public bool Noise { get; set; }
        public bool Collision { get; set; }
        public bool SubEmitters { get; set; }
        public long SubEmittersCount { get; set; }
        public bool Trails { get; set; }
    }

    public sealed class VfxLineRendererInfo
    {
        public double StartWidth { get; set; }
        public double EndWidth { get; set; }
        public double WidthMultiplier { get; set; }
        public VfxCurveData WidthCurve { get; set; }
        public string StartColor { get; set; }
        public string EndColor { get; set; }
        public VfxGradientData ColorGradient { get; set; }
        public string Material { get; set; }
        public long PositionCount { get; set; }
        public List<Vector3Data> Positions { get; set; }
        public bool Loop { get; set; }
        public bool UseWorldSpace { get; set; }
        public string Alignment { get; set; }
        public string TextureMode { get; set; }
        public long NumCornerVertices { get; set; }
        public long NumCapVertices { get; set; }
    }

    public sealed class VfxTrailRendererInfo
    {
        public double Time { get; set; }
        public double StartWidth { get; set; }
        public double EndWidth { get; set; }
        public double WidthMultiplier { get; set; }
        public VfxCurveData WidthCurve { get; set; }
        public string StartColor { get; set; }
        public string EndColor { get; set; }
        public VfxGradientData ColorGradient { get; set; }
        public string Material { get; set; }
        public double MinVertexDistance { get; set; }
        public bool Emitting { get; set; }
        public bool Autodestruct { get; set; }
        public string Alignment { get; set; }
        public string TextureMode { get; set; }
        public long NumCornerVertices { get; set; }
        public long NumCapVertices { get; set; }
    }

    public sealed class VfxCurveData
    {
        public List<VfxCurveKeyData> Keys { get; set; }
    }

    public sealed class VfxCurveKeyData
    {
        public double Time { get; set; }
        public double Value { get; set; }
    }

    public sealed class VfxGradientData
    {
        public List<VfxGradientColorStopData> Colors { get; set; }
        public List<VfxGradientAlphaStopData> Alphas { get; set; }
    }

    public sealed class VfxGradientColorStopData
    {
        public double Time { get; set; }
        public string Color { get; set; }
    }

    public sealed class VfxGradientAlphaStopData
    {
        public double Time { get; set; }
        public double Alpha { get; set; }
    }
}
#endif
