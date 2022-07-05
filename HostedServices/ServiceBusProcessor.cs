using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CircuitBreaker.Config;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.DependencyInjection;
namespace CircuitBreaker.HostedServices.ServiceBus
{
    public class ServiceBusProcessorService : IHostedService
    {   private readonly Config.ServiceBusProcessorOptions serviceBusConnectionAndProcessorOptions;
        private readonly ICircuitBreakerWithWatchdog poller; 
        private readonly ILogger<ServiceBusProcessorService> _logger;
        private bool StopRequested = false;
        private SemaphoreSlim MaxSessionsInParallel = null;
        private Task pollerTask; 
        private ServiceBusClient client = null;
        public ServiceBusProcessorService(IOptions<Config.ServiceBusProcessorOptions> serviceBusConnectionAndProcessorOptions, ILogger<ServiceBusProcessorService> logger,ICircuitBreakerWithWatchdog poller )
        {
            this.serviceBusConnectionAndProcessorOptions = serviceBusConnectionAndProcessorOptions.Value;
            this._logger = logger;
            this.poller = poller;
            this.MaxSessionsInParallel = new SemaphoreSlim(serviceBusConnectionAndProcessorOptions.Value.MaxSessionsInParallel, serviceBusConnectionAndProcessorOptions.Value.MaxSessionsInParallel);
        }
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            client = new ServiceBusClient(serviceBusConnectionAndProcessorOptions.ConnectionString);
            this.pollerTask = poller.StartAsync(cancellationToken); 
            while (!cancellationToken.IsCancellationRequested && !StopRequested)
            {
                if (this.poller.circuitState != CircuitState.Dead)
                {
                    try
                    {
                        await using ServiceBusSessionReceiver receiver = await client.AcceptNextSessionAsync(serviceBusConnectionAndProcessorOptions.QueueName, new ServiceBusSessionReceiverOptions(){ PrefetchCount = 10, ReceiveMode = ServiceBusReceiveMode.PeekLock});

                        // Double check the circuit breaker, this could have been a quite long poll and The service might have dropped in the meantime
                        if (this.poller.circuitState != CircuitState.Dead)
                        {
                            try
                            {
                                MaxSessionsInParallel.Wait();
                                await ProcessServiceBusSession(receiver);
                            }
                            finally 
                            {
                                MaxSessionsInParallel.Release();
                                await receiver.CloseAsync();
                            }  
                                                        }
                        else
                        {
                            await receiver.CloseAsync();
                        }
                    }
                    catch (ServiceBusException exc) when (exc.Reason == ServiceBusFailureReason.ServiceTimeout)
                    {
                        // Nothing to process, carry on with the loop.
              
                    }
                }
            }
            await this.poller.StopAsync(CancellationToken.None); 
                
        }
        private async Task ProcessServiceBusSession(ServiceBusSessionReceiver receiver)
        {
            try
            {
                // Triple check the state in case the target service has dropped in the meantime
                if (this.poller.circuitState != CircuitState.Dead)
                {
                    while(receiver.PeekMessageAsync() != null)
                    {   
                        var batch =  await receiver.ReceiveMessagesAsync(10, new TimeSpan(0,0,10));
                        if (batch.Count > 0)
                        {
                            _logger.LogInformation($"...Batch incoming for {receiver.SessionId} - {batch.Count} Messages received.");

                            foreach(ServiceBusReceivedMessage sbrm in batch)
                            {
                                if(this.poller.circuitState == CircuitState.InTrouble)
                                {
                                    await Task.Delay(serviceBusConnectionAndProcessorOptions.OverloadedDelayInMs);
                                }
                                if(await ProcessMessageRecursiveRetryAsync(sbrm) == RequestStatusType.Success)
                                {
                                    await receiver.CompleteMessageAsync(sbrm); 
                                }
                                else
                                {
                                    await receiver.AbandonMessageAsync(sbrm);
                                }
                            }
                        }
                        else
                        {
                            break; // No more messages in the session, so we can exit the loop.
                        }
                    }
                }
                else
                {
                    await receiver.CloseAsync();
                }
            }
            catch (Exception exc)
            {
                _logger.LogError($"Error processing session {receiver.SessionId} - {exc.Message}");
                await receiver.CloseAsync();
            }
        }
        private async Task<RequestStatusType> ProcessMessageRecursiveRetryAsync(ServiceBusReceivedMessage sbrm, int currentRetries = 0)
        {
            if (currentRetries < serviceBusConnectionAndProcessorOptions.MaxRetryCount)
            {
                var req = await poller.circuitOps.ProcessStandardOperationalMessageAsync(sbrm.ToString());
                if(req != RequestStatusType.Success)
                {
                    // We can call ourself, incrementing by one till we hit the MaxRetryCount and waiting for the retry delay before we call again note that the retry 
                    // Delay extends as a multiple of the number of retries
                    await Task.Delay(serviceBusConnectionAndProcessorOptions.RetryDelay * currentRetries);
                    return await ProcessMessageRecursiveRetryAsync(sbrm, currentRetries + 1);
                }
                else
                {
                    return req; // success! We can return the status.
                }
            }
            else
            {
                _logger.LogError($"Max Retries of {serviceBusConnectionAndProcessorOptions.MaxRetryCount} Exceeded {currentRetries} for Message {sbrm.MessageId}");
                return RequestStatusType.Failure;
            }
        }
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            await this.poller.StopAsync(cancellationToken);
            StopRequested = true; 
            return; 
        }
    }
    public static class ServiceBusProcessorExtensions
    {
        public static void ConfigureServiceBusProcessorService(this IServiceCollection services, HostBuilderContext hostContext)
        {
            services.Configure<HttpCircuitBreakingWatchdogOptions>(hostContext.Configuration.GetSection("HttpWatchdog"));
            services.Configure<Config.ServiceBusProcessorOptions>(hostContext.Configuration.GetSection("ServiceBusProcessor"));
            services.AddSingleton<ICircuitBreakerWithWatchdog, HttpCircuitBreakingWatchdog>();
            services.AddHostedService<ServiceBusProcessorService>();
  
            // Now we need to pass in a set of callbacks in an ICircuitOperations interface. We will leave this down to the user in the 
            // main startup class. We will not register an implementation here.
            // You can either pass a derivative of BaseHttpCircuitOperations or an implementation of ICircuitOperations.

            // services.AddSingleton<ICircuitOperations, SampleCircuitSAPOperations>(); // Like this 
            
           

        }
    }
}
