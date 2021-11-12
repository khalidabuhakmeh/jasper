using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Baseline.ImTools;
using Jasper.Serialization;
using Jasper.Transports;
using Jasper.Transports.Sending;
using Jasper.Util;

namespace Jasper
{
    public enum EnvelopeStatus
    {
        Outgoing,
        Scheduled,
        Incoming
    }

    // Why is this a partial you ask?
    // The elements in this file are all things that only matter
    // inside the Jasper runtime so we can keep it out of the WireProtocol
    public partial class Envelope
    {
        internal void MarkReceived(Uri uri, DateTime now, int currentNodeId)
        {
            if (IsDelayed(now))
            {
                Status = EnvelopeStatus.Scheduled;
                OwnerId = TransportConstants.AnyNode;
            }
            else
            {
                Status = EnvelopeStatus.Incoming;
                OwnerId = currentNodeId;
            }
        }


        [Obsolete("Want to eliminate this if at all possible")]
        public IMessageSerializer Writer { get; set; }



        /// <summary>
        ///     Used by IMessageContext.Invoke<T> to denote the response type
        /// </summary>
        internal Type ResponseType { get; set; }

        /// <summary>
        ///     Also used by IMessageContext.Invoke<T> to catch the response
        /// </summary>
        internal object Response { get; set; }


        /// <summary>
        ///     Status according to the message persistence
        /// </summary>
        internal EnvelopeStatus Status { get; set; }

        /// <summary>
        ///     Node owner of this message. 0 denotes that no node owns this message
        /// </summary>
        internal int OwnerId { get; set; }


        /// <summary>
        ///     Create a new Envelope that is a response to the current
        ///     Envelope
        /// </summary>
        /// <param name="message"></param>
        /// <returns></returns>
        internal Envelope CreateForResponse(object message)
        {
            var child = ForSend(message);
            child.CorrelationId = CorrelationId;
            child.CausationId = CorrelationId;

            if (message.GetType().ToMessageTypeName() == ReplyRequested)
            {
                child.Destination = ReplyUri;
                child.AcceptedContentTypes = AcceptedContentTypes;
            }

            return child;
        }

        internal Envelope ForSend(object message)
        {
            return new Envelope
            {
                Message = message,
                CorrelationId = Id.ToString(),
                CausationId = Id.ToString(),
                SagaId = SagaId
            };
        }

        internal Envelope CloneForWriter(IMessageSerializer writer)
        {
            var envelope = (Envelope)MemberwiseClone();
            envelope.Headers = new Dictionary<string, string>(Headers);
            envelope.Writer = writer;
            envelope.ContentType = writer.ContentType;

            return envelope;
        }

        internal Envelope(object message, IMessageSerializer writer)
        {
            Message = message;
            Writer = writer ?? throw new ArgumentNullException(nameof(writer));
        }

        private bool _enqueued;

        internal ISendingAgent Sender { get; set; }

        internal Task Send()
        {
            if (_enqueued) throw new InvalidOperationException("This envelope has already been enqueued");

            if (Sender == null) throw new InvalidOperationException("This envelope has not been routed");

            _enqueued = true;


            return Sender.StoreAndForward(this);
        }

        internal Task QuickSend()
        {
            if (_enqueued) throw new InvalidOperationException("This envelope has already been enqueued");

            if (Sender == null) throw new InvalidOperationException("This envelope has not been routed");

            _enqueued = true;

            return Sender.EnqueueOutgoing(this);
        }

        /// <summary>
        /// Is this envelope for a "ping" message used by Jasper to evaluate
        /// whether a sending endpoint can be restarted
        /// </summary>
        /// <returns></returns>
        public bool IsPing()
        {
            return MessageType == PingMessageType;
        }




        internal static Envelope ForPing(Uri destination)
        {
            return new Envelope
            {
                MessageType = PingMessageType,
                Data = new byte[] {1, 2, 3, 4},
                ContentType = "jasper/ping",
                Destination = destination
            };
        }


    }
}
