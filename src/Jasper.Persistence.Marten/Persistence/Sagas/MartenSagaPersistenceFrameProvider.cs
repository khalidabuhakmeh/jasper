﻿using System;
using System.Linq;
using Jasper.Configuration;
using Jasper.Persistence.Marten.Codegen;
using Jasper.Persistence.Sagas;
using Jasper.Runtime.Handlers;
using Lamar;
using LamarCodeGeneration.Frames;
using LamarCodeGeneration.Model;
using Marten;
using Marten.Schema;
using Oakton.Parsing;

namespace Jasper.Persistence.Marten.Persistence.Sagas;

public class MartenSagaPersistenceFrameProvider : ISagaPersistenceFrameProvider, ITransactionFrameProvider
{
    public Frame DeterminePersistenceFrame(IContainer container, HandlerChain chain, MethodCall sagaHandler,
        SagaStateExistence existence,
        ref Variable sagaId, Type sagaStateType,
        Variable existingState, out Variable loadedState)
    {
        var frame = new TransactionalFrame();
        if (existence == SagaStateExistence.Existing)
        {
            var doc = frame.LoadDocument(sagaStateType, sagaId);
            loadedState = doc;
        }
        else
        {
            var mapping = new DocumentMapping(sagaStateType, new StoreOptions());

            sagaId = new Variable(mapping.IdMember.GetMemberType(),
                existingState.Usage + "." + mapping.IdMember.Name);


            loadedState = existingState;
        }

        return frame;
    }

    public Type DetermineSagaIdType(Type sagaStateType)
    {
        var mapping = new DocumentMapping(sagaStateType, new StoreOptions());
        return mapping.IdMember.GetMemberType();
    }

    public Frame DetermineStoreOrDeleteFrame(IContainer container, HandlerChain chain, MethodCall sagaHandler,
        Variable document,
        Type sagaHandlerType)
    {
        return new StoreOrDeleteSagaStateFrame(document, sagaHandlerType);
    }

    public void ApplyTransactionSupport(IChain chain, IContainer container)
    {
        if (!chain.Middleware.OfType<TransactionalFrame>().Any())
        {
            chain.Middleware.Add(new TransactionalFrame());
        }
    }
}
