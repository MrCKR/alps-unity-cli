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
        private static void RegisterGoCommands(BridgeCommandRegistry registry)
        {
            RegisterCommand(registry, "go.find", "go", false, GameObjectFind);
        }

        private static TargetResolution ResolveGameObjectTarget(string target)
        {
            var matches = new List<GameObject>();
            if (string.IsNullOrEmpty(target))
            {
                return TargetResolution.FromMatches(matches);
            }

            foreach (var gameObject in UnityEngine.Object.FindObjectsOfType<GameObject>())
            {
                if (gameObject.name == target || gameObject.GetInstanceID().ToString(CultureInfo.InvariantCulture) == target || HierarchyPath(gameObject) == target)
                {
                    matches.Add(gameObject);
                }
            }
            return TargetResolution.FromMatches(matches);
        }

        private static GameObject ResolveUniqueTarget(string target, string requiredMessage)
        {
            if (string.IsNullOrEmpty(target))
            {
                throw new BridgeCommandException("E_TARGET_NOT_FOUND", requiredMessage, "Pass a GameObject name, hierarchy path or instance id.");
            }
            var matches = new List<GameObject>();
            foreach (var gameObject in UnityEngine.Object.FindObjectsOfType<GameObject>())
            {
                if (gameObject.name == target || gameObject.GetInstanceID().ToString(CultureInfo.InvariantCulture) == target || HierarchyPath(gameObject) == target)
                {
                    matches.Add(gameObject);
                }
            }
            if (matches.Count > 1)
            {
                throw new BridgeCommandException("E_TARGET_AMBIGUOUS", "Multiple GameObjects matched: " + target, "Pass a hierarchy path or instance id.");
            }
            if (matches.Count == 0)
            {
                throw new BridgeCommandException("E_TARGET_NOT_FOUND", "Target not found: " + target, "Run unity_cli scene hierarchy or pass a hierarchy path / instance id.");
            }
            return matches[0];
        }

        private static string GameObjectTargetJson(GameObject gameObject)
        {
            return "{\"id\":" + gameObject.GetInstanceID().ToString(CultureInfo.InvariantCulture) + ",\"name\":" + Quote(gameObject.name) + ",\"path\":" + Quote(HierarchyPath(gameObject)) + "}";
        }


        private static string GameObjectFind(BridgeRequest request)
        {
            var target = request.StringArg("target");
            if (string.IsNullOrEmpty(target))
            {
                target = request.StringArg("name");
            }
            if (string.IsNullOrEmpty(target))
            {
                return ErrorJson("E_TARGET_NOT_FOUND", "GameObject find requires --name or a target.", "Pass unity_cli go find --name <name>, a hierarchy path, or an instance id.");
            }
            var resolution = ResolveGameObjectTarget(target);
            var items = new List<string>();
            foreach (var gameObject in resolution.Matches)
            {
                items.Add(HierarchyItemJson(gameObject.transform));
            }
            return SuccessJson("{\"query\":" + Quote(target) + ",\"matchCount\":" + resolution.Matches.Count.ToString(CultureInfo.InvariantCulture) + ",\"items\":[" + string.Join(",", items.ToArray()) + "],\"logPath\":" + Quote(LogPath()) + "}");
        }
        private sealed class Counter
        {
            public int Value;
        }
    }
}
#endif
