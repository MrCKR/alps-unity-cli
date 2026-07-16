#if UNITY_EDITOR
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEngine;

namespace UnityCli.ProBuilder
{
    /// <summary>
    /// ProBuilder Tool 的场景对象解析 helper。
    /// </summary>
    internal static class ProBuilderInput
    {
        /// <summary>
        /// 由两个顶点索引表示的边。
        /// </summary>
        public struct EdgePair
        {
            public int a;
            public int b;
        }

        /// <summary>
        /// 按 instance id、层级路径或名称解析目标对象。
        /// </summary>
        public static GameObject ResolveTarget(string target)
        {
            if (string.IsNullOrEmpty(target)) return null;

            int instanceId;
            if (int.TryParse(target, NumberStyles.Integer, CultureInfo.InvariantCulture, out instanceId))
            {
                var go = EditorUtility.InstanceIDToObject(instanceId) as GameObject;
                if (go != null) return go;
            }

            var found = GameObject.Find(target);
            if (found != null) return found;

            if (target.IndexOf('/') >= 0)
            {
                found = FindByHierarchyPath(target);
                if (found != null) return found;
            }

            for (var i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
            {
                var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects())
                {
                    if (root.name == target) return root;
                    var child = FindDescendant(root.transform, target);
                    if (child != null) return child.gameObject;
                }
            }

            return null;
        }

        /// <summary>
        /// 按 instance id、层级路径或名称解析多个目标对象。
        /// </summary>
        public static GameObject[] ResolveTargets(IEnumerable<string> targets)
        {
            if (targets == null) return new GameObject[0];

            var results = new List<GameObject>();
            foreach (var target in targets)
            {
                var go = ResolveTarget(target);
                if (go != null) results.Add(go);
            }
            return results.ToArray();
        }

        private static GameObject FindByHierarchyPath(string path)
        {
            var parts = path.Trim('/').Split('/');
            if (parts.Length == 0) return null;

            for (var i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
            {
                var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects())
                {
                    if (root.name != parts[0]) continue;
                    var current = root.transform;
                    var matched = true;
                    for (var partIndex = 1; partIndex < parts.Length; partIndex++)
                    {
                        current = FindDirectChild(current, parts[partIndex]);
                        if (current == null)
                        {
                            matched = false;
                            break;
                        }
                    }
                    if (matched) return current.gameObject;
                }
            }

            return null;
        }

        private static Transform FindDirectChild(Transform parent, string name)
        {
            for (var i = 0; i < parent.childCount; i++)
            {
                var child = parent.GetChild(i);
                if (child.name == name) return child;
            }
            return null;
        }

        private static GameObject FindDescendant(Transform parent, string name)
        {
            for (var i = 0; i < parent.childCount; i++)
            {
                var child = parent.GetChild(i);
                if (child.name == name) return child.gameObject;
                var found = FindDescendant(child, name);
                if (found != null) return found;
            }
            return null;
        }
    }
}
#endif
