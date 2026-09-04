#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        private sealed class SceneTransformFrame
        {
            internal Transform Parent;
            internal int NextChildIndex;
        }

        /// <summary>统一在 Unity getter 调用前计费。</summary>
        private static T ConsumeSceneGetter<T>(UnityCliRequestBudget budget, Func<T> getter)
        {
            if (!budget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
            return getter();
        }

        // 按 id、path、target 顺序解析场景对象；只读层级，不修改 Selection。
        private static GameObject ResolveSceneContextGameObject(string target, int? id, string path)
        {
            GameObject gameObject = null;
            if (id.HasValue) gameObject = EditorUtility.InstanceIDToObject(id.Value) as GameObject;
            else if (!string.IsNullOrEmpty(path)) gameObject = GameObject.Find(path);
            else if (!string.IsNullOrEmpty(target))
            {
                var resolution = ResolveGameObjectTarget(target);
                if (resolution.Matches.Count == 1) gameObject = resolution.Target;
                else if (resolution.Matches.Count > 1)
                {
                    throw new BridgeCommandException("E_TARGET_AMBIGUOUS", "Multiple GameObjects matched context target: " + target, "Pass a hierarchy path or instance id for disambiguation.");
                }
            }
            if (gameObject == null)
            {
                throw new BridgeCommandException("E_TARGET_NOT_FOUND", "GameObject context target not found.", "Pass input target, path, or id. Use a scene context such as scene.cameras to discover reusable scene object references.");
            }
            return gameObject;
        }

        /// <summary>只读 Tool 使用的预算化解析；exact path 与名称遍历均在每个 Unity getter 前计费。</summary>
        private static GameObject ResolveSceneContextGameObject(string target, int? id, string path, UnityCliRequestBudget budget)
        {
            if (budget == null) throw new ArgumentNullException("budget");
            if (id.HasValue)
            {
                if (!budget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                var byId = EditorUtility.InstanceIDToObject(id.Value) as GameObject;
                if (byId != null) return byId;
            }
            else if (!string.IsNullOrEmpty(path))
            {
                var byPath = ObjectRefResolver.FindGameObjectByExactPath(path, budget);
                if (byPath != null) return byPath;
            }
            else if (!string.IsNullOrEmpty(target))
            {
                var byTarget = target.IndexOf('/') >= 0 ? ObjectRefResolver.FindGameObjectByExactPath(target, budget) : FindUniqueSceneObjectByName(target, budget);
                if (byTarget != null) return byTarget;
            }
            throw new BridgeCommandException("E_TARGET_NOT_FOUND", "GameObject context target not found.", "Pass input target, path, or id. Use scene.hierarchy to discover reusable scene object references.");
        }

        /// <summary>用 `(parent,nextChildIndex)` 深度优先遍历；任何 child 读取前先扣预算。</summary>
        private static GameObject FindUniqueSceneObjectByName(string name, UnityCliRequestBudget budget)
        {
            GameObject match = null;
            if (!budget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
            var sceneCount = SceneManager.sceneCount;
            for (var sceneIndex = 0; sceneIndex < sceneCount; sceneIndex++)
            {
                if (!budget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                var scene = SceneManager.GetSceneAt(sceneIndex);
                if (!budget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                if (!scene.isLoaded) continue;
                foreach (var root in ObjectRefResolver.BoundedSceneRoots(scene, budget))
                {
                    if (!budget.TryConsumeNode() || !budget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                    if (string.Equals(root.name, name, StringComparison.Ordinal))
                    {
                        if (match != null) throw new BridgeCommandException("E_TARGET_AMBIGUOUS", "Multiple GameObjects matched context target: " + name, "Pass a hierarchy path or instance id for disambiguation.");
                        match = root;
                    }
                    if (!budget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                    var stack = new Stack<SceneTransformFrame>();
                    stack.Push(new SceneTransformFrame { Parent = root.transform, NextChildIndex = 0 });
                    while (stack.Count > 0)
                    {
                        var frame = stack.Peek();
                        if (!budget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                        var childCount = frame.Parent.childCount;
                        if (frame.NextChildIndex >= childCount) { stack.Pop(); continue; }
                        if (!budget.TryConsumeScannedRecord() || !budget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                        var child = frame.Parent.GetChild(frame.NextChildIndex++);
                        if (!budget.TryConsumeNode() || !budget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                        if (string.Equals(child.name, name, StringComparison.Ordinal))
                        {
                            if (match != null) throw new BridgeCommandException("E_TARGET_AMBIGUOUS", "Multiple GameObjects matched context target: " + name, "Pass a hierarchy path or instance id for disambiguation.");
                            if (!budget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                            match = child.gameObject;
                        }
                        stack.Push(new SceneTransformFrame { Parent = child, NextChildIndex = 0 });
                    }
                }
            }
            return match;
        }

        /// <summary>按组件类型解析单个组件；类型查找与 Unity getter 均使用同一请求预算。</summary>
        private static Component ResolveSceneContextComponent(GameObject gameObject, string componentName, UnityCliRequestBudget budget)
        {
            var type = GameObjectToolSupport.ResolveComponentType(componentName, budget);
            if (!budget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
            return gameObject.GetComponent(type);
        }

        /// <summary>构造同 session 内稳定的场景资源 claim；GlobalObjectId 不可用时绑定 scene handle 与 instance id。</summary>
        private static UnityCliProofResourceClaim SceneProofResource(GameObject gameObject, Component component = null)
        {
            if (gameObject == null || !gameObject.scene.IsValid())
                throw new BridgeCommandException("E_COMPLETION_PROBE_RESOURCE_INVALID", "completionProbe requires a live scene GameObject resource.", "Pass scene.gameobject read Tool input that resolves one scene object.");
            var objectGlobalId = GlobalObjectId.GetGlobalObjectIdSlow(gameObject).ToString();
            var objectIdentity = string.IsNullOrWhiteSpace(objectGlobalId)
                ? "scene:" + gameObject.scene.handle.ToString(CultureInfo.InvariantCulture) + ":instance:" + gameObject.GetInstanceID().ToString(CultureInfo.InvariantCulture)
                : objectGlobalId;
            var observed = component == null ? (UnityEngine.Object)gameObject : component;
            var observedGlobalId = GlobalObjectId.GetGlobalObjectIdSlow(observed).ToString();
            var observedIdentity = string.IsNullOrWhiteSpace(observedGlobalId)
                ? objectIdentity + ":instance:" + observed.GetInstanceID().ToString(CultureInfo.InvariantCulture)
                : observedGlobalId;
            return new UnityCliProofResourceClaim
            {
                ResourceKey = "scene-object:" + objectIdentity,
                Identity = observedIdentity,
                InstanceId = observed.GetInstanceID(),
                SceneHandle = gameObject.scene.handle
            };
        }

        // 读取单个组件的公开属性预览；getter 失败时跳过该属性。
        private static Dictionary<string, object> SceneContextComponentReadableProperties(Component component, UnityCliRequestBudget budget)
        {
            var values = new Dictionary<string, object>();
            if (!budget.TryConsumeReflectionCall()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
            foreach (var property in UnityCliReflectionSnapshot.DeclaredProperties(component.GetType(), budget, ObjectSerializer.AbsoluteMaxMembers))
            {
                if (!budget.TryConsumeScannedRecord() || !budget.TryConsumeReflectionCall()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                if (!property.CanRead || property.GetIndexParameters().Length > 0) continue;
                try
                {
                    if (!budget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                    var value = property.GetValue(component, null);
                    values[property.Name] = value == null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
                }
                catch (UnityCliRequestBudgetExceededException) { throw; }
                catch
                {
                    // Unity getter 可能依赖不可用的内部状态，属性级失败不影响其余只读结果。
                }
            }
            return values;
        }

        // 直接构造组件 DTO，避免读取路径出现序列化往返。
        private static List<SceneGameObjectComponentsTool.ComponentInfo> SceneContextComponentDetails(GameObject gameObject, bool includeProperties, UnityCliRequestBudget budget)
        {
            var items = new List<SceneGameObjectComponentsTool.ComponentInfo>();
            foreach (var component in ObjectRefResolver.BoundedComponents(gameObject, budget))
            {
                if (component == null)
                {
                    items.Add(new SceneGameObjectComponentsTool.ComponentInfo { Type = "MissingScript", Enabled = false });
                    continue;
                }
                if (!budget.TryConsumeReflectionCall()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                var componentType = component.GetType();
                items.Add(new SceneGameObjectComponentsTool.ComponentInfo
                {
                    Type = componentType.Name,
                    FullName = componentType.FullName,
                    Enabled = GameObjectToolSupport.ComponentEnabled(component, budget),
                    Properties = includeProperties ? SceneContextComponentReadableProperties(component, budget) : null
                });
            }
            return items;
        }
    }
}
#endif
