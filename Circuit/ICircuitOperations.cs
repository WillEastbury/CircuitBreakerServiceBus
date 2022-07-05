using System.Threading.Tasks;
using CircuitBreaker;
namespace CircuitBreakerServiceBus.Plugins
{
    public interface ICircuitOperations
    {
        Task<RequestStatusType> ProcessStandardOperationalMessageAsync(string message);
        Task<RequestStatusType> ProcessSyntheticTestMessageAsync(string message);
    }
}