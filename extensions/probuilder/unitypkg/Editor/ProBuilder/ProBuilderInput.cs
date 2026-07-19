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
        /// 按 instance id、完整层级路径或名称唯一解析目标；存在多个同名候选时拒绝猜测。
        /// </summary>
        public static GameObject ResolveTarget(string target)
        {
            if (string.IsNullOrWhiteSpace(target)) return null;

            int instanceId;
            if (int.TryParse(target, NumberStyles.Integer, CultureInfo.InvariantCulture, out instanceId))
                return EditorUtility.InstanceIDToObject(instanceId) as GameObject;

            var candidates = new List<GameObject>();
            foreach (var go in EnumerateLoadedObjects())
            {
                if (target.IndexOf('/') >= 0 ? HierarchyPath(go) == target.Trim('/') : go.name == target)
                    candidates.Add(go);
            }
            return candidates.Count == 1 ? candidates[0] : null;
        }

        /// <summary>
        /// 批量解析必须全部唯一且互不重复；任何失败都不返回部分结果，并保持请求顺序。
        /// </summary>
        public static GameObject[] ResolveTargets(IEnumerable<string> targets)
        {
            if (targets == null) return new GameObject[0];

            var results = new List<GameObject>();
            var seen = new HashSet<int>();
            foreach (var target in targets)
            {
                var go = ResolveTarget(target);
                if (go == null || !seen.Add(go.GetInstanceID())) return new GameObject[0];
                results.Add(go);
            }
            return results.ToArray();
        }

        private static IEnumerable<GameObject> EnumerateLoadedObjects()
        {
            for (var i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
            {
                var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects())
                {
                    yield return root;
                    foreach (var child in root.GetComponentsInChildren<Transform>(true))
                        if (child != root.transform) yield return child.gameObject;
                }
            }
        }

        private static string HierarchyPath(GameObject go)
        {
            var names = new Stack<string>();
            for (var current = go.transform; current != null; current = current.parent)
                names.Push(current.name);
            return string.Join("/", names.ToArray());
        }
    }
}
#endif
