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
            // ProBuilder bridge commands are served by single-action extension tools
            // discovered as IUnityCliTool implementations in the UnityCli.ProBuilder namespace.
            // ProjectToolRegistry.DiscoverProjectTools() scans loaded assemblies, so no
            // manual registration is needed here.
        }

        private static bool IsProBuilderInstalled()
        {
            return ProBuilder.ProBuilderReflection.IsAvailable;
        }

        private static string NormalizeProBuilderShape(string rawShape)
        {
            return ProBuilder.ProBuilderReflection.ParseShapeType(rawShape) != null ? rawShape : "Cube";
        }

        private static GameObject ResolveRequiredProBuilderTarget(string target)
        {
            if (string.IsNullOrEmpty(target))
            {
                throw new BridgeCommandException("E_TARGET_NOT_FOUND", "ProBuilder command requires --target <go>.", "Pass a GameObject name, hierarchy path or instance id.");
            }
            var go = ProBuilder.ProBuilderInput.ResolveTarget("{\"target\":\"" + target + "\"}");
            if (go == null)
            {
                throw new BridgeCommandException("E_TARGET_NOT_FOUND", "ProBuilder target not found: " + target, "Pass a GameObject name, hierarchy path or instance id.");
            }
            return go;
        }

        private static string ProBuilderPackageJson()
        {
            var available = ProBuilder.ProBuilderReflection.IsAvailable;
            var meshTypeAvailable = ProBuilder.ProBuilderReflection.GetProBuilderMeshType() != null;
            return new UnityCliJsonWriter().BeginObject()
                .Prop("installed", available)
                .Prop("meshTypeAvailable", meshTypeAvailable)
                .EndObject().ToString();
        }
    }
}
#endif
