#nullable enable
using System;
using System.Collections.Generic;
using NovaGames.Mobile.RemoteConfig;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace NovaGames.Mobile.Tests
{
    public enum TestKey
    {
        inter_ad_on_off = 0,
        inter_ad_capping_time = 1,
        level_show_rate = 2,
        banner_unit_id = 3,
        install_epoch = 4,
        not_declared = 99,
    }

    public sealed class TestDefinitions : RemoteConfigDefinitions<TestKey> { }

    public sealed class RemoteConfigDefinitionsTests
    {
        TestDefinitions _defs = null!;

        [SetUp] public void SetUp() => _defs = ScriptableObject.CreateInstance<TestDefinitions>();
        [TearDown] public void TearDown() => UnityEngine.Object.DestroyImmediate(_defs);

        static RemoteConfigSnapshot Remote(params (string Key, string Value)[] pairs)
        {
            var values = new Dictionary<string, string>();
            foreach (var (key, value) in pairs) values[key] = value;
            return new RemoteConfigSnapshot(1, ConfigSource.Remote, DateTime.UtcNow, values);
        }

        [Test]
        public void Entries_BuildTypedKeys_NamedAfterEnum()
        {
            _defs.SetEntries(new[]
            {
                new ConfigEntry<TestKey>(TestKey.inter_ad_on_off, ConfigValueType.Bool, "true"),
                new ConfigEntry<TestKey>(TestKey.inter_ad_capping_time, ConfigValueType.Int, "30"),
                new ConfigEntry<TestKey>(TestKey.level_show_rate, ConfigValueType.Double, "0.5"),
                new ConfigEntry<TestKey>(TestKey.banner_unit_id, ConfigValueType.String, "unit-default"),
                new ConfigEntry<TestKey>(TestKey.install_epoch, ConfigValueType.Long, "1700000000000"),
            });

            CollectionAssert.IsEmpty(_defs.Validate());
            Assert.AreEqual(5, _defs.AllKeys.Count);
            Assert.AreEqual("inter_ad_capping_time", _defs.Int(TestKey.inter_ad_capping_time).Name);
            Assert.IsTrue(_defs.Bool(TestKey.inter_ad_on_off).Default);
            Assert.AreEqual(30, _defs.Int(TestKey.inter_ad_capping_time).Default);
            Assert.AreEqual(0.5, _defs.Double(TestKey.level_show_rate).Default);
            Assert.AreEqual("unit-default", _defs.String(TestKey.banner_unit_id).Default);
            Assert.AreEqual(1700000000000L, _defs.Long(TestKey.install_epoch).Default);
        }

        [Test]
        public void RemoteValueOfWrongType_FallsBackToDefault()
        {
            _defs.SetEntries(new[] { new ConfigEntry<TestKey>(TestKey.inter_ad_capping_time, ConfigValueType.Int, "30") });
            var key = _defs.Int(TestKey.inter_ad_capping_time);

            Assert.AreEqual(45, key.Read(Remote(("inter_ad_capping_time", "45"))));
            Assert.AreEqual(30, key.Read(Remote(("inter_ad_capping_time", "4.5"))));
        }

        [Test]
        public void InvalidDefault_IsReportedAndFallsBack()
        {
            _defs.SetEntries(new[]
            {
                new ConfigEntry<TestKey>(TestKey.inter_ad_on_off, ConfigValueType.Bool, "maybe"),
                new ConfigEntry<TestKey>(TestKey.inter_ad_capping_time, ConfigValueType.Int, "30s"),
            });

            var errors = _defs.Validate();

            Assert.AreEqual(2, errors.Count);
            Assert.IsFalse(_defs.Bool(TestKey.inter_ad_on_off).Default);
            Assert.AreEqual(0, _defs.Int(TestKey.inter_ad_capping_time).Default);
        }

        public enum DuplicatedKey { first_key = 0, second_key, third_key = 1 }
        public sealed class DuplicatedDefinitions : RemoteConfigDefinitions<DuplicatedKey> { }

        [Test]
        public void EnumWithDuplicateValues_IsReported()
        {
            var defs = ScriptableObject.CreateInstance<DuplicatedDefinitions>();
            try
            {
                var errors = defs.Validate();

                Assert.AreEqual(1, errors.Count);
                StringAssert.Contains("share value 1", errors[0]);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(defs);
            }
        }

        [Test]
        public void DuplicateKey_FirstWins()
        {
            _defs.SetEntries(new[]
            {
                new ConfigEntry<TestKey>(TestKey.inter_ad_on_off, ConfigValueType.Bool, "true"),
                new ConfigEntry<TestKey>(TestKey.inter_ad_on_off, ConfigValueType.Bool, "false"),
            });

            Assert.AreEqual(1, _defs.Validate().Count);
            Assert.IsTrue(_defs.Bool(TestKey.inter_ad_on_off).Default);
        }

        [Test]
        public void WrongTypeOrUndeclared_LogsErrorAndReturnsDefault()
        {
            _defs.SetEntries(new[] { new ConfigEntry<TestKey>(TestKey.inter_ad_on_off, ConfigValueType.Bool, "true") });

            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("is Bool, not Int"));
            Assert.AreEqual(0, _defs.Int(TestKey.inter_ad_on_off).Default);

            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("not_declared.*not declared"));
            Assert.IsFalse(_defs.Bool(TestKey.not_declared).Default);

            // Chỉ log một lần cho mỗi key.
            _defs.Bool(TestKey.not_declared);
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void WorksWithRemoteConfigService()
        {
            _defs.SetEntries(new[] { new ConfigEntry<TestKey>(TestKey.inter_ad_on_off, ConfigValueType.Bool, "true") });
            var service = new FakeRemoteConfigService(Remote(("inter_ad_on_off", "false")));

            Assert.IsFalse(service.GetBool(_defs, TestKey.inter_ad_on_off));
        }

        sealed class FakeRemoteConfigService : IRemoteConfigService
        {
            public FakeRemoteConfigService(RemoteConfigSnapshot snapshot) { Current = new SdkProperty<RemoteConfigSnapshot>(snapshot); }

            public ISdkProperty<RemoteConfigSnapshot> Current { get; }

            public System.Threading.Tasks.Task<SdkResult<RemoteConfigSnapshot>> FetchAndActivateAsync(
                TimeSpan timeout, System.Threading.CancellationToken ct) =>
                System.Threading.Tasks.Task.FromResult(SdkResult<RemoteConfigSnapshot>.Ok(Current.Value));
        }
    }
}
