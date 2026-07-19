#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityCli.ProBuilder
{
    /// <summary>
    /// Reflection-based optional dependency helper for the ProBuilder package.
    /// All ProBuilder types are resolved via reflection so the CLI extension
    /// compiles and runs even when com.unity.probuilder is not installed.
    /// When the package is absent, tools report unavailable with nextActions.
    /// </summary>
    internal static class ProBuilderReflection
    {
        // 未加载到 optional assembly 时不缓存 null；domain reload 完成后下一次调用会重新解析。
        private sealed class RetryingType
        {
            private readonly string fullName;
            private readonly object gate = new object();
            private Type value;

            public RetryingType(string fullName)
            {
                this.fullName = fullName;
            }

            public Type Value
            {
                get
                {
                    if (value != null) return value;
                    lock (gate)
                    {
                        if (value == null) value = FindType(fullName);
                        return value;
                    }
                }
            }
        }

        // ─── Cached successful type lookups ─────────────────────────
        private static readonly RetryingType ProBuilderMeshType = new RetryingType("UnityEngine.ProBuilder.ProBuilderMesh");
        private static readonly RetryingType ShapeGeneratorType = new RetryingType("UnityEngine.ProBuilder.ShapeGenerator");
        private static readonly RetryingType SmoothingType = new RetryingType("UnityEngine.ProBuilder.Smoothing");
        private static readonly RetryingType MeshValidationType = new RetryingType("UnityEngine.ProBuilder.MeshOperations.MeshValidation");
        private static readonly RetryingType FaceType = new RetryingType("UnityEngine.ProBuilder.Face");
        private static readonly RetryingType EdgeType = new RetryingType("UnityEngine.ProBuilder.Edge");
        private static readonly RetryingType PivotType = new RetryingType("UnityEngine.ProBuilder.PivotType");
        private static readonly RetryingType ExtrudeMethodType = new RetryingType("UnityEngine.ProBuilder.ExtrudeMethod");
        private static readonly RetryingType ShapeTypeType = new RetryingType("UnityEngine.ProBuilder.ShapeType");
        private static readonly RetryingType ExtrudeElementsType = new RetryingType("UnityEngine.ProBuilder.MeshOperations.ExtrudeElements");
        private static readonly RetryingType BevelType = new RetryingType("UnityEngine.ProBuilder.MeshOperations.Bevel");
        private static readonly RetryingType DeleteElementsType = new RetryingType("UnityEngine.ProBuilder.MeshOperations.DeleteElements");
        private static readonly RetryingType AppendElementsType = new RetryingType("UnityEngine.ProBuilder.MeshOperations.AppendElements");
        private static readonly RetryingType ConnectElementsType = new RetryingType("UnityEngine.ProBuilder.MeshOperations.ConnectElements");
        private static readonly RetryingType MergeElementsType = new RetryingType("UnityEngine.ProBuilder.MeshOperations.MergeElements");
        private static readonly RetryingType SubdivisionType = new RetryingType("UnityEngine.ProBuilder.MeshOperations.Subdivision");
        private static readonly RetryingType CombineMeshesType = new RetryingType("UnityEngine.ProBuilder.MeshOperations.CombineMeshes");
        private static readonly RetryingType VertexEditingType = new RetryingType("UnityEngine.ProBuilder.MeshOperations.VertexEditing");
        private static readonly RetryingType MeshImporterType = new RetryingType("UnityEngine.ProBuilder.MeshOperations.MeshImporter");
        private static readonly RetryingType MeshImportSettingsType = new RetryingType("UnityEngine.ProBuilder.MeshOperations.MeshImportSettings");
        private static readonly RetryingType ElementSelectionType = new RetryingType("UnityEngine.ProBuilder.MeshOperations.ElementSelection");
        private static readonly RetryingType PivotLocationType = new RetryingType("UnityEngine.ProBuilder.PivotLocation");

        /// <summary>
        /// Whether the ProBuilder package types are available at runtime.
        /// </summary>
        public static bool IsAvailable
        {
            get { return ProBuilderMeshType.Value != null; }
        }

        /// <summary>
        /// Returns the ProBuilderMesh type or null if the package is missing.
        /// </summary>
        public static Type GetProBuilderMeshType() { return ProBuilderMeshType.Value; }

        /// <summary>返回 Face type，供逐面 UV 操作使用。</summary>
        public static Type GetFaceType() { return FaceType.Value; }

        // ─── Core reflection operations ─────────────────────────────

        /// <summary>
        /// Get the ProBuilderMesh component from a GameObject via reflection.
        /// Returns null if the component is not present or the package is missing.
        /// </summary>
        public static object GetProBuilderMesh(GameObject go)
        {
            var type = ProBuilderMeshType.Value;
            if (type == null || go == null) return null;
            return go.GetComponent(type);
        }

        /// <summary>
        /// Add a ProBuilderMesh component to a GameObject via reflection.
        /// </summary>
        public static object AddProBuilderMesh(GameObject go)
        {
            var type = ProBuilderMeshType.Value;
            if (type == null || go == null) return null;
            try
            {
                var comp = go.AddComponent(type);
                return comp;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Check if a GameObject has a ProBuilderMesh component.
        /// </summary>
        public static bool HasProBuilderMesh(GameObject go)
        {
            return GetProBuilderMesh(go) != null;
        }

        /// <summary>通过 ProBuilder 5.x ExtrudeElements 挤出指定面。</summary>
        public static bool Extrude(object mesh, Array faces, float distance, string method)
        {
            var type = ExtrudeElementsType.Value;
            if (type == null || mesh == null || faces == null || faces.Length == 0) return false;
            try
            {
                var faceEnumerableType = typeof(IEnumerable<>).MakeGenericType(FaceType.Value);
                var methodInfo = type.GetMethod("Extrude", BindingFlags.Static | BindingFlags.Public, null,
                    new[] { ProBuilderMeshType.Value, faceEnumerableType, ExtrudeMethodType.Value, typeof(float) }, null);
                if (methodInfo == null) return false;
                var methodName = string.Equals(method, "faceNormal", StringComparison.OrdinalIgnoreCase) ? "FaceNormal" : "VertexNormal";
                var extrudeMethod = Enum.Parse(ExtrudeMethodType.Value, methodName, true);
                return methodInfo.Invoke(null, new object[] { mesh, faces, extrudeMethod, distance }) is Array result && result.Length > 0;
            }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// Call ShapeGenerator.CreateShape via reflection to create a ProBuilder primitive.
        /// Returns the created GameObject, or null on failure.
        /// </summary>
        public static GameObject CreateShape(string shapeType, Vector3 position, Quaternion rotation)
        {
            var type = ShapeGeneratorType.Value;
            if (type == null) return null;
            try
            {
                // ProBuilder 5.x 返回 ProBuilderMesh，并要求显式 PivotLocation。
                var shapeTypeEnum = ParseShapeType(shapeType);
                if (shapeTypeEnum != null && ShapeTypeType.Value != null && PivotLocationType.Value != null)
                {
                    var methodInfo = type.GetMethod("CreateShape", new[] { ShapeTypeType.Value, PivotLocationType.Value });
                    if (methodInfo != null)
                    {
                        var pivot = Enum.Parse(PivotLocationType.Value, "Center", true);
                        var created = methodInfo.Invoke(null, new[] { shapeTypeEnum, pivot });
                        var go = created as GameObject ?? (created as Component)?.gameObject;
                        if (go != null)
                        {
                            go.transform.position = position;
                            go.transform.rotation = rotation;
                        }
                        return go;
                    }
                }

                return null;
            }
            catch (Exception) { }
            return null;
        }

        /// <summary>
        /// Parse a string shape name into the ShapeType enum value via reflection.
        /// Returns null if the type cannot be resolved.
        /// </summary>
        public static object ParseShapeType(string shapeName)
        {
            var shapeTypeEnumType = ShapeTypeType.Value;
            if (shapeTypeEnumType == null || string.IsNullOrEmpty(shapeName)) return null;
            try
            {
                // Normalize
                var normalized = shapeName.Trim();
                // Map common aliases
                switch (normalized.ToLowerInvariant())
                {
                    case "cube":
                    case "box":
                        return Enum.Parse(shapeTypeEnumType, "Cube");
                    case "sphere":
                        return Enum.Parse(shapeTypeEnumType, "Sphere");
                    case "cylinder":
                        return Enum.Parse(shapeTypeEnumType, "Cylinder");
                    case "plane":
                    case "quad":
                        return Enum.Parse(shapeTypeEnumType, "Plane");
                    case "cone":
                        return Enum.Parse(shapeTypeEnumType, "Cone");
                    case "torus":
                        return Enum.Parse(shapeTypeEnumType, "Torus");
                    case "pipe":
                        return Enum.Parse(shapeTypeEnumType, "Pipe");
                    case "arch":
                        return Enum.Parse(shapeTypeEnumType, "Arch");
                    case "stair":
                        return Enum.Parse(shapeTypeEnumType, "Stair");
                    case "curvedstair":
                    case "curved_stair":
                        return Enum.Parse(shapeTypeEnumType, "CurvedStair");
                    case "door":
                        return Enum.Parse(shapeTypeEnumType, "Door");
                    case "prism":
                        return Enum.Parse(shapeTypeEnumType, "Prism");
                    default:
                        // Try direct enum parse
                        return Enum.Parse(shapeTypeEnumType, normalized, true);
                }
            }
            catch (Exception) { return null; }
        }

        /// <summary>通过 ProBuilder 5.x DeleteElements 删除面。</summary>
        public static bool DeleteFaces(object mesh, int[] faceIndices)
        {
            var type = DeleteElementsType.Value;
            if (type == null || mesh == null || faceIndices == null || faceIndices.Length == 0) return false;
            try
            {
                var methodInfo = type.GetMethod("DeleteFaces", BindingFlags.Static | BindingFlags.Public, null,
                    new[] { ProBuilderMeshType.Value, typeof(IList<int>) }, null);
                return methodInfo != null && methodInfo.Invoke(null, new object[] { mesh, faceIndices.ToList() }) is int[];
            }
            catch (Exception) { return false; }
        }

        /// <summary>通过 ProBuilder 5.x MergeElements 合并面。</summary>
        public static bool MergeFaces(object mesh, int[] faceIndices)
        {
            var type = MergeElementsType.Value;
            var faces = GetFacesByIndices(mesh, faceIndices);
            if (type == null || faces == null || faces.Length < 2) return false;
            try
            {
                var faceEnumerableType = typeof(IEnumerable<>).MakeGenericType(FaceType.Value);
                var methodInfo = type.GetMethod("Merge", BindingFlags.Static | BindingFlags.Public, null,
                    new[] { ProBuilderMeshType.Value, faceEnumerableType }, null);
                return methodInfo != null && methodInfo.Invoke(null, new object[] { mesh, faces }) != null;
            }
            catch (Exception) { return false; }
        }

        /// <summary>通过 ProBuilder 5.x Subdivision 细分面。</summary>
        public static bool SubdivideFaces(object mesh, int[] faceIndices)
        {
            var type = SubdivisionType.Value;
            var faces = GetFacesByIndices(mesh, faceIndices);
            if (type == null || faces == null || faces.Length == 0) return false;
            try
            {
                var faceList = ToTypedFaceList(faces);
                var faceListType = typeof(IList<>).MakeGenericType(FaceType.Value);
                var methodInfo = type.GetMethod("Subdivide", BindingFlags.Static | BindingFlags.Public, null,
                    new[] { ProBuilderMeshType.Value, faceListType }, null);
                return methodInfo != null && methodInfo.Invoke(null, new object[] { mesh, faceList }) is Array result && result.Length > 0;
            }
            catch (Exception) { return false; }
        }

        /// <summary>通过 ProBuilder 5.x Face.Reverse 翻转面法线。</summary>
        public static bool FlipFaces(object mesh, int[] faceIndices)
        {
            var faces = GetFacesByIndices(mesh, faceIndices);
            if (mesh == null || faces == null || faces.Length == 0) return false;
            try
            {
                var reverse = FaceType.Value?.GetMethod("Reverse", Type.EmptyTypes);
                if (reverse == null) return false;
                foreach (var face in faces) reverse.Invoke(face, null);
                return true;
            }
            catch (Exception) { return false; }
        }

        /// <summary>通过 ProBuilder 5.x Face.smoothingGroup 设置平滑组。</summary>
        public static bool ApplySmoothingGroup(object mesh, int[] faceIndices, int smoothingGroup)
        {
            var faces = GetFacesByIndices(mesh, faceIndices);
            if (mesh == null || faces == null || faces.Length == 0) return false;
            try
            {
                var property = FaceType.Value?.GetProperty("smoothingGroup");
                if (property == null || !property.CanWrite) return false;
                foreach (var face in faces) property.SetValue(face, smoothingGroup);
                return true;
            }
            catch (Exception) { return false; }
        }

        /// <summary>通过 ProBuilder 5.x Smoothing.ApplySmoothingGroups 自动分组。</summary>
        public static bool ApplySmoothingGroups(object mesh, int[] faceIndices, float angleThreshold)
        {
            var type = SmoothingType.Value;
            var faces = GetFacesByIndices(mesh, faceIndices);
            if (type == null || mesh == null || faces == null || faces.Length == 0) return false;
            try
            {
                var faceEnumerableType = typeof(IEnumerable<>).MakeGenericType(FaceType.Value);
                var methodInfo = type.GetMethod("ApplySmoothingGroups", BindingFlags.Static | BindingFlags.Public, null,
                    new[] { ProBuilderMeshType.Value, faceEnumerableType, typeof(float) }, null);
                if (methodInfo == null) return false;
                methodInfo.Invoke(null, new object[] { mesh, faces, angleThreshold });
                return true;
            }
            catch (Exception) { return false; }
        }

        /// <summary>统计包含退化三角形的面数量。</summary>
        public static int CountDegenerateFaces(object mesh)
        {
            var faces = GetAllFaces(mesh);
            if (MeshValidationType.Value == null || mesh == null || faces == null) return -1;
            try
            {
                var method = MeshValidationType.Value.GetMethod("ContainsDegenerateTriangles", BindingFlags.Static | BindingFlags.Public, null,
                    new[] { ProBuilderMeshType.Value, FaceType.Value }, null);
                if (method == null) return -1;
                var count = 0;
                foreach (var face in faces)
                {
                    if (method.Invoke(null, new[] { mesh, face }) is bool contains && contains) count++;
                }
                return count;
            }
            catch (Exception) { return -1; }
        }

        /// <summary>按 face indexes 统计未被任何面引用的顶点。</summary>
        public static int CountUnusedVertices(object mesh)
        {
            var faces = GetAllFaces(mesh);
            var vertexCount = GetVertexCount(mesh);
            if (mesh == null || faces == null || vertexCount < 0) return -1;
            try
            {
                var indexesProperty = FaceType.Value?.GetProperty("indexes");
                if (indexesProperty == null) return -1;
                var referenced = new HashSet<int>();
                foreach (var face in faces)
                {
                    if (!(indexesProperty.GetValue(face) is System.Collections.IEnumerable indexes)) return -1;
                    foreach (var index in indexes) referenced.Add((int)index);
                }
                return Math.Max(0, vertexCount - referenced.Count);
            }
            catch (Exception) { return -1; }
        }

        // ─── Mesh property accessors via reflection ──────────────────

        /// <summary>
        /// Get the face count from a ProBuilderMesh via reflection.
        /// </summary>
        public static int GetFaceCount(object mesh)
        {
            if (mesh == null) return -1;
            try
            {
                return ProBuilderMeshType.Value?.GetProperty("faceCount")?.GetValue(mesh) is int count ? count : -1;
            }
            catch (Exception) { return -1; }
        }

        /// <summary>读取 ProBuilder 5.x vertexCount。</summary>
        public static int GetVertexCount(object mesh)
        {
            if (mesh == null) return -1;
            try
            {
                return ProBuilderMeshType.Value?.GetProperty("vertexCount")?.GetValue(mesh) is int count ? count : -1;
            }
            catch (Exception) { return -1; }
        }

        /// <summary>读取 ProBuilder 5.x edgeCount。</summary>
        public static int GetEdgeCount(object mesh)
        {
            if (mesh == null) return -1;
            try
            {
                return ProBuilderMeshType.Value?.GetProperty("edgeCount")?.GetValue(mesh) is int count ? count : -1;
            }
            catch (Exception) { return -1; }
        }

        /// <summary>
        /// Get faces by indices from a ProBuilderMesh via reflection.
        /// Returns an Array of Face objects or null.
        /// </summary>
        public static Array GetFacesByIndices(object mesh, int[] indices)
        {
            if (mesh == null || indices == null) return null;
            try
            {
                var facesProp = ProBuilderMeshType.Value?.GetProperty("faces");
                if (facesProp == null) return null;
                var allFaces = facesProp.GetValue(mesh) as System.Collections.IList;
                if (allFaces == null) return null;
                var faceType = FaceType.Value;
                if (faceType == null) return null;
                var result = Array.CreateInstance(faceType, indices.Length);
                for (var i = 0; i < indices.Length; i++)
                {
                    if (indices[i] < 0 || indices[i] >= allFaces.Count) return null;
                    result.SetValue(allFaces[indices[i]], i);
                }
                return result;
            }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// Get all faces from a ProBuilderMesh via reflection.
        /// </summary>
        public static Array GetAllFaces(object mesh)
        {
            if (mesh == null) return null;
            try
            {
                var faces = ProBuilderMeshType.Value?.GetProperty("faces")?.GetValue(mesh) as System.Collections.IList;
                if (faces == null || FaceType.Value == null) return null;
                var result = Array.CreateInstance(FaceType.Value, faces.Count);
                faces.CopyTo(result, 0);
                return result;
            }
            catch (Exception) { return null; }
        }

        /// <summary>通过 ProBuilder 5.x 精确签名重建并刷新网格。</summary>
        public static bool RefreshMesh(object mesh)
        {
            if (mesh == null) return false;
            try
            {
                var meshType = ProBuilderMeshType.Value;
                var toMesh = meshType?.GetMethod("ToMesh", new[] { typeof(MeshTopology) });
                var refresh = meshType?.GetMethod("Refresh", new[] { FindType("UnityEngine.ProBuilder.RefreshMask") });
                if (toMesh == null || refresh == null) return false;
                toMesh.Invoke(mesh, new object[] { MeshTopology.Triangles });
                var refreshMask = Enum.Parse(refresh.GetParameters()[0].ParameterType, "All", true);
                refresh.Invoke(mesh, new[] { refreshMask });
                return true;
            }
            catch (Exception) { return false; }
        }

        /// <summary>通过 ProBuilder 5.x MeshImporter(GameObject) 导入普通 Mesh。</summary>
        public static object ConvertToProBuilderMesh(GameObject go)
        {
            if (go == null || MeshImporterType.Value == null || MeshImportSettingsType.Value == null) return null;
            if (GetProBuilderMesh(go) != null) return null;
            UnityEngine.Object component = null;
            try
            {
                var constructor = MeshImporterType.Value.GetConstructor(new[] { typeof(GameObject) });
                var import = MeshImporterType.Value.GetMethod("Import", new[] { MeshImportSettingsType.Value });
                if (constructor == null || import == null) return null;
                var importer = constructor.Invoke(new object[] { go });
                component = GetProBuilderMesh(go) as UnityEngine.Object;
                if (component == null) return null;
                import.Invoke(importer, new object[] { null });
                Undo.RegisterCreatedObjectUndo(component, "ProBuilder Convert to ProBuilder");
                return component;
            }
            catch (Exception)
            {
                if (component != null) UnityEngine.Object.DestroyImmediate(component);
                return null;
            }
        }

        // ─── Edge resolution helpers ────────────────────────────────────

        /// <summary>
        /// Create an Edge object from two vertex indices via reflection.
        /// </summary>
        public static object CreateEdge(int a, int b)
        {
            var edgeType = EdgeType.Value;
            if (edgeType == null) return null;
            try
            {
                var ctor = edgeType.GetConstructor(new[] { typeof(int), typeof(int) });
                return ctor?.Invoke(new object[] { a, b });
            }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// Get field/property value 'a' from an Edge object.
        /// </summary>
        public static int GetEdgeVertexA(object edge)
        {
            if (edge == null) return -1;
            var edgeType = EdgeType.Value;
            if (edgeType == null) return -1;
            var f = edgeType.GetField("a");
            if (f != null) return (int)f.GetValue(edge);
            var p = edgeType.GetProperty("a");
            return p != null ? (int)p.GetValue(edge) : -1;
        }

        /// <summary>
        /// Get field/property value 'b' from an Edge object.
        /// </summary>
        public static int GetEdgeVertexB(object edge)
        {
            if (edge == null) return -1;
            var edgeType = EdgeType.Value;
            if (edgeType == null) return -1;
            var f = edgeType.GetField("b");
            if (f != null) return (int)f.GetValue(edge);
            var p = edgeType.GetProperty("b");
            return p != null ? (int)p.GetValue(edge) : -1;
        }

        /// <summary>
        /// Collect unique (deduplicated) edges from the mesh.
        /// Edges shared between faces appear only once.
        /// </summary>
        public static List<object> CollectUniqueEdges(object mesh)
        {
            var allFaces = GetAllFaces(mesh);
            if (allFaces == null) return new List<object>();
            var uniqueEdges = new List<object>();
            var edgeSet = new HashSet<(int, int)>();
            var faceType = FaceType.Value;
            if (faceType == null) return new List<object>();
            var edgesProp = faceType.GetProperty("edges");
            if (edgesProp == null) return new List<object>();

            // Build shared vertex lookup for correct deduplication
            var sharedLookup = BuildSharedVertexLookup(mesh);

            foreach (var face in allFaces)
            {
                var faceEdges = edgesProp.GetValue(face) as System.Collections.IList;
                if (faceEdges == null) continue;
                foreach (var edge in faceEdges)
                {
                    int a = GetEdgeVertexA(edge);
                    int b = GetEdgeVertexB(edge);
                    int sa = sharedLookup != null && sharedLookup.ContainsKey(a) ? sharedLookup[a] : a;
                    int sb = sharedLookup != null && sharedLookup.ContainsKey(b) ? sharedLookup[b] : b;
                    var key = (Math.Min(sa, sb), Math.Max(sa, sb));
                    if (edgeSet.Add(key))
                        uniqueEdges.Add(edge);
                }
            }
            return uniqueEdges;
        }

        /// <summary>
        /// Build a shared vertex lookup: maps vertex index -> shared vertex group index.
        /// </summary>
        private static Dictionary<int, int> BuildSharedVertexLookup(object mesh)
        {
            var meshType = ProBuilderMeshType.Value;
            if (meshType == null) return null;
            try
            {
                var sharedVerticesProp = meshType.GetProperty("sharedVertices");
                var sharedVertices = sharedVerticesProp?.GetValue(mesh) as System.Collections.IList;
                if (sharedVertices == null) return null;
                var lookup = new Dictionary<int, int>();
                for (int groupIdx = 0; groupIdx < sharedVertices.Count; groupIdx++)
                {
                    var group = sharedVertices[groupIdx] as System.Collections.IEnumerable;
                    if (group == null) continue;
                    foreach (object vertIdx in group)
                        lookup[(int)vertIdx] = groupIdx;
                }
                return lookup;
            }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// Resolve edges from int[] edgeIndices (indices into the unique edge list) or
        /// vertex pair objects [{a,b},...]. Returns a typed Edge[] array.
        /// </summary>
        public static Array ResolveEdges(object mesh, int[] edgeIndices, ProBuilderInput.EdgePair[] edgePairs)
        {
            var edgeType = EdgeType.Value;
            if (edgeType == null) return null;
            var edgeList = new List<object>();

            if (edgePairs != null && edgePairs.Length > 0)
            {
                foreach (var pair in edgePairs)
                {
                    var edge = CreateEdge(pair.a, pair.b);
                    if (edge != null) edgeList.Add(edge);
                }
            }
            else if (edgeIndices != null && edgeIndices.Length > 0)
            {
                var allEdges = CollectUniqueEdges(mesh);
                foreach (int idx in edgeIndices)
                {
                    if (idx >= 0 && idx < allEdges.Count)
                        edgeList.Add(allEdges[idx]);
                }
            }

            var edgeArray = Array.CreateInstance(edgeType, edgeList.Count);
            for (int i = 0; i < edgeList.Count; i++)
                edgeArray.SetValue(edgeList[i], i);
            return edgeArray;
        }

        /// <summary>
        /// Convert a Face[] array to a typed List&lt;Face&gt; for APIs that require IList&lt;Face&gt;.
        /// </summary>
        public static System.Collections.IList ToTypedFaceList(Array faces)
        {
            var faceType = FaceType.Value;
            if (faceType == null || faces == null) return null;
            var faceListType = typeof(List<>).MakeGenericType(faceType);
            var faceList = Activator.CreateInstance(faceListType) as System.Collections.IList;
            foreach (var face in faces) faceList.Add(face);
            return faceList;
        }

        /// <summary>通过 ProBuilder 5.x ElementSelection 执行种子面选择。</summary>
        public static Array SelectFaces(object mesh, Array seeds, string operation, int growIterations)
        {
            var selectionType = ElementSelectionType.Value;
            var faceType = FaceType.Value;
            if (selectionType == null || faceType == null || mesh == null || seeds == null || seeds.Length == 0) return null;
            try
            {
                if (operation == "grow")
                {
                    var enumerableType = typeof(IEnumerable<>).MakeGenericType(faceType);
                    var method = selectionType.GetMethod("GrowSelection", BindingFlags.Static | BindingFlags.Public, null,
                        new[] { ProBuilderMeshType.Value, enumerableType, typeof(float) }, null);
                    if (method == null) return null;
                    var current = seeds;
                    for (var iteration = 0; iteration < growIterations; iteration++)
                    {
                        var neighboring = ToTypedFaceArray(method.Invoke(null, new object[] { mesh, current, -1f }));
                        if (neighboring == null) return null;
                        current = UnionFaceArrays(current, neighboring);
                    }
                    return current;
                }

                if (operation == "flood")
                {
                    var listType = typeof(IList<>).MakeGenericType(faceType);
                    var method = selectionType.GetMethod("FloodSelection", BindingFlags.Static | BindingFlags.Public, null,
                        new[] { ProBuilderMeshType.Value, listType, typeof(float) }, null);
                    return method == null ? null : ToTypedFaceArray(method.Invoke(null, new object[] { mesh, ToTypedFaceList(seeds), -1f }));
                }

                var loopMethod = selectionType.GetMethod("GetFaceLoop", BindingFlags.Static | BindingFlags.Public, null,
                    new[] { ProBuilderMeshType.Value, faceType.MakeArrayType(), typeof(bool) }, null);
                return loopMethod == null ? null : ToTypedFaceArray(loopMethod.Invoke(null, new object[] { mesh, seeds, operation == "ring" }));
            }
            catch (Exception) { return null; }
        }

        /// <summary>合并当前面与增长结果，保持 grow selection 包含原种子。</summary>
        private static Array UnionFaceArrays(Array current, Array neighboring)
        {
            var faces = new List<object>();
            foreach (var face in current) faces.Add(face);
            foreach (var face in neighboring)
            {
                if (!faces.Any(existing => ReferenceEquals(existing, face))) faces.Add(face);
            }
            var result = Array.CreateInstance(FaceType.Value, faces.Count);
            for (var index = 0; index < faces.Count; index++) result.SetValue(faces[index], index);
            return result;
        }

        /// <summary>把 Face 集合转换为强类型 Face[]。</summary>
        private static Array ToTypedFaceArray(object value)
        {
            if (!(value is System.Collections.IEnumerable enumerable) || FaceType.Value == null) return null;
            var faces = new List<object>();
            foreach (var face in enumerable) faces.Add(face);
            var result = Array.CreateInstance(FaceType.Value, faces.Count);
            for (var index = 0; index < faces.Count; index++) result.SetValue(faces[index], index);
            return result;
        }

        /// <summary>
        /// Convert an Edge[] array to a typed List&lt;Edge&gt; for APIs that require IList&lt;Edge&gt;.
        /// </summary>
        public static System.Collections.IList ToTypedEdgeList(Array edges)
        {
            var edgeType = EdgeType.Value;
            if (edgeType == null) return null;
            var edgeListType = typeof(List<>).MakeGenericType(edgeType);
            var typedList = Activator.CreateInstance(edgeListType) as System.Collections.IList;
            foreach (var e in edges)
                typedList.Add(e);
            return typedList;
        }

        // ─── ProBuilder API methods via reflection ────────────────────────

        /// <summary>
        /// Call ExtrudeElements.Extrude for edges via reflection.
        /// Supports edgeIndices or vertex pair [{a,b}] specification.
        /// </summary>
        public static int ExtrudeEdgesBySelection(object mesh, int[] edgeIndices, ProBuilderInput.EdgePair[] edgePairs, float distance, bool asGroup)
        {
            var extrudeElements = ExtrudeElementsType.Value;
            if (extrudeElements == null || mesh == null) return 0;

            var edgeArray = ResolveEdges(mesh, edgeIndices, edgePairs);
            if (edgeArray == null || edgeArray.Length == 0) return 0;
            var edgeCount = edgeArray.Length;

            try
            {
                var edgeEnumerableType = typeof(IEnumerable<>).MakeGenericType(EdgeType.Value);
                var method = extrudeElements.GetMethod("Extrude", BindingFlags.Static | BindingFlags.Public, null,
                    new[] { ProBuilderMeshType.Value, edgeEnumerableType, typeof(float), typeof(bool), typeof(bool) }, null);
                return method?.Invoke(null, new object[] { mesh, edgeArray, distance, asGroup, true }) is Array result
                    ? result.Length
                    : 0;
            }
            catch (Exception) { return 0; }
        }

        /// <summary>
        /// Call Bevel.BevelEdges via reflection.
        /// </summary>
        public static int BevelEdges(object mesh, int[] edgeIndices, ProBuilderInput.EdgePair[] edgePairs, float amount)
        {
            var bevelType = BevelType.Value;
            if (bevelType == null || mesh == null) return 0;

            var edgeArray = ResolveEdges(mesh, edgeIndices, edgePairs);
            if (edgeArray == null || edgeArray.Length == 0) return 0;
            var edgeCount = edgeArray.Length;
            var typedList = ToTypedEdgeList(edgeArray);
            if (typedList == null) return 0;

            try
            {
                var method = bevelType.GetMethod("BevelEdges",
                    BindingFlags.Static | BindingFlags.Public);
                if (method != null)
                {
                    method.Invoke(null, new object[] { mesh, typedList, amount });
                    return edgeCount;
                }
            }
            catch (Exception) { }
            return 0;
        }

        /// <summary>
        /// Call AppendElements.Bridge via reflection.
        /// </summary>
        public static bool BridgeEdges(object mesh, int aA, int aB, int bA, int bB, bool allowNonManifold)
        {
            var appendElements = AppendElementsType.Value;
            if (appendElements == null || mesh == null) return false;

            var edgeA = CreateEdge(aA, aB);
            var edgeB = CreateEdge(bA, bB);
            if (edgeA == null || edgeB == null) return false;
            var edgeType = EdgeType.Value;
            if (edgeType == null) return false;

            try
            {
                var method = appendElements.GetMethod("Bridge", BindingFlags.Static | BindingFlags.Public, null,
                    new[] { ProBuilderMeshType.Value, edgeType, edgeType, typeof(bool) }, null);
                return method != null && method.Invoke(null, new object[] { mesh, edgeA, edgeB, allowNonManifold }) != null;
            }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// Call ConnectElements.Connect via reflection for edges or faces.
        /// </summary>
        public static bool ConnectElements(object mesh, int[] edgeIndices, ProBuilderInput.EdgePair[] edgePairs, int[] faceIndices)
        {
            var connectType = ConnectElementsType.Value;
            if (connectType == null || mesh == null) return false;

            try
            {
                if (faceIndices != null && faceIndices.Length > 0)
                {
                    var faces = GetFacesByIndices(mesh, faceIndices);
                    if (faces != null && faces.Length > 0)
                    {
                        var faceList = ToTypedFaceList(faces);
                        var method = connectType.GetMethods(BindingFlags.Static | BindingFlags.Public)
                            .FirstOrDefault(m => m.Name == "Connect" && m.GetParameters().Length == 2
                                && m.GetParameters()[1].ParameterType.IsAssignableFrom(faceList.GetType()));
                        if (method != null)
                        {
                            method.Invoke(null, new object[] { mesh, faceList });
                            return true;
                        }
                    }
                }
                else if ((edgeIndices != null && edgeIndices.Length > 0) || (edgePairs != null && edgePairs.Length > 0))
                {
                    var edgeArray = ResolveEdges(mesh, edgeIndices, edgePairs);
                    if (edgeArray != null && edgeArray.Length > 0)
                    {
                        var typedList = ToTypedEdgeList(edgeArray);
                        var method = connectType.GetMethods(BindingFlags.Static | BindingFlags.Public)
                            .FirstOrDefault(m => m.Name == "Connect" && m.GetParameters().Length == 2
                                && m.GetParameters()[1].ParameterType.IsAssignableFrom(typedList.GetType()));
                        if (method != null)
                        {
                            method.Invoke(null, new object[] { mesh, typedList });
                            return true;
                        }
                    }
                }
            }
            catch (Exception) { }
            return false;
        }

        /// <summary>
        /// Call AppendElements.CreateShapeFromPolygon via reflection.
        /// Creates a poly shape from a list of 2D polygon points and extrudeHeight.
        /// </summary>
        public static bool CreateShapeFromPolygon(object mesh, IList<Vector3> points, float extrudeHeight, bool flipNormals)
        {
            var appendType = AppendElementsType.Value;
            if (appendType == null || mesh == null) return false;

            try
            {
                var method = appendType.GetMethod("CreateShapeFromPolygon",
                    BindingFlags.Static | BindingFlags.Public,
                    null,
                    new[] { ProBuilderMeshType.Value, typeof(IList<Vector3>), typeof(float), typeof(bool) },
                    null);

                if (method != null)
                {
                    method.Invoke(null, new object[] { mesh, points, extrudeHeight, flipNormals });
                    return true;
                }
            }
            catch (Exception) { }
            return false;
        }

        /// <summary>
        /// Call AppendElements.DuplicateAndFlip via reflection.
        /// </summary>
        public static bool DuplicateAndFlip(object mesh, Array faces)
        {
            var appendType = AppendElementsType.Value;
            if (appendType == null || mesh == null) return false;

            try
            {
                var faceArrayType = Array.CreateInstance(FaceType.Value, 0).GetType();
                var method = appendType.GetMethod("DuplicateAndFlip",
                    BindingFlags.Static | BindingFlags.Public,
                    null,
                    new[] { ProBuilderMeshType.Value, faceArrayType },
                    null);

                if (method != null)
                {
                    method.Invoke(null, new object[] { mesh, faces });
                    return true;
                }
            }
            catch (Exception) { }
            return false;
        }

        /// <summary>
        /// Call AppendElements.CreatePolygon via reflection.
        /// </summary>
        public static bool CreatePolygon(object mesh, int[] vertexIndices, bool unordered)
        {
            var appendType = AppendElementsType.Value;
            if (appendType == null || mesh == null) return false;

            try
            {
                var method = appendType.GetMethod("CreatePolygon",
                    BindingFlags.Static | BindingFlags.Public,
                    null,
                    new[] { ProBuilderMeshType.Value, typeof(IList<int>), typeof(bool) },
                    null);

                if (method != null)
                {
                    method.Invoke(null, new object[] { mesh, vertexIndices.ToList(), unordered });
                    return true;
                }
            }
            catch (Exception) { }
            return false;
        }

        /// <summary>
        /// Call AppendElements.AppendVerticesToEdge via reflection.
        /// </summary>
        public static bool AppendVerticesToEdge(object mesh, Array edgeArray, int count)
        {
            var appendType = AppendElementsType.Value;
            if (appendType == null || mesh == null) return false;

            var typedList = ToTypedEdgeList(edgeArray);
            if (typedList == null) return false;

            try
            {
                var edgeListType = typeof(IList<>).MakeGenericType(EdgeType.Value);
                var method = appendType.GetMethod("AppendVerticesToEdge", BindingFlags.Static | BindingFlags.Public, null,
                    new[] { ProBuilderMeshType.Value, edgeListType, typeof(int) }, null);
                return method != null && method.Invoke(null, new object[] { mesh, typedList, count }) != null;
            }
            catch (Exception) { return false; }
        }

        /// <summary>通过 ProBuilder 5.x AppendElements 在边上插入顶点。</summary>
        public static bool InsertVertexOnEdgeByPair(object mesh, int edgeA, int edgeB, Vector3 point)
        {
            var appendType = AppendElementsType.Value;
            var edge = CreateEdge(edgeA, edgeB);
            if (appendType == null || mesh == null || edge == null) return false;
            try
            {
                var method = appendType.GetMethod("InsertVertexOnEdge", BindingFlags.Static | BindingFlags.Public, null,
                    new[] { ProBuilderMeshType.Value, EdgeType.Value, typeof(Vector3) }, null);
                return method != null && method.Invoke(null, new object[] { mesh, edge, point }) != null;
            }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// Call AppendElements.InsertVertexInFace via reflection.
        /// </summary>
        public static bool InsertVertexInFace(object mesh, int faceIndex, Vector3 point)
        {
            var appendType = AppendElementsType.Value;
            if (appendType == null || mesh == null) return false;

            var allFaces = GetAllFaces(mesh) as System.Collections.IList;
            if (allFaces == null || faceIndex < 0 || faceIndex >= allFaces.Count) return false;
            var face = allFaces[faceIndex];

            try
            {
                var method = appendType.GetMethod("InsertVertexInFace",
                    BindingFlags.Static | BindingFlags.Public,
                    null,
                    new[] { ProBuilderMeshType.Value, FaceType.Value, typeof(Vector3) },
                    null);

                return method?.Invoke(null, new object[] { mesh, face, point }) is Array insertedFaces && insertedFaces.Length > 0;
            }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// Call VertexEditing.MergeVertices via reflection.
        /// </summary>
        public static int MergeVerticesWithCollapse(object mesh, int[] vertexIndices, bool collapseToFirst)
        {
            var vertexEditing = VertexEditingType.Value;
            if (vertexEditing == null || mesh == null) return -1;

            try
            {
                var method = vertexEditing.GetMethod("MergeVertices", BindingFlags.Static | BindingFlags.Public, null,
                    new[] { ProBuilderMeshType.Value, typeof(int[]), typeof(bool) }, null);
                return method?.Invoke(null, new object[] { mesh, vertexIndices, collapseToFirst }) is int index ? index : -1;
            }
            catch (Exception) { return -1; }
        }

        /// <summary>
        /// Call VertexEditing.WeldVertices via reflection (with radius).
        /// </summary>
        public static int WeldVerticesWithRadius(object mesh, int[] vertexIndices, float radius)
        {
            var vertexEditing = VertexEditingType.Value;
            if (vertexEditing == null || mesh == null) return -1;

            try
            {
                var vertexEnumerableType = typeof(IEnumerable<int>);
                var method = vertexEditing.GetMethod("WeldVertices", BindingFlags.Static | BindingFlags.Public, null,
                    new[] { ProBuilderMeshType.Value, vertexEnumerableType, typeof(float) }, null);
                return method?.Invoke(null, new object[] { mesh, vertexIndices, radius }) is int[] welded ? welded.Length : -1;
            }
            catch (Exception) { return -1; }
        }

        /// <summary>
        /// Call VertexEditing.SplitVertices via reflection (with IList overload).
        /// </summary>
        public static int SplitVerticesWithList(object mesh, int[] vertexIndices)
        {
            var vertexEditing = VertexEditingType.Value;
            if (vertexEditing == null || mesh == null) return -1;

            try
            {
                var method = vertexEditing.GetMethod("SplitVertices", BindingFlags.Static | BindingFlags.Public, null,
                    new[] { ProBuilderMeshType.Value, typeof(IEnumerable<int>) }, null);
                if (method == null) return -1;
                method.Invoke(null, new object[] { mesh, vertexIndices });
                return vertexIndices.Length;
            }
            catch (Exception) { return -1; }
        }

        /// <summary>
        /// Call MeshOperations.SetVertexPositions properly via the positions property.
        /// Gets positions, modifies them, then sets back via property setter or method.
        /// </summary>
        public static int MoveVerticesByOffset(object mesh, int[] vertexIndices, Vector3 offset)
        {
            if (mesh == null) return 0;
            try
            {
                var positionsProp = ProBuilderMeshType.Value?.GetProperty("positions");
                if (positionsProp == null) return 0;
                var positions = positionsProp.GetValue(mesh) as IList<Vector3>;
                if (positions == null) return 0;
                var posList = new List<Vector3>(positions);
                foreach (int idx in vertexIndices)
                {
                    if (idx >= 0 && idx < posList.Count)
                        posList[idx] += offset;
                }
                if (positionsProp.CanWrite)
                    positionsProp.SetValue(mesh, posList);
                else
                {
                    var setPositionsMethod = ProBuilderMeshType.Value?.GetMethod("SetPositions",
                        BindingFlags.Instance | BindingFlags.Public);
                    if (setPositionsMethod != null)
                        setPositionsMethod.Invoke(mesh, new object[] { posList.ToArray() });
                }
                return vertexIndices.Length;
            }
            catch (Exception) { return 0; }
        }

        /// <summary>移除退化三角形并返回 ProBuilder 报告的索引数量。</summary>
        public static int RemoveDegenerateTriangles(object mesh)
        {
            return InvokeMeshRepair("RemoveDegenerateTriangles", mesh);
        }

        /// <summary>移除未使用顶点并返回 ProBuilder 报告的索引数量。</summary>
        public static int RemoveUnusedVertices(object mesh)
        {
            return InvokeMeshRepair("RemoveUnusedVertices", mesh);
        }

        /// <summary>调用 ProBuilder 5.x MeshValidation 修复 API。</summary>
        private static int InvokeMeshRepair(string methodName, object mesh)
        {
            if (MeshValidationType.Value == null || mesh == null) return -1;
            try
            {
                var method = MeshValidationType.Value.GetMethod(methodName, BindingFlags.Static | BindingFlags.Public, null,
                    new[] { ProBuilderMeshType.Value, typeof(List<int>) }, null);
                if (method == null) return -1;
                var removed = new List<int>();
                method.Invoke(null, new object[] { mesh, removed });
                return removed.Count;
            }
            catch (Exception) { return -1; }
        }

        /// <summary>
        /// Call CombineMeshes.Combine via reflection.
        /// </summary>
        public static bool CombineMeshesViaReflection(object[] pbMeshes, object targetMesh)
        {
            var combineType = CombineMeshesType.Value;
            var meshType = ProBuilderMeshType.Value;
            if (combineType == null || meshType == null || pbMeshes == null || targetMesh == null) return false;
            try
            {
                var listType = typeof(List<>).MakeGenericType(meshType);
                var typedList = Activator.CreateInstance(listType) as System.Collections.IList;
                foreach (var mesh in pbMeshes) typedList.Add(mesh);
                var enumerableType = typeof(IEnumerable<>).MakeGenericType(meshType);
                var method = combineType.GetMethod("Combine", BindingFlags.Static | BindingFlags.Public, null,
                    new[] { enumerableType, meshType }, null);
                var result = method?.Invoke(null, new object[] { typedList, targetMesh }) as System.Collections.IList;
                return result != null && result.Count == 1 && ReferenceEquals(result[0], targetMesh);
            }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// Set vertex color on faces using ProBuilderMesh.SetFaceColor via reflection.
        /// </summary>
        public static bool SetFaceColor(object mesh, Array faces, Color color)
        {
            if (mesh == null) return false;
            try
            {
                var method = ProBuilderMeshType.Value?.GetMethod("SetFaceColor",
                    BindingFlags.Instance | BindingFlags.Public,
                    null,
                    new[] { FaceType.Value, typeof(Color) },
                    null);

                if (method != null)
                {
                    foreach (var face in faces)
                        method.Invoke(mesh, new object[] { face, color });
                    return true;
                }
            }
            catch (Exception) { }
            return false;
        }

        /// <summary>
        /// Set face material using ProBuilderMesh.SetMaterial via reflection.
        /// </summary>
        public static bool SetFaceMaterial(object mesh, Array faces, Material material)
        {
            if (mesh == null || faces == null || faces.Length == 0) return false;
            try
            {
                var faceEnumerableType = typeof(IEnumerable<>).MakeGenericType(FaceType.Value);
                var method = ProBuilderMeshType.Value?.GetMethod("SetMaterial", BindingFlags.Instance | BindingFlags.Public, null,
                    new[] { faceEnumerableType, typeof(Material) }, null);
                if (method == null) return false;
                method.Invoke(mesh, new object[] { faces, material });
                return true;
            }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// Get the faceCount property directly from ProBuilderMesh.
        /// </summary>
        public static int GetFaceCountProperty(object mesh)
        {
            return GetFaceCount(mesh);
        }

        /// <summary>
        /// Get the vertexCount property directly from ProBuilderMesh.
        /// </summary>
        public static int GetVertexCountProperty(object mesh)
        {
            return GetVertexCount(mesh);
        }

        /// <summary>
        /// Compute a face normal by reading vertex positions and face indexes.
        /// </summary>
        public static Vector3 ComputeFaceNormal(object mesh, object face)
        {
            try
            {
                var positionsProp = ProBuilderMeshType.Value?.GetProperty("positions");
                var positions = positionsProp?.GetValue(mesh) as System.Collections.IList;
                var indexesProp = FaceType.Value?.GetProperty("indexes");
                var indexes = indexesProp?.GetValue(face) as System.Collections.IList;
                if (positions == null || indexes == null || indexes.Count < 3) return Vector3.up;
                var p0 = (Vector3)positions[(int)indexes[0]];
                var p1 = (Vector3)positions[(int)indexes[1]];
                var p2 = (Vector3)positions[(int)indexes[2]];                var component = mesh as Component;
                var rotation = component != null ? component.transform.rotation : Quaternion.identity;
                var localNormal = Vector3.Cross(p1 - p0, p2 - p0).normalized;
                return rotation * localNormal;
            }
            catch (Exception) { return Vector3.up; }
        }

        /// <summary>
        /// Get the positions array from a ProBuilderMesh.
        /// </summary>
        public static IList<Vector3> GetPositions(object mesh)
        {
            try
            {
                var positionsProp = ProBuilderMeshType.Value?.GetProperty("positions");
                return positionsProp?.GetValue(mesh) as IList<Vector3>;
            }
            catch (Exception) { return null; }
        }

        /// <summary>读取实际顶点颜色，供 set-face-color 的逐面读回使用。</summary>
        public static IList<Color> GetColors(object mesh)
        {
            try { return ProBuilderMeshType.Value?.GetProperty("colors")?.GetValue(mesh) as IList<Color>; }
            catch (Exception) { return null; }
        }

        /// <summary>读取 ProBuilder 材质槽，供 set-face-material 的逐面读回使用。</summary>
        public static IList<Material> GetMaterials(object mesh)
        {
            try { return ProBuilderMeshType.Value?.GetProperty("materials")?.GetValue(mesh) as IList<Material>; }
            catch (Exception) { return null; }
        }

        /// <summary>读取 Face 实际顶点索引。</summary>
        public static IList<int> GetFaceIndexes(object face)
        {
            try { return FaceType.Value?.GetProperty("indexes")?.GetValue(face) as IList<int>; }
            catch (Exception) { return null; }
        }

        /// <summary>读取 Face 实际材质槽索引。</summary>
        public static int GetFaceMaterialIndex(object face)
        {
            try { return FaceType.Value?.GetProperty("material")?.GetValue(face) is int index ? index : -1; }
            catch (Exception) { return -1; }
        }

        /// <summary>逐字段比对 Face.AutoUnwrapSettings，避免 UV 操作仅刷新网格即报告成功。</summary>
        public static bool FaceUvMatches(object face, float? scale, float? offsetU, float? offsetV, float? rotation, bool flipU, bool flipV)
        {
            try
            {
                var uv = FaceType.Value?.GetProperty("uv")?.GetValue(face);
                if (uv == null) return false;
                var type = uv.GetType();
                var actualScale = ReadMember(type, uv, "scale") as Vector2?;
                var actualOffset = ReadMember(type, uv, "offset") as Vector2?;
                return (!scale.HasValue || (actualScale.HasValue && Mathf.Approximately(actualScale.Value.x, scale.Value) && Mathf.Approximately(actualScale.Value.y, scale.Value)))
                    && (!offsetU.HasValue || (actualOffset.HasValue && Mathf.Approximately(actualOffset.Value.x, offsetU.Value)))
                    && (!offsetV.HasValue || (actualOffset.HasValue && Mathf.Approximately(actualOffset.Value.y, offsetV.Value)))
                    && (!rotation.HasValue || (ReadMember(type, uv, "rotation") is float value && Mathf.Approximately(value, rotation.Value)))
                    && ReadMember(type, uv, "flipU") is bool actualFlipU && actualFlipU == flipU
                    && ReadMember(type, uv, "flipV") is bool actualFlipV && actualFlipV == flipV;
            }
            catch (Exception) { return false; }
        }

        /// <summary>验证多边形顶面法线与请求的 points/flipNormals 一致。</summary>
        public static bool HasPolygonNormalOrientation(object mesh, IList<Vector3> points, bool flipNormals)
        {
            var faces = GetAllFaces(mesh);
            if (faces == null || faces.Length == 0 || points == null || points.Count < 3) return false;
            var expected = Vector3.Cross(points[1] - points[0], points[2] - points[0]).normalized;
            if (flipNormals) expected = -expected;
            return faces.Cast<object>().Any(face => Vector3.Dot(ComputeFaceNormal(mesh, face), expected) > 0.5f);
        }

        private static object ReadMember(Type type, object value, string name)
        {
            return (object)type.GetField(name)?.GetValue(value) ?? type.GetProperty(name)?.GetValue(value);
        }

        /// <summary>用于完成证明的 ProBuilder 实际状态快照；不使用操作 API 的返回值。</summary>
        public sealed class MeshStateSnapshot
        {
            public string Positions { get; internal set; }
            public string Faces { get; internal set; }
            public string Edges { get; internal set; }
            public string SharedVertices { get; internal set; }
            public string Materials { get; internal set; }
            public string Uvs { get; internal set; }
            public string Colors { get; internal set; }
            public string SmoothingGroups { get; internal set; }
            public Vector3 Position { get; internal set; }
            public Quaternion Rotation { get; internal set; }
            public Vector3 Scale { get; internal set; }
        }

        /// <summary>从刷新后的 ProBuilderMesh 和 Transform 构造可比较的实际状态。</summary>
        public static MeshStateSnapshot CaptureMeshState(object mesh, GameObject go)
        {
            if (mesh == null || go == null) return null;
            try
            {
                var meshType = ProBuilderMeshType.Value;
                var snapshot = new MeshStateSnapshot
                {
                    Positions = DescribeEnumerable(meshType?.GetProperty("positions")?.GetValue(mesh)),
                    Faces = DescribeFaceProperty(mesh, "indexes"),
                    Edges = string.Join(";", CollectUniqueEdges(mesh).Select(edge => GetEdgeVertexA(edge) + ":" + GetEdgeVertexB(edge))),
                    SharedVertices = DescribeEnumerable(meshType?.GetProperty("sharedVertices")?.GetValue(mesh)),
                    Materials = DescribeEnumerable(meshType?.GetProperty("materials")?.GetValue(mesh)),
                    Colors = DescribeEnumerable(meshType?.GetProperty("colors")?.GetValue(mesh)),
                    Uvs = DescribeFaceProperty(mesh, "uv"),
                    SmoothingGroups = DescribeFaceProperty(mesh, "smoothingGroup"),
                    Position = go.transform.position,
                    Rotation = go.transform.rotation,
                    Scale = go.transform.localScale
                };
                return snapshot;
            }
            catch (Exception) { return null; }
        }

        /// <summary>确认 effect 后的实际网格或 transform 状态发生变化，无法读取时一律失败。</summary>
        public static bool HasObservedMutation(MeshStateSnapshot before, MeshStateSnapshot after)
        {
            if (before == null || after == null) return false;
            return before.Positions != after.Positions || before.Faces != after.Faces || before.Edges != after.Edges
                || before.SharedVertices != after.SharedVertices || before.Materials != after.Materials
                || before.Uvs != after.Uvs || before.Colors != after.Colors || before.SmoothingGroups != after.SmoothingGroups
                || before.Position != after.Position || before.Rotation != after.Rotation || before.Scale != after.Scale;
        }

        /// <summary>验证请求指定的面已实际写入目标平滑组，而非仅依赖操作返回值。</summary>
        public static bool HasSmoothingGroup(object mesh, int[] faceIndices, int smoothingGroup)
        {
            var faces = GetFacesByIndices(mesh, faceIndices);
            var property = FaceType.Value?.GetProperty("smoothingGroup");
            if (faces == null || property == null) return false;
            try { return faces.Cast<object>().All(face => property.GetValue(face) is int value && value == smoothingGroup); }
            catch (Exception) { return false; }
        }

        /// <summary>用于创建、转换和多对象合并的对象存在性与变换状态快照。</summary>
        public sealed class ObjectStateSnapshot
        {
            public bool Exists { get; internal set; }
            public int InstanceId { get; internal set; }
            public string Name { get; internal set; }
            public Vector3 Position { get; internal set; }
            public Quaternion Rotation { get; internal set; }
            public Vector3 Scale { get; internal set; }
            public bool HasProBuilderMesh { get; internal set; }
        }

        /// <summary>捕获对象可见状态；null 表示 effect 前对象不存在。</summary>
        public static ObjectStateSnapshot CaptureObjectState(GameObject go)
        {
            if (go == null) return new ObjectStateSnapshot();
            return new ObjectStateSnapshot
            {
                Exists = true,
                InstanceId = go.GetInstanceID(),
                Name = go.name,
                Position = go.transform.position,
                Rotation = go.transform.rotation,
                Scale = go.transform.localScale,
                HasProBuilderMesh = HasProBuilderMesh(go)
            };
        }

        /// <summary>比较对象读回状态，确保创建、转换或重命名未被 API 返回值掩盖。</summary>
        public static bool HasObservedObjectMutation(ObjectStateSnapshot before, ObjectStateSnapshot after)
        {
            if (before == null || after == null) return false;
            return before.Exists != after.Exists || before.InstanceId != after.InstanceId || before.Name != after.Name
                || before.Position != after.Position || before.Rotation != after.Rotation || before.Scale != after.Scale
                || before.HasProBuilderMesh != after.HasProBuilderMesh;
        }

        /// <summary>验证新建或转换后的对象确实包含可读取的 ProBuilder 几何。</summary>
        public static bool HasReadableGeometry(object mesh)
        {
            return mesh is Component component && GetFaceCount(mesh) > 0 && GetVertexCount(mesh) > 0 && CaptureMeshState(mesh, component.gameObject) != null;
        }

        /// <summary>把反射读取到的集合转为稳定字符串，用于同次调用中的前后状态比较。</summary>
        private static string DescribeFaceProperty(object mesh, string property)
        {
            var faces = GetAllFaces(mesh);
            var member = FaceType.Value?.GetProperty(property);
            if (faces == null || member == null) return null;
            return string.Join(";", faces.Cast<object>().Select(face => DescribeEnumerable(member.GetValue(face)) ?? Convert.ToString(member.GetValue(face), CultureInfo.InvariantCulture)));
        }

        private static string DescribeEnumerable(object value)
        {
            if (!(value is System.Collections.IEnumerable values)) return DescribeValue(value);
            return string.Join(",", values.Cast<object>().Select(DescribeValue));
        }

        /// <summary>展开嵌套共享顶点集合和 AutoUnwrapSettings 的公开字段，避免对象类型名掩盖状态变化。</summary>
        private static string DescribeValue(object value)
        {
            if (value == null) return null;
            if (value is System.Collections.IEnumerable values && !(value is string))
                return "[" + string.Join(",", values.Cast<object>().Select(DescribeValue)) + "]";
            var type = value.GetType();
            if (type.IsPrimitive || value is string || value is decimal || value is Vector2 || value is Vector3 || value is Vector4 || value is Color || value is Quaternion)
                return Convert.ToString(value, CultureInfo.InvariantCulture);
            var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public);
            if (fields.Length == 0) return Convert.ToString(value, CultureInfo.InvariantCulture);
            return "{" + string.Join(",", fields.OrderBy(field => field.Name).Select(field => field.Name + "=" + DescribeValue(field.GetValue(value)))) + "}";
        }

        // ─── Helpers ─────────────────────────────────────────────────

        private static Type FindType(string fullName)
        {
            var type = Type.GetType(fullName);
            if (type != null) return type;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    type = assembly.GetType(fullName);
                    if (type != null) return type;
                }
                catch (Exception) { }
            }
            return null;
        }
    }
}
#endif
