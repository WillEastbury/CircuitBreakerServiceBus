using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using CircuitBreakerServiceBus.Plugins;
using CircuitBreaker.HostedServices.ServiceBus;
namespace CircuitBreaker
{
    class Program
    {   
        public static void Main(string[] args)
        {
            IHost ApplicationHost = Host.CreateDefaultBuilder(args)
            .ConfigureServices((hostContext, services) => {
                services.AddHttpClient("WatchDogClient");
                services.AddHttpClient("OperationalClient");
                services.ConfigureServiceBusProcessorService(hostContext);
                services.AddSingleton<ICircuitOperations, BaseHttpCircuitOperations>();
            }).Build();
            ApplicationHost.Run();
        }
    }
}