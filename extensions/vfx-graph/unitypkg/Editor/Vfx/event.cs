#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// vfx.event - 向 VisualEffect 发送事件与可选属性负载。
    /// </summary>
    public class VfxEventTool : IUnityCliTool<VfxEventTool.Request, VfxEventTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired]
            public string Target { get; set; }
            [UnityCliRequired]
            public string EventName { get; set; }
            public string AttributeName { get; set; }
            public double? AttributeX { get; set; }
            public double? AttributeY { get; set; }
            public double? AttributeZ { get; set; }
            public double? AttributeSize { get; set; }
        }

        public sealed class Result
        {
            public GameObjectRef Target { get; set; }
            public string EventName { get; set; }
            public bool HasAttributes { get; set; }
            public List<VfxPlannedEffect> PlannedEffects { get; set; }
            public string LogPath { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "vfx.event",
            Summary = "Send a VisualEffect event and optionally attach reflected event attributes.",
            Description = "Send a VisualEffect event to the target component. When attributeName is provided, the tool creates a reflected VFXEventAttribute payload and fills vector3 and size fields before dispatching the event.",
            Group = "vfx",
            Version = "1.0.0",
            DestructiveHint = false,
            RequiresPackages = new[] { "com.unity.visualeffectgraph" },
            SideEffects = new[] { "runtime-event-dispatch" },
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Send a plain event", Input = new Request { Target = "Effects/Emitter", EventName = "OnBurst" } },
                new ToolExample { Description = "Send an event with position attribute", Input = new Request { Target = "Effects/Emitter", EventName = "Spawn", AttributeName = "position", AttributeX = 0, AttributeY = 1, AttributeZ = 2 } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            VfxToolSupport.EnsureEditorReady("vfx.event");
            var visualEffectType = VfxToolSupport.RequireVisualEffectType();
            var gameObject = GameObjectToolSupport.ResolveUniqueTarget(request.Target, "VFX event requires target.");
            var component = gameObject.GetComponent(visualEffectType);
            if (component == null)
            {
                throw new UnityCliBridge.BridgeCommandException(
                    "E_VFX_COMPONENT_NOT_FOUND",
                    "VisualEffect component not found on target: " + GameObjectToolSupport.HierarchyPath(gameObject),
                    "Pass a target with VisualEffect.");
            }

            var hasAttributes = !string.IsNullOrWhiteSpace(request.AttributeName);
            object eventAttribute = null;
            Type eventAttributeType = null;
            if (hasAttributes)
            {
                var createMethod = visualEffectType.GetMethod("CreateVFXEventAttribute", Type.EmptyTypes);
                if (createMethod != null)
                {
                    eventAttribute = createMethod.Invoke(component, null);
                    eventAttributeType = eventAttribute == null ? null : eventAttribute.GetType();
                }
                if (eventAttribute != null && eventAttributeType != null)
                {
                    if (request.AttributeX.HasValue || request.AttributeY.HasValue || request.AttributeZ.HasValue)
                    {
                        var setVectorMethod = eventAttributeType.GetMethod("SetVector3", new[] { typeof(string), typeof(Vector3) });
                        if (setVectorMethod != null)
                        {
                            var vector = new Vector3((float)(request.AttributeX ?? 0d), (float)(request.AttributeY ?? 0d), (float)(request.AttributeZ ?? 0d));
                            setVectorMethod.Invoke(eventAttribute, new object[] { request.AttributeName, vector });
                        }
                    }
                    if (request.AttributeSize.HasValue)
                    {
                        var setFloatMethod = eventAttributeType.GetMethod("SetFloat", new[] { typeof(string), typeof(float) });
                        if (setFloatMethod != null)
                        {
                            setFloatMethod.Invoke(eventAttribute, new object[] { request.AttributeName, (float)request.AttributeSize.Value });
                        }
                    }
                }
            }

            var sendMethod = eventAttribute == null
                ? visualEffectType.GetMethod("SendEvent", new[] { typeof(string) })
                : visualEffectType.GetMethod("SendEvent", new[] { typeof(string), eventAttributeType });
            if (sendMethod == null)
            {
                sendMethod = visualEffectType.GetMethod("SendEvent", new[] { typeof(string) });
            }
            if (sendMethod != null)
            {
                sendMethod.Invoke(component, eventAttribute == null ? new object[] { request.EventName } : new[] { (object)request.EventName, eventAttribute });
            }

            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Target = GameObjectToolSupport.BuildGameObjectRef(gameObject),
                EventName = request.EventName,
                HasAttributes = hasAttributes,
                PlannedEffects = new List<VfxPlannedEffect>
                {
                    new VfxPlannedEffect { Type = "vfx.event", Target = GameObjectToolSupport.HierarchyPath(gameObject), EventName = request.EventName }
                },
                LogPath = ctx.LogPath
            });
        }
    }
}
#endif
