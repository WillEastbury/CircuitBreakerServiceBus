using System.Net.Http;
using System.Threading.Tasks;
using CircuitBreaker;
using CircuitBreaker.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
namespace CircuitBreakerServiceBus.Plugins
{
    public class BaseHttpCircuitOperations : ICircuitOperations
    {
        protected HttpCircuitBreakingWatchdogOptions _options;
        protected ILogger<BaseHttpCircuitOperations> _logger;
        protected IHttpClientFactory _clientfactory;
        public BaseHttpCircuitOperations(IOptions<HttpCircuitBreakingWatchdogOptions> options, ILogger<BaseHttpCircuitOperations> logger, IHttpClientFactory clientfactory)
        {
            _options = options.Value;
            _logger = logger;
            _clientfactory = clientfactory;
        }
        public virtual async Task<RequestStatusType> ProcessStandardOperationalMessageAsync(string message)
        {   
            return await ValidateSuccess(await _clientfactory.CreateClient("OperationalClient").GetAsync(_options.PollingUrl));
        }

        public virtual async Task<RequestStatusType> ProcessSyntheticTestMessageAsync(string message)
        {
            return await ValidateSuccess(await _clientfactory.CreateClient("WatchDogClient").GetAsync(_options.PollingUrl));
        }
        protected internal virtual async Task<RequestStatusType> ValidateSuccess(HttpResponseMessage resp)
        {
            if (resp.IsSuccessStatusCode)
            {
                if ((await resp.Content.ReadAsStringAsync()).Contains("ErrorType"))
                {
                    return RequestStatusType.TransientFailure;
                }
                else
                {
                    return RequestStatusType.Success;
                }
            }
            switch (resp.StatusCode)
            {
                case System.Net.HttpStatusCode.RequestTimeout: return RequestStatusType.TransientFailure;
                case System.Net.HttpStatusCode.TooManyRequests: return RequestStatusType.TransientFailure;
                default: return RequestStatusType.Failure;
            }
        }
    }
}