#if UNITY_EDITOR
namespace UnityCli
{
    public sealed class AnimatorParameterWithValue
    {
        public string Name { get; set; }
        public string Type { get; set; }
        [UnityCliAnyJson] public object Value { get; set; }
    }

    public sealed class AnimatorParameterRef
    {
        public string Name { get; set; }
        public string Type { get; set; }
    }

    public sealed class CrossfadeInfo
    {
        public string State { get; set; }
        public double Duration { get; set; }
        public long Layer { get; set; }
    }
}
#endif
