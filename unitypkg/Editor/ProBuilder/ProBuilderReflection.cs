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
        private static readonly Lazy<Type> ExtrudeElementsType = new Lazy<Type>(() => FindType("UnityEngine.ProBuilder.MeshOperations.ExtrudeElements"));
        private static readonly Lazy<Type> BevelType = new Lazy<Type>(() => FindType("UnityEngine.ProBuilder.MeshOperations.Bevel"));
        private static readonly Lazy<Type> DeleteElementsType = new Lazy<Type>(() => FindType("UnityEngine.ProBuilder.MeshOperations.DeleteElements"));
        private static readonly Lazy<Type> AppendElementsType = new Lazy<Type>(() => FindType("UnityEngine.ProBuilder.MeshOperations.AppendElements"));
        private static readonly Lazy<Type> ConnectElementsType = new Lazy<Type>(() => FindType("UnityEngine.ProBuilder.MeshOperations.ConnectElements"));
        private static readonly Lazy<Type> MergeElementsType = new Lazy<Type>(() => FindType("UnityEngine.ProBuilder.MeshOperations.MergeElements"));
        private static readonly Lazy<Type> CombineMeshesType = new Lazy<Type>(() => FindType("UnityEngine.ProBuilder.MeshOperations.CombineMeshes"));
        private static readonly Lazy<Type> VertexEditingType = new Lazy<Type>(() => FindType("UnityEngine.ProBuilder.MeshOperations.VertexEditing"));
        private static readonly Lazy<Type> MeshImporterType = new Lazy<Type>(() => FindType("UnityEngine.ProBuilder.MeshOperations.MeshImporter"));
        private static readonly Lazy<Type> ElementSelectionType = new Lazy<Type>(() => FindType("UnityEngine.ProBuilder.MeshOperations.ElementSelection"));
        private static readonly Lazy<Type> PivotLocationType = new Lazy<Type>(() => FindType("UnityEngine.ProBuilder.PivotLocation"));
        private static readonly Lazy<Type> AxisType = new Lazy<Type>(() => FindType("UnityEngine.ProBuilder.Axis"));

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

        /// <summary>
        /// Returns the ExtrudeElements type or null if the package is missing.
        /// </summary>
        public static Type GetExtrudeElementsType() { return ExtrudeElementsType.Value; }

        /// <summary>
        /// Returns the Bevel type or null if the package is missing.
        /// </summary>
        public static Type GetBevelType() { return BevelType.Value; }

        /// <summary>
        /// Returns the DeleteElements type or null if the package is missing.
        /// </summary>
        public static Type GetDeleteElementsType() { return DeleteElementsType.Value; }

        /// <summary>
        /// Returns the AppendElements type or null if the package is missing.
        /// </summary>
        public static Type GetAppendElementsType() { return AppendElementsType.Value; }

        /// <summary>
        /// Returns the ConnectElements type or null if the package is missing.
        /// </summary>
        public static Type GetConnectElementsType() { return ConnectElementsType.Value; }

        /// <summary>
        /// Returns the MergeElements type or null if the package is missing.
        /// </summary>
        public static Type GetMergeElementsType() { return MergeElementsType.Value; }

        /// <summary>
        /// Returns the CombineMeshes type or null if the package is missing.
        /// </summary>
        public static Type GetCombineMeshesType() { return CombineMeshesType.Value; }

        /// <summary>
        /// Returns the VertexEditing type or null if the package is missing.
        /// </summary>
        public static Type GetVertexEditingType() { return VertexEditingType.Value; }

        /// <summary>
        /// Returns the MeshImporter type or null if the package is missing.
        /// </summary>
        public static Type GetMeshImporterType() { return MeshImporterType.Value; }

        /// <summary>
        /// Returns the ElementSelection type or null if the package is missing.
        /// </summary>
        public static Type GetElementSelectionType() { return ElementSelectionType.Value; }

        /// <summary>
        /// Returns the PivotLocation type or null if the package is missing.
               /// </summary>
        public static Type GetPivotLocationType() { return PivotLocationType.Value; }

        /// <summary>
        /// Returns the Axis type or null if the package is missing.
        /// </summary>
        public static Type GetAxisType() { return AxisType.Value; }

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
            if (faceType == null) return null;
            var faceListType = typeof(List<>).MakeGenericType(faceType);
            var faceList = Activator.CreateInstance(faceListType) as System.Collections.IList;
            foreach (var f in faces)
                faceList.Add(f);
            return faceList;
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
                // Try Extrude(ProBuilderMesh, Edge[], float, bool, bool)
                var method = extrudeElements.GetMethod("Extrude",
                    BindingFlags.Static | BindingFlags.Public,
                    null,
                    new[] { ProBuilderMeshType.Value, edgeArray.GetType(), typeof(float), typeof(bool), typeof(bool) },
                    null);

                if (method != null)
                {
                    method.Invoke(null, new object[] { mesh, edgeArray, distance, asGroup, true });
                    return edgeCount;
                }

                // Fallback: Extrude(ProBuilderMesh, Edge[], ExtrudeMethod, float)
                var extrudeMethodEnum = ExtrudeMethodType.Value;
                if (extrudeMethodEnum != null)
                {
                    var faceMethod = extrudeElements.GetMethod("Extrude",
                        BindingFlags.Static | BindingFlags.Public,
                        null,
                        new[] { ProBuilderMeshType.Value, edgeArray.GetType(), extrudeMethodEnum, typeof(float) },
                        null);
                    if (faceMethod != null)
                    {
                        // ExtrudeMethod.VertexNormal = 0
                        var extrudeMethodVal = Enum.ToObject(extrudeMethodEnum, 0);
                        faceMethod.Invoke(null, new object[] { mesh, edgeArray, extrudeMethodVal, distance });
                        return edgeCount;
                    }
                }
            }
            catch (Exception) { }
            return 0;
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
                // Try Bridge(ProBuilderMesh, Edge, Edge, bool)
                var method = appendElements.GetMethod("Bridge",
                    BindingFlags.Static | BindingFlags.Public,
                    null,
                    new[] { ProBuilderMeshType.Value, edgeType, edgeType, typeof(bool) },
                    null);

                if (method != null)
                {
                    method.Invoke(null, new object[] { mesh, edgeA, edgeB, allowNonManifold });
                    return true;
                }

                // Fallback: Bridge(ProBuilderMesh, Edge, Edge)
                var methodNoFlag = appendElements.GetMethod("Bridge",
                    BindingFlags.Static | BindingFlags.Public,
                    null,
                    new[] { ProBuilderMeshType.Value, edgeType, edgeType },
                    null);

                if (methodNoFlag != null)
                {
                    methodNoFlag.Invoke(null, new object[] { mesh, edgeA, edgeB });
                    return true;
                }
            }
            catch (Exception) { }
            return false;
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
                var method = appendType.GetMethod("AppendVerticesToEdge",
                    BindingFlags.Static | BindingFlags.Public,
                    null,
                    new[] { ProBuilderMeshType.Value, typedList.GetType(), typeof(int) },
                    null);

                if (method != null)
                {
                    method.Invoke(null, new object[] { mesh, typedList, count });
                    return true;
                }

                // Try any 3-param overload
                var altMethod = appendType.GetMethods(BindingFlags.Static | BindingFlags.Public)
                    .FirstOrDefault(m => m.Name == "AppendVerticesToEdge" && m.GetParameters().Length == 3
                        && m.GetParameters()[2].ParameterType == typeof(int));
                if (altMethod != null)
                {
                    altMethod.Invoke(null, new object[] { mesh, typedList, count });
                    return true;
                }
            }
            catch (Exception) { }
            return false;
        }

        /// <summary>
        /// Call AppendElements.InsertVertexOnEdge via reflection.
        /// Returns the new vertex index or -1.
        /// </summary>
        public static int InsertVertexOnEdgeByPair(object mesh, int edgeA, int edgeB, Vector3 point)
        {
            var appendType = AppendElementsType.Value;
            if (appendType == null || mesh == null) return -1;

            var edge = CreateEdge(edgeA, edgeB);
            if (edge == null) return -1;

            try
            {
                var method = appendType.GetMethod("InsertVertexOnEdge",
                    BindingFlags.Static | BindingFlags.Public,
                    null,
                    new[] { ProBuilderMeshType.Value, EdgeType.Value, typeof(Vector3) },
                    null);

                if (method != null)
                    return (int)method.Invoke(null, new object[] { mesh, edge, point });
            }
            catch (Exception) { }
            return -1;
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

                if (method != null)
                {
                    method.Invoke(null, new object[] { mesh, face, point });
                    return true;
                }
            }
            catch (Exception) { }
            return false;
        }

        /// <summary>
        /// Call VertexEditing.MergeVertices via reflection.
        /// </summary>
        public static int MergeVerticesWithCollapse(object mesh, int[] vertexIndices, bool collapseToFirst)
        {
            var vertexEditing = VertexEditingType.Value;
            if (vertexEditing == null || mesh == null) return 0;

            try
            {
                var method = vertexEditing.GetMethod("MergeVertices",
                    BindingFlags.Static | BindingFlags.Public);
                if (method != null)
                {
                    var result = method.Invoke(null, new object[] { mesh, vertexIndices, collapseToFirst });
                    return result is int idx ? idx : vertexIndices.Length;
                }
            }
            catch (Exception) { }
            return MergeVertices(mesh, vertexIndices);
        }

        /// <summary>
        /// Call VertexEditing.WeldVertices via reflection (with radius).
        /// </summary>
        public static int WeldVerticesWithRadius(object mesh, int[] vertexIndices, float radius)
        {
            var vertexEditing = VertexEditingType.Value;
            if (vertexEditing == null || mesh == null) return 0;

            try
            {
                var method = vertexEditing.GetMethod("WeldVertices",
                    BindingFlags.Static | BindingFlags.Public);
                if (method != null)
                {
                    var result = method.Invoke(null, new object[] { mesh, vertexIndices.ToList(), radius });
                    if (result is int[] arr) return arr.Length;
                    if (result is int count) return count;
                    return vertexIndices.Length;
                }
            }
            catch (Exception) { }
            // Fallback to old WeldVertices method
            return WeldVertices(mesh, vertexIndices, radius);
        }

        /// <summary>
        /// Call VertexEditing.SplitVertices via reflection (with IList overload).
        /// </summary>
        public static int SplitVerticesWithList(object mesh, int[] vertexIndices)
        {
            var vertexEditing = VertexEditingType.Value;
            if (vertexEditing == null || mesh == null) return 0;

            try
            {
                var method = vertexEditing.GetMethod("SplitVertices",
                    BindingFlags.Static | BindingFlags.Public,
                    null,
                    new[] { ProBuilderMeshType.Value, typeof(IEnumerable<int>) },
                    null);

                if (method != null)
                {
                    method.Invoke(null, new object[] { mesh, vertexIndices.ToList() });
                    return vertexIndices.Length;
                }
            }
            catch (Exception) { }
            return SplitVertices(mesh, vertexIndices);
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

        /// <summary>
        /// Call MeshOperations.RemoveDegenerateTriangles via reflection.
        /// </summary>
        public static bool RemoveDegenerateTriangles(object mesh)
        {
            var meshOps = MeshOperationsType.Value;
            if (meshOps == null || mesh == null) return false;
            try
            {
                var method = meshOps.GetMethod("RemoveDegenerateTriangles",
                    new[] { ProBuilderMeshType.Value });
                if (method != null)
                {
                    method.Invoke(null, new object[] { mesh });
                    return true;
                }
            }
            catch (Exception) { }
            return false;
        }

        /// <summary>
        /// Call MeshOperations.RemoveUnusedVertices via reflection.
        /// </summary>
        public static bool RemoveUnusedVertices(object mesh)
        {
            var meshOps = MeshOperationsType.Value;
            if (meshOps == null || mesh == null) return false;
            try
            {
                var method = meshOps.GetMethod("RemoveUnusedVertices",
                    new[] { ProBuilderMeshType.Value });
                if (method != null)
                {
                    method.Invoke(null, new object[] { mesh });
                    return true;
                }
            }
            catch (Exception) { }
            return false;
        }

        /// <summary>
        /// Call MeshImporter to convert a standard mesh to ProBuilder mesh.
        /// </summary>
        public static object ConvertViaMeshImporter(GameObject go, object pbMesh)
        {
            var meshImporter = MeshImporterType.Value;
            if (meshImporter == null) return null;
            try
            {
                var meshFilter = go.GetComponent<MeshFilter>();
                if (meshFilter == null || meshFilter.sharedMesh == null) return null;

                // Try MeshImporter(ProBuilderMesh) constructor
                var ctor = meshImporter.GetConstructor(new[] { ProBuilderMeshType.Value });
                if (ctor == null) return null;
                var importer = ctor.Invoke(new object[] { pbMesh });

                var importMethod = meshImporter.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                    .Where(m => m.Name == "Import")
                    .OrderBy(m => m.GetParameters().Length)
                    .FirstOrDefault();
                if (importMethod != null)
                {
                    var importParams = importMethod.GetParameters();
                    if (importParams.Length == 0)
                        importMethod.Invoke(importer, null);
                    else
                        importMethod.Invoke(importer, new object[] { null });
                }
                return pbMesh;
            }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// Call CombineMeshes.Combine via reflection.
        /// </summary>
        public static bool CombineMeshesViaReflection(object[] pbMeshes, object targetMesh)
        {
            var combineType = CombineMeshesType.Value;
            if (combineType == null) return false;
            try
            {
                var listType = typeof(List<>).MakeGenericType(ProBuilderMeshType.Value);
                var typedList = Activator.CreateInstance(listType) as System.Collections.IList;
                foreach (var m in pbMeshes)
                    typedList.Add(m);
                var method = combineType.GetMethod("Combine", BindingFlags.Static | BindingFlags.Public);
                if (method != null)
                {
                    method.Invoke(null, new object[] { typedList, targetMesh });
                    return true;
                }
            }
            catch (Exception) { }
            return false;
        }

        /// <summary>
        /// Call DeleteElements.DetachFaces via reflection.
        /// Returns the detached GameObject or null.
        /// </summary>
        public static GameObject DetachFacesViaExtrudeElements(object mesh, int[] faceIndices, bool deleteSource)
        {
            var extrudeElements = ExtrudeElementsType.Value;
            if (extrudeElements == null || mesh == null) return null;

            try
            {
                var faces = GetFacesByIndices(mesh, faceIndices);
                if (faces == null) return null;
                var faceList = ToTypedFaceList(faces);
                if (faceList == null) return null;

                // Try DetachFaces(ProBuilderMesh, IEnumerable<Face>, bool)
                var method = extrudeElements.GetMethods(BindingFlags.Static | BindingFlags.Public)
                    .FirstOrDefault(m => m.Name == "DetachFaces" && m.GetParameters().Length == 3
                        && m.GetParameters()[1].ParameterType.IsAssignableFrom(faceList.GetType())
                        && m.GetParameters()[2].ParameterType == typeof(bool));

                if (method != null)
                {
                    method.Invoke(null, new object[] { mesh, faceList, deleteSource });
                    // DetachFaces in the ExtrudeElements namespace creates a new GameObject
                    // We need to find it by looking at newly created objects
                    return FindDetachedGameObject(mesh);
                }

                // Fallback: DetachFaces(ProBuilderMesh, IEnumerable<Face>)
                var methodNoFlag = extrudeElements.GetMethods(BindingFlags.Static | BindingFlags.Public)
                    .FirstOrDefault(m => m.Name == "DetachFaces" && m.GetParameters().Length == 2
                        && m.GetParameters()[1].ParameterType.IsAssignableFrom(faceList.GetType()));

                if (methodNoFlag != null)
                {
                    methodNoFlag.Invoke(null, new object[] { mesh, faceList });
                    return FindDetachedGameObject(mesh);
                }
            }
            catch (Exception) { }
            return null;
        }

        /// <summary>
        /// Try to find the newly detached GameObject after DetachFaces.
        /// </summary>
        private static GameObject FindDetachedGameObject(object mesh)
        {
            // DetachFaces should return the new object via the method result or
            // create a new sibling in the scene.
            // Check for new sibling GameObjects with ProBuilderMesh
            if (mesh == null) return null;
            try
            {
                var meshComponent = mesh as Component;
                if (meshComponent == null) return null;
                var parent = meshComponent.transform.parent;
                // Search siblings for new ProBuilderMesh components
                var siblingCount = parent != null ? parent.childCount : 0;
                for (int i = 0; i < siblingCount; i++)
                {
                    var child = parent.GetChild(i);
                    if (child.gameObject != meshComponent.gameObject && HasProBuilderMesh(child.gameObject))
                        return child.gameObject;
                }
                // Also search root objects in the scene
                for (int s = 0; s < UnityEngine.SceneManagement.SceneManager.sceneCount; s++)
                {
                    var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(s);
                    foreach (var root in scene.GetRootGameObjects())
                    {
                        if (root != meshComponent.gameObject && HasProBuilderMesh(root))
                            return root;
                    }
                }
            }
            catch (Exception) { }
            return null;
        }

        /// <summary>
        /// Call MergeElements.Merge via reflection for faces.
        /// </summary>
        public static bool MergeFacesViaMergeElements(object mesh, int[] faceIndices)
        {
            var mergeType = MergeElementsType.Value;
            if (mergeType == null || mesh == null) return false;

            try
            {
                var faces = GetFacesByIndices(mesh, faceIndices);
                if (faces == null) return false;
                var faceList = ToTypedFaceList(faces);
                if (faceList == null) return false;

                var method = mergeType.GetMethods(BindingFlags.Static | BindingFlags.Public)
                    .FirstOrDefault(m => m.Name == "Merge" && m.GetParameters().Length == 2
                        && m.GetParameters()[1].ParameterType.IsAssignableFrom(faceList.GetType()));

                if (method != null)
                {
                    method.Invoke(null, new object[] { mesh, faceList });
                    return true;
                }
            }
            catch (Exception) { }
            return false;
        }

        /// <summary>
        /// Call DeleteElements.DeleteFaces via reflection.
        /// </summary>
        public static bool DeleteFacesViaDeleteElements(object mesh, int[] faceIndices)
        {
            var deleteType = DeleteElementsType.Value;
            if (deleteType == null || mesh == null) return false;

            try
            {
                // Try DeleteFaces(ProBuilderMesh, IList<int>)
                var method = deleteType.GetMethod("DeleteFaces",
                    BindingFlags.Static | BindingFlags.Public,
                    null,
                    new[] { ProBuilderMeshType.Value, typeof(IList<int>) },
                    null);

                if (method != null)
                {
                    method.Invoke(null, new object[] { mesh, faceIndices.ToList() });
                    return true;
                }

                // Fallback: int[] overload
                var methodArr = deleteType.GetMethod("DeleteFaces",
                    BindingFlags.Static | BindingFlags.Public,
                    null,
                    new[] { ProBuilderMeshType.Value, typeof(int[]) },
                    null);

                if (methodArr != null)
                {
                    methodArr.Invoke(null, new object[] { mesh, faceIndices });
                    return true;
                }
            }
            catch (Exception) { }
            // Fallback to old method
            DeleteFaces(mesh, faceIndices);
            return true;
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
            if (mesh == null) return false;
            try
            {
                var method = ProBuilderMeshType.Value?.GetMethod("SetMaterial",
                    BindingFlags.Instance | BindingFlags.Public);
                if (method != null)
                {
                    method.Invoke(mesh, new object[] { faces, material });
                    return true;
                }
            }
            catch (Exception) { }
            return false;
        }

        /// <summary>
        /// Get the faceCount property directly from ProBuilderMesh.
        /// </summary>
        public static int GetFaceCountProperty(object mesh)
        {
            if (mesh == null) return -1;
            try
            {
                var prop = ProBuilderMeshType.Value?.GetProperty("faceCount");
                return prop != null ? (int)prop.GetValue(mesh) : GetFaceCount(mesh);
            }
            catch (Exception) { return GetFaceCount(mesh); }
        }

        /// <summary>
        /// Get the vertexCount property directly from ProBuilderMesh.
        /// </summary>
        public static int GetVertexCountProperty(object mesh)
        {
            if (mesh == null) return -1;
            try
            {
                var prop = ProBuilderMeshType.Value?.GetProperty("vertexCount");
                return prop != null ? (int)prop.GetValue(mesh) : GetVertexCount(mesh);
            }
            catch (Exception) { return GetVertexCount(mesh); }
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
