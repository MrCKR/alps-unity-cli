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
        // 改为 internal：自定义工具 handler（例如 ProBuilder，位于 UnityCli.ProBuilder 等
        // 独立 namespace）需要抛出该异常，驱动 project-tool.run / runtime-tool.run 的
        // 结构化错误 envelope，避免错误被外层 SuccessJson 误包成 ok:true。
        internal sealed class BridgeCommandException : Exception
        {
            public BridgeCommandException(string code, string message, string nextAction = null, bool transient = false, int retryAfterMs = 0) : base(message)
            {
                Code = code;
                NextAction = nextAction;
                Transient = transient;
                RetryAfterMs = retryAfterMs;
            }

            // 新增重载：支持机器可读 details 与多个 next_actions，供需要富错误信息的 handler 使用。
            // NextAction 取首个 action，保证旧的 envelope 逻辑兼容。
            public BridgeCommandException(string code, string message, string detailsJson, IEnumerable<string> nextActions, bool transient = false, int retryAfterMs = 0) : base(message)
            {
                Code = code;
                DetailsJson = detailsJson;
                NextActions = nextActions == null ? null : new List<string>(nextActions);
                NextAction = NextActions != null && NextActions.Count > 0 ? NextActions[0] : null;
                Transient = transient;
                RetryAfterMs = retryAfterMs;
            }

            public string Code { get; private set; }
            public string NextAction { get; private set; }
            // 多个 next_actions，优先于 NextAction 的单值表示。
            public IReadOnlyList<string> NextActions { get; private set; }
            // 结构化错误 details（对象 JSON），由错误 envelope 透传到 error.details。
            public string DetailsJson { get; private set; }
            public bool Transient { get; private set; }
            public int RetryAfterMs { get; private set; }
        }
    }
}
#endif
