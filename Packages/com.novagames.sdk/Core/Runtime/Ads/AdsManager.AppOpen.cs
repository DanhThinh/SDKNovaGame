#nullable enable
using System;
using NovaGames.Mobile.Infrastructure;

namespace NovaGames.Mobile.Ads
{
    // App open: chặn tạm (Suppress) và tự show khi quay lại game.
    public sealed partial class AdsManager
    {
        public IDisposable Suppress(string reason)
        {
            var suppression = new Suppression(this, reason ?? string.Empty);
            _suppressions.Add(suppression);
            _log.Debug("App open suppressed: " + reason);
            return suppression;
        }

        void Release(Suppression suppression)
        {
            if (!_suppressions.Remove(suppression)) return;
            _log.Debug("App open suppression released: " + suppression.Reason);
            RaiseAvailabilityChanges();
        }

        public void SetAutoShowOnResume(AppOpenPlacement? placement) => _autoAppOpen = placement;

        sealed class Suppression : IDisposable
        {
            AdsManager? _owner;

            public Suppression(AdsManager owner, string reason)
            {
                _owner = owner;
                Reason = reason;
            }

            public string Reason { get; }

            public void Dispose()
            {
                var owner = _owner;
                _owner = null;
                owner?.Release(this);
            }
        }
    }
}
