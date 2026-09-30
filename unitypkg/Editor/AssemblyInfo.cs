#if UNITY_EDITOR
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("UnityCli.Editor.Tests")]
// hook 后端只取得 exact 内部契约（backend 接口、预算与派发入口），不取得可执行 Tool 表。
[assembly: InternalsVisibleTo("UnityCli.Hooks.Editor")]
#endif
