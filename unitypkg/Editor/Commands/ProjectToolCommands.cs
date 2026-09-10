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
        private static void RegisterProjectToolCommands(BridgeCommandRegistry registry)
        {
            RegisterCommand(registry, "catalog.editor.health", "catalog", false, ProjectToolCatalogHealth);
            RegisterCommand(registry, "catalog.editor.groups", "catalog", false, ProjectToolGroups);
            RegisterCommand(registry, "catalog.editor.summary", "catalog", false, ProjectToolSummary);
            RegisterCommand(registry, "catalog.editor.describe", "catalog", false, ProjectToolDescribe);
            RegisterCommand(registry, "catalog.editor.list", "catalog", false, ProjectToolList);
            registry.Register<UnityCli.Protocol.BridgeRequest>(
                "catalog.editor.run",
                "catalog",
                false,
                60000,
                ProjectToolRun);
        }
    }
}
#endif
