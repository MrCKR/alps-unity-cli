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
            RegisterCommand(registry, "project-tool.groups", "project-tool", false, ProjectToolGroups);
            RegisterCommand(registry, "project-tool.summary", "project-tool", false, ProjectToolSummary);
            RegisterCommand(registry, "project-tool.describe", "project-tool", false, ProjectToolDescribe);
            RegisterCommand(registry, "project-tool.list", "project-tool", false, ProjectToolList);
            registry.Register<UnityCli.Protocol.BridgeRequest>(
                "project-tool.run",
                "project-tool",
                false,
                60000,
                ProjectToolRun);
        }
    }
}
#endif
