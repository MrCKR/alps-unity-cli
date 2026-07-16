#if UNITY_EDITOR
using System.Globalization;
using System.IO;

namespace UnityCli
{
    public sealed class MemorySnapshotEntry
    {
        public string Path { get; set; }
        public long SizeBytes { get; set; }
        public string Created { get; set; }

        public static MemorySnapshotEntry FromFileInfo(FileInfo fi)
        {
            return new MemorySnapshotEntry
            {
                Path = fi.FullName,
                SizeBytes = fi.Length,
                Created = fi.CreationTimeUtc.ToString("O", CultureInfo.InvariantCulture)
            };
        }
    }

    public sealed class SnapshotDelta
    {
        public long SizeDeltaBytes { get; set; }
        public double TimeDeltaSeconds { get; set; }
    }
}
#endif
