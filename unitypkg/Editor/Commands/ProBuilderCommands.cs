#if UNITY_EDITOR
using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CSharp;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using UiButton = UnityEngine.UI.Button;
using UiCanvasScaler = UnityEngine.UI.CanvasScaler;
using UiGraphicRaycaster = UnityEngine.UI.GraphicRaycaster;
using UiImage = UnityEngine.UI.Image;
using UiText = UnityEngine.UI.Text;

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        private static void RegisterProBuilderCommands(BridgeCommandRegistry registry)
        {
            // ProBuilder core registration disabled — now served by extension package (P7-T2b)
            // The 36 single-action ProBuilder tools will be implemented in the extension's
            // ProBuilderCommands.cs by P2b. This file is retained for reference and
            // to keep the partial class compilable until T2b migration is complete.
        }

        private static bool IsProBuilderInstalled()
        {
            return FindType("UnityEngine.ProBuilder.ProBuilderMesh") != null || FindType("UnityEngine.ProBuilder.ShapeGenerator") != null;
        }

        private static string NormalizeProBuilderShape(string rawShape)
        {
            var shape = string.IsNullOrEmpty(rawShape) ? "cube" : rawShape.ToLowerInvariant();
            if (shape == "box")
            {
                return "cube";
            }
            if (shape == "quad")
            {
                return "plane";
            }
            if (shape == "cube" || shape == "sphere" || shape == "cylinder" || shape == "plane" || shape == "capsule")
            {
                return shape;
            }
            throw new BridgeCommandException("E_PROBUILDER_SHAPE_UNSUPPORTED", "Unsupported ProBuilder shape: " + rawShape, "Use cube, sphere, cylinder, plane or capsule.");
        }

        private static GameObject CreateProBuilderShape(string shape, string name, BridgeRequest request)
        {
            PrimitiveType primitive;
            switch (shape)
            {
                case "sphere":
                    primitive = PrimitiveType.Sphere;
                    break;
                case "cylinder":
                    primitive = PrimitiveType.Cylinder;
                    break;
                case "plane":
                    primitive = PrimitiveType.Plane;
                    break;
                case "capsule":
                    primitive = PrimitiveType.Capsule;
                    break;
                default:
                    primitive = PrimitiveType.Cube;
                    break;
            }
            var gameObject = GameObject.CreatePrimitive(primitive);
            gameObject.name = name;
            var width = Math.Max(0.0001f, request.FloatArg("width", request.FloatArg("radius", 1f) * 2f));
            var height = Math.Max(0.0001f, request.FloatArg("height", 1f));
            var depth = Math.Max(0.0001f, request.FloatArg("depth", width));
            gameObject.transform.localScale = new Vector3(width, height, depth);
            TryAttachProBuilderMesh(gameObject);
            return gameObject;
        }

        private static void TryAttachProBuilderMesh(GameObject gameObject)
        {
            var type = FindType("UnityEngine.ProBuilder.ProBuilderMesh");
            if (type == null || gameObject.GetComponent(type) != null)
            {
                return;
            }
            try
            {
                gameObject.AddComponent(type);
            }
            catch (Exception exception)
            {
                WriteLog("probuilder_mesh_attach_failed " + exception.GetType().Name + ": " + exception.Message);
            }
        }

        private static GameObject ResolveRequiredProBuilderTarget(string target)
        {
            if (string.IsNullOrEmpty(target))
            {
                throw new BridgeCommandException("E_TARGET_NOT_FOUND", "ProBuilder command requires --target <go>.", "Pass a GameObject name, hierarchy path or instance id.");
            }
            var gameObject = ResolveGameObject(target);
            if (gameObject == null)
            {
                throw new BridgeCommandException("E_TARGET_NOT_FOUND", "ProBuilder target not found: " + target, "Pass a GameObject name, hierarchy path or instance id.");
            }
            return gameObject;
        }

        private static string ProBuilderPackageJson()
        {
            return new UnityCliJsonWriter().BeginObject()
                .Prop("installed", IsProBuilderInstalled())
                .Prop("meshTypeAvailable", FindType("UnityEngine.ProBuilder.ProBuilderMesh") != null)
                .EndObject().ToString();
        }

        private static string ProBuilderObjectJson(GameObject gameObject, string shape)
        {
            return new UnityCliJsonWriter().BeginObject()
                .Prop("id", (long)gameObject.GetInstanceID())
                .Prop("name", gameObject.name)
                .Prop("path", HierarchyPath(gameObject))
                .Prop("shape", shape)
                .PropRaw("position", Vector3Json(gameObject.transform.position))
                .PropRaw("scale", Vector3Json(gameObject.transform.localScale))
                .Prop("hasProBuilderMesh", FindType("UnityEngine.ProBuilder.ProBuilderMesh") != null && gameObject.GetComponent(FindType("UnityEngine.ProBuilder.ProBuilderMesh")) != null)
                .EndObject().ToString();
        }

        private static string ProBuilderCreate(BridgeRequest request)
        {
            if (!IsProBuilderInstalled())
            {
                return ErrorJson("E_PROBUILDER_PACKAGE_NOT_FOUND", "Unity ProBuilder package is not installed.", "Install com.unity.probuilder with unity_cli package install com.unity.probuilder");
            }

            var shape = NormalizeProBuilderShape(request.StringArg("shape"));
            var name = request.StringArg("name");
            if (string.IsNullOrEmpty(name))
            {
                name = "UnityCli_ProBuilder_" + shape;
            }
            var gameObject = CreateProBuilderShape(shape, name, request);
            var parent = request.StringArg("parent");
            if (!string.IsNullOrEmpty(parent))
            {
                var parentObject = ResolveGameObject(parent);
                if (parentObject == null)
                {
                    return ErrorJson("E_TARGET_NOT_FOUND", "ProBuilder parent not found: " + parent, "Pass an existing GameObject name, hierarchy path or instance id.");
                }
                gameObject.transform.SetParent(parentObject.transform, false);
            }
            gameObject.transform.position = new Vector3(request.FloatArg("x", gameObject.transform.position.x), request.FloatArg("y", gameObject.transform.position.y), request.FloatArg("z", gameObject.transform.position.z));
            Undo.RegisterCreatedObjectUndo(gameObject, "UnityCli ProBuilder Create");
            EditorSceneManager.MarkSceneDirty(gameObject.scene);
            WriteLog("probuilder_shape_created shape=" + shape + " path=" + HierarchyPath(gameObject));
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("probuilder", ProBuilderPackageJson())
                .PropRaw("object", ProBuilderObjectJson(gameObject, shape))
                .PropArray("plannedEffects", arr =>
                {
                    arr.BeginObject().Prop("type", "probuilder.create").Prop("shape", shape).Prop("path", HierarchyPath(gameObject)).EndObject();
                })
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static string ProBuilderCsg(BridgeRequest request)
        {
            if (!IsProBuilderInstalled())
            {
                return ErrorJson("E_PROBUILDER_PACKAGE_NOT_FOUND", "Unity ProBuilder package is not installed.", "Install com.unity.probuilder with unity_cli package install com.unity.probuilder");
            }
            var operation = request.StringArg("operation");
            if (string.IsNullOrEmpty(operation))
            {
                return ErrorJson("E_PROBUILDER_OPERATION_REQUIRED", "ProBuilder CSG requires --operation union|subtract|intersect.", "Pass an explicit CSG operation and target/source objects.");
            }
            var target = ResolveRequiredProBuilderTarget(request.StringArg("target"));
            target.name = target.name + "_" + operation;
            EditorSceneManager.MarkSceneDirty(target.scene);
            WriteLog("probuilder_csg operation=" + operation + " target=" + HierarchyPath(target));
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("probuilder", ProBuilderPackageJson())
                .Prop("operation", operation)
                .PropRaw("object", ProBuilderObjectJson(target, "csg"))
                .PropArray("plannedEffects", arr =>
                {
                    arr.BeginObject().Prop("type", "probuilder.csg").Prop("operation", operation).Prop("target", HierarchyPath(target)).EndObject();
                })
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static string ProBuilderEdit(BridgeRequest request)
        {
            if (!IsProBuilderInstalled())
            {
                return ErrorJson("E_PROBUILDER_PACKAGE_NOT_FOUND", "Unity ProBuilder package is not installed.", "Install com.unity.probuilder with unity_cli package install com.unity.probuilder");
            }
            var target = ResolveRequiredProBuilderTarget(request.StringArg("target"));
            var operation = request.StringArg("operation");
            if (string.IsNullOrEmpty(operation))
            {
                return ErrorJson("E_PROBUILDER_OPERATION_REQUIRED", "ProBuilder edit requires --operation <name>.", "Pass a supported edit operation such as rename or scale.");
            }
            if (string.Equals(operation, "rename", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(request.StringArg("value")))
            {
                target.name = request.StringArg("value");
            }
            if (string.Equals(operation, "scale", StringComparison.OrdinalIgnoreCase))
            {
                target.transform.localScale = new Vector3(request.FloatArg("width", target.transform.localScale.x), request.FloatArg("height", target.transform.localScale.y), request.FloatArg("depth", target.transform.localScale.z));
            }
            EditorSceneManager.MarkSceneDirty(target.scene);
            WriteLog("probuilder_edit operation=" + operation + " target=" + HierarchyPath(target));
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("probuilder", ProBuilderPackageJson())
                .Prop("operation", operation)
                .PropRaw("object", ProBuilderObjectJson(target, "edit"))
                .PropArray("plannedEffects", arr =>
                {
                    arr.BeginObject().Prop("type", "probuilder.edit").Prop("operation", operation).Prop("target", HierarchyPath(target)).EndObject();
                })
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }
    }
}
#endif
