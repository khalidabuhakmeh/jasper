using Baseline.Reflection;
using Jasper.RabbitMQ.Internal;
using NSubstitute;
using RabbitMQ.Client;
using Shouldly;
using Xunit;

namespace Jasper.RabbitMQ.Tests.Internals
{
    public class RabbitMqQueueTests
    {
        [Theory]
        [InlineData(true, false, false)]
        [InlineData(false, true, false)]
        [InlineData(false, false, true)]
        public void declare(bool autoDelete, bool isExclusive, bool isDurable)
        {
            var queue = new RabbitMqQueue("foo")
            {
                AutoDelete = autoDelete,
                IsExclusive = isExclusive,
                IsDurable = isDurable,
            };

            queue.HasDeclared.ShouldBeFalse();

            var channel = Substitute.For<IModel>();
            queue.Declare(channel);

            channel.Received()
                .QueueDeclare("foo", queue.IsDurable, queue.IsExclusive, queue.AutoDelete, queue.Arguments);

            queue.HasDeclared.ShouldBeTrue();
        }

        [Theory]
        [InlineData(true, false, false)]
        [InlineData(false, true, false)]
        [InlineData(false, false, true)]
        public void declare_second_time(bool autoDelete, bool isExclusive, bool isDurable)
        {
            var queue = new RabbitMqQueue("foo")
            {
                AutoDelete = autoDelete,
                IsExclusive = isExclusive,
                IsDurable = isDurable,
            };

            // cheating here.
            var prop = ReflectionHelper.GetProperty<RabbitMqQueue>(x => x.HasDeclared);
            prop.SetValue(queue, true);

            var channel = Substitute.For<IModel>();
            queue.Declare(channel);

            channel.DidNotReceiveWithAnyArgs().QueueDeclare("foo", isDurable, isExclusive, autoDelete, queue.Arguments);
            queue.HasDeclared.ShouldBeTrue();
        }
    }
}
