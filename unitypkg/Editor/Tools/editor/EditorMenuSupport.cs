#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;

namespace UnityCli
{
    /// <summary>一次菜单路径扫描的分页结果与截断证据。</summary>
    internal sealed class EditorMenuScan
    {
        public List<string> Items { get; set; }
        public long Total { get; set; }
        public bool Truncated { get; set; }
        public bool ScanExhausted { get; set; }
        public List<string> Roots { get; set; }
        public string Note { get; set; }
    }

    /// <summary>
    /// editor.menu-* 共享 helper：反射 Unity 内部菜单枚举 API，提供只读路径发现与执行前存在性判断。
    /// 只读取菜单注册元数据；不调用任何菜单 validate 函数，也不执行菜单。
    /// </summary>
    internal static class EditorMenuSupport
    {
        private const BindingFlags AllFlags = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const string ContextRootPrefix = "CONTEXT";
        private const string MenuTypeName = "UnityEditor.Menu";
        private const string MenuItemTypeName = "UnityEditor.ScriptingMenuItem";

        /// <summary>roots 只作诊断，固定上限避免超大项目的包菜单列表撑爆单个响应。</summary>
        internal const int MaxReportedRoots = 64;

        /// <summary>Unity 原生菜单根：原生菜单没有 [MenuItem] attribute，必须显式列举才能展开。</summary>
        private static readonly string[] NativeRoots = { "File", "Edit", "Assets", "GameObject", "Component", "Window", "Help" };

        private static bool resolved;
        private static MethodInfo getMenuItems;
        private static MethodInfo menuItemExists;
        private static MethodInfo getEnabled;
        private static Func<object, string> readPath;
        private static Func<object, bool> readIsSeparator;

        /// <summary>反射句柄按 Unity 版本解析一次；缺失时保持 null，由调用方报告不可用。</summary>
        private static void Resolve()
        {
            if (resolved) return;
            resolved = true;
            var editorAssembly = typeof(EditorApplication).Assembly;
            var menuType = editorAssembly.GetType(MenuTypeName, false);
            var itemType = editorAssembly.GetType(MenuItemTypeName, false);
            if (menuType != null)
            {
                getMenuItems = menuType.GetMethod("GetMenuItems", AllFlags, null, new[] { typeof(string), typeof(bool), typeof(bool) }, null);
                menuItemExists = menuType.GetMethod("MenuItemExists", AllFlags, null, new[] { typeof(string) }, null);
                getEnabled = menuType.GetMethod("GetEnabled", AllFlags, null, new[] { typeof(string) }, null);
            }
            readPath = BuildAccessor<string>(itemType, "path", "m_Path");
            readIsSeparator = BuildAccessor<bool>(itemType, "isSeparator", "m_IsSeparator");
        }

        /// <summary>内部成员可能是 property 也可能是 backing field，两者都支持以跨越 Unity 版本差异。</summary>
        private static Func<object, T> BuildAccessor<T>(Type type, string propertyName, string fieldName)
        {
            var property = type?.GetProperty(propertyName, AllFlags);
            if (property != null) return instance => (T)property.GetValue(instance, null);
            var field = type?.GetField(propertyName, AllFlags) ?? type?.GetField(fieldName, AllFlags);
            if (field != null) return instance => (T)field.GetValue(instance);
            return null;
        }

        internal static bool EnumerationAvailable { get { Resolve(); return getMenuItems != null && readPath != null; } }

        internal static bool ExistenceCheckAvailable { get { Resolve(); return menuItemExists != null; } }

        /// <summary>反射判断路径是否存在；句柄缺失时返回 false 表示无法判断，调用方不得据此断言不存在。</summary>
        internal static bool TryGetItemExists(string path, out bool exists)
        {
            exists = false;
            if (!ExistenceCheckAvailable) return false;
            exists = (bool)menuItemExists.Invoke(null, new object[] { path });
            return true;
        }

        /// <summary>
        /// 反射读取当前 enabled 判定；Unity 会调用该菜单项自己的 validate 函数。
        /// 句柄缺失时返回 false 表示无法判断，调用方不得据此断言已禁用。
        /// </summary>
        internal static bool TryGetItemEnabled(string path, out bool enabled)
        {
            enabled = false;
            Resolve();
            if (getEnabled == null) return false;
            enabled = (bool)getEnabled.Invoke(null, new object[] { path });
            return true;
        }

        /// <summary>
        /// 按 root/filter 扫描真实菜单路径，返回 ordinal 排序后的有界页面与截断证据。
        /// 枚举反射句柄缺失时抛稳定错误；预算耗尽时停止扫描并如实报告 scanExhausted。
        /// </summary>
        internal static EditorMenuScan Scan(string rootFilter, string pathFilter, int count, UnityCliRequestBudget budget)
        {
            if (budget == null) throw new ArgumentNullException("budget");
            if (!EnumerationAvailable)
                throw new UnityCliBridge.BridgeCommandException(
                    "E_MENU_ENUMERATION_UNAVAILABLE",
                    "The running Unity version does not expose the internal Editor menu enumeration API.",
                    "Pass the exact menu path to editor.menu-execute instead, or use a Unity version that exposes UnityEditor.Menu.GetMenuItems.");

            var scanExhausted = false;
            var roots = ResolveRoots(rootFilter, budget, ref scanExhausted);
            var paths = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var root in roots)
            {
                if (!budget.TryConsumeReflectionCall()) { scanExhausted = true; break; }
                var items = InvokeGetMenuItems(root);
                foreach (var item in items)
                {
                    if (!budget.TryConsumeScannedRecord()) { scanExhausted = true; break; }
                    budget.CheckpointOrThrow();
                    if (item == null) continue;
                    if (readIsSeparator != null && readIsSeparator(item)) continue;
                    if (!budget.TryConsumeGetter()) { scanExhausted = true; break; }
                    var path = readPath(item);
                    if (string.IsNullOrEmpty(path)) continue;
                    if (!string.IsNullOrEmpty(pathFilter) && path.IndexOf(pathFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    paths.Add(path);
                }
                if (scanExhausted) break;
            }

            var ordered = paths.ToList();
            var page = ordered.Take(count).ToList();
            var truncated = ordered.Count > page.Count;
            var notes = new List<string>();
            if (scanExhausted) notes.Add("The read budget stopped the scan, so total and this page are partial. Narrow root or filter, or lower count.");
            else if (truncated) notes.Add("More matching paths exist than this page returns. Raise count or narrow filter.");
            if (roots.Count > MaxReportedRoots) notes.Add("roots lists the first " + MaxReportedRoots.ToString() + " of " + roots.Count.ToString() + " expanded roots.");
            return new EditorMenuScan
            {
                Items = page,
                Total = ordered.Count,
                Truncated = truncated,
                ScanExhausted = scanExhausted,
                Roots = roots.Count > MaxReportedRoots ? roots.Take(MaxReportedRoots).ToList() : roots,
                Note = notes.Count == 0 ? null : string.Join(" ", notes)
            };
        }

        /// <summary>顶层根 = 显式 root + Unity 原生根 + 所有 [MenuItem] 首段；CONTEXT 上下文菜单不属于菜单栏，排除。</summary>
        private static List<string> ResolveRoots(string rootFilter, UnityCliRequestBudget budget, ref bool scanExhausted)
        {
            var roots = new SortedSet<string>(StringComparer.Ordinal);
            if (!string.IsNullOrEmpty(rootFilter))
            {
                roots.Add(rootFilter);
                return roots.ToList();
            }

            foreach (var root in NativeRoots) roots.Add(root);
            foreach (var method in TypeCache.GetMethodsWithAttribute<MenuItem>())
            {
                if (!budget.TryConsumeScannedRecord()) { scanExhausted = true; break; }
                budget.CheckpointOrThrow();
                foreach (MenuItem attribute in method.GetCustomAttributes(typeof(MenuItem), false))
                {
                    if (attribute.validate || string.IsNullOrEmpty(attribute.menuItem)) continue;
                    var slash = attribute.menuItem.IndexOf('/');
                    var root = slash > 0 ? attribute.menuItem.Substring(0, slash) : attribute.menuItem;
                    if (root.StartsWith(ContextRootPrefix, StringComparison.Ordinal)) continue;
                    roots.Add(root);
                }
            }
            return roots.ToList();
        }

        /// <summary>展开单个根的完整（递归）菜单项；异常按稳定错误上报，不退回猜测数据。</summary>
        private static Array InvokeGetMenuItems(string root)
        {
            try
            {
                return getMenuItems.Invoke(null, new object[] { root, false, false }) as Array;
            }
            catch (Exception exception)
            {
                throw new UnityCliBridge.BridgeCommandException(
                    "E_MENU_ENUMERATION_FAILED",
                    "Enumerating Editor menu root '" + UnityCliBridge.BoundedText(root, 256) + "' failed: " + UnityCliBridge.BoundedText(exception.Message, 4096),
                    "Retry after the next domain reload, or pass the exact menu path to editor.menu-execute.");
            }
        }
    }
}
#endif

