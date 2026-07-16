#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    /// <summary>VFX 单动作 Tool 共用的目标解析、反射调用和结果构造。</summary>
    internal static class VfxActionToolSupport
    {
        // 解析目标上的 VisualEffect；缺组件时必须失败，不能把空操作报告为成功。
        internal static Component RequireEffect(string target, string toolName)
        {
            VfxToolSupport.EnsureEditorReady(toolName);
            var effectType = VfxToolSupport.RequireVisualEffectType();
            var gameObject = GameObjectToolSupport.ResolveUniqueTarget(target, toolName + " requires target.");
            var effect = gameObject.GetComponent(effectType);
            if (effect == null)
            {
                throw new UnityCliBridge.BridgeCommandException(
                    "E_VFX_COMPONENT_NOT_FOUND",
                    "VisualEffect component not found on target: " + GameObjectToolSupport.HierarchyPath(gameObject),
                    "Add one with vfx.effect.add or pass a target that already has VisualEffect.");
            }
            return effect;
        }

        // 调用无参 VFX API；版本不支持时返回稳定错误，而不是静默跳过。
        internal static void InvokeRequired(Component effect, string methodName)
        {
            var method = effect.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
            if (method == null)
            {
                throw new UnityCliBridge.BridgeCommandException(
                    "E_VFX_CONTROL_UNSUPPORTED",
                    "VisualEffect method is unavailable: " + methodName,
                    "Check the installed Visual Effect Graph package version.");
            }
            method.Invoke(effect, null);
        }

        // 写入公开 VFX 属性并标记场景状态。
        internal static void SetRequired(Component effect, string propertyName, object value)
        {
            var property = effect.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
            if (property == null || !property.CanWrite)
            {
                throw new UnityCliBridge.BridgeCommandException(
                    "E_VFX_PROPERTY_UNSUPPORTED",
                    "VisualEffect property is not writable: " + propertyName,
                    "Check the installed Visual Effect Graph package version.");
            }
            property.SetValue(effect, value, null);
            UnityCliSceneMutation.MarkSceneObjectDirty(effect);
            VfxToolSupport.MarkSceneDirty(effect.gameObject);
        }

        // 给现有 VisualEffect 绑定图资产，并在反射 API 不可写时失败。
        internal static void AssignGraph(Component effect, string graphPath)
        {
            var normalizedPath = AssetToolSupport.NormalizeAssetPath(graphPath);
            var graph = AssetDatabase.LoadAssetAtPath(normalizedPath, VfxToolSupport.RequireVfxAssetType());
            if (graph == null)
            {
                throw new UnityCliBridge.BridgeCommandException(
                    "E_VFX_GRAPH_NOT_FOUND",
                    "VFX Graph asset not found: " + graphPath,
                    "Create it with vfx.graph.create or pass an existing Assets/*.vfx path.");
            }
            SetRequired(effect, "visualEffectAsset", graph);
        }

        // 读取 bool 状态；缺失时返回 null，调用动作本身仍由 InvokeRequired 保证。
        internal static bool? ReadBool(Component effect, string propertyName)
        {
            var property = effect.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
            return property != null && property.CanRead && property.PropertyType == typeof(bool)
                ? (bool?)property.GetValue(effect, null)
                : null;
        }

        // 解析 ParticleSystem 目标。
        internal static ParticleSystem RequireParticle(string target, string toolName)
        {
            VfxToolSupport.EnsureEditorReady(toolName);
            var particleSystem = VfxToolSupport.ResolveParticleSystem(target);
            if (particleSystem == null)
            {
                throw new UnityCliBridge.BridgeCommandException(
                    "E_PARTICLE_SYSTEM_NOT_FOUND",
                    "ParticleSystem not found: " + target,
                    "Pass a GameObject name or hierarchy path with a ParticleSystem component.");
            }
            return particleSystem;
        }

        internal static VfxControlActionResult BuildControlResult(Component effect, string action, string logPath)
        {
            return new VfxControlActionResult
            {
                Target = GameObjectToolSupport.BuildGameObjectRef(effect.gameObject),
                Action = action,
                IsPaused = ReadBool(effect, "pause"),
                PlannedEffects = new List<VfxPlannedEffect> { VfxToolSupport.Effect(action, "action", action) },
                LogPath = logPath
            };
        }

        internal static VfxParticleActionResult BuildParticleResult(ParticleSystem particleSystem, string action, string logPath)
        {
            return new VfxParticleActionResult
            {
                Target = GameObjectToolSupport.BuildGameObjectRef(particleSystem.gameObject),
                Action = action,
                IsPlaying = particleSystem.isPlaying,
                IsPaused = particleSystem.isPaused,
                IsEmitting = particleSystem.isEmitting,
                PlannedEffects = new List<VfxPlannedEffect> { VfxToolSupport.Effect(action, "action", action) },
                LogPath = logPath
            };
        }
    }

    public sealed class VfxControlActionResult
    {
        public GameObjectRef Target { get; set; }
        public string Action { get; set; }
        public bool? IsPaused { get; set; }
        public double? Rate { get; set; }
        public long? Seed { get; set; }
        public bool? ResetSeedOnPlay { get; set; }
        public List<VfxPlannedEffect> PlannedEffects { get; set; }
        public string LogPath { get; set; }
    }

    public sealed class VfxEffectActionResult
    {
        public GameObjectRef Target { get; set; }
        public string Action { get; set; }
        public VfxEffectInfo Effect { get; set; }
        public List<VfxPlannedEffect> PlannedEffects { get; set; }
        public string LogPath { get; set; }
    }

    public sealed class VfxParticleActionResult
    {
        public GameObjectRef Target { get; set; }
        public string Action { get; set; }
        public bool IsPlaying { get; set; }
        public bool IsPaused { get; set; }
        public bool IsEmitting { get; set; }
        public List<VfxPlannedEffect> PlannedEffects { get; set; }
        public string LogPath { get; set; }
    }
}
#endif
