using Baseline.Dates;
using Jasper;
using Microsoft.Extensions.Hosting;

namespace DocumentationSamples
{

    public class PublishingSamples
    {
        public static async Task LocalQueuesApp()
                {
                    #region sample_LocalQueuesApp

                    using var host = await Host.CreateDefaultBuilder()
                        .UseJasper(opts =>
                        {
                            // Force a local queue to be
                            // strictly first in, first out
                            // with no more than a single
                            // thread handling messages enqueued
                            // here

                            // Use this option if message ordering is
                            // important
                            opts.LocalQueue("one")
                                .Sequential();

                            // Specify the maximum number of parallel threads
                            opts.LocalQueue("two")
                                .MaximumParallelMessages(5);


                            // Or just edit the ActionBlock options directly
                            opts.LocalQueue("three")
                                .ConfigureExecution(options =>
                                {
                                    options.MaxDegreeOfParallelism = 5;
                                    options.BoundedCapacity = 1000;
                                });

                            // And finally, this enrolls a queue into the persistent inbox
                            // so that messages can happily be retained and processed
                            // after the service is restarted
                            opts.LocalQueue("four").DurablyPersistedLocally();
                        }).StartAsync();

                    #endregion
                }




        #region sample_IServiceBus.Invoke
        public Task Invoke(IExecutionContext bus)
        {
            var @event = new InvoiceCreated
            {
                Time = DateTimeOffset.Now,
                Purchaser = "Guy Fieri",
                Amount = 112.34,
                Item = "Cookbook"
            };

            return bus.InvokeAsync(@event);
        }
        #endregion

        #region sample_IServiceBus.Enqueue
        public ValueTask Enqueue(IExecutionContext bus)
        {
            var @event = new InvoiceCreated
            {
                Time = DateTimeOffset.Now,
                Purchaser = "Guy Fieri",
                Amount = 112.34,
                Item = "Cookbook"
            };

            return bus.EnqueueAsync(@event);
        }
        #endregion

        #region sample_IServiceBus.Enqueue_to_specific_worker_queue
        public ValueTask EnqueueToQueue(IExecutionContext bus)
        {
            var @event = new InvoiceCreated
            {
                Time = DateTimeOffset.Now,
                Purchaser = "Guy Fieri",
                Amount = 112.34,
                Item = "Cookbook"
            };

            // Put this message in a local worker
            // queue named 'highpriority'
            return bus.EnqueueAsync(@event, "highpriority");
        }
        #endregion

        #region sample_send_delayed_message
        public async Task SendScheduledMessage(IExecutionContext bus, Guid invoiceId)
        {
            var message = new ValidateInvoiceIsNotLate
            {
                InvoiceId = invoiceId
            };

            // Schedule the message to be processed in a certain amount
            // of time
            await bus.SchedulePublishAsync(message, 30.Days());

            // Schedule the message to be processed at a certain time
            await bus.SchedulePublishAsync(message, DateTimeOffset.Now.AddDays(30));
        }
        #endregion

        #region sample_schedule_job_locally
        public async Task ScheduleLocally(IExecutionContext bus, Guid invoiceId)
        {
            var message = new ValidateInvoiceIsNotLate
            {
                InvoiceId = invoiceId
            };

            // Schedule the message to be processed in a certain amount
            // of time
            await bus.ScheduleAsync(message, 30.Days());

            // Schedule the message to be processed at a certain time
            await bus.ScheduleAsync(message, DateTimeOffset.Now.AddDays(30));
        }
        #endregion

        #region sample_sending_message_with_servicebus
        public ValueTask SendMessage(IExecutionContext bus)
        {
            // In this case, we're sending an "InvoiceCreated"
            // message
            var @event = new InvoiceCreated
            {
                Time = DateTimeOffset.Now,
                Purchaser = "Guy Fieri",
                Amount = 112.34,
                Item = "Cookbook"
            };

            return bus.SendAsync(@event);
        }
        #endregion


        #region sample_publishing_message_with_servicebus
        public ValueTask PublishMessage(IExecutionContext bus)
        {
            // In this case, we're sending an "InvoiceCreated"
            // message
            var @event = new InvoiceCreated
            {
                Time = DateTimeOffset.Now,
                Purchaser = "Guy Fieri",
                Amount = 112.34,
                Item = "Cookbook"
            };

            return bus.PublishAsync(@event);
        }
        #endregion


        #region sample_send_message_to_specific_destination
        public async Task SendMessageToSpecificDestination(IExecutionContext bus)
        {
            var @event = new InvoiceCreated
            {
                Time = DateTimeOffset.Now,
                Purchaser = "Guy Fieri",
                Amount = 112.34,
                Item = "Cookbook"
            };

            await bus.SendAsync(new Uri("tcp://server1:2222"), @event);
        }


        public class ValidateInvoiceIsNotLate
        {
            public Guid InvoiceId { get; set; }
        }
        #endregion

        public class InvoiceCreated
        {
            public DateTimeOffset Time { get; set; }
            public string Purchaser { get; set; }
            public double Amount { get; set; }
            public string Item { get; set; }
        }

        public class SomeMessage
        {
        }
    }



}
