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
    // Utility & Info tools: probuilder.ping, probuilder.mesh-info,
    // probuilder.validate-mesh, probuilder.ping
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// probuilder.ping — Check ProBuilder package availability and bridge status.
    /// </summary>
    [UnityCliTool(
        "probuilder.ping",
        Title = "ProBuilder Ping",
        Description = "Check ProBuilder package availability and bridge status. Returns package info if installed.",
        Group = "probuilder",
        InputSchema = "{\"type\":\"object\",\"properties\":{}}",
        OutputSchema = "{\"type\":\"object\",\"properties\":{\"installed\":{\"type\":\"boolean\"},\"meshTypeAvailable\":{\"type\":\"boolean\"}}}",
        DestructiveHint = false,
        Target = "editor",
        DefaultTimeoutMs = 5000,
        MaxTimeoutMs = 10000,
        SupportsJob = false,
        ArtifactKinds = "json",
        RequiredInput = "")]
    public static class ProBuilderPingTool
    {
        public static string Execute(UnityCliProjectToolContext context)
        {
            var available = ProBuilderReflection.IsAvailable;
            var meshTypeAvailable = ProBuilderReflection.GetProBuilderMeshType() != null;
            var result = new UnityCliJsonWriter().BeginObject()
                .Prop("installed", available)
                .Prop("meshTypeAvailable", meshTypeAvailable)
                .EndObject().ToString();
            return ProBuilderErrors.Success("probuilder.ping", result);
        }
    }

    /// <summary>
    /// probuilder.mesh-info — Get detailed information about a ProBuilder mesh.
    /// </summary>
    [UnityCliTool(
        "probuilder.mesh-info",
        Title = "ProBuilder Mesh Info",
        Description = "Get detailed information about a ProBuilder mesh including vertex, face, edge counts and bounds.",
        Group = "probuilder",
        InputSchema = "{\"type\":\"object\",\"properties\":{\"target\":{\"type\":\"string\",\"description\":\"Target GameObject\"},\"include\":{\"type\":\"string\",\"enum\":[\"summary\",\"faces\",\"edges\",\"all\"],\"description\":\"Level of detail\"}},\"required\":[\"target\"]}",
        OutputSchema = "{\"type\":\"object\",\"properties\":{\"vertexCount\":{\"type\":\"integer\"},\"faceCount\":{\"type\":\"integer\"},\"edgeCount\":{\"type\":\"integer\"},\"bounds\":{\"type\":\"object\"},\"materials\":{\"type\":\"array\"}}}",
        DestructiveHint = false,
        Target = "editor",
        DefaultTimeoutMs = 10000,
        MaxTimeoutMs = 30000,
        SupportsJob = false,
        ArtifactKinds = "json",
        RequiredInput = "target")]
    public static class ProBuilderMeshInfoTool
    {
        public static string Execute(UnityCliProjectToolContext context)
        {
            if (!ProBuilderReflection.IsAvailable)
                return ProBuilderErrors.PackageNotAvailable();

            var go = ProBuilderInput.ResolveTarget(context.InputJson);
            if (go == null)
                return ProBuilderErrors.TargetNotFound(ProBuilderInput.StringArg(context.InputJson, "target"));

            var mesh = ProBuilderReflection.GetProBuilderMesh(go);
            if (mesh == null)
                return ProBuilderErrors.OperationFailed("mesh-info", "GameObject does not have a ProBuilderMesh component.");

            var faceCount = ProBuilderReflection.GetFaceCount(mesh);
            var vertexCount = ProBuilderReflection.GetVertexCount(mesh);

            var result = new UnityCliJsonWriter().BeginObject()
                .Prop("vertexCount", vertexCount)
                .Prop("faceCount", faceCount)
                .Prop("edgeCount", faceCount * 3 / 2) // approximation for triangles
                .PropRaw("bounds", BoundsJson(go))
                .PropRaw("materials", MaterialsJson(go))
                .EndObject().ToString();

            return ProBuilderErrors.Success("probuilder.mesh-info", result);
        }

        private static string BoundsJson(GameObject go)
        {
            var renderer = go.GetComponent<MeshRenderer>();
            if (renderer == null) return "{}";
            var bounds = renderer.bounds;
            return new UnityCliJsonWriter().BeginObject()
                .PropRaw("center", Vector3Json(bounds.center))
                .PropRaw("size", Vector3Json(bounds.size))
                .EndObject().ToString();
        }

        private static string MaterialsJson(GameObject go)
        {
            var renderer = go.GetComponent<MeshRenderer>();
            if (renderer == null) return "[]";
            var w = new UnityCliJsonWriter().BeginArray();
            foreach (var mat in renderer.sharedMaterials)
            {
                if (mat == null) continue;
                w.BeginObject()
                    .Prop("name", mat.name)
                    .Prop("path", UnityEditor.AssetDatabase.GetAssetPath(mat))
                    .EndObject();
            }
            w.EndArray();
            return w.ToString();
        }

        private static string Vector3Json(Vector3 v)
        {
            return new UnityCliJsonWriter().BeginObject()
                .Prop("x", v.x).Prop("y", v.y).Prop("z", v.z)
                .EndObject().ToString();
        }
    }

    /// <summary>
    /// probuilder.validate-mesh — Validate a ProBuilder mesh for issues.
    /// </summary>
    [UnityCliTool(
        "probuilder.validate-mesh",
        Title = "ProBuilder Validate Mesh",
        Description = "Validate a ProBuilder mesh for issues like degenerate triangles and unused vertices.",
        Group = "probuilder",
        InputSchema = "{\"type\":\"object\",\"properties\":{\"target\":{\"type\":\"string\",\"description\":\"Target GameObject\"}},\"required\":[\"target\"]}",
        OutputSchema = "{\"type\":\"object\",\"properties\":{\"healthy\":{\"type\":\"boolean\"},\"degenerateTriangles\":{\"type\":\"integer\"},\"unusedVertices\":{\"type\":\"integer\"},\"issues\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}}}}",
        DestructiveHint = false,
        Target = "editor",
        DefaultTimeoutMs = 10000,
        MaxTimeoutMs = 30000,
        SupportsJob = false,
        ArtifactKinds = "json",
        RequiredInput = "target")]
    public static class ProBuilderValidateMeshTool
    {
        public static string Execute(UnityCliProjectToolContext context)
        {
            if (!ProBuilderReflection.IsAvailable)
                return ProBuilderErrors.PackageNotAvailable();

            var go = ProBuilderInput.ResolveTarget(context.InputJson);
            if (go == null)
                return ProBuilderErrors.TargetNotFound(ProBuilderInput.StringArg(context.InputJson, "target"));

            var mesh = ProBuilderReflection.GetProBuilderMesh(go);
            if (mesh == null)
                return ProBuilderErrors.OperationFailed("validate-mesh", "GameObject does not have a ProBuilderMesh component.");

            var validationResult = ProBuilderReflection.Validate(mesh);
            var issues = new List<string>();
            var degenerate = 0;
            var unused = 0;

            if (validationResult != null)
            {
                try
                {
                    var resultType = validationResult.GetType();
                    var degProp = resultType.GetProperty("degenerateTriangles");
                    if (degProp != null) degenerate = Convert.ToInt32(degProp.GetValue(validationResult), CultureInfo.InvariantCulture);
                    var unusedProp = resultType.GetProperty("unusedVertices");
                    if (unusedProp != null) unused = Convert.ToInt32(unusedProp.GetValue(validationResult), CultureInfo.InvariantCulture);
                    var issuesProp = resultType.GetProperty("issues");
                    if (issuesProp != null)
                    {
                        var issuesArr = issuesProp.GetValue(validationResult) as IEnumerable<string>;
                        if (issuesArr != null) issues.AddRange(issuesArr);
                    }
                }
                catch (Exception) { }
            }

            var healthy = degenerate == 0 && unused == 0 && issues.Count == 0;

            var result = new UnityCliJsonWriter().BeginObject()
                .Prop("healthy", healthy)
                .Prop("degenerateTriangles", degenerate)
                .Prop("unusedVertices", unused)
                .PropArray("issues", arr =>
                {
                    foreach (var issue in issues) arr.Value(issue);
                })
                .EndObject().ToString();

            return ProBuilderErrors.Success("probuilder.validate-mesh", result);
        }
    }
}
#endif
