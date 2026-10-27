using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace GameAnalytics.Client.Internal
{
    // The ingestion wire format: a JSON array of snake_case event objects, plus the strict reader
    // for the batch acknowledgement. The bytes are produced once per batch and reused verbatim,
    // so a later retry of the same batch is byte-identical.
    internal static class BatchJson
    {
        // Serializes one chunk of the captured work as the batch body. The bytes are produced ONCE per
        // batch and reused verbatim, so every retry of that batch is byte-identical.
        public static byte[] SerializeBatch(IReadOnlyList<EventEnvelope> envelopes, int offset, int count)
        {
            // The writer must be disposed (flushed) BEFORE the buffer is read, so the nesting is
            // deliberate: `using` declarations would read the buffer before the final flush.
            using (var stream = new MemoryStream())
            {
                using (var writer = new Utf8JsonWriter(stream))
                {
                    writer.WriteStartArray();
                    for (var i = offset; i < offset + count; i++)
                        WriteEnvelope(writer, envelopes[i]);
                    writer.WriteEndArray();
                }

                return stream.ToArray();
            }
        }

        // Reads the successful acknowledgement: the accepted count, the stored event IDs in request
        // order and the refused indexes. Unknown extra members are ignored; a missing, mistyped or
        // unparsable member fails the read, and the caller decides whether the verdict actually
        // partitions the batch. An absent `rejected` member means nothing was refused.
        public static bool TryReadAcknowledgement(string body, out int accepted, out IReadOnlyList<Guid> eventIds, out IReadOnlyList<int> rejectedIndexes)
        {
            accepted = 0;
            eventIds = Array.Empty<Guid>();
            rejectedIndexes = Array.Empty<int>();

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

                if (root.TryGetProperty("accepted", out var acceptedElement) == false
                    || acceptedElement.ValueKind != JsonValueKind.Number
                    || acceptedElement.TryGetInt32(out var acceptedValue) == false)
                    return false;

                if (root.TryGetProperty("eventIds", out var idsElement) == false
                    || idsElement.ValueKind != JsonValueKind.Array)
                    return false;

                var ids = new List<Guid>();
                foreach (var idElement in idsElement.EnumerateArray())
                {
                    if (idElement.ValueKind != JsonValueKind.String || Guid.TryParse(idElement.GetString(), out var id) == false)
                        return false;
                    ids.Add(id);
                }

                var rejected = new List<int>();
                // An absent or explicitly null `rejected` member means nothing was refused, exactly
                // like the JavaScript client's reader.
                if (root.TryGetProperty("rejected", out var rejectedElement)
                    && rejectedElement.ValueKind != JsonValueKind.Null)
                {
                    if (rejectedElement.ValueKind != JsonValueKind.Array)
                        return false;

                    foreach (var entry in rejectedElement.EnumerateArray())
                    {
                        if (entry.ValueKind != JsonValueKind.Object
                            || entry.TryGetProperty("index", out var indexElement) == false
                            || indexElement.ValueKind != JsonValueKind.Number
                            || indexElement.TryGetInt32(out var index) == false)
                            return false;
                        rejected.Add(index);
                    }
                }

                accepted = acceptedValue;
                eventIds = ids;
                rejectedIndexes = rejected;
                return true;
            }
        }

        static void WriteEnvelope(Utf8JsonWriter writer, EventEnvelope envelope)
        {
            writer.WriteStartObject();
            writer.WriteString("event_id", envelope.EventId);
            writer.WriteString("event_name", envelope.EventName);
            writer.WriteString("category", envelope.Category);
            writer.WriteString("event_time", envelope.EventTimeUtc);

            // The source authority stamp belongs to the client: this library only ever speaks for
            // the client, so no category and no caller input can change it.
            writer.WriteString("source", "client");

            writer.WriteString("project_id", envelope.ProjectId);

            // An intentionally absent installation stays explicit on the wire; the other optional
            // identity/correlation IDs are omitted when they do not apply.
            if (envelope.InstallationId.HasValue)
                writer.WriteString("installation_id", envelope.InstallationId.Value);
            else
                writer.WriteNull("installation_id");

            writer.WriteString("session_id", envelope.SessionId);
            WriteOptionalUuid(writer, "user_id", envelope.UserId);
            WriteOptionalUuid(writer, "transaction_id", envelope.TransactionId);
            WriteOptionalUuid(writer, "gameplay_attempt_id", envelope.GameplayAttemptId);
            WriteOptionalUuid(writer, "download_id", envelope.DownloadId);

            WriteOptionalString(writer, "platform", envelope.Context.Platform);
            WriteOptionalString(writer, "device_class", envelope.Context.DeviceClass);
            WriteOptionalString(writer, "app_version", envelope.Context.AppVersion);
            WriteOptionalString(writer, "build_id", envelope.Context.BuildId);
            WriteOptionalString(writer, "country", envelope.Context.Country);
            WriteOptionalString(writer, "region", envelope.Context.Region);
            WriteOptionalString(writer, "timezone", envelope.Context.Timezone);
            WriteOptionalString(writer, "locale", envelope.Context.Locale);
            WriteOptionalString(writer, "data_revision", envelope.DataRevision);

            WriteProperties(writer, envelope.Properties);
            writer.WriteEndObject();
        }

        static void WriteOptionalUuid(Utf8JsonWriter writer, string name, Guid? value)
        {
            if (value.HasValue)
                writer.WriteString(name, value.Value);
        }

        static void WriteOptionalString(Utf8JsonWriter writer, string name, string? value)
        {
            if (value != null)
                writer.WriteString(name, value);
        }

        static void WriteProperties(Utf8JsonWriter writer, IReadOnlyList<KeyValuePair<string, object?>>? properties)
        {
            // Absent properties stay absent; an explicitly supplied empty object stays an empty
            // object, exactly as the caller recorded it.
            if (properties == null)
                return;

            writer.WriteStartObject("properties");
            foreach (var property in properties)
            {
                writer.WritePropertyName(property.Key);
                WriteScalar(writer, property.Value);
            }

            writer.WriteEndObject();
        }

        static void WriteScalar(Utf8JsonWriter writer, object? value)
        {
            switch (value)
            {
                case null: writer.WriteNullValue(); break;
                case string text: writer.WriteStringValue(text); break;
                case bool flag: writer.WriteBooleanValue(flag); break;
                case byte number: writer.WriteNumberValue(number); break;
                case sbyte number: writer.WriteNumberValue(number); break;
                case short number: writer.WriteNumberValue(number); break;
                case ushort number: writer.WriteNumberValue(number); break;
                case int number: writer.WriteNumberValue(number); break;
                case uint number: writer.WriteNumberValue(number); break;
                case long number: writer.WriteNumberValue(number); break;
                case ulong number: writer.WriteNumberValue(number); break;
                case float number: writer.WriteNumberValue(number); break;
                case double number: writer.WriteNumberValue(number); break;
                case decimal number: writer.WriteNumberValue(number); break;
                default:
                    // Track refuses every other CLR type before queueing, so an unknown type here
                    // would mean the flat-scalar invariant was broken upstream.
                    throw new InvalidOperationException("properties: a queued property value has a CLR type outside the flat-scalar contract.");
            }
        }
    }
}
