#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
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
                "Install com.unity.visualeffectgraph manually via Unity Package Manager or Packages/manifest.json, then run unity_cli extensions install vfx-graph --project <path>.");
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
                    "Use unity_cli tools run vfx.graph --input '{\"path\":\"Assets/VFX/Effect.vfx\"}'.");
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
                Type = asset == null ? null : asset.GetType().FullName
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

        public static void MarkSceneDirty(GameObject gameObject)
        {
            if (gameObject != null && gameObject.scene.IsValid())
            {
                EditorSceneManager.MarkSceneDirty(gameObject.scene);
            }
        }

        public static UnityEngine.Object CreateVfxGraphAsset(string path, Type assetType)
        {
            try
            {
                var created = CreateVfxGraphAssetWithUnityUtility(path, assetType);
                if (created != null)
                {
                    return created;
                }

                WriteMinimalVfxGraphYaml(path);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                AssetDatabase.Refresh();
                return AssetDatabase.LoadAssetAtPath(path, assetType);
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
                    return ToAssetPath(exactFile);
                }

                foreach (var file in Directory.GetFiles(searchPath, "*.vfx", SearchOption.AllDirectories))
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    if (string.Equals(name, templateName, StringComparison.OrdinalIgnoreCase))
                    {
                        return ToAssetPath(file);
                    }

                    var relative = file.Substring(searchPath.Length).TrimStart(Path.DirectorySeparatorChar, '/');
                    if (string.Equals(relative, templateName + ".vfx", StringComparison.OrdinalIgnoreCase))
                    {
                        return ToAssetPath(file);
                    }
                }
            }

            return null;
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

        public static void ApplyTrailRendererFields(TrailRenderer trail, TrailRendererFields request, List<VfxPlannedEffect> effects)
        {
            if (request.Time.HasValue)
            {
                trail.time = (float)request.Time.Value;
                effects.Add(Effect("vfx.trail", "time", Number(trail.time)));
            }
            if (request.StartWidth.HasValue)
            {
                trail.startWidth = (float)request.StartWidth.Value;
                effects.Add(Effect("vfx.trail", "startWidth", Number(trail.startWidth)));
            }
            if (request.EndWidth.HasValue)
            {
                trail.endWidth = (float)request.EndWidth.Value;
                effects.Add(Effect("vfx.trail", "endWidth", Number(trail.endWidth)));
            }
            if (request.WidthMultiplier.HasValue)
            {
                trail.widthMultiplier = (float)request.WidthMultiplier.Value;
                effects.Add(Effect("vfx.trail", "widthMultiplier", Number(trail.widthMultiplier)));
            }
            if (request.WidthCurve != null && request.WidthCurve.Count > 0)
            {
                trail.widthCurve = BuildCurve(request.WidthCurve);
                effects.Add(Effect("vfx.trail", "widthCurve", "keys=" + request.WidthCurve.Count.ToString(CultureInfo.InvariantCulture)));
            }
            if (!string.IsNullOrWhiteSpace(request.StartColor))
            {
                trail.startColor = GraphicsToolSupport.ParseColor(request.StartColor);
                effects.Add(Effect("vfx.trail", "startColor", request.StartColor));
            }
            if (!string.IsNullOrWhiteSpace(request.EndColor))
            {
                trail.endColor = GraphicsToolSupport.ParseColor(request.EndColor);
                effects.Add(Effect("vfx.trail", "endColor", request.EndColor));
            }
            if (request.ColorGradient != null && request.ColorGradient.Count > 0)
            {
                trail.colorGradient = BuildGradient(request.ColorGradient);
                effects.Add(Effect("vfx.trail", "colorGradient", "keys=" + request.ColorGradient.Count.ToString(CultureInfo.InvariantCulture)));
            }
            if (!string.IsNullOrWhiteSpace(request.Material))
            {
                trail.sharedMaterial = ResolveOptionalMaterial(request.Material);
                effects.Add(Effect("vfx.trail", "material", trail.sharedMaterial == null ? "null" : AssetDatabase.GetAssetPath(trail.sharedMaterial)));
            }
            if (request.MinVertexDistance.HasValue)
            {
                trail.minVertexDistance = (float)request.MinVertexDistance.Value;
                effects.Add(Effect("vfx.trail", "minVertexDistance", Number(trail.minVertexDistance)));
            }
            if (request.Autodestruct.HasValue)
            {
                trail.autodestruct = request.Autodestruct.Value;
                effects.Add(Effect("vfx.trail", "autodestruct", Bool(trail.autodestruct)));
            }
            if (request.Emitting.HasValue)
            {
                trail.emitting = request.Emitting.Value;
                effects.Add(Effect("vfx.trail", "emitting", Bool(trail.emitting)));
            }
            if (!string.IsNullOrWhiteSpace(request.Alignment))
            {
                trail.alignment = ParseEnum<LineAlignment>(request.Alignment, "E_TRAIL_ALIGNMENT_INVALID", "Use View or TransformZ.");
                effects.Add(Effect("vfx.trail", "alignment", trail.alignment.ToString()));
            }
            if (!string.IsNullOrWhiteSpace(request.TextureMode))
            {
                trail.textureMode = ParseEnum<LineTextureMode>(request.TextureMode, "E_TRAIL_TEXTURE_MODE_INVALID", "Use Stretch, Tile, DistributePerSegment, or RepeatPerSegment.");
                effects.Add(Effect("vfx.trail", "textureMode", trail.textureMode.ToString()));
            }
            if (request.NumCornerVertices.HasValue)
            {
                trail.numCornerVertices = request.NumCornerVertices.Value;
                effects.Add(Effect("vfx.trail", "numCornerVertices", trail.numCornerVertices.ToString(CultureInfo.InvariantCulture)));
            }
            if (request.NumCapVertices.HasValue)
            {
                trail.numCapVertices = request.NumCapVertices.Value;
                effects.Add(Effect("vfx.trail", "numCapVertices", trail.numCapVertices.ToString(CultureInfo.InvariantCulture)));
            }
        }

        public static bool ApplyParticleModuleFields(ParticleSystem particleSystem, VfxParticleModuleTool.Request request, List<VfxPlannedEffect> effects)
        {
            var key = (request.Module ?? string.Empty).Replace("-", string.Empty).Replace("_", string.Empty).ToLowerInvariant();
            var enabled = request.Enabled;
            switch (key)
            {
                case "coloroverlifetime":
                case "color":
                    var color = particleSystem.colorOverLifetime;
                    if (enabled.HasValue)
                    {
                        color.enabled = enabled.Value;
                        effects.Add(Effect("vfx.particle-module", "colorOverLifetime.enabled", Bool(color.enabled)));
                    }
                    if (!string.IsNullOrWhiteSpace(request.Color))
                    {
                        color.color = new ParticleSystem.MinMaxGradient(GraphicsToolSupport.ParseColor(request.Color));
                        effects.Add(Effect("vfx.particle-module", "colorOverLifetime.color", request.Color));
                    }
                    return true;
                case "sizeoverlifetime":
                case "size":
                    var size = particleSystem.sizeOverLifetime;
                    if (enabled.HasValue)
                    {
                        size.enabled = enabled.Value;
                        effects.Add(Effect("vfx.particle-module", "sizeOverLifetime.enabled", Bool(size.enabled)));
                    }
                    if (request.Size.HasValue)
                    {
                        size.size = new ParticleSystem.MinMaxCurve((float)request.Size.Value);
                        effects.Add(Effect("vfx.particle-module", "sizeOverLifetime.size", Number(size.size.constant)));
                    }
                    return true;
                case "rotationoverlifetime":
                case "rotation":
                    var rotation = particleSystem.rotationOverLifetime;
                    if (enabled.HasValue)
                    {
                        rotation.enabled = enabled.Value;
                        effects.Add(Effect("vfx.particle-module", "rotationOverLifetime.enabled", Bool(rotation.enabled)));
                    }
                    if (request.Rotation.HasValue)
                    {
                        rotation.z = new ParticleSystem.MinMaxCurve((float)request.Rotation.Value * Mathf.Deg2Rad);
                        effects.Add(Effect("vfx.particle-module", "rotationOverLifetime.z", request.Rotation.Value.ToString(CultureInfo.InvariantCulture)));
                    }
                    return true;
                case "velocityoverlifetime":
                case "velocity":
                    var velocity = particleSystem.velocityOverLifetime;
                    if (enabled.HasValue)
                    {
                        velocity.enabled = enabled.Value;
                        effects.Add(Effect("vfx.particle-module", "velocityOverLifetime.enabled", Bool(velocity.enabled)));
                    }
                    if (request.X.HasValue)
                    {
                        velocity.x = new ParticleSystem.MinMaxCurve((float)request.X.Value);
                        effects.Add(Effect("vfx.particle-module", "velocityOverLifetime.x", Number(velocity.x.constant)));
                    }
                    if (request.Y.HasValue)
                    {
                        velocity.y = new ParticleSystem.MinMaxCurve((float)request.Y.Value);
                        effects.Add(Effect("vfx.particle-module", "velocityOverLifetime.y", Number(velocity.y.constant)));
                    }
                    if (request.Z.HasValue)
                    {
                        velocity.z = new ParticleSystem.MinMaxCurve((float)request.Z.Value);
                        effects.Add(Effect("vfx.particle-module", "velocityOverLifetime.z", Number(velocity.z.constant)));
                    }
                    if (!string.IsNullOrWhiteSpace(request.Space))
                    {
                        velocity.space = ParseEnum<ParticleSystemSimulationSpace>(request.Space, "E_PARTICLE_SPACE_INVALID", "Use Local or World.");
                        effects.Add(Effect("vfx.particle-module", "velocityOverLifetime.space", velocity.space.ToString()));
                    }
                    if (request.SpeedModifier.HasValue)
                    {
                        velocity.speedModifier = new ParticleSystem.MinMaxCurve((float)request.SpeedModifier.Value);
                        effects.Add(Effect("vfx.particle-module", "velocityOverLifetime.speedModifier", Number(velocity.speedModifier.constant)));
                    }
                    return true;
                case "forceoverlifetime":
                case "force":
                    var force = particleSystem.forceOverLifetime;
                    if (enabled.HasValue)
                    {
                        force.enabled = enabled.Value;
                        effects.Add(Effect("vfx.particle-module", "forceOverLifetime.enabled", Bool(force.enabled)));
                    }
                    if (request.X.HasValue)
                    {
                        force.x = new ParticleSystem.MinMaxCurve((float)request.X.Value);
                        effects.Add(Effect("vfx.particle-module", "forceOverLifetime.x", Number(force.x.constant)));
                    }
                    if (request.Y.HasValue)
                    {
                        force.y = new ParticleSystem.MinMaxCurve((float)request.Y.Value);
                        effects.Add(Effect("vfx.particle-module", "forceOverLifetime.y", Number(force.y.constant)));
                    }
                    if (request.Z.HasValue)
                    {
                        force.z = new ParticleSystem.MinMaxCurve((float)request.Z.Value);
                        effects.Add(Effect("vfx.particle-module", "forceOverLifetime.z", Number(force.z.constant)));
                    }
                    if (!string.IsNullOrWhiteSpace(request.Space))
                    {
                        force.space = ParseEnum<ParticleSystemSimulationSpace>(request.Space, "E_PARTICLE_SPACE_INVALID", "Use Local or World.");
                        effects.Add(Effect("vfx.particle-module", "forceOverLifetime.space", force.space.ToString()));
                    }
                    return true;
                case "noise":
                    var noise = particleSystem.noise;
                    if (enabled.HasValue)
                    {
                        noise.enabled = enabled.Value;
                        effects.Add(Effect("vfx.particle-module", "noise.enabled", Bool(noise.enabled)));
                    }
                    if (request.Strength.HasValue)
                    {
                        noise.strength = (float)request.Strength.Value;
                        effects.Add(Effect("vfx.particle-module", "noise.strength", Number(noise.strength.constant)));
                    }
                    if (request.Frequency.HasValue)
                    {
                        noise.frequency = (float)request.Frequency.Value;
                        effects.Add(Effect("vfx.particle-module", "noise.frequency", Number(noise.frequency)));
                    }
                    if (request.ScrollSpeed.HasValue)
                    {
                        noise.scrollSpeed = (float)request.ScrollSpeed.Value;
                        effects.Add(Effect("vfx.particle-module", "noise.scrollSpeed", Number(noise.scrollSpeed.constant)));
                    }
                    if (request.OctaveCount.HasValue)
                    {
                        noise.octaveCount = request.OctaveCount.Value;
                        effects.Add(Effect("vfx.particle-module", "noise.octaveCount", noise.octaveCount.ToString(CultureInfo.InvariantCulture)));
                    }
                    if (!string.IsNullOrWhiteSpace(request.Quality))
                    {
                        noise.quality = ParseEnum<ParticleSystemNoiseQuality>(request.Quality, "E_PARTICLE_NOISE_QUALITY_INVALID", "Use Low, Medium, or High.");
                        effects.Add(Effect("vfx.particle-module", "noise.quality", noise.quality.ToString()));
                    }
                    return true;
                case "collision":
                    var collision = particleSystem.collision;
                    if (enabled.HasValue)
                    {
                        collision.enabled = enabled.Value;
                        effects.Add(Effect("vfx.particle-module", "collision.enabled", Bool(collision.enabled)));
                    }
                    if (!string.IsNullOrWhiteSpace(request.CollisionType))
                    {
                        collision.type = ParseEnum<ParticleSystemCollisionType>(request.CollisionType, "E_PARTICLE_COLLISION_TYPE_INVALID", "Use Planes or World.");
                        effects.Add(Effect("vfx.particle-module", "collision.type", collision.type.ToString()));
                    }
                    if (!string.IsNullOrWhiteSpace(request.CollisionMode))
                    {
                        collision.mode = ParseEnum<ParticleSystemCollisionMode>(request.CollisionMode, "E_PARTICLE_COLLISION_MODE_INVALID", "Use Collision3D or Collision2D.");
                        effects.Add(Effect("vfx.particle-module", "collision.mode", collision.mode.ToString()));
                    }
                    if (request.Dampen.HasValue)
                    {
                        collision.dampen = (float)request.Dampen.Value;
                        effects.Add(Effect("vfx.particle-module", "collision.dampen", Number(collision.dampen.constant)));
                    }
                    if (request.Bounce.HasValue)
                    {
                        collision.bounce = (float)request.Bounce.Value;
                        effects.Add(Effect("vfx.particle-module", "collision.bounce", Number(collision.bounce.constant)));
                    }
                    if (request.LifetimeLoss.HasValue)
                    {
                        collision.lifetimeLoss = (float)request.LifetimeLoss.Value;
                        effects.Add(Effect("vfx.particle-module", "collision.lifetimeLoss", Number(collision.lifetimeLoss.constant)));
                    }
                    if (request.MinKillSpeed.HasValue)
                    {
                        collision.minKillSpeed = (float)request.MinKillSpeed.Value;
                        effects.Add(Effect("vfx.particle-module", "collision.minKillSpeed", Number(collision.minKillSpeed)));
                    }
                    if (request.MaxKillSpeed.HasValue)
                    {
                        collision.maxKillSpeed = (float)request.MaxKillSpeed.Value;
                        effects.Add(Effect("vfx.particle-module", "collision.maxKillSpeed", Number(collision.maxKillSpeed)));
                    }
                    if (request.RadiusScale.HasValue)
                    {
                        collision.radiusScale = (float)request.RadiusScale.Value;
                        effects.Add(Effect("vfx.particle-module", "collision.radiusScale", Number(collision.radiusScale)));
                    }
                    return true;
                case "subemitters":
                case "subemitter":
                case "sub":
                    var sub = particleSystem.subEmitters;
                    if (enabled.HasValue)
                    {
                        sub.enabled = enabled.Value;
                        effects.Add(Effect("vfx.particle-module", "subEmitters.enabled", Bool(sub.enabled)));
                    }
                    ApplySubEmitterFields(sub, request, effects);
                    return true;
                case "textureanimation":
                case "texturesheetanimation":
                case "texture":
                    var texture = particleSystem.textureSheetAnimation;
                    if (enabled.HasValue)
                    {
                        texture.enabled = enabled.Value;
                        effects.Add(Effect("vfx.particle-module", "textureSheetAnimation.enabled", Bool(texture.enabled)));
                    }
                    return true;
                case "lights":
                    var lights = particleSystem.lights;
                    if (enabled.HasValue)
                    {
                        lights.enabled = enabled.Value;
                        effects.Add(Effect("vfx.particle-module", "lights.enabled", Bool(lights.enabled)));
                    }
                    return true;
                case "trails":
                case "trail":
                    var trails = particleSystem.trails;
                    if (enabled.HasValue)
                    {
                        trails.enabled = enabled.Value;
                        effects.Add(Effect("vfx.particle-module", "trails.enabled", Bool(trails.enabled)));
                    }
                    if (request.Ratio.HasValue)
                    {
                        trails.ratio = (float)request.Ratio.Value;
                        effects.Add(Effect("vfx.particle-module", "trails.ratio", Number(trails.ratio)));
                    }
                    if (request.Lifetime.HasValue)
                    {
                        trails.lifetime = (float)request.Lifetime.Value;
                        effects.Add(Effect("vfx.particle-module", "trails.lifetime", Number(trails.lifetime.constant)));
                    }
                    if (request.Width.HasValue)
                    {
                        trails.widthOverTrail = new ParticleSystem.MinMaxCurve((float)request.Width.Value);
                        effects.Add(Effect("vfx.particle-module", "trails.width", Number(trails.widthOverTrail.constant)));
                    }
                    if (!string.IsNullOrWhiteSpace(request.Color))
                    {
                        trails.colorOverTrail = new ParticleSystem.MinMaxGradient(GraphicsToolSupport.ParseColor(request.Color));
                        effects.Add(Effect("vfx.particle-module", "trails.color", request.Color));
                    }
                    return true;
                case "inheritvelocity":
                    var inherit = particleSystem.inheritVelocity;
                    if (enabled.HasValue)
                    {
                        inherit.enabled = enabled.Value;
                        effects.Add(Effect("vfx.particle-module", "inheritVelocity.enabled", Bool(inherit.enabled)));
                    }
                    if (!string.IsNullOrWhiteSpace(request.Mode))
                    {
                        inherit.mode = ParseEnum<ParticleSystemInheritVelocityMode>(request.Mode, "E_PARTICLE_INHERIT_MODE_INVALID", "Use Initial or Current.");
                        effects.Add(Effect("vfx.particle-module", "inheritVelocity.mode", inherit.mode.ToString()));
                    }
                    if (request.Curve.HasValue)
                    {
                        inherit.curve = new ParticleSystem.MinMaxCurve((float)request.Curve.Value);
                        effects.Add(Effect("vfx.particle-module", "inheritVelocity.curve", Number(inherit.curve.constant)));
                    }
                    return true;
                case "limitvelocityoverlifetime":
                case "limitvelocity":
                    var limit = particleSystem.limitVelocityOverLifetime;
                    if (enabled.HasValue)
                    {
                        limit.enabled = enabled.Value;
                        effects.Add(Effect("vfx.particle-module", "limitVelocityOverLifetime.enabled", Bool(limit.enabled)));
                    }
                    if (request.Speed.HasValue)
                    {
                        limit.limit = new ParticleSystem.MinMaxCurve((float)request.Speed.Value);
                        effects.Add(Effect("vfx.particle-module", "limitVelocityOverLifetime.limit", Number(limit.limit.constant)));
                    }
                    if (request.Dampen.HasValue)
                    {
                        limit.dampen = (float)request.Dampen.Value;
                        effects.Add(Effect("vfx.particle-module", "limitVelocityOverLifetime.dampen", Number(limit.dampen)));
                    }
                    return true;
                default:
                    return false;
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
                    Box = new Vector3Data(shape.box),
                    RandomDirection = shape.randomDirection
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
            foreach (var key in keys)
            {
                items.Add(new Keyframe((float)key.Time, (float)key.Value));
            }
            if (items.Count == 0)
            {
                items.Add(new Keyframe(0f, 1f));
                items.Add(new Keyframe(1f, 1f));
            }
            return new AnimationCurve(items.ToArray());
        }

        public static Gradient BuildGradient(List<VfxGradientStopInput> stops)
        {
            var colorKeys = new List<GradientColorKey>();
            var alphaKeys = new List<GradientAlphaKey>();
            foreach (var stop in stops)
            {
                var color = GraphicsToolSupport.ParseColor(stop.Color);
                var time = (float)stop.Time;
                colorKeys.Add(new GradientColorKey(color, time));
                alphaKeys.Add(new GradientAlphaKey(color.a, time));
            }
            if (colorKeys.Count == 0)
            {
                colorKeys.Add(new GradientColorKey(Color.white, 0f));
                colorKeys.Add(new GradientColorKey(Color.white, 1f));
                alphaKeys.Add(new GradientAlphaKey(1f, 0f));
                alphaKeys.Add(new GradientAlphaKey(1f, 1f));
            }
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
            if (Enum.TryParse<T>(value, true, out parsed))
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
            var utilityType = GameObjectToolSupport.FindType("UnityEditor.VisualEffectAssetEditorUtility");
            if (utilityType == null)
            {
                return null;
            }

            var createMethod = utilityType.GetMethod("CreateNewAsset", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (createMethod == null)
            {
                return null;
            }

            var created = createMethod.Invoke(null, new object[] { path }) as UnityEngine.Object;
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            AssetDatabase.Refresh();
            return created == null || !assetType.IsInstanceOfType(created)
                ? AssetDatabase.LoadAssetAtPath(path, assetType)
                : created;
        }

        private static void WriteMinimalVfxGraphYaml(string path)
        {
            var fullPath = Path.Combine(AssetToolSupport.ProjectRoot(), path.Replace('/', Path.DirectorySeparatorChar));
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(
                fullPath,
                "%YAML 1.1\n"
                + "%TAG !u! tag:unity3d.com,2011:\n"
                + "--- !u!114 &114350483966674976\n"
                + "MonoBehaviour:\n"
                + "  m_Script: {fileID: 11500000, guid: 7d4c867f6b72b714dbb5fd1780afe208, type: 3}\n"
                + "--- !u!2058629511 &1\n"
                + "VisualEffectResource:\n"
                + "  m_Graph: {fileID: 114350483966674976}\n");
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

        private static string ToAssetPath(string fullPath)
        {
            var projectRoot = AssetToolSupport.ProjectRoot();
            if (fullPath.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
            {
                return fullPath.Substring(projectRoot.Length).TrimStart(Path.DirectorySeparatorChar, '/').Replace('\\', '/');
            }
            return fullPath.Replace('\\', '/');
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

        private static void ApplySubEmitterFields(ParticleSystem.SubEmittersModule sub, VfxParticleModuleTool.Request request, List<VfxPlannedEffect> effects)
        {
            var action = (request.SubEmitterAction ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(action))
            {
                return;
            }

            if (action == "clear")
            {
                for (var index = sub.subEmittersCount - 1; index >= 0; index--)
                {
                    sub.RemoveSubEmitter(index);
                }
                effects.Add(Effect("vfx.particle-module", "subEmitters", "cleared"));
                return;
            }

            if (action == "remove")
            {
                var index = request.Index ?? 0;
                if (index < 0 || index >= sub.subEmittersCount)
                {
                    throw new UnityCliBridge.BridgeCommandException(
                        "E_SUB_EMITTER_INDEX_INVALID",
                        "Sub-emitter index out of range: " + index.ToString(CultureInfo.InvariantCulture),
                        "Pass a valid sub-emitter index.");
                }
                sub.RemoveSubEmitter(index);
                effects.Add(Effect("vfx.particle-module", "subEmitters", "removed"));
                return;
            }

            if (action == "add")
            {
                var subParticleSystem = ResolveParticleSystem(request.SubEmitterTarget);
                if (subParticleSystem == null)
                {
                    throw new UnityCliBridge.BridgeCommandException(
                        "E_SUB_EMITTER_NOT_FOUND",
                        "Sub-emitter ParticleSystem not found: " + request.SubEmitterTarget,
                        "Pass subEmitterTarget as a GameObject with ParticleSystem.");
                }

                var type = string.IsNullOrWhiteSpace(request.SubEmitterType)
                    ? ParticleSystemSubEmitterType.Birth
                    : ParseEnum<ParticleSystemSubEmitterType>(request.SubEmitterType, "E_SUB_EMITTER_TYPE_INVALID", "Use Birth, Collision, Death, Trigger, or Manual.");
                var properties = string.IsNullOrWhiteSpace(request.SubEmitterProperties)
                    ? ParticleSystemSubEmitterProperties.InheritNothing
                    : ParseEnum<ParticleSystemSubEmitterProperties>(request.SubEmitterProperties, "E_SUB_EMITTER_PROPERTIES_INVALID", "Use InheritNothing or InheritEverything.");
                sub.AddSubEmitter(subParticleSystem, type, properties);
                effects.Add(Effect("vfx.particle-module", "subEmitters", "added"));
                return;
            }

            throw new UnityCliBridge.BridgeCommandException(
                "E_SUB_EMITTER_ACTION_INVALID",
                "Unsupported subEmitterAction: " + action,
                "Use add, remove, or clear.");
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
