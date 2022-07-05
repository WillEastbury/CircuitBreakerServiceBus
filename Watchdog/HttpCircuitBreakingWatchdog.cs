using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CircuitBreaker.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
namespace CircuitBreaker
{
    public class HttpCircuitBreakingWatchdog : ICircuitBreakingWatchdog
    {
        // Public Properties 
        public CircuitState circuitState { get; private set; } = CircuitState.InTrouble;
        public CircuitErrorData circuitErrorData {get; private set;} = new CircuitErrorData();
        // ctor       
        public HttpCircuitBreakingWatchdog(
            IOptions<HttpCircuitBreakingWatchdogOptions> sapPollerOptions, 
            ILogger<HttpCircuitBreakingWatchdog> logger, 
            IHttpClientFactory htc
            )
        {
            this.httpPollerOptions = sapPollerOptions.Value;
            this._logger = logger;
            Htc = htc;
        }
        // Public Methods
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation($"Host Started: SAP Polling Begins | IntervalInMs={httpPollerOptions.PollingIntervalMs} | URL={httpPollerOptions.PollingUrl}");
            StartClearDownTimers(out ti);
            await PollUrlBreakerAsync(cancellationToken);
        }
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            ti.Change(Timeout.Infinite, Timeout.Infinite);        
            _logger.LogWarning("StopAsync Called");
            StopRequested = true;
            await ti.DisposeAsync();           
            return;
        }
        public void RegisterRequest(RequestStatusType requestStatus)
        {
            // Invoke the correct private method depending on the requestStatus
            switch(requestStatus)
            {
                case RequestStatusType.Success:
                    IncrementRequestCounter();
                    break;
                case RequestStatusType.Failure:
                    IncrementFailedRequestCounter(false);
                    break;
                case RequestStatusType.TransientFailure:
                   IncrementFailedRequestCounter(true);
                    break;
                default:
                    break;
            }
        }
        // Private Properties and methods
        private HttpCircuitBreakingWatchdogOptions httpPollerOptions;
        private readonly ILogger<HttpCircuitBreakingWatchdog> _logger;
        private bool StopRequested = false;
        private SemaphoreSlim SemPeriod = new SemaphoreSlim(1, 1);
        private readonly IHttpClientFactory Htc;
        private Timer ti;
        private async Task PollUrlBreakerAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested && !StopRequested)
            {
                await TestUrlForSuccess();
                await Task.Delay(httpPollerOptions.PollingIntervalMs);
            }
        }
        private void IncrementRequestCounter()
        {
            // Wait for the Semaphore to be freed so we have exclusive use on the counters
            SemPeriod.Wait();
            circuitErrorData.TotalRequests++;
            SemPeriod.Release();
        }
        private void IncrementFailedRequestCounter(bool transient = false)
        {
            // Wait for the Semaphore to be freed again so we have exclusive use on the counters to write the errors, Increment each counter, release the locks
            SemPeriod.Wait();
            if (transient) 
            {
                circuitErrorData.TransientErrorCount++;
            }
            else
            {
                circuitErrorData.ErrorCount++;
            }
            SemPeriod.Release();
        }
        private void ProcessCircuitBreakerState()
        {
            int ErrorPercentageThisMinute = (circuitErrorData.ErrorCountThisMinute / circuitErrorData.TotalRequestsPerMinute) * 100;
            int TransientErrorPercentageThisMinute = (circuitErrorData.TransientErrorCountThisMinute / circuitErrorData.TotalRequestsPerMinute)* 100;

            switch(circuitState)
            {
                case CircuitState.InTrouble:

                    if (ErrorPercentageThisMinute < httpPollerOptions.MaxErrorsPercentage)
                    {
                        if(TransientErrorPercentageThisMinute < httpPollerOptions.MaxErrorsTransientPercentage)
                        {
                            // If the error percentage AND transient error rate is now less than the threshold 
                            // we are back to normal and we can open up the taps again
                            circuitState = CircuitState.OK;
                        }
                        else
                        {
                            // If the Transient error rate is still greater than the threshold but not the main error threshold
                            // we are still in trouble and we need to keep throttling whilst we recover
                            circuitState = CircuitState.InTrouble;
                        }
                    }
                    else
                    {
                        // The error percentage is still greater than the threshold, the service is dead so halt processing
                        circuitState = CircuitState.Dead;
                    }
                    break;
                case CircuitState.Dead:
                    // The circuit breaker has already tripped here and we will now be polling the health endpoint to see when 
                    // The service is back up again. If the service is back up we will reset the circuit breaker and start again.
                    if (ErrorPercentageThisMinute < httpPollerOptions.MaxErrorsPercentage)
                    {
                        if(TransientErrorPercentageThisMinute < httpPollerOptions.MaxErrorsTransientPercentage)
                        {
                            // If the error percentage AND transient error rate is now less than the threshold 
                            // we can send some careful traffic through the circuit breaker again
                            circuitState = CircuitState.InTrouble;
                        }
                        else
                        {
                            // If the Transient error rate is still greater than the threshold but not the main error threshold
                            // we are still in trouble but we might be able to enable processing again if the service is back up
                            circuitState = CircuitState.InTrouble;
                        }
                    }
                    else
                    {
                        // The error percentage is still greater than the threshold,
                        // the service is still dead so halt processing
                        circuitState = CircuitState.Dead;
                    }
                    break;
                case CircuitState.OK:
                    // The happy path so far -> The circuit breaker has not tripped here YET
                    if (ErrorPercentageThisMinute < httpPollerOptions.MaxErrorsPercentage && TransientErrorPercentageThisMinute < httpPollerOptions.MaxErrorsTransientPercentage)
                    {
                       // If BOTH of the error percentage or the transient rate are above the threshold we're down
                       circuitState = CircuitState.Dead;      
                    }
                    else if (ErrorPercentageThisMinute < httpPollerOptions.MaxErrorsPercentage || TransientErrorPercentageThisMinute < httpPollerOptions.MaxErrorsTransientPercentage)
                    {
                        // If not both but either the error percentage or the transient rate are above the threshold we're in trouble 
                        // so restrict the flow through the circuit breaker
                        circuitState = CircuitState.InTrouble;      
                    }
                    break;
            }
        }
        private async Task TestUrlForSuccess()
        {
            using (HttpClient client = Htc.CreateClient("WatchDogClient"))
            {
                try
                {
                    IncrementRequestCounter();
                    HttpResponseMessage resp = await client.GetAsync(httpPollerOptions.PollingUrl);
                    if (!resp.IsSuccessStatusCode)
                    {
                        _logger.LogWarning($"SAPPoller returned non-success code {resp.StatusCode} | Polling Url : {httpPollerOptions.PollingUrl}");
                        // only do where resp.StatusCode shows transient errors 
                        switch(resp.StatusCode)
                        {
                            case System.Net.HttpStatusCode.RequestTimeout:
                                IncrementFailedRequestCounter(true);
                                break;
                            case System.Net.HttpStatusCode.TooManyRequests:
                                IncrementFailedRequestCounter(true);
                                break;
                            default:
                                IncrementFailedRequestCounter(false);
                                break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    // I hate blindly catching exceptions but this feels like it makes sense in this circumstance that something exploded so log it and move on
                    _logger.LogWarning($"SAPPoller threw exception {ex.Message} | Polling Url : {httpPollerOptions.PollingUrl}");
                    IncrementFailedRequestCounter(false);
                }
                ProcessCircuitBreakerState();
            }
        }
        private void StartClearDownTimers(out Timer ti)
        {
            ti = new Timer(e =>
            {// Zero the periodic timer, wait for the Semaphore to be freed so we have exclusive use on the counters
                SemPeriod.Wait();
                circuitErrorData.TransientErrorCount = 0;
                circuitErrorData.ErrorCount = 0;
                SemPeriod.Release();
            }, null, 500, httpPollerOptions.DefinedBoundTimerInSeconds * 1000);
        }
    }
}
