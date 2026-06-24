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
    // Shape Creation tools: probuilder.create-shape, probuilder.create-poly-shape
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// probuilder.create-shape — Create a ProBuilder primitive shape in the scene.
    /// </summary>
    [UnityCliTool(
        "probuilder.create-shape",
        Title = "ProBuilder Create Shape",
        Description = "Create a ProBuilder primitive shape in the scene. Supports Cube, Cylinder, Sphere, Plane, Cone, Torus, Pipe, Arch, Stair, CurvedStair, Door, Prism.",
        Group = "probuilder",
        InputSchema = "{\"type\":\"object\",\"properties\":{\"shapeType\":{\"type\":\"string\",\"enum\":[\"Cube\",\"Cylinder\",\"Sphere\",\"Plane\",\"Cone\",\"Torus\",\"Pipe\",\"Arch\",\"Stair\",\"CurvedStair\",\"Door\",\"Prism\"],\"description\":\"Shape type to create\"},\"name\":{\"type\":\"string\",\"description\":\"Name for the created GameObject\"},\"position\":{\"type\":\"object\",\"properties\":{\"x\":{\"type\":\"number\"},\"y\":{\"type\":\"number\"},\"z\":{\"type\":\"number\"}}},\"width\":{\"type\":\"number\"},\"height\":{\"type\":\"number\"},\"depth\":{\"type\":\"number\"},\"radius\":{\"type\":\"number\"},\"segments\":{\"type\":\"integer\"},\"sides\":{\"type\":\"integer\"}},\"required\":[\"shapeType\"]}",
        OutputSchema = "{\"type\":\"object\",\"properties\":{\"object\":{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"integer\"},\"name\":{\"type\":\"string\"},\"path\":{\"type\":\"string\"},\"shape\":{\"type\":\"string\"},\"hasProBuilderMesh\":{\"type\":\"boolean\"}}}}}",
        DestructiveHint = false,
        Target = "editor",
        DefaultTimeoutMs = 10000,
        MaxTimeoutMs = 30000,
        SupportsJob = true,
        ArtifactKinds = "json",
        RequiredInput = "shapeType")]
    public static class ProBuilderCreateShapeTool
    {
        public static string Execute(UnityCliProjectToolContext context)
        {
            if (!ProBuilderReflection.IsAvailable)
                return ProBuilderErrors.PackageNotAvailable();

            var shapeType = ProBuilderInput.StringArg(context.InputJson, "shapeType");
            if (string.IsNullOrEmpty(shapeType))
                return ProBuilderErrors.SchemaInvalid("shapeType is required for probuilder.create-shape.");

            var name = ProBuilderInput.StringArg(context.InputJson, "name") ?? ("ProBuilder_" + shapeType);
            var x = ProBuilderInput.FloatArg(context.InputJson, "x", 0f);
            var y = ProBuilderInput.FloatArg(context.InputJson, "y", 0f);
            var z = ProBuilderInput.FloatArg(context.InputJson, "z", 0f);
            var position = new Vector3(x, y, z);

            var go = ProBuilderReflection.CreateShape(shapeType, position, Quaternion.identity);
            if (go == null)
            {
                // Fallback: create a Unity primitive and convert
                var shape = NormalizeShape(shapeType);
                var primitive = CreateUnityPrimitive(shape);
                if (primitive == null)
                    return ProBuilderErrors.OperationFailed("create-shape", "Failed to create shape: " + shapeType);
                go = primitive;
                go.transform.position = position;
                ProBuilderReflection.AddProBuilderMesh(go);
            }

            go.name = name;

            // Apply dimensions if specified
            var width = ProBuilderInput.FloatArg(context.InputJson, "width");
            var height = ProBuilderInput.FloatArg(context.InputJson, "height");
            var depth = ProBuilderInput.FloatArg(context.InputJson, "depth");
            if (width.HasValue || height.HasValue || depth.HasValue)
            {
                var scale = go.transform.localScale;
                go.transform.localScale = new Vector3(
                    width ?? scale.x,
                    height ?? scale.y,
                    depth ?? scale.z);
            }

            Undo.RegisterCreatedObjectUndo(go, "ProBuilder Create Shape");
            EditorSceneManager.MarkSceneDirty(go.scene);

            var hasPbMesh = ProBuilderReflection.HasProBuilderMesh(go);
            var result = new UnityCliJsonWriter().BeginObject()
                .PropObject("object", obj =>
                {
                    obj.Prop("id", (long)go.GetInstanceID())
                       .Prop("name", go.name)
                       .Prop("path", HierarchyPath(go))
                       .Prop("shape", shapeType)
                       .Prop("hasProBuilderMesh", hasPbMesh);
                })
                .EndObject().ToString();

            return ProBuilderErrors.Success("probuilder.create-shape", result);
        }

        private static string NormalizeShape(string shapeType)
        {
            var s = (shapeType ?? "Cube").Trim();
            if (string.Equals(s, "box", StringComparison.OrdinalIgnoreCase)) return "Cube";
            if (string.Equals(s, "quad", StringComparison.OrdinalIgnoreCase)) return "Plane";
            return s;
        }

        private static GameObject CreateUnityPrimitive(string shape)
        {
            switch (shape.ToLowerInvariant())
            {
                case "cube": return GameObject.CreatePrimitive(PrimitiveType.Cube);
                case "sphere": return GameObject.CreatePrimitive(PrimitiveType.Sphere);
                case "cylinder": return GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                case "plane": return GameObject.CreatePrimitive(PrimitiveType.Plane);
                case "cone": return GameObject.CreatePrimitive(PrimitiveType.Capsule); // closest
                default: return GameObject.CreatePrimitive(PrimitiveType.Cube);
            }
        }

        private static string HierarchyPath(GameObject go)
        {
            var names = new Stack<string>();
            var current = go.transform;
            while (current != null)
            {
                names.Push(current.name);
                current = current.parent;
            }
            return string.Join("/", names.ToArray());
        }
    }

    /// <summary>
    /// probuilder.create-poly-shape — Create a ProBuilder mesh from polygon points.
    /// </summary>
    [UnityCliTool(
        "probuilder.create-poly-shape",
        Title = "ProBuilder Create Poly Shape",
        Description = "Create a ProBuilder mesh from a set of 2D polygon points extruded to a given height.",
        Group = "probuilder",
        InputSchema = "{\"type\":\"object\",\"properties\":{\"points\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"properties\":{\"x\":{\"type\":\"number\"},\"y\":{\"type\":\"number\"},\"z\":{\"type\":\"number\"}}}},\"extrudeHeight\":{\"type\":\"number\"},\"flipNormals\":{\"type\":\"boolean\"},\"name\":{\"type\":\"string\"}},\"required\":[\"points\"]}",
        OutputSchema = "{\"type\":\"object\",\"properties\":{\"object\":{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"integer\"},\"name\":{\"type\":\"string\"},\"path\":{\"type\":\"string\"},\"faceCount\":{\"type\":\"integer\"},\"vertexCount\":{\"type\":\"integer\"}}}}}",
        DestructiveHint = false,
        Target = "editor",
        DefaultTimeoutMs = 15000,
        MaxTimeoutMs = 30000,
        SupportsJob = true,
        ArtifactKinds = "json",
        RequiredInput = "points")]
    public static class ProBuilderCreatePolyShapeTool
    {
        public static string Execute(UnityCliProjectToolContext context)
        {
            if (!ProBuilderReflection.IsAvailable)
                return ProBuilderErrors.PackageNotAvailable();

            var name = ProBuilderInput.StringArg(context.InputJson, "name") ?? "ProBuilder_PolyShape";
            var height = ProBuilderInput.FloatArg(context.InputJson, "extrudeHeight", 1f);
            var flipNormals = ProBuilderInput.BoolArg(context.InputJson, "flipNormals");

            var points = ProBuilderInput.Vector3ListArg(context.InputJson, "points");
            if (points == null || points.Count < 3)
                return ProBuilderErrors.SchemaInvalid("At least 3 points are required for create-poly-shape.");

            // Create a new GameObject with ProBuilderMesh via reflection
            var go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, "ProBuilder Create Poly Shape");

            var pbMesh = ProBuilderReflection.AddProBuilderMesh(go);
            if (pbMesh == null)
            {
                UnityEngine.Object.DestroyImmediate(go);
                return ProBuilderErrors.OperationFailed("create-poly-shape", "Failed to add ProBuilderMesh component.");
            }

            // Use AppendElements.CreateShapeFromPolygon via reflection
            var created = ProBuilderReflection.CreateShapeFromPolygon(pbMesh, points, height, flipNormals);
            if (!created)
            {
                UnityEngine.Object.DestroyImmediate(go);
                return ProBuilderErrors.OperationFailed("create-poly-shape", "CreateShapeFromPolygon method not found or failed in ProBuilder assembly.");
            }

            ProBuilderReflection.RefreshMesh(pbMesh);
            EditorSceneManager.MarkSceneDirty(go.scene);

            var faceCount = ProBuilderReflection.GetFaceCount(pbMesh);
            var vertexCount = ProBuilderReflection.GetVertexCount(pbMesh);

            var result = new UnityCliJsonWriter().BeginObject()
                .PropObject("object", obj =>
                {
                    obj.Prop("id", (long)go.GetInstanceID())
                       .Prop("name", go.name)
                       .Prop("path", HierarchyPath(go))
                       .Prop("faceCount", faceCount)
                       .Prop("vertexCount", vertexCount);
                })
                .EndObject().ToString();

            return ProBuilderErrors.Success("probuilder.create-poly-shape", result);
        }

        private static string HierarchyPath(GameObject go)
        {
            var names = new Stack<string>();
            var current = go.transform;
            while (current != null) { names.Push(current.name); current = current.parent; }
            return string.Join("/", names.ToArray());
        }
    }
}
#endif
