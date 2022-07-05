using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CircuitBreaker.Config;
using CircuitBreakerServiceBus.Plugins;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
namespace CircuitBreaker
{
    public class HttpCircuitBreakingWatchdog : ICircuitBreakerWithWatchdog
    {
        public CircuitState circuitState { get; private set; } = CircuitState.InTrouble;
        public CircuitHistory circuitHistory { get; private set; } = new CircuitHistory(200);
        public ICircuitOperations circuitOps {get; private set;}
        private HttpCircuitBreakingWatchdogOptions httpPollerOptions;
        private readonly ILogger<HttpCircuitBreakingWatchdog> _logger;
        private bool StopRequested = false;
        private SemaphoreSlim SemPeriod = new SemaphoreSlim(1, 1);
        private readonly IHttpClientFactory Htc;    
        public HttpCircuitBreakingWatchdog(IOptions<HttpCircuitBreakingWatchdogOptions> PollerOptions,ILogger<HttpCircuitBreakingWatchdog> logger,IHttpClientFactory htc,ICircuitOperations circuitOps)
        {
            this.httpPollerOptions = PollerOptions.Value;
            this._logger = logger;
            Htc = htc;
            this.circuitOps = circuitOps;
        }
        public async Task StartAsync(CancellationToken cancellationToken)
        {
             while (!cancellationToken.IsCancellationRequested && !StopRequested)
            {
                circuitHistory.AddNewRequestStatus(await circuitOps.ProcessSyntheticTestMessageAsync("TestMessage"));
                int ErrorPercentageThisMinute = 0, TransientErrorPercentageThisMinute = 0;
                if (circuitHistory.GetAllRequestCount() > 0)
                {
                    ErrorPercentageThisMinute = (int)((((double)circuitHistory.GetStatsByRequestType(RequestStatusType.Failure) / (double)circuitHistory.GetAllRequestCount()) * 100));
                    TransientErrorPercentageThisMinute = (int)((((double)circuitHistory.GetStatsByRequestType(RequestStatusType.TransientFailure) / (double)circuitHistory.GetAllRequestCount()) * 100));
                }
                if (ErrorPercentageThisMinute >= httpPollerOptions.MaxErrorsPercentage) // Main errors have crossed the threshold, so take it down.
                { 
                    circuitState = CircuitState.Dead; 
                }
                else if (TransientErrorPercentageThisMinute >= httpPollerOptions.MaxErrorsTransientPercentage) // Only transient metrics are down, so throttle only.
                { 
                    circuitState = CircuitState.InTrouble; 
                    }
                else // All errors are inside threshold, so take it up.
                { 
                    circuitState = CircuitState.OK; 
                }
                await Task.Delay(httpPollerOptions.PollingIntervalMs);
            }
        }
        public Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogWarning("StopAsync Called");
            StopRequested = true;
            return Task.CompletedTask;
        }       
    }
}
