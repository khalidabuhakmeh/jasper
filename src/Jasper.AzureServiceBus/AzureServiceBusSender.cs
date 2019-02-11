using System;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using Jasper.Messaging.Logging;
using Jasper.Messaging.Runtime;
using Jasper.Messaging.Transports.Sending;
using Jasper.Util;
using Microsoft.Azure.Amqp.Framing;
using Microsoft.Azure.ServiceBus;
using Microsoft.Azure.ServiceBus.Core;

namespace Jasper.AzureServiceBus
{
    public class AzureServiceBusSender : ISender
    {
        private readonly IEnvelopeMapper _mapper;
        private readonly ITransportLogger _logger;
        private readonly CancellationToken _cancellation;
        private IMessageSender _sender;
        private ActionBlock<Envelope> _sending;
        private ActionBlock<Envelope> _serialization;
        private ISenderCallback _callback;
        private readonly AzureServiceBusSettings _settings;
        private readonly bool _isDurable;

        public AzureServiceBusSender(Uri destination, IEnvelopeMapper mapper, AzureServiceBusSettings settings, ITransportLogger logger, CancellationToken cancellation)
        {
            _mapper = mapper;
            _logger = logger;
            _cancellation = cancellation;
            Destination = destination;
            _settings = settings;

            _isDurable = destination.IsDurable();
        }

        public void Dispose()
        {
            _sender.CloseAsync().GetAwaiter().GetResult();
        }

        public Uri Destination { get; }
        public int QueuedCount => _sending.InputCount;
        public bool Latched { get; private set; }


        public void Start(ISenderCallback callback)
        {
            _sender = _settings.BuildSender(Destination);
            _callback = callback;

            _serialization = new ActionBlock<Envelope>(e =>
            {
                try
                {
                    e.EnsureData();
                    _sending.Post(e);
                }
                catch (Exception exception)
                {
                    _logger.LogException(exception, e.Id, "Serialization Failure!");
                }
            });

            _sending = new ActionBlock<Envelope>(send, new ExecutionDataflowBlockOptions
            {
                CancellationToken = _cancellation
            });
        }

        public Task Enqueue(Envelope envelope)
        {
            _serialization.Post(envelope);

            return Task.CompletedTask;
        }

        public Task LatchAndDrain()
        {
            Latched = true;

            _sender.CloseAsync().GetAwaiter().GetResult();

            _sending.Complete();
            _serialization.Complete();


            _logger.CircuitBroken(Destination);

            return Task.CompletedTask;
        }

        public void Unlatch()
        {
            _logger.CircuitResumed(Destination);

            Start(_callback);
            Latched = false;
        }

        public Task Ping()
        {
            var envelope = Envelope.ForPing(Destination);
            var message = _mapper.WriteFromEnvelope(envelope);
            return _sender.SendAsync(message);
        }

        public bool SupportsNativeScheduledSend { get; } = true;

        private async Task send(Envelope envelope)
        {
            try
            {
                var message = _mapper.WriteFromEnvelope(envelope);
                await _sender.SendAsync(message);

                await _callback.Successful(envelope);
            }
            catch (Exception e)
            {
                try
                {
                    await _callback.ProcessingFailure(envelope, e);
                }
                catch (Exception exception)
                {
                    _logger.LogException(exception);
                }
            }
        }
    }
}
