using System;
using Jasper.Attributes;
using Jasper.Runtime.Handlers;
using LamarCodeGeneration;
using LamarCodeGeneration.Frames;
using Shouldly;
using TestingSupport;
using TestMessages;
using Xunit;

namespace Jasper.Testing.Compilation
{
    public class can_customize_handler_chains_with_attributes
    {
        private void forMessage<T>(Action<HandlerChain> action)
        {
            using (var runtime = JasperHost.For(opts =>
            {
                opts.Handlers.DisableConventionalDiscovery();
                opts.Handlers.IncludeType<FakeHandler1>();
                opts.Handlers.IncludeType<FakeHandler2>();
            }))
            {
                var chain = runtime.Get<HandlerGraph>().ChainFor<T>();
                action(chain);
            }
        }


        [Fact]
        public void apply_attribute_on_class()
        {
            forMessage<Message2>(chain => chain.SourceCode.ShouldContain("// fake frame here"));
        }

        [Fact]
        public void apply_attribute_on_message_type()
        {
            forMessage<ErrorHandledMessage>(chain =>
            {
                chain.SourceCode.ShouldContain("// fake frame here");
                chain.Retries.MaximumAttempts.ShouldBe(5);
            });
        }

        [Fact]
        public void apply_attribute_on_method()
        {
            forMessage<Message1>(chain => chain.SourceCode.ShouldContain("// fake frame here"));
        }
    }


    public class FakeHandler1
    {
        [FakeFrame]
        [MaximumAttempts(3)]
        public void Handle(Message1 message)
        {
        }

        public void Handle(ErrorHandledMessage message)
        {
        }
    }

    [FakeFrame]
    [MaximumAttempts(5)]
    public class ErrorHandledMessage
    {
    }

    [FakeFrame]
    public class FakeHandler2
    {
        public void Handle(Message2 message)
        {
        }
    }

    public class FakeFrameAttribute : ModifyHandlerChainAttribute
    {
        public override void Modify(HandlerChain chain, GenerationRules rules)
        {
            chain.Middleware.Add(new CustomFrame());
        }
    }

    public class CustomFrame : Frame
    {
        public CustomFrame() : base(false)
        {
        }

        public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
        {
            writer.Write("// fake frame here");
            Next?.GenerateCode(method, writer);
        }
    }
}
