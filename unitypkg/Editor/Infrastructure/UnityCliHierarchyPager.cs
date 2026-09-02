#if UNITY_EDITOR
using System;
using System.Globalization;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityCli
{
    /// <summary>基于 next GameObject instance id 的前序游标；续页只做 O(depth) 定位，不重扫旧页面。</summary>
    internal static class UnityCliHierarchyPager
    {
        internal static GameObject First(Scene scene, GameObject boundaryParent)
        {
            if (boundaryParent != null) return boundaryParent.transform.childCount == 0 ? null : boundaryParent.transform.GetChild(0).gameObject;
            var roots = scene.GetRootGameObjects();
            return roots.Length == 0 ? null : roots[0];
        }

        internal static GameObject Next(GameObject current, Scene scene, GameObject boundaryParent, int maxDepth)
        {
            if (current == null) return null;
            var relativeDepth = Depth(current.transform, boundaryParent == null ? null : boundaryParent.transform);
            if (relativeDepth < maxDepth && current.transform.childCount > 0) return current.transform.GetChild(0).gameObject;
            var cursor = current.transform;
            var boundary = boundaryParent == null ? null : boundaryParent.transform;
            while (cursor != null && cursor != boundary)
            {
                var parent = cursor.parent;
                if (parent != null)
                {
                    var sibling = cursor.GetSiblingIndex() + 1;
                    if (sibling < parent.childCount) return parent.GetChild(sibling).gameObject;
                    cursor = parent;
                    continue;
                }
                var roots = scene.GetRootGameObjects();
                var rootSibling = cursor.GetSiblingIndex() + 1;
                return rootSibling < roots.Length ? roots[rootSibling] : null;
            }
            return null;
        }

        internal static GameObject FirstLoadedSceneObject()
        {
            for (var sceneIndex = 0; sceneIndex < SceneManager.sceneCount; sceneIndex++)
            {
                var scene = SceneManager.GetSceneAt(sceneIndex);
                if (!scene.isLoaded) continue;
                var first = First(scene, null);
                if (first != null) return first;
            }
            return null;
        }

        internal static GameObject NextAcrossLoadedScenes(GameObject current)
        {
            if (current == null) return null;
            var scene = current.scene;
            var next = Next(current, scene, null, int.MaxValue);
            if (next != null) return next;
            var start = -1;
            for (var index = 0; index < SceneManager.sceneCount; index++) if (SceneManager.GetSceneAt(index).handle == scene.handle) { start = index; break; }
            for (var sceneIndex = start + 1; sceneIndex < SceneManager.sceneCount; sceneIndex++)
            {
                var candidate = SceneManager.GetSceneAt(sceneIndex);
                if (!candidate.isLoaded) continue;
                var first = First(candidate, null);
                if (first != null) return first;
            }
            return null;
        }

        internal static int Depth(Transform value, Transform boundary)
        {
            var depth = 0;
            var current = value.parent;
            while (current != null && current != boundary) { depth++; current = current.parent; }
            return depth;
        }

        internal static string Cursor(string scope, string generation, string phase, GameObject next)
        {
            if (next == null) return null;
            return UnityCliContinuationToken.Create(scope, generation, (phase ?? "scene") + ":" + next.GetInstanceID().ToString(CultureInfo.InvariantCulture));
        }

        internal static GameObject ResolveCursor(string token, string scope, string generation, out string phase)
        {
            phase = null;
            string position;
            if (!UnityCliContinuationToken.TryParse(token, scope, generation, out position)) throw Stale();
            var separator = position.IndexOf(':');
            int instanceId;
            if (separator <= 0 || !int.TryParse(position.Substring(separator + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out instanceId)) throw Stale();
            phase = position.Substring(0, separator);
            var value = EditorUtility.InstanceIDToObject(instanceId) as GameObject;
            if (value == null) throw Stale();
            return value;
        }

        internal static string Path(GameObject gameObject, UnityCliRequestBudget budget)
        {
            var value = gameObject.name;
            var current = gameObject.transform.parent;
            while (current != null)
            {
                if (!budget.TryConsumeNode() || !budget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
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
