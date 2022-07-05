using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using CircuitBreaker.Config;
using CircuitBreaker.Processors.ServiceBus;

namespace CircuitBreaker
{
    class Program
    {   
        public static void Main(string[] args)
        {
             IHost ApplicationHost = Host.CreateDefaultBuilder(args)
            .ConfigureServices((hostContext, services) => {
                services.Configure<HttpCircuitBreakingWatchdogOptions>(hostContext.Configuration.GetSection("HttpWatchdog"));
                services.Configure<ServiceBusProcessorOptions>(hostContext.Configuration.GetSection("ServiceBusProcessor"));
                services.AddHttpClient("WatchDogClient");
                services.AddSingleton<ICircuitBreakingWatchdog, HttpCircuitBreakingWatchdog>();
                services.AddHostedService<ServiceBusProcessorService>();
            })
            .Build();
            ApplicationHost.Run();
        }
    }
}