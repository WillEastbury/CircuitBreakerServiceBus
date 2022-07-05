Usage - create a class and override the circuit break methods, a default implementation is given in BaseHttpCircuitOptions 
--------------------------------------------------------------------------------------------------------------------------

    // public class SampleCircuitSAPOperations : BaseHttpCircuitOperations, ICircuitOperations
    // {
    //     public SampleCircuitSAPOperations(IOptions<HttpCircuitBreakingWatchdogOptions> options, ILogger<BaseHttpCircuitOperations> logger, IHttpClientFactory clientfactory) : base(options, logger, clientfactory){}    
    //     public override async Task<RequestStatusType> ProcessStandardOperationalMessageAsync(string message)
    //     {
    //         return await ValidateSuccess(await _clientfactory.CreateClient("OperationalClient").GetAsync(_options.PollingUrl));
    //     }
    //     public override async Task<RequestStatusType> ProcessSyntheticTestMessageAsync(string message)
    //     {
    //         return await ValidateSuccess(await _clientfactory.CreateClient("WatchDogClient").GetAsync(_options.PollingUrl));
    //     }
    //     protected internal override async Task<RequestStatusType> ValidateSuccess(HttpResponseMessage resp)
    //     {
    //         return await base.ValidateSuccess(resp);
    //     }
    // }
... or you can directly implement ICircuitOperations 
----------------------------

    public interface ICircuitOperations
    {
        Task<RequestStatusType> ProcessStandardOperationalMessageAsync(string message);
        Task<RequestStatusType> ProcessSyntheticTestMessageAsync(string message);
    }

If you are creating a worker or background process then you will need to start the SB host. 

    class Program
    {   
        public static void Main(string[] args)
        {
            IHost ApplicationHost = Host.CreateDefaultBuilder(args)
            .ConfigureServices((hostContext, services) => {
                services.AddHttpClient("WatchDogClient");
                services.AddHttpClient("OperationalClient");
                services.ConfigureServiceBusProcessorService(hostContext);
                services.AddSingleton<ICircuitOperations, MyCircuitOperations>(); // This is your earlier ICircuitOperations class
            }).Build();
            ApplicationHost.Run();
        }
    }

Configuration -> We expect the following to be present on startup, in appsettings.json or otherwise (we load with the Options Pattern)

{
  "HttpWatchdog": {
    "PollingIntervalMs": 500,
    "PollingUrl": "http://localhost:5131/",
    "MaxErrorsPercentage":5,
    "MaxErrorsTransientPercentage":25
  },
  "ServiceBusProcessor": {
    "ConnectionString": "Endpoint=sb://<snip>",
    "QueueName": "myq",
    "MaxRetryCount": 3,
    "RetryDelay":"00:00:10",
    "OverloadedDelayInMs" : 250,
    "MaxSessionsInParallel" :1 
  }
}

Customisation 

If you want something more custom then you can inject your own dependencies happily.

This is what you will need if you don't want to use the basic spin up extension method (ConfigureServiceBusProcessorService) in  ServiceBusProcessorExtensions provided for you.

            // Load the settings
            services.Configure<HttpCircuitBreakingWatchdogOptions>(hostContext.Configuration.GetSection("HttpWatchdog"));
            services.Configure<Config.ServiceBusProcessorOptions>(hostContext.Configuration.GetSection("ServiceBusProcessor"));

            // Inject an ICircuitBreakerWithWatchdog
            services.AddSingleton<ICircuitBreakerWithWatchdog, HttpCircuitBreakingWatchdog>();
            
            // Host the service itself 
            services.AddHostedService<ServiceBusProcessorService>();

            // Wire up the operations to run through the circuit breaker 
            services.AddSingleton<ICircuitOperations, BaseHttpCircuitOperations>();
