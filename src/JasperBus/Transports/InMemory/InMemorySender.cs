﻿using System;
using System.Collections.Generic;
using JasperBus.Configuration;
using System.Threading.Tasks;
using JasperBus.Runtime;

namespace JasperBus.Transports.InMemory
{
    public class InMemorySender : ISender
    {
        private readonly Uri _destination;
        private readonly InMemoryQueue _queue;
        
        public InMemorySender(Uri destination, InMemoryQueue queue)
        {
            _queue = queue;
            _destination = destination;
        }

        public Task Send(Envelope envelope)
        {
            return _queue.Send(envelope, _destination);
        }
    }
}
