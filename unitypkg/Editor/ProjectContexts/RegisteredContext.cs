#if UNITY_EDITOR
using System;
using System.Threading;
using System.Threading.Tasks;

namespace UnityCli
{
    internal sealed class RegisteredContext
    {
        public string Name { get; set; }
        public string Summary { get; set; }
        public string Description { get; set; }
        public string Category { get; set; }

        public string InputSchema { get; set; }
        public string OutputSchema { get; set; }

        public string Source { get; set; }
        public string Target { get; set; }
        public string AvailabilityScope { get; set; }
        public string PlayModeSupport { get; set; }
        public ContextExample[] Examples { get; set; }

        public string[] RequiresPackages { get; set; }
        public string RegisteredBy { get; set; }
        public string DescriptorSource { get; set; }
        public string Assembly { get; set; }
        public string Method { get; set; }

        public string Fingerprint { get; set; }
        public string PinnedFingerprint { get; set; }
        public string FingerprintStatus { get; set; }
        public string ExpectedFingerprint { get; set; }
        public string PinnedFingerprintOwner { get; set; }
        public string PinnedFingerprintAlgorithm { get; set; }
        public string PinnedFingerprintFormat { get; set; }
        public string FingerprintMessage { get; set; }
        public string DescriptorSha256 { get; set; }
        public string HandlerSha256 { get; set; }

        public ContextDescriptor Descriptor { get; set; }
        public Type RequestType { get; set; }
        public Type ResultType { get; set; }
        public Func<object, UnityCliProjectToolContext, CancellationToken, ValueTask<object>> ReadAsync { get; set; }
        public Func<object, UnityCliProofResourceClaim> ResolveProofResource { get; set; }
    }
}
#endif
