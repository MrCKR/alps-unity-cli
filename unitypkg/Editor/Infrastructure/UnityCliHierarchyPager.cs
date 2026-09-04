#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityCli
{
    /// <summary>基于 next GameObject instance id 的前序游标；续页只做 O(depth) 定位，不重扫旧页面。</summary>
    internal static class UnityCliHierarchyPager
    {
        /// <summary>单场景遍历只获取一次 root snapshot；子节点始终通过 child-at 向前推进。</summary>
        internal sealed class Traversal
        {
            private readonly Scene scene;
            private readonly GameObject boundaryParent;
            private readonly Transform boundary;
            private readonly int maxDepth;
            private GameObject[] roots;

            internal Traversal(Scene scene, GameObject boundaryParent, int maxDepth, UnityCliRequestBudget budget)
            {
                this.scene = scene;
                this.boundaryParent = boundaryParent;
                if (boundaryParent != null && !budget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                boundary = boundaryParent == null ? null : boundaryParent.transform;
                this.maxDepth = maxDepth;
            }

            internal Scene Scene { get { return scene; } }

            internal GameObject First(UnityCliRequestBudget budget)
            {
                if (boundaryParent != null)
                {
                    if (!budget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                    var childCount = boundary.childCount;
                    if (childCount == 0) return null;
                    if (!budget.TryConsumeGetters(2)) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                    return boundary.GetChild(0).gameObject;
                }
                roots = roots ?? UnityCliBridge.ObjectRefResolver.BoundedSceneRoots(scene, budget);
                return roots.Length == 0 ? null : roots[0];
            }

            internal GameObject Next(GameObject current, UnityCliRequestBudget budget)
            {
                if (current == null) return null;
                if (!budget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                var currentTransform = current.transform;
                var relativeDepth = Depth(currentTransform, boundary, budget);
                if (!budget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                if (relativeDepth < maxDepth && currentTransform.childCount > 0)
                {
                    if (!budget.TryConsumeGetters(2)) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                    return currentTransform.GetChild(0).gameObject;
                }
                var cursor = currentTransform;
                while (cursor != null && cursor != boundary)
                {
                    if (!budget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                    var parent = cursor.parent;
                    if (parent != null)
                    {
                        if (!budget.TryConsumeGetter() || !budget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                        var sibling = cursor.GetSiblingIndex() + 1;
                        if (sibling < parent.childCount)
                        {
                            if (!budget.TryConsumeGetters(2)) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                            return parent.GetChild(sibling).gameObject;
                        }
                        cursor = parent;
                        continue;
                    }
                    if (!budget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                    var rootSibling = cursor.GetSiblingIndex() + 1;
                    roots = roots ?? UnityCliBridge.ObjectRefResolver.BoundedSceneRoots(scene, budget);
                    return rootSibling < roots.Length ? roots[rootSibling] : null;
                }
                return null;
            }
        }

        /// <summary>加载场景集合在请求开始时冻结，每个场景 root array 最多 materialize 一次。</summary>
        internal sealed class LoadedSceneTraversal
        {
            private readonly List<Scene> scenes = new List<Scene>();
            private readonly List<Traversal> traversals = new List<Traversal>();
            private readonly Dictionary<int, int> sceneIndices = new Dictionary<int, int>();

            internal LoadedSceneTraversal(UnityCliRequestBudget budget, int excludedSceneHandle = 0)
            {
                if (!budget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                var sceneCount = SceneManager.sceneCount;
                for (var sceneIndex = 0; sceneIndex < sceneCount; sceneIndex++)
                {
                    if (!budget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                    var scene = SceneManager.GetSceneAt(sceneIndex);
                    if (!budget.TryConsumeGetters(2)) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                    if (!scene.isLoaded || scene.handle == excludedSceneHandle) continue;
                    var handle = scene.handle;
                    sceneIndices[handle] = scenes.Count;
                    scenes.Add(scene);
                    traversals.Add(null);
                }
            }

            internal GameObject First(UnityCliRequestBudget budget)
            {
                for (var index = 0; index < traversals.Count; index++)
                {
                    var first = GetTraversal(index, budget).First(budget);
                    if (first != null) return first;
                }
                return null;
            }

            internal GameObject Next(GameObject current, UnityCliRequestBudget budget)
            {
                if (current == null) return null;
                int index;
                if (!budget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                if (!sceneIndices.TryGetValue(current.scene.handle, out index)) return null;
                var next = GetTraversal(index, budget).Next(current, budget);
                if (next != null) return next;
                for (index += 1; index < traversals.Count; index += 1)
                {
                    next = GetTraversal(index, budget).First(budget);
                    if (next != null) return next;
                }
                return null;
            }

            private Traversal GetTraversal(int index, UnityCliRequestBudget budget)
            {
                var traversal = traversals[index];
                if (traversal != null) return traversal;
                traversal = new Traversal(scenes[index], null, int.MaxValue, budget);
                traversals[index] = traversal;
                return traversal;
            }
        }

        internal static int Depth(Transform value, Transform boundary, UnityCliRequestBudget budget)
        {
            var depth = 0;
            if (!budget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
            var current = value.parent;
            while (current != null && current != boundary)
            {
                if (!budget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                depth++;
                current = current.parent;
            }
            return depth;
        }

        internal static string Cursor(string scope, string generation, string phase, GameObject next, UnityCliRequestBudget budget)
        {
            if (next == null) return null;
            if (!budget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
            return UnityCliContinuationToken.Create(scope, generation, (phase ?? "scene") + ":" + next.GetInstanceID().ToString(CultureInfo.InvariantCulture));
        }

        internal static GameObject ResolveCursor(string token, string scope, string generation, UnityCliRequestBudget budget, out string phase)
        {
            phase = null;
            string position;
            if (!UnityCliContinuationToken.TryParse(token, scope, generation, out position)) throw Stale();
            var separator = position.IndexOf(':');
            int instanceId;
            if (separator <= 0 || !int.TryParse(position.Substring(separator + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out instanceId)) throw Stale();
            phase = position.Substring(0, separator);
            if (!budget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
            var value = EditorUtility.InstanceIDToObject(instanceId) as GameObject;
            if (value == null) throw Stale();
            return value;
        }

        internal static string Path(GameObject gameObject, UnityCliRequestBudget budget)
        {
            if (!budget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
            var value = gameObject.name;
            if (!budget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
            var current = gameObject.transform.parent;
            while (current != null)
            {
                if (!budget.TryConsumeNode() || !budget.TryConsumeGetter() || !budget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                value = current.name + "/" + value;
                current = current.parent;
            }
            return value;
        }

        internal static UnityCliBridge.BridgeCommandException Stale()
        {
            return new UnityCliBridge.BridgeCommandException("E_CONTINUATION_STALE", "The hierarchy continuation no longer identifies the same Unity generation or selector.", "Start a new hierarchy read without cursor.");
        }
    }
}
#endif
