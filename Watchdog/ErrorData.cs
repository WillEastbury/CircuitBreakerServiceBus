namespace CircuitBreaker
{
    public class CircuitErrorData
    {
        public int ErrorCount { get; set; } = 0;
        public int ErrorCountThisMinute { get; set; } = 0;
        public int TransientErrorCount { get; set; } = 0;
        public int TransientErrorCountThisMinute { get; set; } = 0;
        public int TotalRequestsPerMinute { get; set; } = 0;
        public int TotalRequests { get; set; } = 0;
    }
}
