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
        private sealed class BridgeCommandRegistry
        {
            private readonly Dictionary<string, BridgeCommandDefinition> commands = new Dictionary<string, BridgeCommandDefinition>(StringComparer.Ordinal);

            public void Register(string command, string domain, bool readOnly, bool destructive, int timeoutMs, Func<BridgeRequest, string> handler)
            {
                if (string.IsNullOrEmpty(command))
                {
                    throw new ArgumentException("Bridge command registration requires a command name.", "command");
                }
                if (handler == null)
                {
                    throw new ArgumentNullException("handler");
                }
                commands[command] = new BridgeCommandDefinition(command, domain, readOnly, destructive, timeoutMs, handler);
            }

            public string Execute(BridgeRequest request)
            {
                BridgeCommandDefinition definition;
                if (request == null || !commands.TryGetValue(request.Command, out definition))
                {
                    return null;
                }
                return definition.Handler(request);
            }

            public bool IsMutating(string command)
            {
                BridgeCommandDefinition definition;
                return command != null && commands.TryGetValue(command, out definition) && definition.Destructive;
            }
        }

        private sealed class BridgeCommandDefinition
        {
            public BridgeCommandDefinition(string command, string domain, bool readOnly, bool destructive, int timeoutMs, Func<BridgeRequest, string> handler)
            {
                Command = command;
                Domain = domain;
                ReadOnly = readOnly;
                Destructive = destructive;
                TimeoutMs = timeoutMs;
                Handler = handler;
            }

            public string Command { get; private set; }
            public string Domain { get; private set; }
            public bool ReadOnly { get; private set; }
            public bool Destructive { get; private set; }
            public int TimeoutMs { get; private set; }
            public Func<BridgeRequest, string> Handler { get; private set; }
        }

        private static BridgeCommandRegistry CreateCommandRegistry()
        {
            var registry = new BridgeCommandRegistry();
            RegisterCameraCommands(registry);
            RegisterSceneCommands(registry);
            RegisterPrefabCommands(registry);
            RegisterGameObjectCommands(registry);
            RegisterAssetCommands(registry);
            RegisterUiCommands(registry);
            RegisterMaterialCommands(registry);
            RegisterGraphicsCommands(registry);
            RegisterPhysicsCommands(registry);
            RegisterProfilerCommands(registry);
            RegisterInstalledExtensionCommands(registry);
            RegisterAnimationCommands(registry);
            RegisterShaderCommands(registry);
            RegisterTextureCommands(registry);
            RegisterEditorCommands(registry);
            RegisterDiagnoseCommands(registry);
            RegisterScriptCommands(registry);
            RegisterScriptableObjectCommands(registry);
            RegisterCodeExecCommand(registry);
            RegisterProjectToolCommands(registry);
            RegisterRuntimeToolCommands(registry);
            RegisterContextCommands(registry);
            RegisterExtensionCommands(registry);
            return registry;
        }

        private static void RegisterCommand(BridgeCommandRegistry registry, string command, string domain, bool destructive, Func<BridgeRequest, string> handler)
        {
            registry.Register(command, domain, !destructive, destructive, destructive ? 60000 : 30000, handler);
        }

        /// <summary>
        /// 通过方法名反射注册已复制到项目中的可选 extension 命令，避免 core 静态引用可选包代码。
        /// </summary>
        private static void RegisterInstalledExtensionCommands(BridgeCommandRegistry registry)
        {
            var methodNames = new[]
            {
                "RegisterProBuilderCommands",
                "RegisterCinemachineCommands",
                "RegisterVfxCommands",
                "RegisterUrpCommands",
                "RegisterHdrpCommands"
            };
            foreach (var methodName in methodNames)
            {
                var method = typeof(UnityCliBridge).GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic);
                if (method == null)
                {
                    continue;
                }
                method.Invoke(null, new object[] { registry });
            }
        }
    }
}
#endif
