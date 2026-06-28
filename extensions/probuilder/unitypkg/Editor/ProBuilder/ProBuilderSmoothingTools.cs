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
    // Smoothing & Utility tools: convert-to-probuilder, set-smoothing,
    // auto-smooth, center-pivot, freeze-transform, set-pivot, repair-mesh
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// probuilder.convert-to-probuilder — Convert a regular mesh to ProBuilder mesh.
    /// </summary>
    [UnityCliTool(
        "probuilder.convert-to-probuilder",
        Summary = "ProBuilder Convert to ProBuilder",
        Description = "Convert a regular Unity mesh to a ProBuilder mesh.",
        Group = "probuilder",
        InputSchema = "{\"type\":\"object\",\"properties\":{\"target\":{\"type\":\"string\"}},\"required\":[\"target\"]}",
        OutputSchema = "{\"type\":\"object\",\"properties\":{\"converted\":{\"type\":\"boolean\"},\"vertexCount\":{\"type\":\"integer\"},\"faceCount\":{\"type\":\"integer\"}}}",
        DestructiveHint = false,
        Target = "editor",
        DefaultTimeoutMs = 10000,
        MaxTimeoutMs = 30000,
        SupportsJob = true,
        ArtifactKinds = "json",
        RequiredInput = "target")]
    public static class ProBuilderConvertToProBuilderTool
    {
        public static string Execute(UnityCliProjectToolContext context)
        {
            if (!ProBuilderReflection.IsAvailable)
                return ProBuilderErrors.PackageNotAvailable();

            var go = ProBuilderInput.ResolveTarget(context.InputJson);
            if (go == null) return ProBuilderErrors.TargetNotFound(ProBuilderInput.StringArg(context.InputJson, "target"));

            var existingMesh = ProBuilderReflection.GetProBuilderMesh(go);
            if (existingMesh != null)
                return ProBuilderErrors.OperationFailed("convert-to-probuilder", "Target already has a ProBuilderMesh component.");

            Undo.RecordObject(go, "ProBuilder Convert to ProBuilder");

            // Add ProBuilderMesh component
            var pbMesh = ProBuilderReflection.AddProBuilderMesh(go);
            if (pbMesh == null)
                return ProBuilderErrors.OperationFailed("convert-to-probuilder", "Failed to add ProBuilderMesh component.");

            // Use MeshImporter to properly convert the mesh
            var imported = ProBuilderReflection.ConvertViaMeshImporter(go, pbMesh);

            var converted = pbMesh != null;
            var vertexCount = converted ? ProBuilderReflection.GetVertexCount(pbMesh) : 0;
            var faceCount = converted ? ProBuilderReflection.GetFaceCount(pbMesh) : 0;

            if (converted)
            {
                ProBuilderReflection.RefreshMesh(pbMesh);
                EditorSceneManager.MarkSceneDirty(go.scene);
            }

            var result = new UnityCliJsonWriter().BeginObject()
                .Prop("converted", converted)
                .Prop("vertexCount", vertexCount)
                .Prop("faceCount", faceCount)
                .EndObject().ToString();

            return ProBuilderErrors.Success("probuilder.convert-to-probuilder", result);
        }
    }

    /// <summary>
    /// probuilder.set-smoothing — Set smoothing group for selected faces.
    /// </summary>
    [UnityCliTool(
        "probuilder.set-smoothing",
        Summary = "ProBuilder Set Smoothing",
        Description = "Set the smoothing group for selected faces on a ProBuilder mesh.",
        Group = "probuilder",
        InputSchema = "{\"type\":\"object\",\"properties\":{\"target\":{\"type\":\"string\"},\"faceIndices\":{\"type\":\"array\",\"items\":{\"type\":\"integer\"}},\"smoothingGroup\":{\"type\":\"integer\"}},\"required\":[\"target\",\"smoothingGroup\"]}",
        OutputSchema = "{\"type\":\"object\",\"properties\":{\"modifiedFaceCount\":{\"type\":\"integer\"}}}",
        DestructiveHint = false,
        Target = "editor",
        DefaultTimeoutMs = 10000,
        MaxTimeoutMs = 30000,
        SupportsJob = true,
        ArtifactKinds = "json",
        RequiredInput = "target,smoothingGroup")]
    public static class ProBuilderSetSmoothingTool
    {
        public static string Execute(UnityCliProjectToolContext context)
        {
            if (!ProBuilderReflection.IsAvailable)
                return ProBuilderErrors.PackageNotAvailable();

            var go = ProBuilderInput.ResolveTarget(context.InputJson);
            if (go == null) return ProBuilderErrors.TargetNotFound(ProBuilderInput.StringArg(context.InputJson, "target"));

            var mesh = ProBuilderReflection.GetProBuilderMesh(go);
            if (mesh == null) return ProBuilderErrors.OperationFailed("set-smoothing", "No ProBuilderMesh on target.");

            var smoothingGroup = ProBuilderInput.IntArg(context.InputJson, "smoothingGroup", -1);
            if (smoothingGroup < 0)
                return ProBuilderErrors.SchemaInvalid("smoothingGroup must be a non-negative integer.");

            var faceIndices = ProBuilderInput.IntArrayArg(context.InputJson, "faceIndices");
            var faceCount = ProBuilderReflection.GetFaceCount(mesh);

            Undo.RecordObject(go, "ProBuilder Set Smoothing");
            if (faceIndices != null && faceIndices.Length > 0)
                ProBuilderReflection.ApplySmoothingGroup(mesh, faceIndices, smoothingGroup);
            else
                ProBuilderReflection.ApplySmoothingGroup(mesh, Enumerable.Range(0, faceCount).ToArray(), smoothingGroup);

            ProBuilderReflection.RefreshMesh(mesh);
            EditorSceneManager.MarkSceneDirty(go.scene);

            var modified = faceIndices != null ? faceIndices.Length : faceCount;
            var result = new UnityCliJsonWriter().BeginObject()
                .Prop("modifiedFaceCount", modified)
                .EndObject().ToString();

            return ProBuilderErrors.Success("probuilder.set-smoothing", result);
        }
    }

    /// <summary>
    /// probuilder.auto-smooth — Auto-apply smoothing based on angle threshold.
    /// </summary>
    [UnityCliTool(
        "probuilder.auto-smooth",
        Summary = "ProBuilder Auto Smooth",
        Description = "Automatically apply smoothing to faces based on an angle threshold.",
        Group = "probuilder",
        InputSchema = "{\"type\":\"object\",\"properties\":{\"target\":{\"type\":\"string\"},\"angleThreshold\":{\"type\":\"number\"},\"faceIndices\":{\"type\":\"array\",\"items\":{\"type\":\"integer\"}}},\"required\":[\"target\"]}",
        OutputSchema = "{\"type\":\"object\",\"properties\":{\"smoothedFaceCount\":{\"type\":\"integer\"}}}",
        DestructiveHint = false,
        Target = "editor",
        DefaultTimeoutMs = 10000,
        MaxTimeoutMs = 30000,
        SupportsJob = true,
        ArtifactKinds = "json",
        RequiredInput = "target")]
    public static class ProBuilderAutoSmoothTool
    {
        public static string Execute(UnityCliProjectToolContext context)
        {
            if (!ProBuilderReflection.IsAvailable)
                return ProBuilderErrors.PackageNotAvailable();

            var go = ProBuilderInput.ResolveTarget(context.InputJson);
            if (go == null) return ProBuilderErrors.TargetNotFound(ProBuilderInput.StringArg(context.InputJson, "target"));

            var mesh = ProBuilderReflection.GetProBuilderMesh(go);
            if (mesh == null) return ProBuilderErrors.OperationFailed("auto-smooth", "No ProBuilderMesh on target.");

            var angleThreshold = ProBuilderInput.FloatArg(context.InputJson, "angleThreshold", 30f);
            var faceIndices = ProBuilderInput.IntArrayArg(context.InputJson, "faceIndices");
            var faceCount = ProBuilderReflection.GetFaceCount(mesh);

            Undo.RecordObject(go, "ProBuilder Auto Smooth");
            if (faceIndices != null && faceIndices.Length > 0)
                ProBuilderReflection.ApplySmoothingGroups(mesh, faceIndices, angleThreshold);
            else
                ProBuilderReflection.ApplySmoothingGroups(mesh, Enumerable.Range(0, faceCount).ToArray(), angleThreshold);

            ProBuilderReflection.RefreshMesh(mesh);
            EditorSceneManager.MarkSceneDirty(go.scene);

            var smoothed = faceIndices != null ? faceIndices.Length : faceCount;
            var result = new UnityCliJsonWriter().BeginObject()
                .Prop("smoothedFaceCount", smoothed)
                .EndObject().ToString();

            return ProBuilderErrors.Success("probuilder.auto-smooth", result);
        }
    }

    /// <summary>
    /// probuilder.center-pivot — Center pivot to bounds center.
    /// </summary>
    [UnityCliTool(
        "probuilder.center-pivot",
        Summary = "ProBuilder Center Pivot",
        Description = "Center the pivot point of a ProBuilder mesh to its bounds center.",
        Group = "probuilder",
        InputSchema = "{\"type\":\"object\",\"properties\":{\"target\":{\"type\":\"string\"}},\"required\":[\"target\"]}",
        OutputSchema = "{\"type\":\"object\",\"properties\":{\"pivotOffset\":{\"type\":\"object\"},\"newPosition\":{\"type\":\"object\"}}}",
        DestructiveHint = false,
        Target = "editor",
        DefaultTimeoutMs = 10000,
        MaxTimeoutMs = 30000,
        SupportsJob = true,
        ArtifactKinds = "json",
        RequiredInput = "target")]
    public static class ProBuilderCenterPivotTool
    {
        public static string Execute(UnityCliProjectToolContext context)
        {
            if (!ProBuilderReflection.IsAvailable)
                return ProBuilderErrors.PackageNotAvailable();

            var go = ProBuilderInput.ResolveTarget(context.InputJson);
            if (go == null) return ProBuilderErrors.TargetNotFound(ProBuilderInput.StringArg(context.InputJson, "target"));

            var mesh = ProBuilderReflection.GetProBuilderMesh(go);
            if (mesh == null) return ProBuilderErrors.OperationFailed("center-pivot", "No ProBuilderMesh on target.");

            var oldPivot = go.transform.position;
            Undo.RecordObject(go.transform, "ProBuilder Center Pivot");

            var renderer = go.GetComponent<Renderer>();
            if (renderer != null)
            {
                var center = renderer.bounds.center;
                go.transform.position = center;
            }

            EditorSceneManager.MarkSceneDirty(go.scene);

            var newPivot = go.transform.position;
            var offset = newPivot - oldPivot;

            var result = new UnityCliJsonWriter().BeginObject()
                .PropObject("pivotOffset", o => o.Prop("x", offset.x).Prop("y", offset.y).Prop("z", offset.z))
                .PropObject("newPosition", o => o.Prop("x", newPivot.x).Prop("y", newPivot.y).Prop("z", newPivot.z))
                .EndObject().ToString();

            return ProBuilderErrors.Success("probuilder.center-pivot", result);
        }
    }

    /// <summary>
    /// probuilder.freeze-transform — Bake transform into mesh vertices.
    /// </summary>
    [UnityCliTool(
        "probuilder.freeze-transform",
        Summary = "ProBuilder Freeze Transform",
        Description = "Bake the current transform into the ProBuilder mesh vertices, resetting the transform to identity.",
        Group = "probuilder",
        InputSchema = "{\"type\":\"object\",\"properties\":{\"target\":{\"type\":\"string\"}},\"required\":[\"target\"]}",
        OutputSchema = "{\"type\":\"object\",\"properties\":{\"bakedVertexCount\":{\"type\":\"integer\"}}}",
        DestructiveHint = false,
        Target = "editor",
        DefaultTimeoutMs = 10000,
        MaxTimeoutMs = 30000,
        SupportsJob = true,
        ArtifactKinds = "json",
        RequiredInput = "target")]
    public static class ProBuilderFreezeTransformTool
    {
        public static string Execute(UnityCliProjectToolContext context)
        {
            if (!ProBuilderReflection.IsAvailable)
                return ProBuilderErrors.PackageNotAvailable();

            var go = ProBuilderInput.ResolveTarget(context.InputJson);
            if (go == null) return ProBuilderErrors.TargetNotFound(ProBuilderInput.StringArg(context.InputJson, "target"));

            var mesh = ProBuilderReflection.GetProBuilderMesh(go);
            if (mesh == null) return ProBuilderErrors.OperationFailed("freeze-transform", "No ProBuilderMesh on target.");

            var vertexCount = ProBuilderReflection.GetVertexCount(mesh);
            Undo.RecordObject(go, "ProBuilder Freeze Transform");

            // Bake transform into vertex positions, then reset transform to identity
            // Read positions, transform to world space, reset transform, write back
            try
            {
                var positions = ProBuilderReflection.GetPositions(mesh);
                if (positions != null)
                {
                    var posList = new List<Vector3>(positions);
                    // Transform positions from local to world space
                    for (int i = 0; i < posList.Count; i++)
                        posList[i] = go.transform.TransformPoint(posList[i]);

                    // Reset transform
                    go.transform.position = Vector3.zero;
                    go.transform.rotation = Quaternion.identity;
                    go.transform.localScale = Vector3.one;

                    // Write positions back (now in local space = world space since transform is identity)
                    var positionsProp = ProBuilderReflection.GetProBuilderMeshType()?.GetProperty("positions");
                    if (positionsProp != null && positionsProp.CanWrite)
                        positionsProp.SetValue(mesh, posList);
                    else
                    {
                        var setPositionsMethod = ProBuilderReflection.GetProBuilderMeshType()?.GetMethod("SetPositions",
                            BindingFlags.Instance | BindingFlags.Public);
                        if (setPositionsMethod != null)
                            setPositionsMethod.Invoke(mesh, new object[] { posList.ToArray() });
                    }
                }
            }
            catch (Exception) { }

            ProBuilderReflection.RefreshMesh(mesh);
            EditorSceneManager.MarkSceneDirty(go.scene);

            var result = new UnityCliJsonWriter().BeginObject()
                .Prop("bakedVertexCount", vertexCount)
                .EndObject().ToString();

            return ProBuilderErrors.Success("probuilder.freeze-transform", result);
        }
    }

    /// <summary>
    /// probuilder.set-pivot — Set pivot to a specific position.
    /// </summary>
    [UnityCliTool(
        "probuilder.set-pivot",
        Summary = "ProBuilder Set Pivot",
        Description = "Set the pivot point of a ProBuilder mesh to a specific position.",
        Group = "probuilder",
        InputSchema = "{\"type\":\"object\",\"properties\":{\"target\":{\"type\":\"string\"},\"x\":{\"type\":\"number\"},\"y\":{\"type\":\"number\"},\"z\":{\"type\":\"number\"}},\"required\":[\"target\"]}",
        OutputSchema = "{\"type\":\"object\",\"properties\":{\"newPivotPosition\":{\"type\":\"object\"}}}",
        DestructiveHint = false,
        Target = "editor",
        DefaultTimeoutMs = 10000,
        MaxTimeoutMs = 30000,
        SupportsJob = true,
        ArtifactKinds = "json",
        RequiredInput = "target")]
    public static class ProBuilderSetPivotTool
    {
        public static string Execute(UnityCliProjectToolContext context)
        {
            if (!ProBuilderReflection.IsAvailable)
                return ProBuilderErrors.PackageNotAvailable();

            var go = ProBuilderInput.ResolveTarget(context.InputJson);
            if (go == null) return ProBuilderErrors.TargetNotFound(ProBuilderInput.StringArg(context.InputJson, "target"));

            var mesh = ProBuilderReflection.GetProBuilderMesh(go);
            if (mesh == null) return ProBuilderErrors.OperationFailed("set-pivot", "No ProBuilderMesh on target.");

            var x = ProBuilderInput.FloatArg(context.InputJson, "x", go.transform.position.x);
            var y = ProBuilderInput.FloatArg(context.InputJson, "y", go.transform.position.y);
            var z = ProBuilderInput.FloatArg(context.InputJson, "z", go.transform.position.z);
            var newPivot = new Vector3(x, y, z);

            Undo.RecordObject(go.transform, "ProBuilder Set Pivot");
            go.transform.position = newPivot;
            EditorSceneManager.MarkSceneDirty(go.scene);

            var result = new UnityCliJsonWriter().BeginObject()
                .PropObject("newPivotPosition", o => o.Prop("x", x).Prop("y", y).Prop("z", z))
                .EndObject().ToString();

            return ProBuilderErrors.Success("probuilder.set-pivot", result);
        }
    }

    /// <summary>
    /// probuilder.repair-mesh — Repair a ProBuilder mesh.
    /// </summary>
    [UnityCliTool(
        "probuilder.repair-mesh",
        Summary = "ProBuilder Repair Mesh",
        Description = "Attempt to repair a ProBuilder mesh by removing degenerate triangles and unused vertices.",
        Group = "probuilder",
        InputSchema = "{\"type\":\"object\",\"properties\":{\"target\":{\"type\":\"string\"}},\"required\":[\"target\"]}",
        OutputSchema = "{\"type\":\"object\",\"properties\":{\"removedDegenerateCount\":{\"type\":\"integer\"},\"fixedCount\":{\"type\":\"integer\"},\"remainingIssues\":{\"type\":\"integer\"}}}",
        DestructiveHint = false,
        Target = "editor",
        DefaultTimeoutMs = 10000,
        MaxTimeoutMs = 30000,
        SupportsJob = true,
        ArtifactKinds = "json",
        RequiredInput = "target")]
    public static class ProBuilderRepairMeshTool
    {
        public static string Execute(UnityCliProjectToolContext context)
        {
            if (!ProBuilderReflection.IsAvailable)
                return ProBuilderErrors.PackageNotAvailable();

            var go = ProBuilderInput.ResolveTarget(context.InputJson);
            if (go == null) return ProBuilderErrors.TargetNotFound(ProBuilderInput.StringArg(context.InputJson, "target"));

            var mesh = ProBuilderReflection.GetProBuilderMesh(go);
            if (mesh == null) return ProBuilderErrors.OperationFailed("repair-mesh", "No ProBuilderMesh on target.");

            // Validate first to count issues
            var validationResult = ProBuilderReflection.Validate(mesh);
            var degenerateCount = 0;
            var unusedCount = 0;

            if (validationResult != null)
            {
                try
                {
                    var resultType = validationResult.GetType();
                    var degProp = resultType.GetProperty("degenerateTriangles");
                    if (degProp != null) degenerateCount = Convert.ToInt32(degProp.GetValue(validationResult), CultureInfo.InvariantCulture);
                    var unusedProp = resultType.GetProperty("unusedVertices");
                    if (unusedProp != null) unusedCount = Convert.ToInt32(unusedProp.GetValue(validationResult), CultureInfo.InvariantCulture);
                }
                catch (Exception) { }
            }

            Undo.RecordObject(go, "ProBuilder Repair Mesh");

            // Remove degenerate triangles via reflection
            var meshOpsType = ProBuilderReflection.GetMeshOperationsType();
            if (meshOpsType != null)
            {
                try
                {
                    var removeDegMethod = meshOpsType.GetMethod("RemoveDegenerateTriangles", new[] { ProBuilderReflection.GetProBuilderMeshType() });
                    if (removeDegMethod != null)
                        removeDegMethod.Invoke(null, new object[] { mesh });

                    var removeUnusedMethod = meshOpsType.GetMethod("RemoveUnusedVertices", new[] { ProBuilderReflection.GetProBuilderMeshType() });
                    if (removeUnusedMethod != null)
                        removeUnusedMethod.Invoke(null, new object[] { mesh });
                }
                catch (Exception) { }
            }

            ProBuilderReflection.RefreshMesh(mesh);
            EditorSceneManager.MarkSceneDirty(go.scene);

            var fixedCount = degenerateCount + unusedCount;

            // Re-validate to check remaining issues
            var remainingIssues = 0;
            var postValidation = ProBuilderReflection.Validate(mesh);
            if (postValidation != null)
            {
                try
                {
                    var resultType = postValidation.GetType();
                    var degProp = resultType.GetProperty("degenerateTriangles");
                    var unusedProp = resultType.GetProperty("unusedVertices");
                    if (degProp != null) remainingIssues += Convert.ToInt32(degProp.GetValue(postValidation), CultureInfo.InvariantCulture);
                    if (unusedProp != null) remainingIssues += Convert.ToInt32(unusedProp.GetValue(postValidation), CultureInfo.InvariantCulture);
                }
                catch (Exception) { }
            }

            var result = new UnityCliJsonWriter().BeginObject()
                .Prop("removedDegenerateCount", degenerateCount)
                .Prop("fixedCount", fixedCount)
                .Prop("remainingIssues", remainingIssues)
                .EndObject().ToString();

            return ProBuilderErrors.Success("probuilder.repair-mesh", result);
        }
    }
}
#endif
