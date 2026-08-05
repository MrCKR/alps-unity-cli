#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
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
        private static readonly RetryingType ShapeTypeType = new RetryingType("UnityEngine.ProBuilder.ShapeType");
        private static readonly RetryingType ExtrudeElementsType = new RetryingType("UnityEngine.ProBuilder.MeshOperations.ExtrudeElements");
        private static readonly RetryingType DeleteElementsType = new RetryingType("UnityEngine.ProBuilder.MeshOperations.DeleteElements");
        private static readonly RetryingType AppendElementsType = new RetryingType("UnityEngine.ProBuilder.MeshOperations.AppendElements");
        private static readonly RetryingType CombineMeshesType = new RetryingType("UnityEngine.ProBuilder.MeshOperations.CombineMeshes");
        private static readonly RetryingType VertexEditingType = new RetryingType("UnityEngine.ProBuilder.MeshOperations.VertexEditing");
        private static readonly RetryingType MeshImporterType = new RetryingType("UnityEngine.ProBuilder.MeshOperations.MeshImporter");
        private static readonly RetryingType MeshImportSettingsType = new RetryingType("UnityEngine.ProBuilder.MeshOperations.MeshImportSettings");
        private static readonly RetryingType ElementSelectionType = new RetryingType("UnityEngine.ProBuilder.MeshOperations.ElementSelection");
        private static readonly RetryingType PivotLocationType = new RetryingType("UnityEngine.ProBuilder.PivotLocation");

        internal const string SupportedPackageVersion = "5.2.4";

        /// <summary>版本由 asmdef exact versionDefine 在编译期证明，Tool request 路径不得重入 PackageManager。</summary>
        internal static string InstalledPackageVersion
        {
            get
            {
#if UNITY_CLI_PROBUILDER_5_2_4
                return SupportedPackageVersion;
#else
                return null;
#endif
            }
        }

        /// <summary>本扩展的 operation replay 只对精确 ProBuilder 5.2.4 闭合。</summary>
        public static bool IsAvailable
        {
            get { return ProBuilderMeshType.Value != null && string.Equals(InstalledPackageVersion, SupportedPackageVersion, StringComparison.Ordinal); }
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
        /// Check if a GameObject has a ProBuilderMesh component.
        /// </summary>
        public static bool HasProBuilderMesh(GameObject go)
        {
            return GetProBuilderMesh(go) != null;
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

        /// <summary>通过 ProBuilder 5.x 精确签名重建并刷新全部派生通道。</summary>
        public static bool RefreshMesh(object mesh) => RefreshMesh(mesh, "All", false);

        /// <summary>保留 raw Vertex payload，并在派生通道刷新后同步回 compiled Mesh。</summary>
        public static bool RefreshMeshPreservingVertexPayload(object mesh) => RefreshMesh(mesh, "Normals, Tangents, Collisions, Bounds", true);

        /// <summary>按固定 RefreshMask 刷新；payload-preserving 路径不执行 Auto UV。</summary>
        private static bool RefreshMesh(object mesh, string mask, bool applyVertexPayload)
        {
            if (mesh == null) return false;
            try
            {
                var meshType = ProBuilderMeshType.Value;
                var refreshMaskType = FindType("UnityEngine.ProBuilder.RefreshMask");
                var vertexType = applyVertexPayload ? FindType("UnityEngine.ProBuilder.Vertex") : null;
                var vertexListType = vertexType == null ? null : typeof(IList<>).MakeGenericType(vertexType);
                var toMesh = meshType?.GetMethod("ToMesh", new[] { typeof(MeshTopology) });
                var refresh = meshType?.GetMethod("Refresh", new[] { refreshMaskType });
                var getVertices = applyVertexPayload ? meshType?.GetMethod("GetVertices", new[] { typeof(IList<int>) }) : null;
                var setVertices = applyVertexPayload && vertexListType != null
                    ? meshType?.GetMethod("SetVertices", new[] { vertexListType, typeof(bool) })
                    : null;
                if (toMesh == null || refresh == null || (applyVertexPayload && (getVertices == null || setVertices == null))) return false;
                toMesh.Invoke(mesh, new object[] { MeshTopology.Triangles });
                var refreshMask = Enum.Parse(refresh.GetParameters()[0].ParameterType, mask, true);
                refresh.Invoke(mesh, new[] { refreshMask });
                if (applyVertexPayload)
                {
                    var vertices = getVertices.Invoke(mesh, new object[] { null });
                    if (vertices == null) return false;
                    setVertices.Invoke(mesh, new[] { vertices, (object)true });
                }
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
            if (edgeType == null || mesh == null) return null;
            // 两种选择编码同时出现时不能猜测优先级；调用方必须在 effect 前修正请求。
            if (edgeIndices != null && edgeIndices.Length > 0 && edgePairs != null && edgePairs.Length > 0) return null;
            var edgeList = new List<object>();

            if (edgePairs != null && edgePairs.Length > 0)
            {
                var allEdges = CollectUniqueEdges(mesh);
                foreach (var pair in edgePairs)
                {
                    if (pair.a == pair.b) return null;
                    // Edge 是值类型，构造成功并不代表该 pair 是当前网格的真实 unique edge。
                    var edge = allEdges.FirstOrDefault(candidate =>
                        (GetEdgeVertexA(candidate) == pair.a && GetEdgeVertexB(candidate) == pair.b) ||
                        (GetEdgeVertexA(candidate) == pair.b && GetEdgeVertexB(candidate) == pair.a));
                    if (edge == null) return null;
                    edgeList.Add(edge);
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

            if (edgeList.Count == 0 || edgeList.Select(edge =>
            {
                var a = GetEdgeVertexA(edge); var b = GetEdgeVertexB(edge);
                return a < b ? a + ":" + b : b + ":" + a;
            }).Distinct().Count() != edgeList.Count) return null;
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
        public static bool TryExtrudeFrozenEdges(object mesh, Array frozenEdges, float distance, bool asGroup, out int actualCount)
        {
            actualCount = 0;
            var extrudeElements = ExtrudeElementsType.Value;
            if (extrudeElements == null || mesh == null || frozenEdges == null || frozenEdges.Length == 0 || EdgeType.Value == null || frozenEdges.GetType().GetElementType() != EdgeType.Value) return false;
            try
            {
                var edgeEnumerableType = typeof(IEnumerable<>).MakeGenericType(EdgeType.Value);
                var method = extrudeElements.GetMethod("Extrude", BindingFlags.Static | BindingFlags.Public, null,
                    new[] { ProBuilderMeshType.Value, edgeEnumerableType, typeof(float), typeof(bool), typeof(bool) }, null);
                if (method == null) return false;
                var result = method.Invoke(null, new object[] { mesh, frozenEdges, distance, asGroup, false }) as Array;
                actualCount = result == null ? 0 : result.Length;
                return true;
            }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// Call AppendElements.Bridge via reflection.
        /// </summary>
        public static bool TryBridgeFrozenEdges(object mesh, object frozenEdgeA, object frozenEdgeB, bool allowNonManifold, out bool createdFace)
        {
            createdFace = false;
            var appendElements = AppendElementsType.Value;
            var edgeType = EdgeType.Value;
            if (appendElements == null || mesh == null || edgeType == null || frozenEdgeA == null || frozenEdgeB == null || frozenEdgeA.GetType() != edgeType || frozenEdgeB.GetType() != edgeType) return false;
            try
            {
                var method = appendElements.GetMethod("Bridge", BindingFlags.Static | BindingFlags.Public, null,
                    new[] { ProBuilderMeshType.Value, edgeType, edgeType, typeof(bool) }, null);
                if (method == null) return false;
                createdFace = method.Invoke(null, new object[] { mesh, frozenEdgeA, frozenEdgeB, allowNonManifold }) != null;
                return true;
            }
            catch (Exception) { return false; }
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
        public static bool TryMergeVerticesWithCollapse(object mesh, int[] vertexIndices, bool collapseToFirst, out int mergedIndex)
        {
            mergedIndex = -1;
            var vertexEditing = VertexEditingType.Value;
            if (vertexEditing == null || mesh == null) return false;

            try
            {
                var method = vertexEditing.GetMethod("MergeVertices", BindingFlags.Static | BindingFlags.Public, null,
                    new[] { ProBuilderMeshType.Value, typeof(int[]), typeof(bool) }, null);
                if (method == null) return false;
                var value = method.Invoke(null, new object[] { mesh, vertexIndices, collapseToFirst });
                if (!(value is int)) return false;
                mergedIndex = (int)value;
                return true;
            }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// Call VertexEditing.WeldVertices via reflection (with radius).
        /// </summary>
        public static bool TryWeldVerticesWithRadius(object mesh, int[] vertexIndices, float radius, out int weldedCount)
        {
            weldedCount = 0;
            var vertexEditing = VertexEditingType.Value;
            if (vertexEditing == null || mesh == null) return false;

            try
            {
                var vertexEnumerableType = typeof(IEnumerable<int>);
                var method = vertexEditing.GetMethod("WeldVertices", BindingFlags.Static | BindingFlags.Public, null,
                    new[] { ProBuilderMeshType.Value, vertexEnumerableType, typeof(float) }, null);
                if (method == null) return false;
                var welded = method.Invoke(null, new object[] { mesh, vertexIndices, radius }) as int[];
                if (welded == null) return false;
                weldedCount = welded.Length;
                return true;
            }
            catch (Exception) { return false; }
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
