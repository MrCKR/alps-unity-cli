using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;

namespace UnityCli.Tests
{
    public sealed class UnityCliRuntimeCatalogValidationTests
    {
        [Test]
        public void RuntimeDescribeAcceptsOnlyTheExactNamePayload()
        {
            var bridge = Type.GetType("UnityCli.UnityCliRuntimeBridge, UnityCli.Runtime", true);
            var validate = bridge.GetMethod("ValidateRuntimeCatalogInput", BindingFlags.NonPublic | BindingFlags.Static);
            var exact = new Dictionary<string, object> { { "name", "player-runtime.state" } };
            Assert.That(() => validate.Invoke(null, new object[] { "catalog.player.describe", exact }), Throws.Nothing);

            var fallback = new Dictionary<string, object>
            {
                { "name", "player-runtime.state" },
                { "fallbackName", "UnityPipeline_runtime.state" }
            };
            var exception = Assert.Throws<TargetInvocationException>(() =>
                validate.Invoke(null, new object[] { "catalog.player.describe", fallback }));
            Assert.That(exception.InnerException, Is.TypeOf<FormatException>());
            Assert.That(exception.InnerException.Message, Does.Contain("unknown field"));
        }
    }
}
