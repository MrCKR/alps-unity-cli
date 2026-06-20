#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityCli.ProBuilder
{
    /// <summary>
    /// Common error JSON helpers for ProBuilder tool handlers.
    /// All errors use unified envelope with E_EXTENSION_DEPENDENCY_MISSING,
    /// E_PROBUILDER_PACKAGE_NOT_FOUND, E_TARGET_NOT_FOUND, E_SCHEMA_INVALID,
    /// E_PROBUILDER_OPERATION_FAILED and snake_case next_actions.
    /// </summary>
    internal static class ProBuilderErrors
    {
        public static string PackageNotAvailable()
        {
            return new UnityCliJsonWriter().BeginObject()
                .Prop("ok", false)
                .PropObject("error", error =>
                {
                    error.Prop("code", "E_EXTENSION_DEPENDENCY_MISSING")
                        .Prop("message", "ProBuilder package (com.unity.probuilder) is not installed or not available.")
                        .PropObject("details", d =>
                        {
                            d.Prop("packageName", "com.unity.probuilder")
                             .Prop("available", false);
                        });
                })
                .PropArray("nextActions", arr =>
                {
                    arr.Value("install_com_unity_probuilder_via_package_manager");
                    arr.Value("run_unity_cli_extensions_doctor_to_verify_setup");
                })
                .EndObject().ToString();
        }

        public static string TargetNotFound(string target)
        {
            return new UnityCliJsonWriter().BeginObject()
                .Prop("ok", false)
                .PropObject("error", error =>
                {
                    error.Prop("code", "E_TARGET_NOT_FOUND")
                        .Prop("message", "ProBuilder target not found: " + (target ?? "(null)"))
                        .PropObject("details", d =>
                        {
                            d.Prop("target", target ?? "");
                        });
                })
                .PropArray("nextActions", arr =>
                {
                    arr.Value("verify_target_gameobject_exists_in_active_scene");
                    arr.Value("use_scene_hierarchy_path_or_instance_id");
                })
                .EndObject().ToString();
        }

        public static string SchemaInvalid(string message)
        {
            return new UnityCliJsonWriter().BeginObject()
                .Prop("ok", false)
                .PropObject("error", error =>
                {
                    error.Prop("code", "E_SCHEMA_INVALID")
                        .Prop("message", message ?? "Input schema validation failed.")
                        .PropRaw("details", "{}");
                })
                .PropArray("nextActions", arr =>
                {
                    arr.Value("check_tool_input_schema_with_tools_describe");
                    arr.Value("ensure_all_required_properties_are_provided");
                })
                .EndObject().ToString();
        }

        public static string OperationFailed(string operation, string reason)
        {
            return new UnityCliJsonWriter().BeginObject()
                .Prop("ok", false)
                .PropObject("error", error =>
                {
                    error.Prop("code", "E_PROBUILDER_OPERATION_FAILED")
                        .Prop("message", "ProBuilder operation '" + (operation ?? "unknown") + "' failed: " + (reason ?? "unknown error"))
                        .PropObject("details", d =>
                        {
                            d.Prop("operation", operation ?? "unknown")
                             .Prop("reason", reason ?? "unknown");
                        });
                })
                .PropArray("nextActions", arr =>
                {
                    arr.Value("check_unity_console_for_errors");
                    arr.Value("verify_probuilder_mesh_is_valid");
                    arr.Value("try_repair_mesh_before_operation");
                })
                .EndObject().ToString();
        }

        public static string PackageNotFound(string message)
        {
            return new UnityCliJsonWriter().BeginObject()
                .Prop("ok", false)
                .PropObject("error", error =>
                {
                    error.Prop("code", "E_PROBUILDER_PACKAGE_NOT_FOUND")
                        .Prop("message", message ?? "Unity ProBuilder package is not installed.")
                        .PropObject("details", d =>
                        {
                            d.Prop("packageName", "com.unity.probuilder");
                        });
                })
                .PropArray("nextActions", arr =>
                {
                    arr.Value("install_com_unity_probuilder_with_unity_cli_package_install");
                })
                .EndObject().ToString();
        }

        /// <summary>
        /// Wrap a successful result in the standard ProBuilder success envelope.
        /// </summary>
        public static string Success(string toolName, string resultJson)
        {
            return new UnityCliJsonWriter().BeginObject()
                .Prop("ok", true)
                .Prop("tool", toolName)
                .PropRaw("result", string.IsNullOrEmpty(resultJson) ? "{}" : resultJson)
                .EndObject().ToString();
        }
    }
}
#endif
