using System;
using System.Collections.Generic;

namespace GameAnalytics.Client.Internal
{
    // Tracked-input rules mirrored from the verified server contract (EventValidator plus the
    // reviewed JavaScript client) and from the approved generic-client identity design. Every
    // rule runs BEFORE the queue, the event ID or the clock is touched, and every rule REJECTS
    // rather than normalizing: string content and case are preserved exactly.
    internal static class EventRules
    {
        public const int EventNameMax = 64;
        public const int ProjectIdMax = 64;
        public const int DataRevisionMax = 64;
        public const int AppVersionMax = 32;
        public const int BuildIdMax = 64;
        public const int CountryMax = 8;
        public const int PropertyCountMax = 32;
        public const int PropertyKeyMax = 64;
        public const int PropertyStringMax = 256;
        const decimal LevelMin = 1;
        const decimal LevelMax = 200;

        // Revenue and identity truth may only come from the backend: a client entry point can
        // never emit one of these names, whatever category it stamps.
        static readonly HashSet<string> BackendAuthoritative = new HashSet<string>(StringComparer.Ordinal)
        {
            "login_succeeded",
            "new_player",
            "purchase_validated",
            "purchase_rejected",
            "reward_granted",
            "reward_failed",
        };

        public static void ValidateProjectId(string? projectId)
        {
            if (projectId == null)
                throw new ArgumentNullException(nameof(projectId));
            if (projectId.Trim().Length == 0)
                throw new ArgumentException("ProjectId: must be a non-empty string.", nameof(projectId));
            if (projectId.Length > ProjectIdMax)
                throw new ArgumentException($"ProjectId: must be at most {ProjectIdMax} characters.", nameof(projectId));
        }

        // The existing event-name grammar: a non-blank string of at most 64 characters. The
        // vocabulary stays open here — the only name-level authority boundary is the backend
        // set below — and the content is preserved exactly as supplied.
        public static void ValidateEventName(string? eventName)
        {
            if (eventName == null)
                throw new ArgumentNullException(nameof(eventName));
            if (eventName.Trim().Length == 0)
                throw new ArgumentException("eventName: is required.", nameof(eventName));
            if (eventName.Length > EventNameMax)
                throw new ArgumentException($"eventName: must be at most {EventNameMax} characters.", nameof(eventName));
        }

        public static void ValidateClientAuthority(string eventName)
        {
            if (BackendAuthoritative.Contains(eventName))
                throw new ArgumentException($"eventName: '{eventName}' is backend-authoritative and cannot be sent by a client.", nameof(eventName));
        }

        public static bool IsPreLoginEvent(string eventName)
        {
            switch (eventName)
            {
                case "app_started":
                case "config_fetch_started":
                case "config_fetch_completed":
                case "config_fetch_failed":
                case "login_presented":
                case "terms_accepted":
                case "login_started":
                case "login_failed":
                case "login_cancelled":
                    return true;
                default:
                    return false;
            }
        }

        // Nullable construction remains, but the current server cannot confirm an absent installation.
        public static void ValidateIdentity(Guid? installationId)
        {
            if (installationId == null)
                throw new ArgumentNullException(nameof(installationId), "installationId: the current server requires a real installation identity.");
        }

        // The context bounds carried by the wire envelope; the other context fields have no
        // server-side limit and are therefore not bounded here. A supplied AppVersion is only an
        // assertion about the release identity the client was initialized with, so it must already
        // be one of the canonical forms the initialization argument accepts.
        public static void ValidateContext(EventContext context)
        {
            if (context.AppVersion != null && IsCanonicalClientVersion(context.AppVersion) == false)
                throw new ArgumentException($"AppVersion: must be three dot-separated numeric components without leading zeros (for example 1.2.3), at most {AppVersionMax} characters.", nameof(context));
            if (context.BuildId != null && context.BuildId.Length > BuildIdMax)
                throw new ArgumentException($"BuildId: must be at most {BuildIdMax} characters.", nameof(context));
            if (context.Country != null && context.Country.Length > CountryMax)
                throw new ArgumentException($"Country: must be at most {CountryMax} characters.", nameof(context));
        }

        // The required SDK release identity: exactly three dot-separated ASCII decimal components,
        // each either "0" or starting 1-9 (no leading zeros, signs, spaces, prefixes, prerelease or
        // build text), within the existing app_version bound. The value is scanned character by
        // character and never trimmed, normalized or numerically converted, so an arbitrarily large
        // component is valid as long as the total length fits.
        public static void ValidateClientVersion(string? clientVersion)
        {
            if (clientVersion == null)
                throw new ArgumentNullException(nameof(clientVersion));
            if (IsCanonicalClientVersion(clientVersion) == false)
                throw new ArgumentException($"clientVersion: must be three dot-separated numeric components without leading zeros (for example 1.2.3), at most {AppVersionMax} characters.", nameof(clientVersion));
        }

        // Properties are optional. When present they must be a flat object of scalar values
        // (string/number/boolean/null) within the documented key, count and value limits; nested
        // objects, arrays and custom mutable values are outside the contract.
        public static void ValidateProperties(IReadOnlyDictionary<string, object?>? properties)
        {
            if (properties == null)
                return;

            if (properties.Count > PropertyCountMax)
                throw new ArgumentException($"properties: at most {PropertyCountMax} properties are allowed.", nameof(properties));

            foreach (var property in properties)
            {
                if (property.Key.Length > PropertyKeyMax)
                    throw new ArgumentException($"properties: property key must be at most {PropertyKeyMax} characters.", nameof(properties));

                if (TryDescribeScalarViolation(property.Value, out var error) == false)
                    throw new ArgumentException(error, nameof(properties));
            }
        }

        // level_completed carries the player-level funnel contract: its level evidence lives in
        // properties.level and an accepted event without conforming evidence would corrupt stage
        // counts. The VALUE is judged, not the spelling (25.0 and 1e1 are integral, matching the
        // server's decimal truncation rule).
        public static void ValidateLevelCompleted(string eventName, IReadOnlyDictionary<string, object?>? properties)
        {
            if (string.Equals(eventName, "level_completed", StringComparison.Ordinal) == false)
                return;

            if (properties == null || properties.TryGetValue("level", out var level) == false)
                throw new ArgumentException("properties: level_completed requires a properties object with an integer 'level' (1..200).", nameof(properties));

            decimal value;
            switch (level)
            {
                case byte number: value = number; break;
                case sbyte number: value = number; break;
                case short number: value = number; break;
                case ushort number: value = number; break;
                case int number: value = number; break;
                case uint number: value = number; break;
                case long number: value = number; break;
                case ulong number: value = number; break;
                case decimal number when decimal.Truncate(number) == number: value = number; break;
                case float number when IsIntegerInLevelRange(number): value = (decimal)number; break;
                case double number when IsIntegerInLevelRange(number): value = (decimal)number; break;
                default: throw new ArgumentException("properties: level must be a JSON number holding an integer from 1 through 200.", nameof(properties));
            }

            if (value < LevelMin || value > LevelMax)
                throw new ArgumentException("properties: level must be a JSON number holding an integer from 1 through 200.", nameof(properties));
        }

        // Natural correlation IDs per event family: the purchase flow, the gameplay run and the
        // content download each share one causal ID with their sibling events.
        public static void ValidateCorrelation(string eventName, Guid? transactionId, Guid? gameplayAttemptId, Guid? downloadId)
        {
            if (eventName.StartsWith("purchase", StringComparison.Ordinal) && transactionId == null)
                throw new ArgumentNullException(nameof(transactionId), "transactionId: required for purchase flow events.");

            if ((string.Equals(eventName, "gameplay_started", StringComparison.Ordinal) || string.Equals(eventName, "gameplay_ended", StringComparison.Ordinal))
                && gameplayAttemptId == null)
                throw new ArgumentNullException(nameof(gameplayAttemptId), "gameplayAttemptId: required for gameplay run events.");

            if (eventName.StartsWith("content_download", StringComparison.Ordinal) && downloadId == null)
                throw new ArgumentNullException(nameof(downloadId), "downloadId: required for content download events.");
        }

        // The client-owned active data revision: non-blank, at most 64 characters, preserved
        // exactly (never trimmed, parsed or compared).
        public static void ValidateDataRevision(string? revision)
        {
            if (revision == null)
                throw new ArgumentNullException(nameof(revision));
            if (revision.Trim().Length == 0)
                throw new ArgumentException("revision: must not be blank.", nameof(revision));
            if (revision.Length > DataRevisionMax)
                throw new ArgumentException($"revision: must be at most {DataRevisionMax} characters.", nameof(revision));
        }

        // Canonical release identity shared by initialization and the per-event context assertion, so
        // both boundaries accept exactly the same forms. Components are delimited by the two dots the
        // grammar allows; the final iteration closes the last component (and exposes a trailing dot as
        // an empty one).
        static bool IsCanonicalClientVersion(string value)
        {
            if (value.Length > AppVersionMax)
                return false;

            var components = 0;
            var componentStart = 0;
            for (var i = 0; i <= value.Length; i++)
            {
                if (i < value.Length && value[i] != '.')
                    continue;

                if (IsCanonicalComponent(value, componentStart, i) == false)
                    return false;

                components++;
                componentStart = i + 1;
            }

            return components == 3;
        }

        // One component: non-empty, either the single character "0" or a 1-9 leading digit followed
        // only by decimal digits. This is where leading zeros, signs, spaces and text are refused.
        static bool IsCanonicalComponent(string value, int start, int end)
        {
            if (end == start)
                return false;
            if (value[start] == '0')
                return end - start == 1;
            if (value[start] < '1' || value[start] > '9')
                return false;

            for (var i = start + 1; i < end; i++)
            {
                if (value[i] < '0' || value[i] > '9')
                    return false;
            }

            return true;
        }

        // True when the value is a flat scalar the wire contract accepts; otherwise error
        // explains the first violation.
        static bool TryDescribeScalarViolation(object? value, out string error)
        {
            error = "";
            switch (value)
            {
                case null:
                case bool _:
                case byte _:
                case sbyte _:
                case short _:
                case ushort _:
                case int _:
                case uint _:
                case long _:
                case ulong _:
                case decimal _:
                    return true;
                case string text:
                {
                    if (text.Length <= PropertyStringMax)
                        return true;
                    error = $"properties: string property value must be at most {PropertyStringMax} characters.";
                    return false;
                }

                case float single:
                {
                    if (float.IsNaN(single) == false && float.IsInfinity(single) == false)
                        return true;
                    error = NonFiniteNumberError;
                    return false;
                }

                case double number:
                {
                    if (double.IsNaN(number) == false && double.IsInfinity(number) == false)
                        return true;
                    error = NonFiniteNumberError;
                    return false;
                }

                default:
                    error = "properties: only flat scalar values (string/number/boolean/null) are allowed.";
                    return false;
            }
        }

        // JSON has no NaN/Infinity literal, so a non-finite CLR number would be silently delivered
        // as null, contradicting what the caller recorded.
        const string NonFiniteNumberError = "properties: numeric property values must be finite JSON numbers (NaN and Infinity are rejected).";

        static bool IsIntegerInLevelRange(double value)
            => double.IsNaN(value) == false
               && double.IsInfinity(value) == false
               && value >= (double)LevelMin
               && value <= (double)LevelMax
               && Math.Truncate(value) == value;
    }
}
