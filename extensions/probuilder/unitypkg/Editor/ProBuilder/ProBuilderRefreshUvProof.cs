#if UNITY_EDITOR
using System.Linq;
using UnityEngine;
using UnityEngine.ProBuilder;

namespace UnityCli.ProBuilder
{
    /// <summary>读取 canonical 快照的 UV0，验证其是否已处于 RefreshUV 可产生的终态。</summary>
    internal static class ProBuilderRefreshUvProof
    {
        /// <summary>只读取 snapshot；要求 local-space、无 texture group 的自动 UV 可被确定性复演。</summary>
        internal static bool RefreshUvsAreTerminal(CanonicalMeshSnapshot snapshot)
        {
            try
            {
                if (snapshot == null || snapshot.RawVertices == null || snapshot.Faces == null || snapshot.RawVertices.Any(raw => !raw.Uvs[0].IsPresent || raw.Uvs[1].IsPresent)) return false;
                var positions = snapshot.RawVertices.Select(raw => raw.LocalPosition).ToArray();
                var expected = snapshot.RawVertices.Select(raw => raw.Uvs[0]).ToArray();
                foreach (var face in snapshot.Faces)
                {
                    var settings = new AutoUnwrapSettings(face.Uv);
                    if (face.TextureGroup > 0 || settings.useWorldSpace) return false;
                    if (face.ManualUv) continue;
                    var indexes = face.DirectedRawTriangleStream.Distinct().ToArray(); var stream = face.DirectedRawTriangleStream;
                    if (indexes.Length < 3 || stream.Length < 3) return false;
                    var normal = UnityEngine.ProBuilder.Math.Normal(positions[stream[0]], positions[stream[1]], positions[stream[2]]);
                    if (stream.Length > 6) { var plane = Projection.FindBestPlane(positions, indexes).normal; normal = Vector3.Dot(normal, plane) < 0f ? -plane : plane; }
                    var values = Projection.PlanarProject(positions, indexes, normal);
                    if (values.Length != indexes.Length || values.Any(value => !Finite(value))) return false;
                    ApplySettings(values, settings);
                    if (values.Any(value => !Finite(value))) return false;
                    for (var index = 0; index < indexes.Length; index++) expected[indexes[index]] = new CanonicalMeshSnapshot.ChannelValue(true, values[index]);
                }
                return expected.Where((value, index) => !CanonicalMeshSnapshot.ChannelValueEquals(value, snapshot.RawVertices[index].Uvs[0])).Count() == 0;
            }
            catch { return false; }
        }

        private static void ApplySettings(Vector2[] values, AutoUnwrapSettings settings)
        {
            var min = new Vector2(values.Min(value => value.x), values.Min(value => value.y)); var max = new Vector2(values.Max(value => value.x), values.Max(value => value.y)); var center = (min + max) * .5f; var size = max - min;
            if (settings.fill == AutoUnwrapSettings.Fill.Fit) { var scale = Mathf.Max(size.x, size.y); Scale(values, new Vector2(scale, scale), center); center /= scale; }
            else if (settings.fill == AutoUnwrapSettings.Fill.Stretch) { Scale(values, size, center); center = new Vector2(center.x / size.x, center.y / size.y); }
            if (settings.scale.x != 1f || settings.scale.y != 1f || settings.rotation != 0f) { var scaledCenter = Vector2.Scale(center, settings.scale); var delta = center - scaledCenter; for (var index = 0; index < values.Length; index++) { values[index] -= delta; values[index] = Vector2.Scale(values[index] - scaledCenter, settings.scale) + scaledCenter; var px = values[index].x - scaledCenter.x; var py = values[index].y - scaledCenter.y; var sine = Mathf.Sin(settings.rotation * Mathf.Deg2Rad); var cosine = Mathf.Cos(settings.rotation * Mathf.Deg2Rad); values[index] = new Vector2(px * cosine + py * sine + scaledCenter.x, -px * sine + py * cosine + scaledCenter.y); } }
            if (settings.anchor != AutoUnwrapSettings.Anchor.None) Anchor(values, settings.anchor);
            for (var index = 0; index < values.Length; index++) { var u = settings.flipU ? -values[index].x : values[index].x; var v = settings.flipV ? -values[index].y : values[index].y; values[index] = settings.swapUV ? new Vector2(v, u) : new Vector2(u, v); values[index] -= settings.offset; }
        }
        private static void Scale(Vector2[] values, Vector2 scale, Vector2 center) { var scaledCenter = new Vector2(center.x / scale.x, center.y / scale.y); var delta = center - scaledCenter; for (var index = 0; index < values.Length; index++) { var value = values[index] - delta; values[index] = new Vector2((value.x - scaledCenter.x) / scale.x + scaledCenter.x, (value.y - scaledCenter.y) / scale.y + scaledCenter.y); } }
        private static void Anchor(Vector2[] values, AutoUnwrapSettings.Anchor anchor) { var min = new Vector2(values.Min(value => value.x), values.Min(value => value.y)); var max = new Vector2(values.Max(value => value.x), values.Max(value => value.y)); var offset = Vector2.zero; if (anchor == AutoUnwrapSettings.Anchor.UpperLeft || anchor == AutoUnwrapSettings.Anchor.MiddleLeft || anchor == AutoUnwrapSettings.Anchor.LowerLeft) offset.x = min.x; else if (anchor == AutoUnwrapSettings.Anchor.UpperRight || anchor == AutoUnwrapSettings.Anchor.MiddleRight || anchor == AutoUnwrapSettings.Anchor.LowerRight) offset.x = max.x - 1f; else offset.x = min.x + (max.x - min.x) * .5f - .5f; if (anchor == AutoUnwrapSettings.Anchor.UpperLeft || anchor == AutoUnwrapSettings.Anchor.UpperCenter || anchor == AutoUnwrapSettings.Anchor.UpperRight) offset.y = max.y - 1f; else if (anchor == AutoUnwrapSettings.Anchor.MiddleLeft || anchor == AutoUnwrapSettings.Anchor.MiddleCenter || anchor == AutoUnwrapSettings.Anchor.MiddleRight) offset.y = min.y + (max.y - min.y) * .5f - .5f; else offset.y = min.y; for (var index = 0; index < values.Length; index++) values[index] -= offset; }
        private static bool Finite(Vector2 value) => !float.IsNaN(value.x) && !float.IsInfinity(value.x) && !float.IsNaN(value.y) && !float.IsInfinity(value.y);
    }
}
#endif
