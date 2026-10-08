#nullable enable
using System;
using NovaGames.Mobile.Rating;
using NUnit.Framework;

namespace NovaGames.Mobile.Tests
{
    public sealed class RatingPolicyTests
    {
        static readonly DateTime Now = new DateTime(2026, 1, 10, 12, 0, 0, DateTimeKind.Utc);
        static readonly RatingOptions Options = new RatingOptions { MinLevel = 3, LaterCooldown = TimeSpan.FromHours(24), MaxPrompts = 3 };

        [Test]
        public void NewPlayer_IsEligibleFromTheMinLevel()
        {
            var state = new RatingState();

            Assert.AreEqual(RatingEligibility.LevelTooLow, RatingPolicy.Check(state, Options, 2, Now));
            Assert.AreEqual(RatingEligibility.Eligible, RatingPolicy.Check(state, Options, 3, Now));
        }

        [Test]
        public void Done_IsNeverAskedAgain()
        {
            var state = new RatingState { Done = true };

            Assert.AreEqual(RatingEligibility.AlreadyRated, RatingPolicy.Check(state, Options, 100, Now));
        }

        [Test]
        public void Later_WaitsForTheCooldown()
        {
            var state = new RatingState { PromptCount = 1, LastPromptUtc = Now.AddHours(-23) };
            Assert.AreEqual(RatingEligibility.Cooldown, RatingPolicy.Check(state, Options, 5, Now));

            state.LastPromptUtc = Now.AddHours(-24);
            Assert.AreEqual(RatingEligibility.Eligible, RatingPolicy.Check(state, Options, 5, Now));
        }

        [Test]
        public void MaxPrompts_StopsAsking_ZeroMeansUnlimited()
        {
            var state = new RatingState { PromptCount = 3, LastPromptUtc = Now.AddDays(-10) };

            Assert.AreEqual(RatingEligibility.MaxPromptsReached, RatingPolicy.Check(state, Options, 5, Now));
            Assert.AreEqual(RatingEligibility.Eligible, RatingPolicy.Check(state, Options with { MaxPrompts = 0 }, 5, Now));
        }

        [Test]
        public void State_RoundTripsAndToleratesBadData()
        {
            var state = new RatingState { Done = true, PromptCount = 2, LastPromptUtc = Now };

            var parsed = RatingState.Parse(state.Serialize());
            Assert.IsTrue(parsed.Done);
            Assert.AreEqual(2, parsed.PromptCount);
            Assert.AreEqual(Now, parsed.LastPromptUtc);

            var empty = RatingState.Parse(new RatingState().Serialize());
            Assert.IsFalse(empty.Done);
            Assert.IsNull(empty.LastPromptUtc);

            Assert.IsFalse(RatingState.Parse("garbage").Done);
            Assert.IsFalse(RatingState.Parse(null).Done);
            Assert.AreEqual(0, RatingState.Parse("0|-5|x").PromptCount);
        }
    }
}
