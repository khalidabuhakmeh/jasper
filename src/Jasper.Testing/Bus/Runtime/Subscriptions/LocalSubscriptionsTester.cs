﻿using System;
using System.Collections.Generic;
using System.Linq;
using Baseline;
using Jasper.Bus;
using Jasper.Bus.Configuration;
using Jasper.Bus.Model;
using Jasper.Bus.Runtime;
using Jasper.Bus.Runtime.Serializers;
using Jasper.Bus.Runtime.Subscriptions;
using Jasper.Conneg;
using Jasper.Testing.Conneg;
using Jasper.Util;
using Shouldly;
using Xunit;

namespace Jasper.Testing.Bus.Runtime.Subscriptions
{

    public class LocalSubscriptionsTester
    {
        public readonly BusSettings _settings = new BusSettings
        {
            Upstream = new Uri("loopback://upstream"),
            Downstream = new Uri("loopback://downstream"),
            Outbound = new Uri("loopback://outbound")
        };
        private readonly ChannelGraph _graph;
        private readonly Uri _localReplyUri;
        private readonly IEnumerable<Subscription> theSubscriptions;
        private SerializationGraph theSerialization;

        public LocalSubscriptionsTester()
        {
            _graph = new ChannelGraph
            {
                Name = "FooNode",
            };

            var serializers = new ISerializer[]{new NewtonsoftSerializer(new Jasper.Bus.BusSettings())};
            var readers = new IMediaReader[] {new FakeReader(typeof(FooMessage), "foo/special"),  };

            theSerialization =
                new SerializationGraph(new HandlerGraph(), serializers, readers, new List<IMediaWriter>());


            var requirement = new LocalSubscriptionRequirement(_settings.Upstream);
            requirement.AddType(typeof(FooMessage));
            requirement.AddType(typeof(BarMessage));

            _localReplyUri = _settings.Downstream;

            _graph.AddChannelIfMissing("fake2://2".ToUri()).Incoming = true;
            _graph.AddChannelIfMissing(_localReplyUri).Incoming = true;
            _graph.AddChannelIfMissing("fake1://1".ToUri()).Incoming = true;

            theSubscriptions = requirement.Determine(_graph, theSerialization);
        }

        [Fact]
        public void should_specify_the_accepts_with_json_last()
        {
            theSubscriptions.First().Accepts.ShouldBe("foo/special,application/json");
            theSubscriptions.Last().Accepts.ShouldBe("application/json");
        }

        [Fact]
        public void should_set_the_message_type()
        {
            theSubscriptions.First().MessageType.ShouldBe(typeof(FooMessage).ToTypeAlias());
        }


        [Fact]
        public void should_set_the_receiver_uri_to_the_reply_uri_of_the_matching_transport()
        {
            theSubscriptions.First().Receiver
                .ShouldBe(_localReplyUri);
        }

        [Fact]
        public void sets_the_node_name_from_the_channel_graph()
        {
            theSubscriptions.Select(x => x.Publisher).Distinct()
                .Single().ShouldBe(_graph.Name);
        }

        [Fact]
        public void should_set_the_source_uri_to_the_requested_source_from_settings()
        {
            theSubscriptions.First().Source
                .ShouldBe(_settings.Upstream);
        }

        [Fact]
        public void should_add_a_subscription_for_each_type()
        {
            theSubscriptions.Select(x => x.MessageType)
                .ShouldHaveTheSameElementsAs(typeof(FooMessage).ToTypeAlias(), typeof(BarMessage).ToTypeAlias());
        }

        public class BusSettings
        {
            public Uri Outbound { get; set; }
            public Uri Downstream { get; set; }
            public Uri Upstream { get; set; }
        }

        public class FooMessage{}
        public class BarMessage{}
    }
}
