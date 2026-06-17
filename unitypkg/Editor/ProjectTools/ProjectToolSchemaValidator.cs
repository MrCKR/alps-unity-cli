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
        private static string ValidateProjectToolInput(UnityCliProjectToolDefinition definition, string inputJson)
        {
            var schemaJson = definition.InputSchema;
            if (!string.IsNullOrEmpty(schemaJson) && schemaJson.Trim() != "{}")
            {
                var schemaType = JsonStringProperty(schemaJson, "type");
                if (!string.IsNullOrEmpty(schemaType) && !JsonValueMatchesType(inputJson, schemaType))
                {
                    return "Input does not match inputSchema; expected $ to be " + schemaType + ".";
                }

                var requiredJson = JsonRawProperty(schemaJson, "required");
                foreach (var key in JsonStringArray(requiredJson))
                {
                    if (JsonRawProperty(inputJson, key) == null)
                    {
                        return "Input does not match inputSchema; missing required property: " + key;
                    }
                }

                var propertiesJson = JsonRawProperty(schemaJson, "properties");
                foreach (var key in JsonObjectPropertyNames(propertiesJson))
                {
                    var valueJson = JsonRawProperty(inputJson, key);
                    if (valueJson == null)
                    {
                        continue;
                    }
                    var propertySchema = JsonRawProperty(propertiesJson, key);
                    var propertyType = JsonStringProperty(propertySchema, "type");
                    if (!string.IsNullOrEmpty(propertyType) && !JsonValueMatchesType(valueJson, propertyType))
                    {
                        return "Input does not match inputSchema; property " + key + " must be " + propertyType + ".";
                    }
                }
            }

            foreach (var key in definition.RequiredInput ?? new string[0])
            {
                if (JsonRawProperty(inputJson, key) == null)
                {
                    return "Input does not match inputSchema; missing required property: " + key;
                }
            }
            return null;
        }
    }
}
#endif
