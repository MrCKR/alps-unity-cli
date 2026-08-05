#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityCli
{
    /// <summary>统一处理场景实例的 Edit/Play Mode 变更。</summary>
    public static class UnityCliSceneMutation
    {
        private static bool IsPlaying => EditorApplication.isPlaying;

        /// <summary>为一个 Tool 请求建立独立 Undo group，避免跨 Bridge 请求合并。</summary>
        public static int BeginUndoGroup(string name)
        {
            if (IsPlaying) return -1;
            Undo.IncrementCurrentGroup();
            var group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(name);
            return group;
        }

        /// <summary>折叠并关闭当前 Tool 的 Undo group；下一请求不能继续写入该 entry。</summary>
        public static void EndUndoGroup(int group)
        {
            if (group < 0) return;
            Undo.CollapseUndoOperations(group);
            Undo.IncrementCurrentGroup();
        }

        // Edit Mode 记录 Undo；Play Mode 仅修改当前运行时实例。
        public static void RecordObject(UnityEngine.Object target, string name)
        {
            if (!IsPlaying && target != null) Undo.RecordObject(target, name);
        }

        // Edit Mode 注册创建 Undo；Play Mode 创建物只属于当前会话。
        public static void RegisterCreatedObject(GameObject target, string name)
        {
            if (!IsPlaying && target != null) Undo.RegisterCreatedObjectUndo(target, name);
        }

        public static T AddComponent<T>(GameObject target) where T : Component
        {
            return IsPlaying ? target.AddComponent<T>() : Undo.AddComponent<T>(target);
        }

        public static Component AddComponent(GameObject target, Type type)
        {
            return IsPlaying ? target.AddComponent(type) : Undo.AddComponent(target, type);
        }

        // Bridge 命令必须在返回前完成销毁；PlayMode 仅同步销毁当前场景实例，默认禁止销毁持久化资产。
        public static void DestroyObject(UnityEngine.Object target)
        {
            if (target == null) return;
            if (IsPlaying) UnityEngine.Object.DestroyImmediate(target);
            else Undo.DestroyObjectImmediate(target);
        }

        public static void SetParent(Transform target, Transform parent, string name, bool worldPositionStays)
        {
            if (IsPlaying) target.SetParent(parent, worldPositionStays);
            else
            {
                Undo.SetTransformParent(target, parent, name);
                target.SetParent(parent, worldPositionStays);
            }
        }

        public static void MarkSceneDirty(Scene scene)
        {
            if (!IsPlaying && scene.IsValid()) EditorSceneManager.MarkSceneDirty(scene);
        }

        public static void MarkSceneObjectDirty(UnityEngine.Object target)
        {
            if (!IsPlaying && target != null) EditorUtility.SetDirty(target);
        }
    }
}
#endif
