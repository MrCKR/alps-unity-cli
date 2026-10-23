#if UNITY_EDITOR
using System.Collections.Generic;
using NUnit.Framework;

namespace UnityCli.Tests
{
    /// <summary>锁定 editorSettings 帧率成员的版本分派：frameRate 优先、fps 回退、缺失才失败。</summary>
    public sealed class TimelineFrameRateMemberTests
    {
        private sealed class ModernSettings
        {
            public float fps { get; set; }
            public double frameRate { get; set; }
        }

        private sealed class LegacySettings
        {
            public float fps { get; set; }
        }

        private sealed class ReadOnlyLegacySettings
        {
            public float fps { get { return 0f; } }
        }

        private sealed class UnsupportedSettings
        {
            public string frameRate { get; set; }
        }

        [Test]
        public void ModernPackageUsesDoubleFrameRateMember()
        {
            var member = TimelineToolSupport.RequireFrameRateMember(typeof(ModernSettings), typeof(ModernSettings), "Assets/Timelines/Test.playable");

            Assert.That(member.Name, Is.EqualTo("frameRate"));
            Assert.That(member.IsSinglePrecision, Is.False);
            Assert.That(member.Persist(29.97d), Is.EqualTo(29.97d));

            var settings = new ModernSettings();
            member.Write(settings, 29.97d);
            Assert.That(member.Read(settings), Is.EqualTo(29.97d));
            Assert.That((double)settings.fps, Is.EqualTo(0d));
        }

        [Test]
        public void LegacyPackageFallsBackToSinglePrecisionFpsMember()
        {
            var member = TimelineToolSupport.RequireFrameRateMember(typeof(LegacySettings), typeof(LegacySettings), "Assets/Timelines/Test.playable");

            Assert.That(member.Name, Is.EqualTo("fps"));
            Assert.That(member.IsSinglePrecision, Is.True);

            var settings = new LegacySettings();
            member.Write(settings, 29.97d);
            Assert.That(settings.fps, Is.EqualTo(29.97f));
            Assert.That(member.Read(settings), Is.EqualTo((double)29.97f));
            Assert.That(member.Read(settings), Is.Not.EqualTo(29.97d));
        }

        [Test]
        public void LegacyProofComparesThePersistedSinglePrecisionValue()
        {
            var member = TimelineToolSupport.RequireFrameRateMember(typeof(LegacySettings), typeof(LegacySettings), "Assets/Timelines/Test.playable");
            var settings = new LegacySettings();
            member.Write(settings, 29.97d);

            var observed = member.Read(settings);
            Assert.That(TimelineToolSupport.SameFrameRate(observed, member.Persist(29.97d), true), Is.True);
            Assert.That(TimelineToolSupport.SameFrameRate(observed, 29.97d, true), Is.False);
            Assert.That(TimelineToolSupport.SameFrameRate(29.97d, 29.97d, false), Is.True);
            Assert.That(TimelineToolSupport.SameFrameRate(29.97d, 29.9701d, false), Is.False);
        }

        [Test]
        public void UnsupportedPackageReportsVersionBoundaryBeforeAnyEffect()
        {
            var error = Assert.Throws<UnityCliBridge.BridgeCommandException>(() =>
                TimelineToolSupport.RequireFrameRateMember(typeof(UnsupportedSettings), typeof(UnsupportedSettings), "Assets/Timelines/Test.playable"));

            Assert.That(error.Code, Is.EqualTo("E_TIMELINE_FRAME_RATE_UNSUPPORTED"));
            var details = (IDictionary<string, object>)error.Details;
            Assert.That(details["requiredPackage"], Is.EqualTo("com.unity.timeline"));
            Assert.That(details.ContainsKey("packageVersion"), Is.True);
            Assert.That(details.ContainsKey("foundMembers"), Is.True);
            Assert.That(details["acceptedMembers"], Is.EqualTo(new[] { "frameRate", "fps" }));
            Assert.That(error.NextActions.Count, Is.GreaterThan(0));
        }

        [Test]
        public void ReadOnlyLegacyMemberIsNotSilentlyTreatedAsUnavailableValue()
        {
            var error = Assert.Throws<UnityCliBridge.BridgeCommandException>(() =>
                TimelineToolSupport.RequireFrameRateMember(typeof(ReadOnlyLegacySettings), typeof(ReadOnlyLegacySettings), "Assets/Timelines/Test.playable"));

            Assert.That(error.Code, Is.EqualTo("E_TIMELINE_FRAME_RATE_UNSUPPORTED"));
            var details = (IDictionary<string, object>)error.Details;
            Assert.That(details["foundMembers"], Is.EqualTo(new object[] { "fps (Single)" }));
        }
    }
}
#endif
