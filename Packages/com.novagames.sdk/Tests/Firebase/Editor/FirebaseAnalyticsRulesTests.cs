#nullable enable
using NUnit.Framework;

namespace NovaGames.Mobile.Firebase.Tests
{
    public sealed class FirebaseAnalyticsRulesTests
    {
        [TestCase("level_start", true)]
        [TestCase("ad_impression", true)]
        [TestCase("A1_b2", true)]
        [TestCase("", false)]
        [TestCase("_level", false)]
        [TestCase("level start", false)]
        [TestCase("ga_event", false)]
        [TestCase("google_event", false)]
        [TestCase("first_open", false)]
        [TestCase("in_app_purchase", false)]
        public void EventNames(string name, bool valid)
        {
            Assert.AreEqual(valid, FirebaseAnalyticsRules.IsValidEventName(name, out _));
        }

        [Test]
        public void EventName_MaxLengthIs40()
        {
            Assert.IsTrue(FirebaseAnalyticsRules.IsValidEventName(new string('a', 40), out _));
            Assert.IsFalse(FirebaseAnalyticsRules.IsValidEventName(new string('a', 41), out _));
        }

        [TestCase("days_played", true)]
        [TestCase("user_id", false)]
        [TestCase("first_open_time", false)]
        [TestCase("firebase_level", false)]
        [TestCase("abcdefghijklmnopqrstuvwxy", false)] // 25 ký tự > 24
        public void UserPropertyNames(string name, bool valid)
        {
            Assert.AreEqual(valid, FirebaseAnalyticsRules.IsValidUserPropertyName(name, out _));
        }
    }
}
