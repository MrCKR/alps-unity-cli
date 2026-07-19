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

        internal static Component ResolveObservedEffect(int gameObjectId, Type effectType, string toolName)
        {
            var root = VfxToolSupport.FindGameObjectByTarget(gameObjectId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var effect = root == null ? null : root.GetComponent(effectType);
            if (effect == null) throw new UnityCliBridge.BridgeCommandException("E_VFX_OBSERVED_TARGET_MISSING", toolName + " target/component disappeared after commit.", "Retry after checking the target still exists.");
            return effect;
        }

        // 控制 API 的可调用签名必须在 CommitEffect 前确定，避免在 effect 后才发现版本不支持。
        internal static void PrepareControlMethod(Type effectType, string methodName)
        {
            var method = effectType.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
            if (method == null || method.ReturnType != typeof(void))
                throw new UnityCliBridge.BridgeCommandException("E_VFX_CONTROL_UNSUPPORTED", "VisualEffect method is unavailable: " + methodName, "Check the installed Visual Effect Graph package version.");
        }

        internal static void PrepareWritableBoolProperty(Type effectType, string propertyName)
        {
            var property = effectType.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
            if (property == null || !property.CanRead || !property.CanWrite || property.PropertyType != typeof(bool))
                throw new UnityCliBridge.BridgeCommandException("E_VFX_PROPERTY_UNSUPPORTED", "VisualEffect property is not readable and writable: " + propertyName, "Check the installed Visual Effect Graph package version.");
        }

        // 调用无参 VFX API；调用前已经由 PrepareControlMethod 验证签名。
        internal static void InvokeRequired(Component effect, string methodName)
        {
            PrepareControlMethod(effect.GetType(), methodName);
            effect.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null).Invoke(effect, null);
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

        // 写入后必须从公开属性读回，避免把请求值当作实际状态返回。
        internal static T SetAndReadRequired<T>(Component effect, string propertyName, T value)
        {
            SetRequired(effect, propertyName, value);
            var property = effect.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
            if (property == null || !property.CanRead || property.PropertyType != typeof(T))
                throw new UnityCliBridge.BridgeCommandException("E_VFX_PROPERTY_UNSUPPORTED", "VisualEffect property is not readable: " + propertyName, "Check the installed Visual Effect Graph package version.");
            var actual = (T)property.GetValue(effect, null);
            if (!EqualityComparer<T>.Default.Equals(actual, value))
                throw new UnityCliBridge.BridgeCommandException("E_VFX_STATE_UNOBSERVABLE", "VisualEffect property did not retain the requested value: " + propertyName, "Check the Visual Effect Graph component state and retry.");
            return actual;
        }

        internal static void PrepareGraphAssignment(Type effectType)
        {
            var property = effectType.GetProperty("visualEffectAsset", BindingFlags.Instance | BindingFlags.Public);
            if (property == null || !property.CanRead || !property.CanWrite || !typeof(UnityEngine.Object).IsAssignableFrom(property.PropertyType))
                throw new UnityCliBridge.BridgeCommandException("E_VFX_PROPERTY_UNSUPPORTED", "VisualEffect graph property is not readable and writable.", "Check the installed Visual Effect Graph package version.");
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

        internal static void VerifyAssignedGraph(Component effect, string graphPath)
        {
            var expectedPath = AssetToolSupport.NormalizeAssetPath(graphPath);
            var property = effect.GetType().GetProperty("visualEffectAsset", BindingFlags.Instance | BindingFlags.Public);
            var actual = property == null ? null : property.GetValue(effect, null) as UnityEngine.Object;
            var actualPath = actual == null ? null : AssetToolSupport.NormalizeAssetPath(AssetDatabase.GetAssetPath(actual));
            if (!string.Equals(actualPath, expectedPath, StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(actualPath)))
                throw new UnityCliBridge.BridgeCommandException("E_VFX_GRAPH_VERIFY_FAILED", "VisualEffect graph assignment could not be read back by asset path/GUID.", "Check the graph asset and VisualEffect component state.");
        }

        // 读取 bool 状态；缺失时返回 null，调用动作本身仍由 InvokeRequired 保证。
        internal static bool? ReadBool(Component effect, string propertyName)
        {
            var property = effect.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
            return property != null && property.CanRead && property.PropertyType == typeof(bool)
                ? (bool?)property.GetValue(effect, null)
                : null;
        }

        // 读取公开数值状态；用于以 aliveParticleCount 证明 play/stop/reinitialize 终态。
        internal static long? ReadLong(Component effect, string propertyName)
        {
            var property = effect.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
            if (property == null || !property.CanRead) return null;
            var value = property.GetValue(effect, null);
            try { return value == null ? (long?)null : Convert.ToInt64(value); }
            catch { return null; }
        }

        internal static async System.Threading.Tasks.ValueTask RequireAliveState(Component effect, System.Func<long, bool> predicate, string action, UnityCliProjectToolContext context, System.Threading.CancellationToken ct)
        {
            var rootId = effect.gameObject.GetInstanceID(); var effectType = effect.GetType();
            var observed = await context.Wait.UntilStable(() =>
            {
                var current = ResolveObservedEffect(rootId, effectType, action);
                var count = ReadLong(current, "aliveParticleCount");
                return count.HasValue && predicate(count.Value);
            }, System.TimeSpan.FromMilliseconds(context.TimeoutMs), ct: ct);
            if (!observed) throw new UnityCliBridge.BridgeCommandException("E_VFX_STATE_TIMEOUT", "VisualEffect " + action + " did not reach its observable alive-particle terminal state before timeout.", "Check the VFX Graph spawn/output behavior and retry only when the target state is observable.");
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

        // 只以 VisualEffect 公开 pause 属性作为控制终态；不存在或不匹配时不能报告成功。
        internal static async System.Threading.Tasks.ValueTask RequirePauseState(Component effect, bool expected, string action, UnityCliProjectToolContext context, System.Threading.CancellationToken ct)
        {
            var rootId = effect.gameObject.GetInstanceID(); var effectType = effect.GetType();
            var stable = await context.Wait.UntilStable(() =>
            {
                var current = ResolveObservedEffect(rootId, effectType, action);
                var observed = ReadBool(current, "pause");
                return observed.HasValue && observed.Value == expected;
            }, System.TimeSpan.FromMilliseconds(context.TimeoutMs), ct: ct);
            var finalState = ReadBool(ResolveObservedEffect(rootId, effectType, action), "pause");
            if (!stable || !finalState.HasValue || finalState.Value != expected)
                throw new UnityCliBridge.BridgeCommandException("E_VFX_STATE_UNOBSERVABLE", "VisualEffect " + action + " did not produce an observable pause-state readback.", "Use a Visual Effect Graph version exposing the public pause property, then retry.");
        }

        // ParticleSystem 公开状态是可观察终态；包含子系统时全部子系统都必须满足。
        internal static async System.Threading.Tasks.ValueTask RequireParticleState(ParticleSystem particleSystem, bool withChildren, System.Func<ParticleSystem, bool> predicate, string action, UnityCliProjectToolContext context, System.Threading.CancellationToken ct)
        {
            var rootId = particleSystem.gameObject.GetInstanceID();
            var observed = await context.Wait.UntilStable(() =>
            {
                // 每次稳定等待轮询都从 instance ID 新鲜解析根与全部子系统，避免销毁/替换后的旧引用成为证明。
                var root = VfxToolSupport.FindGameObjectByTarget(rootId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                var current = root == null ? null : root.GetComponent<ParticleSystem>();
                if (current == null) return false;
                var systems = withChildren ? current.GetComponentsInChildren<ParticleSystem>(true) : new[] { current };
                if (systems == null || systems.Length == 0) return false;
                foreach (var system in systems) if (system == null || !predicate(system)) return false;
                return true;
            }, System.TimeSpan.FromMilliseconds(context.TimeoutMs), ct: ct);
            if (!observed) throw new UnityCliBridge.BridgeCommandException("E_PARTICLE_STATE_TIMEOUT", "ParticleSystem " + action + " did not reach its observable terminal state before timeout.", "Check particle simulation state and retry.");
        }

        internal static VfxControlActionResult BuildControlResult(Component effect, string action, string logPath)
        {
            return new VfxControlActionResult
            {
                Target = GameObjectToolSupport.BuildGameObjectRef(effect.gameObject),
                Action = action,
                IsPaused = ReadBool(effect, "pause"),
                AliveParticleCount = ReadLong(effect, "aliveParticleCount"),
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
        public long? AliveParticleCount { get; set; }
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
