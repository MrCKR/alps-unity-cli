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
        /// <summary>
        /// Register context.* internal bridge commands.
        ///
        /// Context commands are read-only observations of Unity project and editor state.
        /// They are always allowed in Play Mode because they never mutate state.
        /// If a capability has side effects, it must be a Tool, not a Context.
        ///
        /// P1 baseline: command routing skeleton. P2 will populate built-in context handlers.
        /// </summary>
        private static void RegisterContextCommands(BridgeCommandRegistry registry)
        {
            RegisterCommand(registry, "context.list", "context", false, ContextList);
            RegisterCommand(registry, "context.describe", "context", false, ContextDescribe);
            RegisterCommand(registry, "context.read", "context", false, ContextRead);
        }

        private static string ContextList(BridgeRequest request)
        {
            // P1 skeleton: return empty list. P2 will enumerate built-in contexts.
            return Json(new { ok = true, contexts = new object[0] });
        }

        private static string ContextDescribe(BridgeRequest request)
        {
            var name = request.StringArg("contextName") ?? request.StringArg("name");
            if (string.IsNullOrEmpty(name))
            {
                return ErrorJson("E_CONTEXT_NAME_REQUIRED", "contextName is required.", "Pass contextName as a string argument.");
            }
            // P1 skeleton: context not found. P2 will look up built-in context descriptors.
            return ErrorJson("E_CONTEXT_NOT_FOUND", $"Context not found: {name}.", "Run context list to see available contexts.");
        }

        private static string ContextRead(BridgeRequest request)
        {
            var name = request.StringArg("contextName") ?? request.StringArg("name");
            if (string.IsNullOrEmpty(name))
            {
                return ErrorJson("E_CONTEXT_NAME_REQUIRED", "contextName is required.", "Pass contextName as a string argument.");
            }
            // P1 skeleton: context not found. P2 will read built-in context data.
            return ErrorJson("E_CONTEXT_NOT_FOUND", $"Context not found: {name}.", "Run context list to see available contexts.");
        }
    }
}
#endif
