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
    // ──────────────────────────────────────────────────────────────
    // Vertex tools: merge-vertices, weld-vertices, split-vertices,
    // move-vertices, insert-vertex, append-vertices-to-edge,
    // create-polygon
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// probuilder.merge-vertices — Merge selected vertices into one.
    /// </summary>
    [UnityCliTool(
        "probuilder.merge-vertices",
        Summary = "ProBuilder Merge Vertices",
        Description = "Merge selected vertices on a ProBuilder mesh into a single vertex.",
        Group = "probuilder",
        InputSchema = "{\"type\":\"object\",\"properties\":{\"target\":{\"type\":\"string\"},\"vertexIndices\":{\"type\":\"array\",\"items\":{\"type\":\"integer\"}},\"collapseToFirst\":{\"type\":\"boolean\"}},\"required\":[\"target\",\"vertexIndices\"]}",
        OutputSchema = "{\"type\":\"object\",\"properties\":{\"mergedVertexCount\":{\"type\":\"integer\"}}}",
        DestructiveHint = false,
        Target = "editor",
        DefaultTimeoutMs = 10000,
        MaxTimeoutMs = 30000,
        SupportsJob = true,
        ArtifactKinds = "json",
        RequiredInput = "target,vertexIndices")]
    public static class ProBuilderMergeVerticesTool
    {
        public static string Execute(UnityCliProjectToolContext context)
        {
            if (!ProBuilderReflection.IsAvailable)
                throw ProBuilderErrors.PackageNotAvailable();

            var go = ProBuilderInput.ResolveTarget(context.InputJson);
            if (go == null) throw ProBuilderErrors.TargetNotFound(ProBuilderInput.StringArg(context.InputJson, "target"));

            var mesh = ProBuilderReflection.GetProBuilderMesh(go);
            if (mesh == null) throw ProBuilderErrors.OperationFailed("merge-vertices", "No ProBuilderMesh on target.");

            var vertexIndices = ProBuilderInput.IntArrayArg(context.InputJson, "vertexIndices");
            if (vertexIndices == null || vertexIndices.Length < 2)
                throw ProBuilderErrors.SchemaInvalid("At least 2 vertexIndices required for merge-vertices.");

            var collapseToFirst = ProBuilderInput.BoolArg(context.InputJson, "collapseToFirst");

            Undo.RecordObject(go, "ProBuilder Merge Vertices");
            var merged = ProBuilderReflection.MergeVerticesWithCollapse(mesh, vertexIndices, collapseToFirst);
            ProBuilderReflection.RefreshMesh(mesh);
            EditorSceneManager.MarkSceneDirty(go.scene);

            var result = new UnityCliJsonWriter().BeginObject()
                .Prop("mergedVertexCount", merged)
                .EndObject().ToString();

            return result;
        }
    }

    /// <summary>
    /// probuilder.weld-vertices — Weld vertices within a radius.
    /// </summary>
    [UnityCliTool(
        "probuilder.weld-vertices",
        Summary = "ProBuilder Weld Vertices",
        Description = "Weld vertices within a radius on a ProBuilder mesh.",
        Group = "probuilder",
        InputSchema = "{\"type\":\"object\",\"properties\":{\"target\":{\"type\":\"string\"},\"vertexIndices\":{\"type\":\"array\",\"items\":{\"type\":\"integer\"}},\"radius\":{\"type\":\"number\"}},\"required\":[\"target\"]}",
        OutputSchema = "{\"type\":\"object\",\"properties\":{\"weldedCount\":{\"type\":\"integer\"}}}",
        DestructiveHint = false,
        Target = "editor",
        DefaultTimeoutMs = 10000,
        MaxTimeoutMs = 30000,
        SupportsJob = true,
        ArtifactKinds = "json",
        RequiredInput = "target")]
    public static class ProBuilderWeldVerticesTool
    {
        public static string Execute(UnityCliProjectToolContext context)
        {
            if (!ProBuilderReflection.IsAvailable)
                throw ProBuilderErrors.PackageNotAvailable();

            var go = ProBuilderInput.ResolveTarget(context.InputJson);
            if (go == null) throw ProBuilderErrors.TargetNotFound(ProBuilderInput.StringArg(context.InputJson, "target"));

            var mesh = ProBuilderReflection.GetProBuilderMesh(go);
            if (mesh == null) throw ProBuilderErrors.OperationFailed("weld-vertices", "No ProBuilderMesh on target.");

            var vertexIndices = ProBuilderInput.IntArrayArg(context.InputJson, "vertexIndices");
            var radius = ProBuilderInput.FloatArg(context.InputJson, "radius", 0.01f);

            Undo.RecordObject(go, "ProBuilder Weld Vertices");
            var welded = ProBuilderReflection.WeldVerticesWithRadius(mesh, vertexIndices ?? new int[0], radius);
            ProBuilderReflection.RefreshMesh(mesh);
            EditorSceneManager.MarkSceneDirty(go.scene);

            var result = new UnityCliJsonWriter().BeginObject()
                .Prop("weldedCount", welded)
                .EndObject().ToString();

            return result;
        }
    }

    /// <summary>
    /// probuilder.split-vertices — Split shared vertices.
    /// </summary>
    [UnityCliTool(
        "probuilder.split-vertices",
        Summary = "ProBuilder Split Vertices",
        Description = "Split shared vertices on a ProBuilder mesh into separate vertices per face.",
        Group = "probuilder",
        InputSchema = "{\"type\":\"object\",\"properties\":{\"target\":{\"type\":\"string\"},\"vertexIndices\":{\"type\":\"array\",\"items\":{\"type\":\"integer\"}}},\"required\":[\"target\",\"vertexIndices\"]}",
        OutputSchema = "{\"type\":\"object\",\"properties\":{\"splitCount\":{\"type\":\"integer\"}}}",
        DestructiveHint = false,
        Target = "editor",
        DefaultTimeoutMs = 10000,
        MaxTimeoutMs = 30000,
        SupportsJob = true,
        ArtifactKinds = "json",
        RequiredInput = "target,vertexIndices")]
    public static class ProBuilderSplitVerticesTool
    {
        public static string Execute(UnityCliProjectToolContext context)
        {
            if (!ProBuilderReflection.IsAvailable)
                throw ProBuilderErrors.PackageNotAvailable();

            var go = ProBuilderInput.ResolveTarget(context.InputJson);
            if (go == null) throw ProBuilderErrors.TargetNotFound(ProBuilderInput.StringArg(context.InputJson, "target"));

            var mesh = ProBuilderReflection.GetProBuilderMesh(go);
            if (mesh == null) throw ProBuilderErrors.OperationFailed("split-vertices", "No ProBuilderMesh on target.");

            var vertexIndices = ProBuilderInput.IntArrayArg(context.InputJson, "vertexIndices");
            if (vertexIndices == null || vertexIndices.Length == 0)
                throw ProBuilderErrors.SchemaInvalid("vertexIndices is required for split-vertices.");

            Undo.RecordObject(go, "ProBuilder Split Vertices");
            var split = ProBuilderReflection.SplitVerticesWithList(mesh, vertexIndices);
            ProBuilderReflection.RefreshMesh(mesh);
            EditorSceneManager.MarkSceneDirty(go.scene);

            var result = new UnityCliJsonWriter().BeginObject()
                .Prop("splitCount", split)
                .EndObject().ToString();

            return result;
        }
    }

    /// <summary>
    /// probuilder.move-vertices — Move vertices by an offset.
    /// </summary>
    [UnityCliTool(
        "probuilder.move-vertices",
        Summary = "ProBuilder Move Vertices",
        Description = "Move selected vertices on a ProBuilder mesh by an offset.",
        Group = "probuilder",
        InputSchema = "{\"type\":\"object\",\"properties\":{\"target\":{\"type\":\"string\"},\"vertexIndices\":{\"type\":\"array\",\"items\":{\"type\":\"integer\"}},\"x\":{\"type\":\"number\"},\"y\":{\"type\":\"number\"},\"z\":{\"type\":\"number\"}},\"required\":[\"target\",\"vertexIndices\"]}",
        OutputSchema = "{\"type\":\"object\",\"properties\":{\"movedCount\":{\"type\":\"integer\"}}}",
        DestructiveHint = false,
        Target = "editor",
        DefaultTimeoutMs = 10000,
        MaxTimeoutMs = 30000,
        SupportsJob = true,
        ArtifactKinds = "json",
        RequiredInput = "target,vertexIndices")]
    public static class ProBuilderMoveVerticesTool
    {
        public static string Execute(UnityCliProjectToolContext context)
        {
            if (!ProBuilderReflection.IsAvailable)
                throw ProBuilderErrors.PackageNotAvailable();

            var go = ProBuilderInput.ResolveTarget(context.InputJson);
            if (go == null) throw ProBuilderErrors.TargetNotFound(ProBuilderInput.StringArg(context.InputJson, "target"));

            var mesh = ProBuilderReflection.GetProBuilderMesh(go);
            if (mesh == null) throw ProBuilderErrors.OperationFailed("move-vertices", "No ProBuilderMesh on target.");

            var vertexIndices = ProBuilderInput.IntArrayArg(context.InputJson, "vertexIndices");
            if (vertexIndices == null || vertexIndices.Length == 0)
                throw ProBuilderErrors.SchemaInvalid("vertexIndices is required for move-vertices.");

            var x = ProBuilderInput.FloatArg(context.InputJson, "x", 0f);
            var y = ProBuilderInput.FloatArg(context.InputJson, "y", 0f);
            var z = ProBuilderInput.FloatArg(context.InputJson, "z", 0f);
            var offset = new Vector3(x, y, z);

            Undo.RecordObject(go, "ProBuilder Move Vertices");
            var moved = ProBuilderReflection.MoveVerticesByOffset(mesh, vertexIndices, offset);
            ProBuilderReflection.RefreshMesh(mesh);
            EditorSceneManager.MarkSceneDirty(go.scene);

            var result = new UnityCliJsonWriter().BeginObject()
                .Prop("movedCount", moved)
                .EndObject().ToString();

            return result;
        }
    }

    /// <summary>
    /// probuilder.insert-vertex — Insert a new vertex on an edge or face.
    /// </summary>
    [UnityCliTool(
        "probuilder.insert-vertex",
        Summary = "ProBuilder Insert Vertex",
        Description = "Insert a new vertex on an edge or face of a ProBuilder mesh.",
        Group = "probuilder",
        InputSchema = "{\"type\":\"object\",\"properties\":{\"target\":{\"type\":\"string\"},\"edge\":{\"type\":\"object\"},\"faceIndex\":{\"type\":\"integer\"},\"point\":{\"type\":\"object\",\"properties\":{\"x\":{\"type\":\"number\"},\"y\":{\"type\":\"number\"},\"z\":{\"type\":\"number\"}}}},\"required\":[\"target\"]}",
        OutputSchema = "{\"type\":\"object\",\"properties\":{\"insertedVertexIndex\":{\"type\":\"integer\"}}}",
        DestructiveHint = false,
        Target = "editor",
        DefaultTimeoutMs = 10000,
        MaxTimeoutMs = 30000,
        SupportsJob = true,
        ArtifactKinds = "json",
        RequiredInput = "target")]
    public static class ProBuilderInsertVertexTool
    {
        public static string Execute(UnityCliProjectToolContext context)
        {
            if (!ProBuilderReflection.IsAvailable)
                throw ProBuilderErrors.PackageNotAvailable();

            var go = ProBuilderInput.ResolveTarget(context.InputJson);
            if (go == null) throw ProBuilderErrors.TargetNotFound(ProBuilderInput.StringArg(context.InputJson, "target"));

            var mesh = ProBuilderReflection.GetProBuilderMesh(go);
            if (mesh == null) throw ProBuilderErrors.OperationFailed("insert-vertex", "No ProBuilderMesh on target.");

            var x = ProBuilderInput.FloatArg(context.InputJson, "x", 0f);
            var y = ProBuilderInput.FloatArg(context.InputJson, "y", 0f);
            var z = ProBuilderInput.FloatArg(context.InputJson, "z", 0f);
            var point = new Vector3(x, y, z);

            Undo.RecordObject(go, "ProBuilder Insert Vertex");

            var edgePair = ProBuilderInput.EdgeArg(context.InputJson, "edge");
            var faceIndex = ProBuilderInput.IntArg(context.InputJson, "faceIndex");

            int insertedIndex = -1;
            if (edgePair != null)
            {
                insertedIndex = ProBuilderReflection.InsertVertexOnEdgeByPair(mesh, edgePair.Value.a, edgePair.Value.b, point);
            }
            else if (faceIndex.HasValue)
            {
                var inserted = ProBuilderReflection.InsertVertexInFace(mesh, faceIndex.Value, point);
                if (inserted) insertedIndex = ProBuilderReflection.GetVertexCount(mesh) - 1;
            }
            else
            {
                // Fallback: try old method with null edge
                insertedIndex = ProBuilderReflection.InsertVertexOnEdge(mesh, null, point);
            }

            ProBuilderReflection.RefreshMesh(mesh);
            EditorSceneManager.MarkSceneDirty(go.scene);

            var result = new UnityCliJsonWriter().BeginObject()
                .Prop("insertedVertexIndex", insertedIndex)
                .EndObject().ToString();

            return result;
        }
    }

    /// <summary>
    /// probuilder.append-vertices-to-edge — Append vertices along an edge.
    /// </summary>
    [UnityCliTool(
        "probuilder.append-vertices-to-edge",
        Summary = "ProBuilder Append Vertices to Edge",
        Description = "Append new vertices along an edge of a ProBuilder mesh.",
        Group = "probuilder",
        InputSchema = "{\"type\":\"object\",\"properties\":{\"target\":{\"type\":\"string\"},\"edgeIndices\":{\"type\":\"array\"},\"edges\":{\"type\":\"array\"},\"count\":{\"type\":\"integer\"}},\"required\":[\"target\"]}",
        OutputSchema = "{\"type\":\"object\",\"properties\":{\"insertedCount\":{\"type\":\"integer\"}}}",
        DestructiveHint = false,
        Target = "editor",
        DefaultTimeoutMs = 10000,
        MaxTimeoutMs = 30000,
        SupportsJob = true,
        ArtifactKinds = "json",
        RequiredInput = "target")]
    public static class ProBuilderAppendVerticesToEdgeTool
    {
        public static string Execute(UnityCliProjectToolContext context)
        {
            if (!ProBuilderReflection.IsAvailable)
                throw ProBuilderErrors.PackageNotAvailable();

            var go = ProBuilderInput.ResolveTarget(context.InputJson);
            if (go == null) throw ProBuilderErrors.TargetNotFound(ProBuilderInput.StringArg(context.InputJson, "target"));

            var mesh = ProBuilderReflection.GetProBuilderMesh(go);
            if (mesh == null) throw ProBuilderErrors.OperationFailed("append-vertices-to-edge", "No ProBuilderMesh on target.");

            var count = ProBuilderInput.IntArg(context.InputJson, "count", 1);
            var edgeIndices = ProBuilderInput.IntArrayArg(context.InputJson, "edgeIndices");
            var edgePairs = ProBuilderInput.EdgePairArrayArg(context.InputJson, "edges");

            if ((edgeIndices == null || edgeIndices.Length == 0) && (edgePairs == null || edgePairs.Length == 0))
                throw ProBuilderErrors.SchemaInvalid("edgeIndices or edges parameter is required for append-vertices-to-edge.");

            Undo.RecordObject(go, "ProBuilder Append Vertices");

            var edgeArray = ProBuilderReflection.ResolveEdges(mesh, edgeIndices, edgePairs);
            if (edgeArray == null || edgeArray.Length == 0)
                throw ProBuilderErrors.OperationFailed("append-vertices-to-edge", "Could not resolve edges from input.");

            var appended = ProBuilderReflection.AppendVerticesToEdge(mesh, edgeArray, count);
            if (!appended)
                throw ProBuilderErrors.OperationFailed("append-vertices-to-edge", "AppendElements.AppendVerticesToEdge method not found or failed in ProBuilder assembly.");

            ProBuilderReflection.RefreshMesh(mesh);
            EditorSceneManager.MarkSceneDirty(go.scene);

            var result = new UnityCliJsonWriter().BeginObject()
                .Prop("insertedCount", count * edgeArray.Length)
                .EndObject().ToString();

            return result;
        }
    }

    /// <summary>
    /// probuilder.create-polygon — Create a polygon face from vertices.
    /// </summary>
    [UnityCliTool(
        "probuilder.create-polygon",
        Summary = "ProBuilder Create Polygon",
        Description = "Create a new polygon face from selected vertices on a ProBuilder mesh.",
        Group = "probuilder",
        InputSchema = "{\"type\":\"object\",\"properties\":{\"target\":{\"type\":\"string\"},\"vertexIndices\":{\"type\":\"array\",\"items\":{\"type\":\"integer\"}},\"unordered\":{\"type\":\"boolean\"}},\"required\":[\"target\",\"vertexIndices\"]}",
        OutputSchema = "{\"type\":\"object\",\"properties\":{\"newFaceIndex\":{\"type\":\"integer\"}}}",
        DestructiveHint = false,
        Target = "editor",
        DefaultTimeoutMs = 10000,
        MaxTimeoutMs = 30000,
        SupportsJob = true,
        ArtifactKinds = "json",
        RequiredInput = "target,vertexIndices")]
    public static class ProBuilderCreatePolygonTool
    {
        public static string Execute(UnityCliProjectToolContext context)
        {
            if (!ProBuilderReflection.IsAvailable)
                throw ProBuilderErrors.PackageNotAvailable();

            var go = ProBuilderInput.ResolveTarget(context.InputJson);
            if (go == null) throw ProBuilderErrors.TargetNotFound(ProBuilderInput.StringArg(context.InputJson, "target"));

            var mesh = ProBuilderReflection.GetProBuilderMesh(go);
            if (mesh == null) throw ProBuilderErrors.OperationFailed("create-polygon", "No ProBuilderMesh on target.");

            var vertexIndices = ProBuilderInput.IntArrayArg(context.InputJson, "vertexIndices");
            if (vertexIndices == null || vertexIndices.Length < 3)
                throw ProBuilderErrors.SchemaInvalid("At least 3 vertexIndices required for create-polygon.");

            var unordered = ProBuilderInput.BoolArg(context.InputJson, "unordered", true);

            Undo.RecordObject(go, "ProBuilder Create Polygon");
            var created = ProBuilderReflection.CreatePolygon(mesh, vertexIndices, unordered);
            if (!created)
                throw ProBuilderErrors.OperationFailed("create-polygon", "AppendElements.CreatePolygon method not found or failed in ProBuilder assembly.");

            ProBuilderReflection.RefreshMesh(mesh);
            EditorSceneManager.MarkSceneDirty(go.scene);

            var faceCount = ProBuilderReflection.GetFaceCount(mesh);
            var result = new UnityCliJsonWriter().BeginObject()
                .Prop("newFaceIndex", faceCount - 1)
                .EndObject().ToString();

            return result;
        }
    }
}
#endif
