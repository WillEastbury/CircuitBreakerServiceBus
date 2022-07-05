using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CircuitBreaker.Config;
using Azure.Messaging.ServiceBus;
namespace CircuitBreaker.Processors.ServiceBus
{
    public class ServiceBusProcessorService : IHostedService
    {   private readonly Config.ServiceBusProcessorOptions serviceBusConnectionAndProcessorOptions;
        private readonly ICircuitBreakingWatchdog poller; 
        private readonly ILogger<ServiceBusProcessorService> _logger;
        private bool StopRequested = false;
        private SemaphoreSlim MaxSessionsInParallel = null;
        private Task pollerTask; 
        private ServiceBusClient client = null;
        public ServiceBusProcessorService(
            IOptions<Config.ServiceBusProcessorOptions> serviceBusConnectionAndProcessorOptions, 
            ILogger<ServiceBusProcessorService> logger, 
            ICircuitBreakingWatchdog poller
        )
        {
            this.serviceBusConnectionAndProcessorOptions = serviceBusConnectionAndProcessorOptions.Value;
            this._logger = logger;
            this.poller = poller;
            this.MaxSessionsInParallel = new SemaphoreSlim(serviceBusConnectionAndProcessorOptions.Value.MaxSessionsInParallel, serviceBusConnectionAndProcessorOptions.Value.MaxSessionsInParallel);
        }
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Host Started: Service Bus Streaming Begins");
            _logger.LogInformation($"Opening Sessions from Queue={serviceBusConnectionAndProcessorOptions.QueueName}");
            client = new ServiceBusClient(serviceBusConnectionAndProcessorOptions.ConnectionString);
            _logger.LogInformation($"Starting HttpPolling");

            // Don't await this, we want this to run on a background thread and no state machine to be triggered.
            this.pollerTask = poller.StartAsync(cancellationToken); 

            while (!cancellationToken.IsCancellationRequested && !StopRequested)
            {
                if (this.poller.circuitState != CircuitState.Dead)
                {
                    try
                    {
                        await using ServiceBusSessionReceiver receiver = 
                            await client.AcceptNextSessionAsync(
                                serviceBusConnectionAndProcessorOptions.QueueName,
                                new ServiceBusSessionReceiverOptions(){
                                    PrefetchCount = 10,
                                    ReceiveMode = ServiceBusReceiveMode.PeekLock
                                }
                            );

                            // Double check the circuit breaker, this could have been a quite long poll and 
                            // The service might have dropped in the meantime
                            if (this.poller.circuitState != CircuitState.Dead)
                            {
                                _logger.LogInformation($".Waiting for a lock on the session serializer to process the session.");
                                MaxSessionsInParallel.Wait();

                                await ProcessServiceBusSession(receiver);

                                MaxSessionsInParallel.Release();
                                await receiver.CloseAsync();
                                _logger.LogInformation($"..Released lock on the session serializer to process the next session.");
                            }
                            else
                            {
                                _logger.LogInformation($"Circuit Breaker just tripped to DownOpen, skipping this session");
                                await receiver.CloseAsync();
 
                            }
                    }
                    catch (ServiceBusException exc) when (exc.Reason == ServiceBusFailureReason.ServiceTimeout)
                    {
                        _logger.LogInformation("Service Bus Session Timeout, nothing to process");
                    }
                }
                else
                {
                    _logger.LogInformation($"Http Breaker - Circuit is {this.poller.circuitState}");
                }
                await Task.Delay(250);
            }
            await this.poller.StopAsync(CancellationToken.None); 
            _logger.LogWarning("Shutting down host, ceasing Service Bus Connection");
        }
        private async Task ProcessServiceBusSession(ServiceBusSessionReceiver receiver)
        {
            // Receive all of the messages in the session in a receiver loop, and if we have a message we will process it, If there are no messages, then wait for a short delay and try again, if still no messages then return
            try
            {
                // Whilst there are still messages to be processed
                // ToDo: Edge case ? 
                // where the session is empty but there are messages in the session yet to be sent by the upstream service for more than 5 seconds

                while(receiver.PeekMessageAsync() != null)
                {   
                    var batch =  await receiver.ReceiveMessagesAsync(10, new TimeSpan(0,0,5));
                    if (batch.Count > 0)
                    {
                        _logger.LogInformation($"...Batch incoming for {receiver.SessionId} - {batch.Count} Messages received.");

                        foreach(ServiceBusReceivedMessage sbrm in batch)
                        {
                            if(this.poller.circuitState == CircuitState.InTrouble)
                            {
                                // Service is recovering or is overloaded -- SLOW DOWN.
                                // This is an additional throttle ON TOP OF the BackOff delay on the retry policy. 
                                await Task.Delay(serviceBusConnectionAndProcessorOptions.OverloadedDelayInMs);
                            }
                            if(await ProcessMessageRecursiveRetryAsync(sbrm))
                            {
                                _logger.LogInformation($"...Processed Message {sbrm.MessageId}");
                                await receiver.CompleteMessageAsync(sbrm); 
                            }
                            else
                            {
                                _logger.LogInformation($"...Failed to process Message after retries {sbrm.MessageId}, abandoning message");
                                await receiver.AbandonMessageAsync(sbrm);
                            }
                        }
                    }
                    else
                    {
                        _logger.LogInformation($"... empty Batch incoming for {receiver.SessionId} Session Complete");
                        break; // No more messages in the session, so we can exit the loop.
                    }

                }
            }
            catch (Exception exc)
            {
                _logger.LogError($"Error processing session {receiver.SessionId} - {exc.Message}");
                await receiver.CloseAsync();
            }
        }

        private async Task<bool> ProcessMessageRecursiveRetryAsync(ServiceBusReceivedMessage sbrm, int currentRetries = 0)
        {
            _logger.LogInformation($"Processing Message {sbrm.MessageId} FOR THE {currentRetries} TIME.");
            if (currentRetries < serviceBusConnectionAndProcessorOptions.MaxRetryCount)
            {
                if(await ProcessMessageAsync(sbrm))
                {
                   return true;
                }
                else
                {
                    // We can call ourself, incrementing by one till we hit the MaxRetryCount
                    // and waiting for the retry delay before we call again note that the retry 
                    // Delay extends as a multiple of the number of retries

                    await Task.Delay(serviceBusConnectionAndProcessorOptions.RetryDelay * currentRetries);
                    return await ProcessMessageRecursiveRetryAsync(sbrm, currentRetries + 1);
                }
            }
            else
            {
                _logger.LogError($"Max Retries of {serviceBusConnectionAndProcessorOptions.MaxRetryCount} Exceeded {currentRetries} for Message {sbrm.MessageId}");
                return false;
            }
        }
        private async Task<bool> ProcessMessageAsync(ServiceBusReceivedMessage sbrm)
        {
            // Process the message, if we get an exception, then return false, otherwise return true
            return await Task.Factory.StartNew(() =>
            {
                _logger.LogInformation($"Processing Message {sbrm.MessageId} from Session ID : {sbrm.SessionId}");

                // Call your downstream service here, and return true if it processed ok, and false if it needs to be put back in the Q
                try
                {
                    // Do something with the message
                    Thread.Sleep(50);
                    _logger.LogDebug(sbrm.Body.ToString()); 
                    return true;
                }
                catch(Exception)
                {
                    return false; 
                }
            });
        }
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            await this.poller.StopAsync(cancellationToken);
            _logger.LogWarning("Service Bus Processor - StopAsync Called");
            StopRequested = true; 
            return; 
        }
    }
}
