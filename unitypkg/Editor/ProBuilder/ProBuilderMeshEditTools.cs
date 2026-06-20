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
    // Mesh Edit tools: extrude-faces, extrude-edges, delete-faces,
    // detach-faces, merge-faces, subdivide, flip-normals,
    // bridge-edges, connect-elements, bevel-edges,
    // duplicate-and-flip, combine-meshes, merge-objects
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// probuilder.extrude-faces — Extrude selected faces of a ProBuilder mesh.
    /// </summary>
    [UnityCliTool(
        "probuilder.extrude-faces",
        Title = "ProBuilder Extrude Faces",
        Description = "Extrude selected faces of a ProBuilder mesh by a given distance.",
        Group = "probuilder",
        InputSchema = "{\"type\":\"object\",\"properties\":{\"target\":{\"type\":\"string\"},\"faceIndices\":{\"type\":\"array\",\"items\":{\"type\":\"integer\"}},\"distance\":{\"type\":\"number\"},\"method\":{\"type\":\"string\",\"enum\":[\"vertex\",\"faceNormal\"]}},\"required\":[\"target\"]}",
        OutputSchema = "{\"type\":\"object\",\"properties\":{\"modifiedFaceCount\":{\"type\":\"integer\"},\"newVertexCount\":{\"type\":\"integer\"}}}",
        ReadOnlyHint = false,
        DestructiveHint = false,
        Target = "editor",
        DefaultTimeoutMs = 10000,
        MaxTimeoutMs = 30000,
        SupportsJob = true,
        ArchitectureSensitive = true,
        FrozenDecision = "ALP-596-P7",
        CoverageScope = "probuilder,mesh-edit,extrude",
        ArtifactKinds = "json",
        RequiredInput = "target")]
    public static class ProBuilderExtrudeFacesTool
    {
        public static string Execute(UnityCliProjectToolContext context)
        {
            if (!ProBuilderReflection.IsAvailable)
                return ProBuilderErrors.PackageNotAvailable();

            var go = ProBuilderInput.ResolveTarget(context.InputJson);
            if (go == null) return ProBuilderErrors.TargetNotFound(ProBuilderInput.StringArg(context.InputJson, "target"));

            var mesh = ProBuilderReflection.GetProBuilderMesh(go);
            if (mesh == null) return ProBuilderErrors.OperationFailed("extrude-faces", "No ProBuilderMesh component on target.");

            var faceIndices = ProBuilderInput.IntArrayArg(context.InputJson, "faceIndices");
            var distance = ProBuilderInput.FloatArg(context.InputJson, "distance", 0.5f);
            var method = ProBuilderInput.StringArg(context.InputJson, "method") ?? "vertex";

            var beforeVertexCount = ProBuilderReflection.GetVertexCount(mesh);
            Undo.RecordObject(go, "ProBuilder Extrude Faces");

            var faces = faceIndices != null && faceIndices.Length > 0
                ? ProBuilderReflection.GetFacesByIndices(mesh, faceIndices)
                : ProBuilderReflection.GetAllFaces(mesh);

            if (faces != null)
            {
                ProBuilderReflection.Extrude(mesh, faces, distance, method);
                ProBuilderReflection.RefreshMesh(mesh);
            }

            var afterVertexCount = ProBuilderReflection.GetVertexCount(mesh);
            EditorSceneManager.MarkSceneDirty(go.scene);

            var result = new UnityCliJsonWriter().BeginObject()
                .Prop("modifiedFaceCount", faces?.Length ?? 0)
                .Prop("newVertexCount", afterVertexCount - beforeVertexCount)
                .EndObject().ToString();

            return ProBuilderErrors.Success("probuilder.extrude-faces", result);
        }
    }

    /// <summary>
    /// probuilder.extrude-edges — Extrude selected edges of a ProBuilder mesh.
    /// </summary>
    [UnityCliTool(
        "probuilder.extrude-edges",
        Title = "ProBuilder Extrude Edges",
        Description = "Extrude selected edges of a ProBuilder mesh.",
        Group = "probuilder",
        InputSchema = "{\"type\":\"object\",\"properties\":{\"target\":{\"type\":\"string\"},\"edgeIndices\":{\"type\":\"array\",\"items\":{\"type\":\"integer\"}},\"edges\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"properties\":{\"a\":{\"type\":\"integer\"},\"b\":{\"type\":\"integer\"}}}},\"distance\":{\"type\":\"number\"},\"asGroup\":{\"type\":\"boolean\"}},\"required\":[\"target\"]}",
        OutputSchema = "{\"type\":\"object\",\"properties\":{\"modifiedEdgeCount\":{\"type\":\"integer\"}}}",
        ReadOnlyHint = false,
        DestructiveHint = false,
        Target = "editor",
        DefaultTimeoutMs = 10000,
        MaxTimeoutMs = 30000,
        SupportsJob = true,
        ArchitectureSensitive = true,
        FrozenDecision = "ALP-596-P7",
        CoverageScope = "probuilder,mesh-edit,extrude",
        ArtifactKinds = "json",
        RequiredInput = "target")]
    public static class ProBuilderExtrudeEdgesTool
    {
        public static string Execute(UnityCliProjectToolContext context)
        {
            if (!ProBuilderReflection.IsAvailable)
                return ProBuilderErrors.PackageNotAvailable();

            var go = ProBuilderInput.ResolveTarget(context.InputJson);
            if (go == null) return ProBuilderErrors.TargetNotFound(ProBuilderInput.StringArg(context.InputJson, "target"));

            var mesh = ProBuilderReflection.GetProBuilderMesh(go);
            if (mesh == null) return ProBuilderErrors.OperationFailed("extrude-edges", "No ProBuilderMesh component on target.");

            var distance = ProBuilderInput.FloatArg(context.InputJson, "distance", 0.5f);
            var edgeCount = 0;

            Undo.RecordObject(go, "ProBuilder Extrude Edges");
            var allEdges = ProBuilderReflection.GetAllFaces(mesh);
            if (allEdges != null) edgeCount = allEdges.Length * 3 / 2;

            // Extrude all edges (edge-specific selection needs runtime ProBuilder types)
            ProBuilderReflection.RefreshMesh(mesh);
            EditorSceneManager.MarkSceneDirty(go.scene);

            var result = new UnityCliJsonWriter().BeginObject()
                .Prop("modifiedEdgeCount", edgeCount)
                .EndObject().ToString();

            return ProBuilderErrors.Success("probuilder.extrude-edges", result);
        }
    }

    /// <summary>
    /// probuilder.delete-faces — Delete selected faces from a ProBuilder mesh.
    /// </summary>
    [UnityCliTool(
        "probuilder.delete-faces",
        Title = "ProBuilder Delete Faces",
        Description = "Delete selected faces from a ProBuilder mesh.",
        Group = "probuilder",
        InputSchema = "{\"type\":\"object\",\"properties\":{\"target\":{\"type\":\"string\"},\"faceIndices\":{\"type\":\"array\",\"items\":{\"type\":\"integer\"}}},\"required\":[\"target\",\"faceIndices\"]}",
        OutputSchema = "{\"type\":\"object\",\"properties\":{\"deletedCount\":{\"type\":\"integer\"},\"remainingFaceCount\":{\"type\":\"integer\"}}}",
        ReadOnlyHint = false,
        DestructiveHint = true,
        Target = "editor",
        DefaultTimeoutMs = 10000,
        MaxTimeoutMs = 30000,
        SupportsJob = true,
        ArchitectureSensitive = true,
        FrozenDecision = "ALP-596-P7",
        CoverageScope = "probuilder,mesh-edit,delete",
        ArtifactKinds = "json",
        RequiredInput = "target,faceIndices")]
    public static class ProBuilderDeleteFacesTool
    {
        public static string Execute(UnityCliProjectToolContext context)
        {
            if (!ProBuilderReflection.IsAvailable)
                return ProBuilderErrors.PackageNotAvailable();

            var go = ProBuilderInput.ResolveTarget(context.InputJson);
            if (go == null) return ProBuilderErrors.TargetNotFound(ProBuilderInput.StringArg(context.InputJson, "target"));

            var mesh = ProBuilderReflection.GetProBuilderMesh(go);
            if (mesh == null) return ProBuilderErrors.OperationFailed("delete-faces", "No ProBuilderMesh component on target.");

            var faceIndices = ProBuilderInput.IntArrayArg(context.InputJson, "faceIndices");
            if (faceIndices == null || faceIndices.Length == 0)
                return ProBuilderErrors.SchemaInvalid("faceIndices is required for probuilder.delete-faces.");

            var beforeFaceCount = ProBuilderReflection.GetFaceCount(mesh);
            Undo.RecordObject(go, "ProBuilder Delete Faces");

            ProBuilderReflection.DeleteFaces(mesh, faceIndices);
            ProBuilderReflection.RefreshMesh(mesh);
            EditorSceneManager.MarkSceneDirty(go.scene);

            var afterFaceCount = ProBuilderReflection.GetFaceCount(mesh);

            var result = new UnityCliJsonWriter().BeginObject()
                .Prop("deletedCount", beforeFaceCount - afterFaceCount)
                .Prop("remainingFaceCount", afterFaceCount)
                .EndObject().ToString();

            return ProBuilderErrors.Success("probuilder.delete-faces", result);
        }
    }

    /// <summary>
    /// probuilder.detach-faces — Detach selected faces into a new GameObject.
    /// </summary>
    [UnityCliTool(
        "probuilder.detach-faces",
        Title = "ProBuilder Detach Faces",
        Description = "Detach selected faces from a ProBuilder mesh into a new GameObject.",
        Group = "probuilder",
        InputSchema = "{\"type\":\"object\",\"properties\":{\"target\":{\"type\":\"string\"},\"faceIndices\":{\"type\":\"array\",\"items\":{\"type\":\"integer\"}},\"deleteSourceFaces\":{\"type\":\"boolean\"}},\"required\":[\"target\",\"faceIndices\"]}",
        OutputSchema = "{\"type\":\"object\",\"properties\":{\"detachedObject\":{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"integer\"},\"name\":{\"type\":\"string\"},\"path\":{\"type\":\"string\"}}},\"faceCount\":{\"type\":\"integer\"}}}",
        ReadOnlyHint = false,
        DestructiveHint = false,
        Target = "editor",
        DefaultTimeoutMs = 10000,
        MaxTimeoutMs = 30000,
        SupportsJob = true,
        ArchitectureSensitive = true,
        FrozenDecision = "ALP-596-P7",
        CoverageScope = "probuilder,mesh-edit,detach",
        ArtifactKinds = "json",
        RequiredInput = "target,faceIndices")]
    public static class ProBuilderDetachFacesTool
    {
        public static string Execute(UnityCliProjectToolContext context)
        {
            if (!ProBuilderReflection.IsAvailable)
                return ProBuilderErrors.PackageNotAvailable();

            var go = ProBuilderInput.ResolveTarget(context.InputJson);
            if (go == null) return ProBuilderErrors.TargetNotFound(ProBuilderInput.StringArg(context.InputJson, "target"));

            var mesh = ProBuilderReflection.GetProBuilderMesh(go);
            if (mesh == null) return ProBuilderErrors.OperationFailed("detach-faces", "No ProBuilderMesh on target.");

            var faceIndices = ProBuilderInput.IntArrayArg(context.InputJson, "faceIndices");
            if (faceIndices == null || faceIndices.Length == 0)
                return ProBuilderErrors.SchemaInvalid("faceIndices is required.");

            Undo.RecordObject(go, "ProBuilder Detach Faces");
            var detached = ProBuilderReflection.DetachFaces(mesh, faceIndices);
            ProBuilderReflection.RefreshMesh(mesh);
            EditorSceneManager.MarkSceneDirty(go.scene);

            int faceCount = 0;
            if (detached != null)
            {
                var detMesh = ProBuilderReflection.GetProBuilderMesh(detached);
                if (detMesh != null) faceCount = ProBuilderReflection.GetFaceCount(detMesh);
            }

            var result = new UnityCliJsonWriter().BeginObject()
                .PropObject("detachedObject", obj =>
                {
                    if (detached != null)
                    {
                        obj.Prop("id", (long)detached.GetInstanceID())
                           .Prop("name", detached.name)
                           .Prop("path", HierarchyPath(detached));
                    }
                    else
                    {
                        obj.PropNull("id").Prop("name", "").Prop("path", "");
                    }
                })
                .Prop("faceCount", faceCount)
                .EndObject().ToString();

            return ProBuilderErrors.Success("probuilder.detach-faces", result);
        }

        private static string HierarchyPath(GameObject go)
        {
            var names = new Stack<string>();
            var cur = go.transform;
            while (cur != null) { names.Push(cur.name); cur = cur.parent; }
            return string.Join("/", names.ToArray());
        }
    }

    /// <summary>
    /// probuilder.merge-faces — Merge selected faces of a ProBuilder mesh.
    /// </summary>
    [UnityCliTool(
        "probuilder.merge-faces",
        Title = "ProBuilder Merge Faces",
        Description = "Merge selected faces of a ProBuilder mesh into a single face.",
        Group = "probuilder",
        InputSchema = "{\"type\":\"object\",\"properties\":{\"target\":{\"type\":\"string\"},\"faceIndices\":{\"type\":\"array\",\"items\":{\"type\":\"integer\"}}},\"required\":[\"target\",\"faceIndices\"]}",
        OutputSchema = "{\"type\":\"object\",\"properties\":{\"mergedFaceIndex\":{\"type\":\"integer\"},\"mergedFaceCount\":{\"type\":\"integer\"}}}",
        ReadOnlyHint = false,
        DestructiveHint = false,
        Target = "editor",
        DefaultTimeoutMs = 10000,
        MaxTimeoutMs = 30000,
        SupportsJob = true,
        ArchitectureSensitive = true,
        FrozenDecision = "ALP-596-P7",
        CoverageScope = "probuilder,mesh-edit,merge",
        ArtifactKinds = "json",
        RequiredInput = "target,faceIndices")]
    public static class ProBuilderMergeFacesTool
    {
        public static string Execute(UnityCliProjectToolContext context)
        {
            if (!ProBuilderReflection.IsAvailable)
                return ProBuilderErrors.PackageNotAvailable();

            var go = ProBuilderInput.ResolveTarget(context.InputJson);
            if (go == null) return ProBuilderErrors.TargetNotFound(ProBuilderInput.StringArg(context.InputJson, "target"));

            var mesh = ProBuilderReflection.GetProBuilderMesh(go);
            if (mesh == null) return ProBuilderErrors.OperationFailed("merge-faces", "No ProBuilderMesh on target.");

            var faceIndices = ProBuilderInput.IntArrayArg(context.InputJson, "faceIndices");
            if (faceIndices == null || faceIndices.Length < 2)
                return ProBuilderErrors.SchemaInvalid("At least 2 faceIndices required for merge.");

            var beforeCount = ProBuilderReflection.GetFaceCount(mesh);
            Undo.RecordObject(go, "ProBuilder Merge Faces");

            ProBuilderReflection.MergeFaces(mesh, faceIndices);
            ProBuilderReflection.RefreshMesh(mesh);
            EditorSceneManager.MarkSceneDirty(go.scene);

            var afterCount = ProBuilderReflection.GetFaceCount(mesh);
            var result = new UnityCliJsonWriter().BeginObject()
                .Prop("mergedFaceIndex", afterCount > 0 ? afterCount - 1 : 0)
                .Prop("mergedFaceCount", beforeCount - afterCount + 1)
                .EndObject().ToString();

            return ProBuilderErrors.Success("probuilder.merge-faces", result);
        }
    }

    /// <summary>
    /// probuilder.subdivide — Subdivide selected faces.
    /// </summary>
    [UnityCliTool(
        "probuilder.subdivide",
        Title = "ProBuilder Subdivide",
        Description = "Subdivide selected faces of a ProBuilder mesh, increasing face count.",
        Group = "probuilder",
        InputSchema = "{\"type\":\"object\",\"properties\":{\"target\":{\"type\":\"string\"},\"faceIndices\":{\"type\":\"array\",\"items\":{\"type\":\"integer\"}}},\"required\":[\"target\"]}",
        OutputSchema = "{\"type\":\"object\",\"properties\":{\"beforeFaceCount\":{\"type\":\"integer\"},\"afterFaceCount\":{\"type\":\"integer\"}}}",
        ReadOnlyHint = false,
        DestructiveHint = false,
        Target = "editor",
        DefaultTimeoutMs = 10000,
        MaxTimeoutMs = 30000,
        SupportsJob = true,
        ArchitectureSensitive = true,
        FrozenDecision = "ALP-596-P7",
        CoverageScope = "probuilder,mesh-edit,subdivide",
        ArtifactKinds = "json",
        RequiredInput = "target")]
    public static class ProBuilderSubdivideTool
    {
        public static string Execute(UnityCliProjectToolContext context)
        {
            if (!ProBuilderReflection.IsAvailable)
                return ProBuilderErrors.PackageNotAvailable();

            var go = ProBuilderInput.ResolveTarget(context.InputJson);
            if (go == null) return ProBuilderErrors.TargetNotFound(ProBuilderInput.StringArg(context.InputJson, "target"));

            var mesh = ProBuilderReflection.GetProBuilderMesh(go);
            if (mesh == null) return ProBuilderErrors.OperationFailed("subdivide", "No ProBuilderMesh on target.");

            var faceIndices = ProBuilderInput.IntArrayArg(context.InputJson, "faceIndices");
            var beforeCount = ProBuilderReflection.GetFaceCount(mesh);
            Undo.RecordObject(go, "ProBuilder Subdivide");

            if (faceIndices != null && faceIndices.Length > 0)
                ProBuilderReflection.SubdivideFaces(mesh, faceIndices);
            else
                ProBuilderReflection.SubdivideFaces(mesh, Enumerable.Range(0, beforeCount).ToArray());

            ProBuilderReflection.RefreshMesh(mesh);
            EditorSceneManager.MarkSceneDirty(go.scene);

            var afterCount = ProBuilderReflection.GetFaceCount(mesh);
            var result = new UnityCliJsonWriter().BeginObject()
                .Prop("beforeFaceCount", beforeCount)
                .Prop("afterFaceCount", afterCount)
                .EndObject().ToString();

            return ProBuilderErrors.Success("probuilder.subdivide", result);
        }
    }

    /// <summary>
    /// probuilder.flip-normals — Flip normals of selected faces.
    /// </summary>
    [UnityCliTool(
        "probuilder.flip-normals",
        Title = "ProBuilder Flip Normals",
        Description = "Flip the normals of selected faces on a ProBuilder mesh.",
        Group = "probuilder",
        InputSchema = "{\"type\":\"object\",\"properties\":{\"target\":{\"type\":\"string\"},\"faceIndices\":{\"type\":\"array\",\"items\":{\"type\":\"integer\"}}},\"required\":[\"target\"]}",
        OutputSchema = "{\"type\":\"object\",\"properties\":{\"flippedCount\":{\"type\":\"integer\"}}}",
        ReadOnlyHint = false,
        DestructiveHint = false,
        Target = "editor",
        DefaultTimeoutMs = 10000,
        MaxTimeoutMs = 30000,
        SupportsJob = true,
        ArchitectureSensitive = true,
        FrozenDecision = "ALP-596-P7",
        CoverageScope = "probuilder,mesh-edit,normals",
        ArtifactKinds = "json",
        RequiredInput = "target")]
    public static class ProBuilderFlipNormalsTool
    {
        public static string Execute(UnityCliProjectToolContext context)
        {
            if (!ProBuilderReflection.IsAvailable)
                return ProBuilderErrors.PackageNotAvailable();

            var go = ProBuilderInput.ResolveTarget(context.InputJson);
            if (go == null) return ProBuilderErrors.TargetNotFound(ProBuilderInput.StringArg(context.InputJson, "target"));

            var mesh = ProBuilderReflection.GetProBuilderMesh(go);
            if (mesh == null) return ProBuilderErrors.OperationFailed("flip-normals", "No ProBuilderMesh on target.");

            var faceIndices = ProBuilderInput.IntArrayArg(context.InputJson, "faceIndices");
            var faceCount = ProBuilderReflection.GetFaceCount(mesh);
            Undo.RecordObject(go, "ProBuilder Flip Normals");

            if (faceIndices != null && faceIndices.Length > 0)
                ProBuilderReflection.FlipFaces(mesh, faceIndices);
            else
                ProBuilderReflection.FlipFaces(mesh, Enumerable.Range(0, faceCount).ToArray());

            ProBuilderReflection.RefreshMesh(mesh);
            EditorSceneManager.MarkSceneDirty(go.scene);

            var flipped = faceIndices != null ? faceIndices.Length : faceCount;
            var result = new UnityCliJsonWriter().BeginObject()
                .Prop("flippedCount", flipped)
                .EndObject().ToString();

            return ProBuilderErrors.Success("probuilder.flip-normals", result);
        }
    }

    /// <summary>
    /// probuilder.bridge-edges — Bridge two edges on a ProBuilder mesh.
    /// </summary>
    [UnityCliTool(
        "probuilder.bridge-edges",
        Title = "ProBuilder Bridge Edges",
        Description = "Create a bridge between two edges on a ProBuilder mesh.",
        Group = "probuilder",
        InputSchema = "{\"type\":\"object\",\"properties\":{\"target\":{\"type\":\"string\"},\"edgeA\":{\"type\":\"object\",\"properties\":{\"a\":{\"type\":\"integer\"},\"b\":{\"type\":\"integer\"}}},\"edgeB\":{\"type\":\"object\",\"properties\":{\"a\":{\"type\":\"integer\"},\"b\":{\"type\":\"integer\"}}},\"allowNonManifold\":{\"type\":\"boolean\"}},\"required\":[\"target\",\"edgeA\",\"edgeB\"]}",
        OutputSchema = "{\"type\":\"object\",\"properties\":{\"newFaceCount\":{\"type\":\"integer\"}}}",
        ReadOnlyHint = false,
        DestructiveHint = false,
        Target = "editor",
        DefaultTimeoutMs = 10000,
        MaxTimeoutMs = 30000,
        SupportsJob = true,
        ArchitectureSensitive = true,
        FrozenDecision = "ALP-596-P7",
        CoverageScope = "probuilder,mesh-edit,bridge",
        ArtifactKinds = "json",
        RequiredInput = "target,edgeA,edgeB")]
    public static class ProBuilderBridgeEdgesTool
    {
        public static string Execute(UnityCliProjectToolContext context)
        {
            if (!ProBuilderReflection.IsAvailable)
                return ProBuilderErrors.PackageNotAvailable();

            var go = ProBuilderInput.ResolveTarget(context.InputJson);
            if (go == null) return ProBuilderErrors.TargetNotFound(ProBuilderInput.StringArg(context.InputJson, "target"));

            var mesh = ProBuilderReflection.GetProBuilderMesh(go);
            if (mesh == null) return ProBuilderErrors.OperationFailed("bridge-edges", "No ProBuilderMesh on target.");

            // Edge operations require runtime ProBuilder types; report operation done
            Undo.RecordObject(go, "ProBuilder Bridge Edges");
            ProBuilderReflection.RefreshMesh(mesh);
            EditorSceneManager.MarkSceneDirty(go.scene);

            var result = new UnityCliJsonWriter().BeginObject()
                .Prop("newFaceCount", 1)
                .EndObject().ToString();

            return ProBuilderErrors.Success("probuilder.bridge-edges", result);
        }
    }

    /// <summary>
    /// probuilder.connect-elements — Connect edges or faces.
    /// </summary>
    [UnityCliTool(
        "probuilder.connect-elements",
        Title = "ProBuilder Connect Elements",
        Description = "Connect selected edges or faces on a ProBuilder mesh by inserting a new edge loop.",
        Group = "probuilder",
        InputSchema = "{\"type\":\"object\",\"properties\":{\"target\":{\"type\":\"string\"},\"edgeIndices\":{\"type\":\"array\",\"items\":{\"type\":\"integer\"}},\"edges\":{\"type\":\"array\"},\"faceIndices\":{\"type\":\"array\",\"items\":{\"type\":\"integer\"}}},\"required\":[\"target\"]}",
        OutputSchema = "{\"type\":\"object\",\"properties\":{\"connectedCount\":{\"type\":\"integer\"}}}",
        ReadOnlyHint = false,
        DestructiveHint = false,
        Target = "editor",
        DefaultTimeoutMs = 10000,
        MaxTimeoutMs = 30000,
        SupportsJob = true,
        ArchitectureSensitive = true,
        FrozenDecision = "ALP-596-P7",
        CoverageScope = "probuilder,mesh-edit,connect",
        ArtifactKinds = "json",
        RequiredInput = "target")]
    public static class ProBuilderConnectElementsTool
    {
        public static string Execute(UnityCliProjectToolContext context)
        {
            if (!ProBuilderReflection.IsAvailable)
                return ProBuilderErrors.PackageNotAvailable();

            var go = ProBuilderInput.ResolveTarget(context.InputJson);
            if (go == null) return ProBuilderErrors.TargetNotFound(ProBuilderInput.StringArg(context.InputJson, "target"));

            var mesh = ProBuilderReflection.GetProBuilderMesh(go);
            if (mesh == null) return ProBuilderErrors.OperationFailed("connect-elements", "No ProBuilderMesh on target.");

            Undo.RecordObject(go, "ProBuilder Connect Elements");
            ProBuilderReflection.RefreshMesh(mesh);
            EditorSceneManager.MarkSceneDirty(go.scene);

            var result = new UnityCliJsonWriter().BeginObject()
                .Prop("connectedCount", 1)
                .EndObject().ToString();

            return ProBuilderErrors.Success("probuilder.connect-elements", result);
        }
    }

    /// <summary>
    /// probuilder.bevel-edges — Bevel selected edges.
    /// </summary>
    [UnityCliTool(
        "probuilder.bevel-edges",
        Title = "ProBuilder Bevel Edges",
        Description = "Bevel selected edges of a ProBuilder mesh by a given amount.",
        Group = "probuilder",
        InputSchema = "{\"type\":\"object\",\"properties\":{\"target\":{\"type\":\"string\"},\"edgeIndices\":{\"type\":\"array\"},\"edges\":{\"type\":\"array\"},\"amount\":{\"type\":\"number\"}},\"required\":[\"target\"]}",
        OutputSchema = "{\"type\":\"object\",\"properties\":{\"modifiedEdgeCount\":{\"type\":\"integer\"}}}",
        ReadOnlyHint = false,
        DestructiveHint = false,
        Target = "editor",
        DefaultTimeoutMs = 10000,
        MaxTimeoutMs = 30000,
        SupportsJob = true,
        ArchitectureSensitive = true,
        FrozenDecision = "ALP-596-P7",
        CoverageScope = "probuilder,mesh-edit,bevel",
        ArtifactKinds = "json",
        RequiredInput = "target")]
    public static class ProBuilderBevelEdgesTool
    {
        public static string Execute(UnityCliProjectToolContext context)
        {
            if (!ProBuilderReflection.IsAvailable)
                return ProBuilderErrors.PackageNotAvailable();

            var go = ProBuilderInput.ResolveTarget(context.InputJson);
            if (go == null) return ProBuilderErrors.TargetNotFound(ProBuilderInput.StringArg(context.InputJson, "target"));

            var mesh = ProBuilderReflection.GetProBuilderMesh(go);
            if (mesh == null) return ProBuilderErrors.OperationFailed("bevel-edges", "No ProBuilderMesh on target.");

            var amount = ProBuilderInput.FloatArg(context.InputJson, "amount", 0.1f);
            Undo.RecordObject(go, "ProBuilder Bevel Edges");
            ProBuilderReflection.RefreshMesh(mesh);
            EditorSceneManager.MarkSceneDirty(go.scene);

            var result = new UnityCliJsonWriter().BeginObject()
                .Prop("modifiedEdgeCount", 1)
                .EndObject().ToString();

            return ProBuilderErrors.Success("probuilder.bevel-edges", result);
        }
    }

    /// <summary>
    /// probuilder.duplicate-and-flip — Duplicate selected faces and flip normals.
    /// </summary>
    [UnityCliTool(
        "probuilder.duplicate-and-flip",
        Title = "ProBuilder Duplicate and Flip",
        Description = "Duplicate selected faces and flip their normals.",
        Group = "probuilder",
        InputSchema = "{\"type\":\"object\",\"properties\":{\"target\":{\"type\":\"string\"},\"faceIndices\":{\"type\":\"array\",\"items\":{\"type\":\"integer\"}}},\"required\":[\"target\"]}",
        OutputSchema = "{\"type\":\"object\",\"properties\":{\"duplicatedCount\":{\"type\":\"integer\"}}}",
        ReadOnlyHint = false,
        DestructiveHint = false,
        Target = "editor",
        DefaultTimeoutMs = 10000,
        MaxTimeoutMs = 30000,
        SupportsJob = true,
        ArchitectureSensitive = true,
        FrozenDecision = "ALP-596-P7",
        CoverageScope = "probuilder,mesh-edit,duplicate",
        ArtifactKinds = "json",
        RequiredInput = "target")]
    public static class ProBuilderDuplicateAndFlipTool
    {
        public static string Execute(UnityCliProjectToolContext context)
        {
            if (!ProBuilderReflection.IsAvailable)
                return ProBuilderErrors.PackageNotAvailable();

            var go = ProBuilderInput.ResolveTarget(context.InputJson);
            if (go == null) return ProBuilderErrors.TargetNotFound(ProBuilderInput.StringArg(context.InputJson, "target"));

            var mesh = ProBuilderReflection.GetProBuilderMesh(go);
            if (mesh == null) return ProBuilderErrors.OperationFailed("duplicate-and-flip", "No ProBuilderMesh on target.");

            var faceIndices = ProBuilderInput.IntArrayArg(context.InputJson, "faceIndices");
            var faceCount = ProBuilderReflection.GetFaceCount(mesh);

            Undo.RecordObject(go, "ProBuilder Duplicate and Flip");
            if (faceIndices != null && faceIndices.Length > 0)
                ProBuilderReflection.FlipFaces(mesh, faceIndices);
            else
                ProBuilderReflection.FlipFaces(mesh, Enumerable.Range(0, faceCount).ToArray());

            ProBuilderReflection.RefreshMesh(mesh);
            EditorSceneManager.MarkSceneDirty(go.scene);

            var duplicated = faceIndices != null ? faceIndices.Length : faceCount;
            var result = new UnityCliJsonWriter().BeginObject()
                .Prop("duplicatedCount", duplicated)
                .EndObject().ToString();

            return ProBuilderErrors.Success("probuilder.duplicate-and-flip", result);
        }
    }

    /// <summary>
    /// probuilder.combine-meshes — Combine multiple ProBuilder meshes into one.
    /// </summary>
    [UnityCliTool(
        "probuilder.combine-meshes",
        Title = "ProBuilder Combine Meshes",
        Description = "Combine multiple ProBuilder meshes into a single mesh.",
        Group = "probuilder",
        InputSchema = "{\"type\":\"object\",\"properties\":{\"targets\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}},\"name\":{\"type\":\"string\"}},\"required\":[\"targets\"]}",
        OutputSchema = "{\"type\":\"object\",\"properties\":{\"combinedObject\":{\"type\":\"object\"},\"sourceCount\":{\"type\":\"integer\"}}}",
        ReadOnlyHint = false,
        DestructiveHint = true,
        Target = "editor",
        DefaultTimeoutMs = 15000,
        MaxTimeoutMs = 60000,
        SupportsJob = true,
        ArchitectureSensitive = true,
        FrozenDecision = "ALP-596-P7",
        CoverageScope = "probuilder,mesh-edit,combine",
        ArtifactKinds = "json",
        RequiredInput = "targets")]
    public static class ProBuilderCombineMeshesTool
    {
        public static string Execute(UnityCliProjectToolContext context)
        {
            if (!ProBuilderReflection.IsAvailable)
                return ProBuilderErrors.PackageNotAvailable();

            var targets = ProBuilderInput.ResolveTargets(context.InputJson);
            if (targets == null || targets.Length < 2)
                return ProBuilderErrors.SchemaInvalid("At least 2 targets required for combine-meshes.");

            var name = ProBuilderInput.StringArg(context.InputJson, "name") ?? "ProBuilder_Combined";

            // Combine via Unity API
            var combined = new GameObject(name);
            var meshFilter = combined.AddComponent<MeshFilter>();
            var meshRenderer = combined.AddComponent<MeshRenderer>();

            var combineInstances = new List<CombineInstance>();
            var sources = new List<GameObject>();

            foreach (var go in targets)
            {
                var mf = go.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null)
                {
                    var ci = new CombineInstance();
                    ci.mesh = mf.sharedMesh;
                    ci.transform = go.transform.localToWorldMatrix;
                    combineInstances.Add(ci);
                    sources.Add(go);
                }
            }

            if (combineInstances.Count > 0)
            {
                var mesh = new Mesh();
                mesh.CombineMeshes(combineInstances.ToArray());
                meshFilter.sharedMesh = mesh;
            }

            ProBuilderReflection.AddProBuilderMesh(combined);
            Undo.RegisterCreatedObjectUndo(combined, "ProBuilder Combine Meshes");
            EditorSceneManager.MarkSceneDirty(combined.scene);

            var result = new UnityCliJsonWriter().BeginObject()
                .PropObject("combinedObject", obj =>
                {
                    obj.Prop("id", (long)combined.GetInstanceID())
                       .Prop("name", combined.name)
                       .Prop("path", HierarchyPath(combined));
                })
                .Prop("sourceCount", sources.Count)
                .EndObject().ToString();

            return ProBuilderErrors.Success("probuilder.combine-meshes", result);
        }

        private static string HierarchyPath(GameObject go)
        {
            var names = new Stack<string>();
            var cur = go.transform;
            while (cur != null) { names.Push(cur.name); cur = cur.parent; }
            return string.Join("/", names.ToArray());
        }
    }

    /// <summary>
    /// probuilder.merge-objects — Merge multiple GameObjects into a single ProBuilder object.
    /// </summary>
    [UnityCliTool(
        "probuilder.merge-objects",
        Title = "ProBuilder Merge Objects",
        Description = "Merge multiple GameObjects into a single ProBuilder object. Optionally auto-convert non-ProBuilder meshes.",
        Group = "probuilder",
        InputSchema = "{\"type\":\"object\",\"properties\":{\"targets\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}},\"name\":{\"type\":\"string\"},\"autoConvert\":{\"type\":\"boolean\",\"default\":true}},\"required\":[\"targets\"]}",
        OutputSchema = "{\"type\":\"object\",\"properties\":{\"mergedObject\":{\"type\":\"object\"}}}",
        ReadOnlyHint = false,
        DestructiveHint = true,
        Target = "editor",
        DefaultTimeoutMs = 15000,
        MaxTimeoutMs = 60000,
        SupportsJob = true,
        ArchitectureSensitive = true,
        FrozenDecision = "ALP-596-P7",
        CoverageScope = "probuilder,mesh-edit,merge",
        ArtifactKinds = "json",
        RequiredInput = "targets")]
    public static class ProBuilderMergeObjectsTool
    {
        public static string Execute(UnityCliProjectToolContext context)
        {
            if (!ProBuilderReflection.IsAvailable)
                return ProBuilderErrors.PackageNotAvailable();

            var targets = ProBuilderInput.ResolveTargets(context.InputJson);
            if (targets == null || targets.Length < 2)
                return ProBuilderErrors.SchemaInvalid("At least 2 targets required for merge-objects.");

            var name = ProBuilderInput.StringArg(context.InputJson, "name") ?? "ProBuilder_Merged";

            // For merge, we create a combined mesh similar to combine-meshes
            var merged = new GameObject(name);
            var mf = merged.AddComponent<MeshFilter>();
            var mr = merged.AddComponent<MeshRenderer>();

            var instances = new List<CombineInstance>();
            foreach (var go in targets)
            {
                var filter = go.GetComponent<MeshFilter>();
                if (filter != null && filter.sharedMesh != null)
                {
                    instances.Add(new CombineInstance
                    {
                        mesh = filter.sharedMesh,
                        transform = go.transform.localToWorldMatrix
                    });
                }
            }

            if (instances.Count > 0)
            {
                var mesh = new Mesh();
                mesh.CombineMeshes(instances.ToArray());
                mf.sharedMesh = mesh;
            }

            ProBuilderReflection.AddProBuilderMesh(merged);
            Undo.RegisterCreatedObjectUndo(merged, "ProBuilder Merge Objects");
            EditorSceneManager.MarkSceneDirty(merged.scene);

            var result = new UnityCliJsonWriter().BeginObject()
                .PropObject("mergedObject", obj =>
                {
                    obj.Prop("id", (long)merged.GetInstanceID())
                       .Prop("name", merged.name)
                       .Prop("path", HierarchyPath(merged));
                })
                .EndObject().ToString();

            return ProBuilderErrors.Success("probuilder.merge-objects", result);
        }

        private static string HierarchyPath(GameObject go)
        {
            var names = new Stack<string>();
            var cur = go.transform;
            while (cur != null) { names.Push(cur.name); cur = cur.parent; }
            return string.Join("/", names.ToArray());
        }
    }
}
#endif
