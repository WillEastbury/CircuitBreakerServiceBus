using System.Threading;
using System.Threading.Tasks;
using CircuitBreakerServiceBus.Plugins;

namespace CircuitBreaker
{
    public interface ICircuitBreakerWithWatchdog 
    {
        CircuitState circuitState { get; }
        CircuitHistory circuitHistory { get; }
        ICircuitOperations circuitOps {get;}
        Task StartAsync(CancellationToken cancellationToken);
        Task StopAsync(CancellationToken cancellationToken); 
    }
}
