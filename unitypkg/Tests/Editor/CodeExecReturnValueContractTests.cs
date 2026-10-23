#if UNITY_EDITOR
using NUnit.Framework;

namespace UnityCli.Tests
{
    /// <summary>
    /// debug.code-execute 返回值投影契约：返回值可以被有界裁剪，但裁剪事实必须一直随结果公开，
    /// 调用方必须能够区分完整值与残片（ISSUE-098）。
    /// </summary>
    public sealed class CodeExecReturnValueContractTests
    {
        [Test]
        public void ReturnedStringsInsideTheBudgetAreReportedAsComplete()
        {
            bool truncated;
            var value = UnityCliBridge.NormalizeReturnValue("a-small-returned-string", out truncated);
            Assert.That(value, Is.EqualTo("a-small-returned-string"));
            Assert.That(truncated, Is.False, "a value inside the projection budget is complete");
        }

        [Test]
        public void ReturnedStringsBeyondTheBudgetCarryAnExplicitTruncationFact()
        {
            // 预算值本身属于唯一 owner，测试只验证“被裁剪”这一事实与残片确实是原值前缀。
            var oversized = new string('x', 512 * 1024);
            bool truncated;
            var value = UnityCliBridge.NormalizeReturnValue(oversized, out truncated) as string;
            Assert.That(truncated, Is.True, "a value beyond the projection budget must be reported as cut");
            Assert.That(value, Is.Not.Null);
            Assert.That(value.Length, Is.LessThan(oversized.Length), "the cut value is only a bounded prefix");
            Assert.That(value, Is.EqualTo(oversized.Substring(0, value.Length)));
        }

        [Test]
        public void NullAndScalarReturnsAreNeverReportedAsTruncated()
        {
            bool truncated;
            Assert.That(UnityCliBridge.NormalizeReturnValue(null, out truncated), Is.Null);
            Assert.That(truncated, Is.False);
            Assert.That(UnityCliBridge.NormalizeReturnValue(7, out truncated), Is.EqualTo(7));
            Assert.That(truncated, Is.False);
            Assert.That(UnityCliBridge.NormalizeReturnValue(true, out truncated), Is.EqualTo(true));
            Assert.That(truncated, Is.False);
            Assert.That(UnityCliBridge.NormalizeReturnValue(1.5d, out truncated), Is.EqualTo(1.5d));
            Assert.That(truncated, Is.False);
        }
    }
}
#endif
