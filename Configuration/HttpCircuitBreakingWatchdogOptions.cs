namespace CircuitBreaker.Config
{
    public class HttpCircuitBreakingWatchdogOptions
    {
        public int PollingIntervalMs { get; set; }
        public string PollingUrl { get; set; }
        public int MaxErrorsPercentage {get;set;}
        public int MaxErrorsTransientPercentage  {get;set;}

    }
}