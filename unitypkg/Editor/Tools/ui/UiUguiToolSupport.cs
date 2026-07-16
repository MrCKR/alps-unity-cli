#if UNITY_EDITOR
using UnityEngine;

namespace UnityCli
{
    internal static class UiRectHelper
    {
        public static void ApplyRect(RectTransform rt, float? width, float? height, float? x, float? y)
        {
            if (width.HasValue) rt.sizeDelta = new Vector2(width.Value, rt.sizeDelta.y);
            if (height.HasValue) rt.sizeDelta = new Vector2(rt.sizeDelta.x, height.Value);
            if (x.HasValue || y.HasValue) rt.anchoredPosition = new Vector2(x ?? rt.anchoredPosition.x, y ?? rt.anchoredPosition.y);
        }

        public static void StretchRect(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }
    }
}
#endif
