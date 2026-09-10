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
        private static void RegisterRuntimeToolCommands(BridgeCommandRegistry registry)
        {
            RegisterCommand(registry, "catalog.player.groups", "catalog", false, RuntimeToolGroups);
            RegisterCommand(registry, "catalog.player.summary", "catalog", false, RuntimeToolSummary);
            RegisterCommand(registry, "catalog.player.describe", "catalog", false, RuntimeToolDescribe);
            RegisterCommand(registry, "catalog.player.list", "catalog", false, RuntimeToolList);
            registry.Register<UnityCli.Protocol.BridgeRequest>(
                "catalog.player.run",
                "catalog",
                false,
                60000,
                RuntimeToolRun);
        }
    }
}
#endif