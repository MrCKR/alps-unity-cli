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
    /// When the package is absent, tools report unavailable with next_actions.
    /// </summary>
    internal static class ProBuilderReflection
    {
        // ─── Cached type lookups ───────────────────────────────────
        private static readonly Lazy<Type> ProBuilderMeshType = new Lazy<Type>(() => FindType("UnityEngine.ProBuilder.ProBuilderMesh"));
        private static readonly Lazy<Type> ShapeGeneratorType = new Lazy<Type>(() => FindType("UnityEngine.ProBuilder.ShapeGenerator"));
        private static readonly Lazy<Type> MeshOperationsType = new Lazy<Type>(() => FindType("UnityEngine.ProBuilder.MeshOperations"));
        private static readonly Lazy<Type> EditorMeshUtilityType = new Lazy<Type>(() => FindType("UnityEditor.ProBuilder.EditorMeshUtility"));
        private static readonly Lazy<Type> SmoothingType = new Lazy<Type>(() => FindType("UnityEngine.ProBuilder.Smoothing"));
        private static readonly Lazy<Type> MeshValidationType = new Lazy<Type>(() => FindType("UnityEngine.ProBuilder.MeshValidation"));
        private static readonly Lazy<Type> ProBuilderEditorType = new Lazy<Type>(() => FindType("UnityEditor.ProBuilder.ProBuilderEditor"));
        private static readonly Lazy<Type> FaceType = new Lazy<Type>(() => FindType("UnityEngine.ProBuilder.Face"));
        private static readonly Lazy<Type> EdgeType = new Lazy<Type>(() => FindType("UnityEngine.ProBuilder.Edge"));
        private static readonly Lazy<Type> VertexType = new Lazy<Type>(() => FindType("UnityEngine.ProBuilder.Vertex"));
        private static readonly Lazy<Type> SharedVertexType = new Lazy<Type>(() => FindType("UnityEngine.ProBuilder.SharedVertex"));
        private static readonly Lazy<Type> ColorType = new Lazy<Type>(() => FindType("UnityEngine.ProBuilder.Color"));
        private static readonly Lazy<Type> PivotType = new Lazy<Type>(() => FindType("UnityEngine.ProBuilder.PivotType"));
        private static readonly Lazy<Type> ExtrudeMethodType = new Lazy<Type>(() => FindType("UnityEngine.ProBuilder.ExtrudeMethod"));
        private static readonly Lazy<Type> ShapeTypeType = new Lazy<Type>(() => FindType("UnityEngine.ProBuilder.ShapeType"));

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

        /// <summary>
        /// Returns the ShapeGenerator type or null if the package is missing.
        /// </summary>
        public static Type GetShapeGeneratorType() { return ShapeGeneratorType.Value; }

        /// <summary>
        /// Returns the MeshOperations type or null if the package is missing.
        /// </summary>
        public static Type GetMeshOperationsType() { return MeshOperationsType.Value; }

        /// <summary>
        /// Returns the EditorMeshUtility type or null if the package is missing.
        /// </summary>
        public static Type GetEditorMeshUtilityType() { return EditorMeshUtilityType.Value; }

        /// <summary>
        /// Returns the Smoothing type or null if the package is missing.
        /// </summary>
        public static Type GetSmoothingType() { return SmoothingType.Value; }

        /// <summary>
        /// Returns the MeshValidation type or null if the package is missing.
        /// </summary>
        public static Type GetMeshValidationType() { return MeshValidationType.Value; }

        /// <summary>
        /// Returns the Face type or null if the package is missing.
        /// </summary>
        public static Type GetFaceType() { return FaceType.Value; }

        /// <summary>
        /// Returns the Edge type or null if the package is missing.
        /// </summary>
        public static Type GetEdgeType() { return EdgeType.Value; }

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

        /// <summary>
        /// Call MeshOperations.Extrude via reflection.
        /// </summary>
        public static void Extrude(object mesh, object faces, float distance, string method)
        {
            var type = MeshOperationsType.Value;
            if (type == null || mesh == null) return;
            var extrudeMethod = (int)0; // ExtrudeMethod.VertexNormal
            if (!string.IsNullOrEmpty(method) && string.Equals(method, "faceNormal", StringComparison.OrdinalIgnoreCase))
            {
                extrudeMethod = 1; // ExtrudeMethod.FaceNormal
            }
            try
            {
                var methodInfo = type.GetMethod("Extrude", new[] { ProBuilderMeshType.Value, FaceType.Value.MakeArrayType(), typeof(float), ExtrudeMethodType.Value });
                if (methodInfo != null)
                {
                    methodInfo.Invoke(null, new[] { mesh, faces, distance, extrudeMethod });
                }
            }
            catch (Exception) { }
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
                // Try the enum-based overload: CreateShape(ShapeType, PivotLocation)
                var shapeTypeEnum = ParseShapeType(shapeType);
                if (shapeTypeEnum != null && ShapeTypeType.Value != null)
                {
                    var methodInfo = type.GetMethod("CreateShape", new[] { ShapeTypeType.Value });
                    if (methodInfo != null)
                    {
                        var go = methodInfo.Invoke(null, new[] { shapeTypeEnum }) as GameObject;
                        if (go != null)
                        {
                            go.transform.position = position;
                            go.transform.rotation = rotation;
                        }
                        return go;
                    }
                }

                // Fallback: try CreateShape with Vector3, Quaternion
                var altMethod = type.GetMethod("CreateShape", new[] { ShapeTypeType.Value, typeof(Vector3), typeof(Quaternion) });
                if (altMethod != null && shapeTypeEnum != null)
                {
                    return altMethod.Invoke(null, new object[] { shapeTypeEnum, position, rotation }) as GameObject;
                }
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

        /// <summary>
        /// Call MeshOperations.DeleteFaces via reflection.
        /// </summary>
        public static void DeleteFaces(object mesh, int[] faceIndices)
        {
            var type = MeshOperationsType.Value;
            if (type == null || mesh == null) return;
            try
            {
                var methodInfo = type.GetMethod("DeleteFaces", new[] { ProBuilderMeshType.Value, typeof(int[]) });
                if (methodInfo != null)
                {
                    methodInfo.Invoke(null, new object[] { mesh, faceIndices });
                }
            }
            catch (Exception) { }
        }

        /// <summary>
        /// Call MeshOperations.DetachFaces via reflection.
        /// Returns the detached GameObject or null.
        /// </summary>
        public static GameObject DetachFaces(object mesh, int[] faceIndices)
        {
            var type = MeshOperationsType.Value;
            if (type == null || mesh == null) return null;
            try
            {
                var methodInfo = type.GetMethod("DetachFaces", new[] { ProBuilderMeshType.Value, FaceType.Value.MakeArrayType() });
                if (methodInfo != null)
                {
                    var faces = GetFacesByIndices(mesh, faceIndices);
                    if (faces == null) return null;
                    var result = methodInfo.Invoke(null, new[] { mesh, faces });
                    // DetachFaces returns the new GameObject
                    if (result is GameObject go) return go;
                    // Some overloads return a struct with gameObject
                    var goProp = result?.GetType().GetProperty("gameObject");
                    return goProp?.GetValue(result) as GameObject;
                }
            }
            catch (Exception) { }
            return null;
        }

        /// <summary>
        /// Call MeshOperations.MergeFaces via reflection.
        /// </summary>
        public static void MergeFaces(object mesh, int[] faceIndices)
        {
            var type = MeshOperationsType.Value;
            if (type == null || mesh == null) return;
            try
            {
                var faces = GetFacesByIndices(mesh, faceIndices);
                if (faces == null) return;
                var methodInfo = type.GetMethod("MergeFaces", new[] { ProBuilderMeshType.Value, FaceType.Value.MakeArrayType() });
                if (methodInfo != null)
                {
                    methodInfo.Invoke(null, new[] { mesh, faces });
                }
            }
            catch (Exception) { }
        }

        /// <summary>
        /// Call MeshOperations.SubdivideFaces via reflection.
        /// </summary>
        public static void SubdivideFaces(object mesh, int[] faceIndices)
        {
            var type = MeshOperationsType.Value;
            if (type == null || mesh == null) return;
            try
            {
                var faces = GetFacesByIndices(mesh, faceIndices);
                if (faces == null) return;
                var methodInfo = type.GetMethod("SubdivideFaces", new[] { ProBuilderMeshType.Value, FaceType.Value.MakeArrayType() });
                if (methodInfo != null)
                {
                    methodInfo.Invoke(null, new[] { mesh, faces });
                }
            }
            catch (Exception) { }
        }

        /// <summary>
        /// Call MeshOperations.FlipFaces via reflection.
        /// </summary>
        public static void FlipFaces(object mesh, int[] faceIndices)
        {
            var type = MeshOperationsType.Value;
            if (type == null || mesh == null) return;
            try
            {
                var faces = GetFacesByIndices(mesh, faceIndices);
                if (faces == null) return;
                var methodInfo = type.GetMethod("FlipFaces", new[] { ProBuilderMeshType.Value, FaceType.Value.MakeArrayType() });
                if (methodInfo != null)
                {
                    methodInfo.Invoke(null, new[] { mesh, faces });
                }
            }
            catch (Exception) { }
        }

        /// <summary>
        /// Call MeshOperations.BridgeFaces via reflection.
        /// </summary>
        public static void Bridge(object mesh, object edgeA, object edgeB)
        {
            var type = MeshOperationsType.Value;
            if (type == null || mesh == null) return;
            try
            {
                var methodInfo = type.GetMethod("Bridge", new[] { ProBuilderMeshType.Value, EdgeType.Value, EdgeType.Value });
                if (methodInfo != null)
                {
                    methodInfo.Invoke(null, new[] { mesh, edgeA, edgeB });
                }
            }
            catch (Exception) { }
        }

        /// <summary>
        /// Call MeshOperations.Connect via reflection for edges.
        /// </summary>
        public static void Connect(object mesh, object edges)
        {
            var type = MeshOperationsType.Value;
            if (type == null || mesh == null) return;
            try
            {
                var methodInfo = type.GetMethod("Connect", new[] { ProBuilderMeshType.Value, EdgeType.Value.MakeArrayType() });
                if (methodInfo != null)
                {
                    methodInfo.Invoke(null, new[] { mesh, edges });
                }
            }
            catch (Exception) { }
        }

        /// <summary>
        /// Call MeshOperations.BevelEdges via reflection.
        /// </summary>
        public static void Bevel(object mesh, object edges, float amount)
        {
            var type = MeshOperationsType.Value;
            if (type == null || mesh == null) return;
            try
            {
                var methodInfo = type.GetMethod("BevelEdges", new[] { ProBuilderMeshType.Value, EdgeType.Value.MakeArrayType(), typeof(float) });
                if (methodInfo != null)
                {
                    methodInfo.Invoke(null, new object[] { mesh, edges, amount });
                }
            }
            catch (Exception) { }
        }

        /// <summary>
        /// Call MeshOperations.Extrude for edges via reflection.
        /// </summary>
        public static void ExtrudeEdges(object mesh, object edges, float distance)
        {
            var type = MeshOperationsType.Value;
            if (type == null || mesh == null) return;
            try
            {
                var methodInfo = type.GetMethod("Extrude", new[] { ProBuilderMeshType.Value, EdgeType.Value.MakeArrayType(), typeof(float) });
                if (methodInfo != null)
                {
                    methodInfo.Invoke(null, new object[] { mesh, edges, distance });
                }
            }
            catch (Exception) { }
        }

        /// <summary>
        /// Call MeshOperations.MergeVertices via reflection.
        /// </summary>
        public static int MergeVertices(object mesh, int[] vertexIndices)
        {
            var type = MeshOperationsType.Value;
            if (type == null || mesh == null) return 0;
            try
            {
                var sharedType = SharedVertexType.Value;
                var methodInfo = type.GetMethod("MergeVertices", new[] { ProBuilderMeshType.Value, typeof(int[]) });
                if (methodInfo != null)
                {
                    return (int)methodInfo.Invoke(null, new object[] { mesh, vertexIndices });
                }
            }
            catch (Exception) { }
            return 0;
        }

        /// <summary>
        /// Call MeshOperations.WeldVertices via reflection.
        /// </summary>
        public static int WeldVertices(object mesh, int[] vertexIndices, float radius)
        {
            var type = MeshOperationsType.Value;
            if (type == null || mesh == null) return 0;
            try
            {
                var methodInfo = type.GetMethod("WeldVertices", new[] { ProBuilderMeshType.Value, typeof(int[]), typeof(float) });
                if (methodInfo != null)
                {
                    return (int)methodInfo.Invoke(null, new object[] { mesh, vertexIndices, radius });
                }
            }
            catch (Exception) { }
            return 0;
        }

        /// <summary>
        /// Call MeshOperations.SplitVertices via reflection.
        /// </summary>
        public static int SplitVertices(object mesh, int[] vertexIndices)
        {
            var type = MeshOperationsType.Value;
            if (type == null || mesh == null) return 0;
            try
            {
                var methodInfo = type.GetMethod("SplitVertices", new[] { ProBuilderMeshType.Value, typeof(int[]) });
                if (methodInfo != null)
                {
                    return (int)methodInfo.Invoke(null, new object[] { mesh, vertexIndices });
                }
            }
            catch (Exception) { }
            return 0;
        }

        /// <summary>
        /// Call MeshOperations.SetVertexPosition via reflection.
        /// </summary>
        public static void SetVertexPositions(object mesh, int[] vertexIndices, Vector3[] positions)
        {
            var type = MeshOperationsType.Value;
            if (type == null || mesh == null) return;
            try
            {
                var methodInfo = type.GetMethod("SetVertexPositions", new[] { ProBuilderMeshType.Value, typeof(int[]), typeof(Vector3[]) });
                if (methodInfo != null)
                {
                    methodInfo.Invoke(null, new object[] { mesh, vertexIndices, positions });
                }
            }
            catch (Exception) { }
        }

        /// <summary>
        /// Call MeshOperations.InsertVertexOnEdge via reflection.
        /// Returns the new vertex index or -1.
        /// </summary>
        public static int InsertVertexOnEdge(object mesh, object edge, Vector3 point)
        {
            var type = MeshOperationsType.Value;
            if (type == null || mesh == null) return -1;
            try
            {
                var methodInfo = type.GetMethod("InsertVertexOnEdge", new[] { ProBuilderMeshType.Value, EdgeType.Value, typeof(Vector3) });
                if (methodInfo != null)
                {
                    return (int)methodInfo.Invoke(null, new object[] { mesh, edge, point });
                }
            }
            catch (Exception) { }
            return -1;
        }

        /// <summary>
        /// Call Smoothing.ApplySmoothingGroup via reflection.
        /// </summary>
        public static void ApplySmoothingGroup(object mesh, int[] faceIndices, int smoothingGroup)
        {
            var type = SmoothingType.Value;
            if (type == null || mesh == null) return;
            try
            {
                var methodInfo = type.GetMethod("ApplySmoothingGroup", new[] { ProBuilderMeshType.Value, FaceType.Value.MakeArrayType(), typeof(int) });
                if (methodInfo != null)
                {
                    var faces = GetFacesByIndices(mesh, faceIndices);
                    if (faces != null)
                    {
                        methodInfo.Invoke(null, new object[] { mesh, faces, smoothingGroup });
                    }
                }
            }
            catch (Exception) { }
        }

        /// <summary>
        /// Call Smoothing.ApplySmoothingGroups via reflection for auto-smooth.
        /// </summary>
        public static void ApplySmoothingGroups(object mesh, int[] faceIndices, float angleThreshold)
        {
            var type = SmoothingType.Value;
            if (type == null || mesh == null) return;
            try
            {
                var faces = GetFacesByIndices(mesh, faceIndices);
                if (faces == null) return;
                var methodInfo = type.GetMethod("ApplySmoothingGroups", new[] { ProBuilderMeshType.Value, FaceType.Value.MakeArrayType(), typeof(float) });
                if (methodInfo != null)
                {
                    methodInfo.Invoke(null, new object[] { mesh, faces, angleThreshold });
                }
            }
            catch (Exception) { }
        }

        /// <summary>
        /// Call MeshValidation.Validate via reflection.
        /// Returns the validation result object or null.
        /// </summary>
        public static object Validate(object mesh)
        {
            var type = MeshValidationType.Value;
            if (type == null || mesh == null) return null;
            try
            {
                var methodInfo = type.GetMethod("Validate", new[] { ProBuilderMeshType.Value });
                if (methodInfo != null)
                {
                    return methodInfo.Invoke(null, new object[] { mesh });
                }
            }
            catch (Exception) { }
            return null;
        }

        /// <summary>
        /// Call EditorMeshUtility.Optimize via reflection.
        /// </summary>
        public static void Optimize(object mesh)
        {
            var type = EditorMeshUtilityType.Value;
            if (type == null || mesh == null) return;
            try
            {
                var methodInfo = type.GetMethod("Optimize", new[] { ProBuilderMeshType.Value });
                if (methodInfo != null)
                {
                    methodInfo.Invoke(null, new object[] { mesh });
                }
            }
            catch (Exception) { }
        }

        // ─── Mesh property accessors via reflection ──────────────────

        /// <summary>
        /// Get the face count from a ProBuilderMesh via reflection.
        /// </summary>
        public static int GetFaceCount(object mesh)
        {
            if (mesh == null) return 0;
            try
            {
                var facesProp = ProBuilderMeshType.Value?.GetProperty("faces");
                if (facesProp == null) return 0;
                var faces = facesProp.GetValue(mesh);
                if (faces is Array arr) return arr.Length;
                return 0;
            }
            catch (Exception) { return 0; }
        }

        /// <summary>
        /// Get the vertex count from a ProBuilderMesh via reflection.
        /// </summary>
        public static int GetVertexCount(object mesh)
        {
            if (mesh == null) return 0;
            try
            {
                var verticesProp = ProBuilderMeshType.Value?.GetProperty("vertices");
                if (verticesProp == null) return 0;
                var vertices = verticesProp.GetValue(verticesProp.PropertyType.IsArray ? mesh : null);
                if (verticesProp.PropertyType.IsArray)
                {
                    var arr = verticesProp.GetValue(mesh) as Array;
                    return arr?.Length ?? 0;
                }
                return 0;
            }
            catch (Exception) { return 0; }
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
                var allFaces = facesProp.GetValue(mesh) as Array;
                if (allFaces == null) return null;
                var faceType = FaceType.Value;
                if (faceType == null) return null;
                var result = Array.CreateInstance(faceType, indices.Length);
                for (var i = 0; i < indices.Length; i++)
                {
                    if (indices[i] >= 0 && indices[i] < allFaces.Length)
                    {
                        result.SetValue(allFaces.GetValue(indices[i]), i);
                    }
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
                var facesProp = ProBuilderMeshType.Value?.GetProperty("faces");
                if (facesProp == null) return null;
                return facesProp.GetValue(mesh) as Array;
            }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// Refresh/ToMesh a ProBuilderMesh via reflection.
        /// </summary>
        public static void RefreshMesh(object mesh)
        {
            if (mesh == null) return;
            try
            {
                // Try ToMesh() then Refresh()
                var toMesh = ProBuilderMeshType.Value?.GetMethod("ToMesh", Type.EmptyTypes);
                toMesh?.Invoke(mesh, null);
                var refresh = ProBuilderMeshType.Value?.GetMethod("Refresh", Type.EmptyTypes);
                refresh?.Invoke(mesh, null);
            }
            catch (Exception) { }
        }

        /// <summary>
        /// Convert a regular mesh to a ProBuilder mesh via reflection.
        /// </summary>
        public static object ConvertToProBuilderMesh(GameObject go)
        {
            if (go == null) return null;
            try
            {
                var methodInfo = ProBuilderMeshType.Value?.GetMethod("AddProBuilderMesh", new[] { typeof(GameObject) });
                if (methodInfo != null)
                {
                    return methodInfo.Invoke(null, new object[] { go });
                }
                // Alternative: try adding the component directly
                return AddProBuilderMesh(go);
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
