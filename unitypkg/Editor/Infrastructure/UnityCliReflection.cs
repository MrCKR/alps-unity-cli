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
        private static object MemberValue(Type type, object instance, string name)
        {
            var property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
            if (property != null) return property.GetValue(instance, null);
            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public);
            return field == null ? null : field.GetValue(instance);
        }

        private static string AssetReferenceJson(UnityEngine.Object asset)
        {
            if (asset == null) return "null";
            var path = NormalizeAssetPath(AssetDatabase.GetAssetPath(asset));
            return "{\"name\":" + Quote(asset.name) + ",\"path\":" + Quote(path) + ",\"guid\":" + Quote(string.IsNullOrEmpty(path) ? null : AssetDatabase.AssetPathToGUID(path)) + "}";
        }

        private static Type FindType(string fullName)
        {
            var type = Type.GetType(fullName);
            if (type != null)
            {
                return type;
            }
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                type = assembly.GetType(fullName);
                if (type != null)
                {
                    return type;
                }
            }
            return null;
        }
    }
}
#endif
