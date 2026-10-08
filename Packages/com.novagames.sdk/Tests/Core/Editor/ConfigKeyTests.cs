#nullable enable
using System;
using System.Collections.Generic;
using NovaGames.Mobile.RemoteConfig;
using NUnit.Framework;

namespace NovaGames.Mobile.Tests
{
    public sealed class ConfigKeyTests
    {
        static RemoteConfigSnapshot Values(params (string Key, string Value)[] pairs)
        {
            var values = new Dictionary<string, string>();
            foreach (var (key, value) in pairs) values[key] = value;
            return new RemoteConfigSnapshot(1, ConfigSource.Remote, DateTime.UtcNow, values);
        }

        [TestCase("true", true)]
        [TestCase("1", true)]
        [TestCase("on", true)]
        [TestCase("FALSE", false)]
        [TestCase("0", false)]
        [TestCase("maybe", true)] // invalid -> default
        [TestCase("", true)]      // invalid -> default
        public void BoolKey_ParsesFirebasePatterns(string raw, bool expected)
        {
            var key = new BoolKey("flag", defaultValue: true);
            Assert.AreEqual(expected, key.Read(Values(("flag", raw))));
        }

        [Test]
        public void IntKey_OutOfRangeOrMalformed_FallsBackToDefault()
        {
            var key = new IntKey("capping", 30, min: 0, max: 300);

            Assert.AreEqual(45, key.Read(Values(("capping", "45"))));
            Assert.AreEqual(30, key.Read(Values(("capping", "301"))));
            Assert.AreEqual(30, key.Read(Values(("capping", "4.5"))));
            Assert.AreEqual(30, key.Read(Values()));
        }

        [Test]
        public void DoubleKey_UsesInvariantCulture()
        {
            var key = new DoubleKey("rate", 0.5, min: 0, max: 1);

            Assert.AreEqual(0.25, key.Read(Values(("rate", "0.25"))));
            Assert.AreEqual(0.5, key.Read(Values(("rate", "0,25"))));
            Assert.AreEqual(0.5, key.Read(Values(("rate", "NaN"))));
        }

        [Test]
        public void NonOverridableKey_IgnoresRemoteValue()
        {
            var key = new BoolKey("safety_floor", true, remoteOverridable: false);
            Assert.IsTrue(key.Read(Values(("safety_floor", "false"))));
        }

        [Test]
        public void JsonKey_ParserThrows_IsInvalid()
        {
            var key = new JsonKey<int>("json", 7, (string raw, out int value) =>
            {
                if (raw == "throw") throw new FormatException();
                return int.TryParse(raw, out value);
            });

            Assert.AreEqual(7, key.Read(Values(("json", "throw"))));
            Assert.IsFalse(key.IsRawValid("throw"));
            Assert.AreEqual(3, key.Read(Values(("json", "3"))));
        }

        [Test]
        public void Constructor_RejectsDefaultOutsideRange()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new IntKey("bad", 10, min: 0, max: 5));
        }
    }
}
