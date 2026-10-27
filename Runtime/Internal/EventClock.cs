using System;
using System.Diagnostics;
using System.Globalization;

namespace GameAnalytics.Client.Internal
{
    // The client-owned event clock: ONE server-UTC/monotonic midpoint anchor established by a
    // successful time-sync round trip, plus the newest accepted monotonic reading. It never
    // consults the host wall clock, keeps no offset registry and resynchronizes nowhere — the
    // anchor is a single immutable mapping for the whole client lifetime.
    internal sealed class EventClock
    {
        readonly Func<double> _monotonic;
        bool _anchored;
        double _serverMidpointEpochMs;
        double _monotonicMidpointMs;
        double _lastMonotonicMs;

        public EventClock(Func<double> monotonic)
        {
            _monotonic = monotonic;
        }

        // The default runtime source: Stopwatch is monotonic by contract and reads elapsed time
        // since an unspecified origin, so only differences between readings are meaningful. The
        // host wall clock is never involved.
        public static double DefaultMonotonicMilliseconds()
            => Stopwatch.GetTimestamp() * (1000.0 / Stopwatch.Frequency);

        // One monotonic sample. A throwing or non-finite source is a deliberate, caller-visible
        // failure rather than a raw propagated error or a silently invented reading.
        public double ReadMonotonicMilliseconds()
        {
            double reading;
            try
            {
                reading = _monotonic();
            }
            catch (Exception error)
            {
                throw new AnalyticsClientException("the monotonic clock source is unavailable.", null, error);
            }

            if (double.IsNaN(reading) || double.IsInfinity(reading))
                throw new AnalyticsClientException("the monotonic clock produced a non-finite reading.");

            return reading;
        }

        // The exact anchor: the midpoint of the server receive/send values maps onto the midpoint
        // of the sender's two monotonic measurements, and the receive sample becomes the accepted
        // watermark.
        public void Anchor(double sendMonotonicMs, double receiveMonotonicMs, double serverReceivedEpochMs, double serverSentEpochMs)
        {
            _serverMidpointEpochMs = serverReceivedEpochMs + ((serverSentEpochMs - serverReceivedEpochMs) / 2.0);
            _monotonicMidpointMs = sendMonotonicMs + ((receiveMonotonicMs - sendMonotonicMs) / 2.0);
            _lastMonotonicMs = receiveMonotonicMs;
            _anchored = true;
        }

        // The current event time: the server-UTC anchor plus the monotonic elapsed time since the
        // sender midpoint, as an explicit-UTC millisecond ISO timestamp.
        public string CurrentEventTimeUtc()
        {
            if (_anchored == false)
                throw new AnalyticsClientException("no event time exists before time synchronization succeeded.");

            var readMs = ReadMonotonicMilliseconds();

            // A decreasing reading would move event time backward: fail deliberately and leave the
            // accepted watermark untouched rather than emitting a rewound timestamp.
            if (readMs < _lastMonotonicMs)
                throw new AnalyticsClientException("the monotonic reading decreased; refusing to move event time backward.");

            _lastMonotonicMs = readMs;
            return FormatUtcMilliseconds(_serverMidpointEpochMs + (readMs - _monotonicMidpointMs));
        }

        // The wire form is culture-independent: an explicit 'Z' designator and exactly three
        // fractional digits, so every emitted value is unambiguous.
        static string FormatUtcMilliseconds(double epochMs)
        {
            var utc = DateTimeOffset.FromUnixTimeMilliseconds((long)epochMs).UtcDateTime;
            return utc.ToString("yyyy-MM-ddTHH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        }
    }
}
