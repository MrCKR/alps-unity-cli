using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;

namespace UnityCli.Tests
{
    public sealed class UnityCliExceptionProjectionTests
    {
        [Test]
        public void NestedReflectionAndNUnitFailureUsesDeepestActionableCause()
        {
            var assertion = new AssertionException("Expected health check to pass, but it failed.");
            var wrapper = new InvalidOperationException("NUnit invocation failed.", assertion);
            var reflection = new TargetInvocationException("Reflected method failed.", wrapper);

            var result = UnityCliExceptionProjection.Project(reflection);

            Assert.That(result.PublicMessage, Is.EqualTo(assertion.Message));
            Assert.That(result.DeepestExceptionType, Is.EqualTo(typeof(AssertionException).FullName));
            Assert.That(result.Causes, Has.Count.EqualTo(3));
            Assert.That(result.Causes[0]["exceptionType"], Is.EqualTo(typeof(TargetInvocationException).FullName));
            Assert.That(result.Causes[2]["message"], Is.EqualTo(assertion.Message));
            Assert.That(result.Truncated, Is.False);
        }

        [Test]
        public void CauseChainStopsAtCyclesAndStaysDepthAndByteBounded()
        {
            Exception deep = new Exception(new string('z', 100000));
            for (var index = 0; index < UnityCliExceptionProjection.MaxCauseDepth + 4; index++)
                deep = new InvalidOperationException(new string((char)('a' + index), 100000), deep);
            var deepResult = UnityCliExceptionProjection.Project(deep);
            Assert.That(deepResult.Causes, Has.Count.EqualTo(UnityCliExceptionProjection.MaxCauseDepth));
            Assert.That(deepResult.CauseTextBytes, Is.LessThanOrEqualTo(UnityCliExceptionProjection.MaxCauseTextBytes));
            Assert.That(deepResult.Truncated, Is.True);

            var first = new InvalidOperationException("first");
            var second = new Exception("second");
            var next = new Dictionary<Exception, Exception>
            {
                { first, second },
                { second, first }
            };
            var cycleResult = UnityCliExceptionProjection.Project(first, value => next[value]);
            Assert.That(cycleResult.Causes, Has.Count.EqualTo(2));
            Assert.That(cycleResult.Truncated, Is.True);
            Assert.That(cycleResult.PublicMessage, Is.EqualTo("second"));
        }
    }
}
