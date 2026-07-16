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
