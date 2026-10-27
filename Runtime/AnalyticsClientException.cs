using System;

namespace GameAnalytics.Client
{
    // Caller-visible failure of a client operation (initialization/time synchronization or an
    // event-clock read). Messages are safe to log: they carry no response bodies, headers or
    // credential values, and the HTTP status is preserved separately when the attempt reached
    // the server.
    public class AnalyticsClientException : Exception
    {
        public AnalyticsClientException(string message)
            : base(message)
        {
        }

        public AnalyticsClientException(string message, int? httpStatusCode)
            : base(message)
        {
            HttpStatusCode = httpStatusCode;
        }

        public AnalyticsClientException(string message, int? httpStatusCode, Exception innerException)
            : base(message, innerException)
        {
            HttpStatusCode = httpStatusCode;
        }

        // The status the server answered with, when the attempt reached it at all.
        public int? HttpStatusCode { get; }
    }
}
