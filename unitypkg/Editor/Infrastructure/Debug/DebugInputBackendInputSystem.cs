#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

namespace UnityCli
{
    /// <summary>
    /// 可选的 Input System 设备后端：以反射探测并按现场 API 形状建立会话拥有的虚拟设备。
    ///
    /// 规则：
    /// - 只探测类型与成员形状；探测本身不建立设备、不改写状态、不安装任何包。
    /// - 需要的成员不完整时整体报告 unavailable 并列出缺失成员，不提供半套设备能力。
    /// - 只操作由本后端建立的设备对象，从不列出、reset 或改写真实设备与其他 owner 的设备。
    /// - 每次 API 调用都必须能拒绝：异常归约为稳定原因文本，绝不吞掉成成功。
    /// </summary>
    internal sealed class DebugInputBackendInputSystem : IUnityCliDebugInputDeviceBackend
    {
        private const string ApiTypeName = "UnityEngine.InputSystem.InputSystem";
        private const string KeyboardLayout = "Keyboard";
        private const string PointerLayout = "Mouse";

        private readonly Type apiType;
        private readonly Type deviceType;
        private readonly MethodInfo addDevice;
        private readonly MethodInfo removeDevice;
        private readonly MethodInfo queueStateEvent;
        private readonly PropertyInfo devices;
        private readonly Type keyEnum;
        private readonly Type keyboardState;
        private readonly ConstructorInfo keyboardStateCtor;
        private readonly Type keyboardType;
        private readonly PropertyInfo pressedKeys;
        private readonly PropertyInfo keyOfControl;
        private readonly Type mouseState;
        private readonly Type mouseButtonEnum;
        private readonly Type mouseType;
        private readonly FieldInfo mousePosition;
        private readonly FieldInfo mouseButtons;
        private readonly PropertyInfo leftButton;
        private readonly PropertyInfo rightButton;
        private readonly PropertyInfo middleButton;
        private readonly Type vector2;
        private readonly PropertyInfo pressedProperty;
        private readonly List<string> missing = new List<string>();

        private DebugInputBackendInputSystem(Type apiType, Type deviceType, MethodInfo addDevice, MethodInfo removeDevice, MethodInfo queueStateEvent, PropertyInfo devices,
            Type keyEnum, Type keyboardState, ConstructorInfo keyboardStateCtor, Type keyboardType, PropertyInfo pressedKeys, PropertyInfo keyOfControl,
            Type mouseState, Type mouseButtonEnum, Type mouseType, FieldInfo mousePosition, FieldInfo mouseButtons,
            PropertyInfo leftButton, PropertyInfo rightButton, PropertyInfo middleButton, Type vector2, PropertyInfo pressedProperty, List<string> missing)
        {
            this.apiType = apiType;
            this.deviceType = deviceType;
            this.addDevice = addDevice;
            this.removeDevice = removeDevice;
            this.queueStateEvent = queueStateEvent;
            this.devices = devices;
            this.keyEnum = keyEnum;
            this.keyboardState = keyboardState;
            this.keyboardStateCtor = keyboardStateCtor;
            this.keyboardType = keyboardType;
            this.pressedKeys = pressedKeys;
            this.keyOfControl = keyOfControl;
            this.mouseState = mouseState;
            this.mouseButtonEnum = mouseButtonEnum;
            this.mouseType = mouseType;
            this.mousePosition = mousePosition;
            this.mouseButtons = mouseButtons;
            this.leftButton = leftButton;
            this.rightButton = rightButton;
            this.middleButton = middleButton;
            this.vector2 = vector2;
            this.pressedProperty = pressedProperty;
            this.missing = missing;
        }

        public string Name { get { return UnityCliDebugInputBackends.InputSystem; } }

        public bool Available { get { return missing.Count == 0; } }

        public string UnavailableReason
        {
            get { return Available ? string.Empty : UnityCliDebugInputReasons.ApiMemberMissing; }
        }

        public string UnavailableDetail
        {
            get { return Available ? string.Empty : "the loaded Input System API is missing: " + string.Join(", ", missing.ToArray()); }
        }

        public string ApiIdentity
        {
            get { return apiType == null ? string.Empty : UnityCliBridge.BoundedText(apiType.Assembly.FullName, DebugLimits.MaxInputNameBytes); }
        }

        public IList<string> MissingApiMembers { get { return missing; } }

        /// <summary>
        /// 现场探测一次；缺少可选输入包时返回 backend-not-loaded，类型存在但形状不符时返回 api-member-missing 与缺失成员。
        /// </summary>
        internal static IUnityCliDebugInputDeviceBackend Probe()
        {
            var missingMembers = new List<string>();
            Type api = FindType(ApiTypeName);
            if (api == null)
            {
                return DebugInputDeviceBackends.Unavailable(
                    UnityCliDebugInputReasons.BackendNotLoaded,
                    "The optional Input System API type " + ApiTypeName + " is not loaded, and this owner never installs it.",
                    new List<string> { ApiTypeName });
            }

            var add = StaticMethod(api, "AddDevice", new[] { typeof(string), typeof(string), typeof(string) }, missingMembers, "InputSystem.AddDevice(string, string, string)");
            var device = add == null ? null : add.ReturnType;
            if (device == null || device == typeof(void))
            {
                missingMembers.Add("InputDevice");
                device = FindType("UnityEngine.InputSystem.InputDevice");
            }
            var remove = device == null ? null : StaticMethod(api, "RemoveDevice", new[] { device }, missingMembers, "InputSystem.RemoveDevice(InputDevice)");
            var deviceList = StaticProperty(api, "devices", missingMembers, "InputSystem.devices");
            var queue = StaticMethodByName(api, "QueueStateEvent", 3, missingMembers, "InputSystem.QueueStateEvent<TState>(InputDevice, TState, double)");

            var keyEnumType = FindType("UnityEngine.InputSystem.Key");
            var keyboardStateType = FindType("UnityEngine.InputSystem.LowLevel.KeyboardState");
            ConstructorInfo keyboardCtor = null;
            if (keyboardStateType != null && keyEnumType != null)
            {
                keyboardCtor = keyboardStateType.GetConstructor(new[] { keyEnumType.MakeArrayType() });
                if (keyboardCtor == null) missingMembers.Add("KeyboardState.KeyboardState(params Key[])");
            }
            else
            {
                if (keyEnumType == null) missingMembers.Add("UnityEngine.InputSystem.Key");
                if (keyboardStateType == null) missingMembers.Add("UnityEngine.InputSystem.LowLevel.KeyboardState");
            }
            var keyboardDeviceType = FindType("UnityEngine.InputSystem.Keyboard");
            PropertyInfo keyboardPressedKeys = null;
            PropertyInfo controlKey = null;
            if (keyboardDeviceType != null)
            {
                keyboardPressedKeys = keyboardDeviceType.GetProperty("pressedKeys", BindingFlags.Public | BindingFlags.Instance);
                if (keyboardPressedKeys == null) missingMembers.Add("Keyboard.pressedKeys");
            }
            else
            {
                missingMembers.Add("UnityEngine.InputSystem.Keyboard");
            }
            var keyControlType = FindType("UnityEngine.InputSystem.Controls.KeyControl");
            if (keyControlType != null)
            {
                controlKey = keyControlType.GetProperty("key", BindingFlags.Public | BindingFlags.Instance);
                if (controlKey == null) missingMembers.Add("KeyControl.key");
            }
            else
            {
                missingMembers.Add("UnityEngine.InputSystem.Controls.KeyControl");
            }

            var mouseStateType = FindType("UnityEngine.InputSystem.LowLevel.MouseState");
            FieldInfo positionField = null;
            FieldInfo buttonsField = null;
            if (mouseStateType != null)
            {
                positionField = mouseStateType.GetField("position", BindingFlags.Public | BindingFlags.Instance);
                buttonsField = mouseStateType.GetField("buttons", BindingFlags.Public | BindingFlags.Instance);
                if (positionField == null) missingMembers.Add("MouseState.position");
                if (buttonsField == null) missingMembers.Add("MouseState.buttons");
            }
            else
            {
                missingMembers.Add("UnityEngine.InputSystem.LowLevel.MouseState");
            }
            var mouseButtonType = FindType("UnityEngine.InputSystem.MouseButton");
            if (mouseButtonType == null) missingMembers.Add("UnityEngine.InputSystem.MouseButton");
            var mouseDeviceType = FindType("UnityEngine.InputSystem.Mouse");
            PropertyInfo left = null;
            PropertyInfo right = null;
            PropertyInfo middle = null;
            if (mouseDeviceType != null)
            {
                left = mouseDeviceType.GetProperty("leftButton", BindingFlags.Public | BindingFlags.Instance);
                right = mouseDeviceType.GetProperty("rightButton", BindingFlags.Public | BindingFlags.Instance);
                middle = mouseDeviceType.GetProperty("middleButton", BindingFlags.Public | BindingFlags.Instance);
                if (left == null) missingMembers.Add("Mouse.leftButton");
                if (right == null) missingMembers.Add("Mouse.rightButton");
                if (middle == null) missingMembers.Add("Mouse.middleButton");
            }
            else
            {
                missingMembers.Add("UnityEngine.InputSystem.Mouse");
            }
            var vector2Type = FindType("UnityEngine.Vector2");
            if (vector2Type == null) missingMembers.Add("UnityEngine.Vector2");
            var buttonControlType = FindType("UnityEngine.InputSystem.Controls.ButtonControl");
            PropertyInfo pressed = null;
            if (buttonControlType != null)
            {
                pressed = buttonControlType.GetProperty("isPressed", BindingFlags.Public | BindingFlags.Instance);
                if (pressed == null) missingMembers.Add("ButtonControl.isPressed");
            }
            else
            {
                missingMembers.Add("UnityEngine.InputSystem.Controls.ButtonControl");
            }

            if (missingMembers.Count > DebugLimits.MaxInputMissingApiMembers)
                missingMembers.RemoveRange(DebugLimits.MaxInputMissingApiMembers, missingMembers.Count - DebugLimits.MaxInputMissingApiMembers);

            return new DebugInputBackendInputSystem(api, device, add, remove, queue, deviceList,
                keyEnumType, keyboardStateType, keyboardCtor, keyboardDeviceType, keyboardPressedKeys, controlKey,
                mouseStateType, mouseButtonType, mouseDeviceType, positionField, buttonsField, left, right, middle,
                vector2Type, pressed, missingMembers);
        }

        public bool TryCreateDevice(string layout, string deviceName, out object device, out string reason)
        {
            device = null;
            reason = string.Empty;
            if (!Available) { reason = UnavailableReason; return false; }
            if (string.IsNullOrEmpty(layout) || string.IsNullOrEmpty(deviceName)) { reason = "layout-required"; return false; }
            try
            {
                device = addDevice.Invoke(null, new object[] { layout, deviceName, null });
                if (device == null || !deviceType.IsInstanceOfType(device)) { device = null; reason = "device-not-created"; return false; }
                return true;
            }
            catch (Exception exception) { device = null; reason = Describe(exception); return false; }
        }

        public bool TryRemoveDevice(object device, out string reason)
        {
            reason = string.Empty;
            if (!Available) { reason = UnavailableReason; return false; }
            if (device == null || !deviceType.IsInstanceOfType(device)) { reason = "device-required"; return false; }
            try
            {
                removeDevice.Invoke(null, new[] { device });
                return true;
            }
            catch (Exception exception) { reason = Describe(exception); return false; }
        }

        public bool TryQueueKeys(object device, IList<string> keys, out string reason)
        {
            reason = string.Empty;
            if (!Available) { reason = UnavailableReason; return false; }
            if (device == null || keyboardType == null || !keyboardType.IsInstanceOfType(device)) { reason = "keyboard-device-required"; return false; }
            try
            {
                var values = Array.CreateInstance(keyEnum, keys == null ? 0 : keys.Count);
                for (var index = 0; index < (keys == null ? 0 : keys.Count); index++)
                    values.SetValue(Enum.Parse(keyEnum, keys[index], true), index);
                var state = keyboardStateCtor.Invoke(new object[] { values });
                return Queue(device, state, keyboardState, out reason);
            }
            catch (Exception exception) { reason = Describe(exception); return false; }
        }

        public bool TryQueuePointer(object device, IList<string> buttons, double x, double y, out string reason)
        {
            reason = string.Empty;
            if (!Available) { reason = UnavailableReason; return false; }
            if (device == null || mouseType == null || !mouseType.IsInstanceOfType(device)) { reason = "pointer-device-required"; return false; }
            try
            {
                var state = Activator.CreateInstance(mouseState);
                mousePosition.SetValue(state, Activator.CreateInstance(vector2, new object[] { (float)x, (float)y }));
                mouseButtons.SetValue(state, ButtonMask(buttons));
                return Queue(device, state, mouseState, out reason);
            }
            catch (Exception exception) { reason = Describe(exception); return false; }
        }

        public bool TryReadHeldControls(object device, out IList<string> held, out string reason)
        {
            held = new List<string>();
            reason = string.Empty;
            if (!Available) { reason = UnavailableReason; return false; }
            try
            {
                if (keyboardType != null && keyboardType.IsInstanceOfType(device))
                {
                    var pressed = pressedKeys.GetValue(device) as IEnumerable;
                    if (pressed == null) { reason = "pressed-keys-unreadable"; return false; }
                    foreach (var control in pressed)
                    {
                        if (control == null) continue;
                        var value = keyOfControl.GetValue(control);
                        if (value != null) held.Add(value.ToString());
                    }
                    return true;
                }
                if (mouseType != null && mouseType.IsInstanceOfType(device))
                {
                    ReadButton(leftButton, device, UnityCliDebugInputButtons.Left, held);
                    ReadButton(rightButton, device, UnityCliDebugInputButtons.Right, held);
                    ReadButton(middleButton, device, UnityCliDebugInputButtons.Middle, held);
                    return true;
                }
                reason = "device-kind-unsupported";
                return false;
            }
            catch (Exception exception) { reason = Describe(exception); return false; }
        }

        public bool TryReadDeviceNames(out IList<string> names, out string reason)
        {
            names = new List<string>();
            reason = string.Empty;
            if (!Available) { reason = UnavailableReason; return false; }
            try
            {
                var list = devices.GetValue(null, null) as IEnumerable;
                if (list == null) { reason = "device-list-unreadable"; return false; }
                foreach (var entry in list)
                {
                    if (entry == null) continue;
                    var name = entry.GetType().GetProperty("name", BindingFlags.Public | BindingFlags.Instance);
                    var text = name == null ? null : name.GetValue(entry) as string;
                    if (!string.IsNullOrEmpty(text)) names.Add(text);
                }
                return true;
            }
            catch (Exception exception) { reason = Describe(exception); return false; }
        }

        public bool IsKnownControl(string name)
        {
            if (string.IsNullOrEmpty(name) || !Available) return false;
            if (keyEnum != null)
            {
                try { Enum.Parse(keyEnum, name, true); return true; }
                catch { return false; }
            }
            return false;
        }

        /// <summary>设备布局名；只有本后端支持的两种布局可被建立。</summary>
        internal static string LayoutOf(string deviceKind)
        {
            return string.Equals(deviceKind, UnityCliDebugInputDeviceKinds.Keyboard, StringComparison.Ordinal) ? KeyboardLayout : PointerLayout;
        }

        private bool Queue(object device, object state, Type stateType, out string reason)
        {
            reason = string.Empty;
            try
            {
                var generic = queueStateEvent.MakeGenericMethod(stateType);
                generic.Invoke(null, new object[] { device, state, -1d });
                return true;
            }
            catch (Exception exception) { reason = Describe(exception); return false; }
        }

        private ushort ButtonMask(IList<string> buttons)
        {
            var mask = 0;
            if (buttons == null) return 0;
            foreach (var button in buttons)
            {
                if (string.IsNullOrEmpty(button)) continue;
                mask |= Convert.ToInt32(Enum.Parse(mouseButtonEnum, button, true), CultureInfo.InvariantCulture);
            }
            return (ushort)mask;
        }

        private void ReadButton(PropertyInfo property, object device, string name, IList<string> held)
        {
            var control = property == null ? null : property.GetValue(device);
            if (control == null) return;
            if (Convert.ToBoolean(pressedProperty.GetValue(control), CultureInfo.InvariantCulture)) held.Add(name);
        }

        private static string Describe(Exception exception)
        {
            var inner = exception is TargetInvocationException && exception.InnerException != null ? exception.InnerException : exception;
            return UnityCliBridge.BoundedText(inner.GetType().Name + ": " + inner.Message, DebugLimits.MaxDiagnosticTextBytes);
        }

        private static Type FindType(string fullName)
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            foreach (var assembly in assemblies)
            {
                try
                {
                    var type = assembly.GetType(fullName, false);
                    if (type != null) return type;
                }
                catch
                {
                    // 某个程序集不可读不影响探测其他程序集。
                }
            }
            return null;
        }

        private static MethodInfo StaticMethod(Type owner, string name, Type[] parameters, List<string> missingMembers, string label)
        {
            var method = owner == null ? null : owner.GetMethod(name, BindingFlags.Public | BindingFlags.Static, null, parameters, null);
            if (method == null) missingMembers.Add(label);
            return method;
        }

        private static MethodInfo StaticMethodByName(Type owner, string name, int parameterCount, List<string> missingMembers, string label)
        {
            if (owner == null) { missingMembers.Add(label); return null; }
            foreach (var candidate in owner.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (!string.Equals(candidate.Name, name, StringComparison.Ordinal)) continue;
                if (!candidate.IsGenericMethodDefinition) continue;
                if (candidate.GetParameters().Length != parameterCount) continue;
                return candidate;
            }
            missingMembers.Add(label);
            return null;
        }

        private static PropertyInfo StaticProperty(Type owner, string name, List<string> missingMembers, string label)
        {
            var property = owner == null ? null : owner.GetProperty(name, BindingFlags.Public | BindingFlags.Static);
            if (property == null) missingMembers.Add(label);
            return property;
        }
    }
}
#endif
