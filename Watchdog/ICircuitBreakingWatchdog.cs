using System.Threading;
using System.Threading.Tasks;
using CircuitBreaker.Config;
using Microsoft.Extensions.Hosting;
namespace CircuitBreaker
{
    public interface ICircuitBreakingWatchdog : IHostedService 
    {
        CircuitState circuitState { get; }
        CircuitErrorData circuitErrorData { get; }
        void RegisterRequest(RequestStatusType requestStatus);
        new Task StartAsync(CancellationToken cancellationToken);
        new Task StopAsync(CancellationToken cancellationToken); 
    }
}
