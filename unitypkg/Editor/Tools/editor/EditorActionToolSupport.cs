#if UNITY_EDITOR
using System;
using System.Linq;
using UnityEditor;

namespace UnityCli
{
    public sealed class PageInfo
    {
        public long PageSize { get; set; }
        public long Cursor { get; set; }
        public long? NextCursor { get; set; }
        public long Total { get; set; }
    }

    public sealed class ResolutionInfo
    {
        public long Width { get; set; }
        public long Height { get; set; }
        public string FullScreenMode { get; set; }
    }

    internal static class EditorActionToolSupport
    {
        internal sealed class TagLayerChangePlan
        {
            internal SerializedObject Serialized { get; set; }
            internal bool Layer { get; set; }
            internal bool Add { get; set; }
            internal string Name { get; set; }
            internal int Index { get; set; }
            internal bool Changed { get; set; }
        }

        /// <summary>只读解析 TagManager、目标元素和可用 layer slot；commit 前完成全部可预见失败。</summary>
        internal static TagLayerChangePlan PrepareTagLayerChange(bool layer, bool add, string name, int? requestedIndex)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new UnityCliBridge.BridgeCommandException("E_SCHEMA_INVALID", layer ? "Layer name is required." : "Tag name is required.", "Pass a tag or layer name.");
            var asset = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset").FirstOrDefault();
            if (asset == null)
                throw new UnityCliBridge.BridgeCommandException("E_TAG_MANAGER_NOT_FOUND", "Unity TagManager asset was not found.", "Open the project in Unity and retry.");
            var serialized = new SerializedObject(asset);
            var property = serialized.FindProperty(layer ? "layers" : "tags");
            if (property == null)
                throw new UnityCliBridge.BridgeCommandException("E_TAG_MANAGER_SCHEMA", "Unity TagManager schema was not recognized.", "Use the Unity Editor Project Settings UI.");

            var existingIndex = -1;
            var start = layer ? 8 : 0;
            var end = layer ? Math.Min(32, property.arraySize) : property.arraySize;
            for (var i = start; i < end; i++)
            {
                if (property.GetArrayElementAtIndex(i).stringValue == name) { existingIndex = i; break; }
            }
            if (existingIndex >= 0)
                return new TagLayerChangePlan { Serialized = serialized, Layer = layer, Add = add, Name = name, Index = existingIndex, Changed = !add };
            if (!add)
                return new TagLayerChangePlan { Serialized = serialized, Layer = layer, Add = false, Name = name, Index = -1, Changed = false };
            if (!layer)
                return new TagLayerChangePlan { Serialized = serialized, Layer = false, Add = true, Name = name, Index = property.arraySize, Changed = true };

            var index = requestedIndex ?? -1;
            if (requestedIndex.HasValue)
            {
                if (index < 8 || index >= end)
                    throw new UnityCliBridge.BridgeCommandException("E_LAYER_INDEX_INVALID", "Layer index must be between 8 and 31.", "Pass an available user layer index.");
                if (!string.IsNullOrEmpty(property.GetArrayElementAtIndex(index).stringValue))
                    throw new UnityCliBridge.BridgeCommandException("E_LAYER_SLOT_OCCUPIED", "Layer slot " + index + " is already occupied.", "Pass an empty user layer index or omit index.");
            }
            else
            {
                for (var i = 8; i < end; i++)
                {
                    if (string.IsNullOrEmpty(property.GetArrayElementAtIndex(i).stringValue)) { index = i; break; }
                }
            }
            if (index < 8 || index >= end)
                throw new UnityCliBridge.BridgeCommandException("E_LAYER_SLOT_UNAVAILABLE", "No user layer slot is available.", "Remove an unused user layer or pass an empty index from 8 through 31.");
            return new TagLayerChangePlan { Serialized = serialized, Layer = true, Add = true, Name = name, Index = index, Changed = true };
        }

        /// <summary>执行已验证 plan，并重新读取 TagManager 证明请求状态。</summary>
        internal static (bool, int) ApplyTagLayerChange(TagLayerChangePlan plan)
        {
            var property = plan.Serialized.FindProperty(plan.Layer ? "layers" : "tags");
            if (plan.Changed)
            {
                if (plan.Layer)
                {
                    property.GetArrayElementAtIndex(plan.Index).stringValue = plan.Add ? plan.Name : string.Empty;
                }
                else if (plan.Add)
                {
                    property.InsertArrayElementAtIndex(property.arraySize);
                    property.GetArrayElementAtIndex(property.arraySize - 1).stringValue = plan.Name;
                }
                else
                {
                    property.DeleteArrayElementAtIndex(plan.Index);
                }
                plan.Serialized.ApplyModifiedProperties();
            }

            plan.Serialized.Update();
            property = plan.Serialized.FindProperty(plan.Layer ? "layers" : "tags");
            var present = false;
            for (var i = plan.Layer ? 8 : 0; i < (plan.Layer ? Math.Min(32, property.arraySize) : property.arraySize); i++)
            {
                if (property.GetArrayElementAtIndex(i).stringValue == plan.Name) { present = true; break; }
            }
            if (present != plan.Add)
                throw new UnityCliBridge.BridgeCommandException("E_TAG_LAYER_STATE_MISMATCH", "TagManager readback did not match the requested state for " + plan.Name + ".", "Inspect Project Settings before retrying.");
            return (plan.Changed, plan.Layer ? plan.Index : -1);
        }
    }
}
#endif
