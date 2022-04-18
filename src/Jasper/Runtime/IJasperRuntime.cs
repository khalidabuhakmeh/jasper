﻿using System;
using System.Collections.Generic;
using System.Threading;
using Jasper.Logging;
using Jasper.Persistence.Durability;
using Jasper.Runtime.Routing;
using Jasper.Runtime.Scheduled;
using Microsoft.Extensions.Logging;

namespace Jasper.Runtime
{
    public interface IJasperRuntime : IDisposable
    {
        IScheduledJobProcessor ScheduledJobs { get; }
        IEnvelopeRouter Router { get; }
        IHandlerPipeline Pipeline { get; }
        IMessageLogger MessageLogger { get; }
        JasperOptions Options { get; }

        IEnvelopePersistence? Persistence { get; }
        ILogger Logger { get; }
        AdvancedSettings Settings { get; }
        ITransportRuntime Runtime { get; }
        CancellationToken Cancellation { get; }

        IAcknowledgementSender Acknowledgements { get; }

        bool TryFindMessageType(string? messageTypeName, out Type messageType);

        Type DetermineMessageType(Envelope? envelope);

        void RegisterMessageType(Type messageType);
    }
}
