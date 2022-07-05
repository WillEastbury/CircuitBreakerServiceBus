using System;

namespace CircuitBreaker.Config
{
    public class ServiceBusProcessorOptions
    {
        public string ConnectionString { get; set; }
        public string QueueName { get; set; }
        public int MaxRetryCount { get; set; }
        public TimeSpan RetryDelay { get; set; }
        public int OverloadedDelayInMs {get;set;}
        public int MaxSessionsInParallel {get;set;}
    }
}