namespace GameAnalytics.Client
{
    // Optional client/device context carried by the wire envelope (platform, device class, app
    // version, build, country/region/timezone/locale). AnalyticsClientOptions.Context supplies
    // the per-client defaults; the optional Track argument overrides individual fields per
    // event. The client snapshots both into each queued envelope, so mutating an instance
    // after Track returns never changes what was queued.
    public sealed class EventContext
    {
        public string? Platform { get; set; }

        public string? DeviceClass { get; set; }

        // Optional consistency assertion: when supplied, it must be the exact canonical release
        // identity the client was initialized with. The client stamps that identity into every
        // event-owned context, so omitting it never removes app_version from the wire.
        public string? AppVersion { get; set; }

        public string? BuildId { get; set; }

        public string? Country { get; set; }

        public string? Region { get; set; }

        public string? Timezone { get; set; }

        public string? Locale { get; set; }
    }
}
