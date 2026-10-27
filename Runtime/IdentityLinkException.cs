namespace GameAnalytics.Client
{
    // Safe, actionable identity failures: never include a response body, credential or player ID.
    public sealed class IdentityLinkException : AnalyticsClientException
    {
        public IdentityLinkException(string code, string message, int? httpStatusCode = null)
            : base(message, httpStatusCode)
        {
            Code = code;
        }

        public string Code { get; }
    }
}
