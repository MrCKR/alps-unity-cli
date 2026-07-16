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
        // 修改 TagManager 中的 tag 或用户 layer。
        internal static (bool, int) ApplyTagLayerChange(bool layer, bool add, string name, int? index)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new UnityCliBridge.BridgeCommandException("E_SCHEMA_INVALID", layer ? "Layer name is required." : "Tag name is required.", "Pass a tag or layer name.");
            var asset = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset").FirstOrDefault();
            if (asset == null) throw new UnityCliBridge.BridgeCommandException("E_TAG_MANAGER_NOT_FOUND", "Unity TagManager asset was not found.", "Open the project in Unity and retry.");
            var serialized = new SerializedObject(asset);
            var property = serialized.FindProperty(layer ? "layers" : "tags");
            if (property == null) throw new UnityCliBridge.BridgeCommandException("E_TAG_MANAGER_SCHEMA", "Unity TagManager schema was not recognized.", "Use the Unity Editor Project Settings UI.");
            bool changed;
            int resultIndex = -1;
            if (layer) changed = ApplyLayerChange(property, add, name, index, out resultIndex);
            else changed = ApplyTagChange(property, add, name);
            if (changed) serialized.ApplyModifiedProperties();
            return (changed, resultIndex);
        }

        private static bool ApplyTagChange(SerializedProperty tags, bool add, string name)
        {
            for (var i = 0; i < tags.arraySize; i++) if (tags.GetArrayElementAtIndex(i).stringValue == name) { if (!add) { tags.DeleteArrayElementAtIndex(i); return true; } return false; }
            if (!add) return false;
            tags.InsertArrayElementAtIndex(tags.arraySize); tags.GetArrayElementAtIndex(tags.arraySize - 1).stringValue = name; return true;
        }

        private static bool ApplyLayerChange(SerializedProperty layers, bool add, string name, int? requestedIndex, out int usedIndex)
        {
            usedIndex = -1;
            for (var i = 8; i < Math.Min(32, layers.arraySize); i++) if (layers.GetArrayElementAtIndex(i).stringValue == name) { if (!add) { layers.GetArrayElementAtIndex(i).stringValue = string.Empty; return true; } return false; }
            if (!add) return false;
            var index = requestedIndex ?? -1;
            if (index < 8 || index >= Math.Min(32, layers.arraySize)) for (var i = 8; i < Math.Min(32, layers.arraySize); i++) if (string.IsNullOrEmpty(layers.GetArrayElementAtIndex(i).stringValue)) { index = i; break; }
            if (index < 8 || index >= Math.Min(32, layers.arraySize)) throw new UnityCliBridge.BridgeCommandException("E_LAYER_SLOT_UNAVAILABLE", "No user layer slot is available.", "Remove an unused user layer or pass --index 8..31.");
            layers.GetArrayElementAtIndex(index).stringValue = name; usedIndex = index; return true;
        }
    }
}
#endif
