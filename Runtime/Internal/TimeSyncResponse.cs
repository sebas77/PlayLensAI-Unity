using System;
using System.Globalization;
using System.Text.Json;

namespace GameAnalytics.Client.Internal
{
    // Strict reader for the POST /v1/time-sync success payload: a JSON object carrying exactly
    // the two camelCase UTC timestamps the endpoint produces.
    internal static class TimeSyncResponse
    {
        // DateTime.TryParseExact is the only oracle. The literal 'Z' format makes it reject a
        // numeric offset and a bare local time, and it also rejects a calendar-invalid date such
        // as February 30 instead of silently normalizing it, so no separate grammar layer and no
        // lenient parse is involved. Both documented forms are covered: no fractional seconds, and
        // the up-to-seven fractional digits DateTime.ToString("O") emits.
        static readonly string[] ExplicitUtcFormats =
        {
            "yyyy-MM-dd'T'HH:mm:ss'Z'",
            "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
        };

        public static bool TryRead(string body, out double serverReceivedEpochMs, out double serverSentEpochMs)
        {
            serverReceivedEpochMs = 0;
            serverSentEpochMs = 0;

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(body);
            }
            catch (JsonException)
            {
                return false;
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    return false;

                // Exactly the two server fields: a missing, extra or duplicated member is a
                // contract violation, not something to be tolerated.
                var members = 0;
                foreach (var member in root.EnumerateObject())
                {
                    if (member.NameEquals("serverReceivedAt") == false && member.NameEquals("serverSentAt") == false)
                        return false;
                    members++;
                }

                if (members != 2)
                    return false;

                return TryReadTimestamp(root, "serverReceivedAt", out serverReceivedEpochMs)
                    && TryReadTimestamp(root, "serverSentAt", out serverSentEpochMs);
            }
        }

        static bool TryReadTimestamp(JsonElement root, string name, out double epochMs)
        {
            epochMs = 0;
            if (root.TryGetProperty(name, out var element) == false || element.ValueKind != JsonValueKind.String)
                return false;

            var text = element.GetString();
            if (text == null)
                return false;

            if (DateTimeOffset.TryParseExact(text, ExplicitUtcFormats, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var instant) == false)
                return false;

            epochMs = instant.ToUnixTimeMilliseconds();
            return true;
        }
    }
}
