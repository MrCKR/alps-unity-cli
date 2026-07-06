#if UNITY_EDITOR
using System;
using System.Collections.Generic;

namespace UnityCli
{
    internal static class UnityCliEnvelope
    {
        public static string Success(string requestId, string dataJson)
        {
            var writer = new UnityCliJsonWriter().BeginObject()
                .Prop("ok", true);
            if (!string.IsNullOrEmpty(requestId)) writer.Prop("requestId", requestId);
            writer.PropRaw("data", string.IsNullOrEmpty(dataJson) ? "{}" : dataJson)
                .EndObject();
            return writer.ToString();
        }

        public static string Success(string dataJson)
        {
            return Success(null, dataJson);
        }

        public static string Error(string requestId, string code, string message, string detailsJson, IEnumerable<string> nextActions)
        {
            var writer = new UnityCliJsonWriter().BeginObject()
                .Prop("ok", false);
            if (!string.IsNullOrEmpty(requestId)) writer.Prop("requestId", requestId);
            writer.PropObject("error", error =>
            {
                error.Prop("code", code)
                    .Prop("message", message)
                    .PropRaw("details", ObjectOrEmpty(detailsJson));
            });
            writer.PropArray("nextActions", array =>
            {
                foreach (var action in nextActions ?? new string[0])
                {
                    if (!string.IsNullOrEmpty(action)) array.Value(action);
                }
            });
            writer.EndObject();
            return writer.ToString();
        }

        public static string Error(string code, string message, string nextAction)
        {
            return Error(null, code, message, "{}", SingleAction(nextAction));
        }

        public static string ErrorWithDetails(string code, string message, string detailsJson, string nextAction)
        {
            return Error(null, code, message, detailsJson, SingleAction(nextAction));
        }

        public static string TransientError(string code, string message, string nextAction, int retryAfterMs)
        {
            var writer = new UnityCliJsonWriter().BeginObject()
                .Prop("ok", false)
                .PropObject("error", error =>
                {
                    error.Prop("code", code)
                        .Prop("message", message)
                        .Prop("transient", true)
                        .Prop("retryAfterMs", retryAfterMs)
                        .PropRaw("details", "{}");
                })
                .PropArray("nextActions", array =>
                {
                    if (!string.IsNullOrEmpty(nextAction)) array.Value(nextAction);
                })
                .EndObject();
            return writer.ToString();
        }

        public static string TargetAmbiguous(string label, string target, int matchCount, string matchesJson, string nextAction)
        {
            var writer = new UnityCliJsonWriter().BeginObject()
                .Prop("ok", false)
                .PropObject("error", error =>
                {
                    error.Prop("code", "E_TARGET_AMBIGUOUS")
                        .Prop("message", label + " target is ambiguous: " + target)
                        .Prop("target", target)
                        .Prop("matchCount", matchCount)
                        .PropRaw("matches", string.IsNullOrEmpty(matchesJson) ? "[]" : matchesJson)
                        .PropRaw("details", "{}");
                })
                .PropArray("nextActions", array =>
                {
                    if (!string.IsNullOrEmpty(nextAction)) array.Value(nextAction);
                })
                .EndObject();
            return writer.ToString();
        }

        public static string ObjectOrEmpty(string rawJson)
        {
            return UnityCliJsonWriter.IsCompleteJsonValue(rawJson) && rawJson.Trim().StartsWith("{", StringComparison.Ordinal) ? rawJson.Trim() : "{}";
        }

        public static string ArrayOrEmpty(string rawJson)
        {
            return UnityCliJsonWriter.IsCompleteJsonValue(rawJson) && rawJson.Trim().StartsWith("[", StringComparison.Ordinal) ? rawJson.Trim() : "[]";
        }

        private static IEnumerable<string> SingleAction(string nextAction)
        {
            if (!string.IsNullOrEmpty(nextAction)) yield return nextAction;
        }
    }
}
#endif
