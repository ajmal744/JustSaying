using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Amazon;
using Amazon.SQS.Model;
using JustSaying.AwsTools.MessageHandling;
using JustSaying.AwsTools.QueueCreation;
using JustSaying.Messaging.MessageHandling;
using JustSaying.Messaging.MessageSerialisation;
using JustSaying.Messaging.Monitoring;
using JustSaying.TestingFramework;
using NSubstitute;
using NUnit.Framework;

namespace JustSaying.AwsTools.IntegrationTests
{
    // OK, I know it ain't pretty, but we needed this asap & it does the job. Deal with it. :)

    [TestFixture]
    public class BasicHandlingThrottlingTest
    {
        [TestCase(1000), Explicit]
        // Use this to manually test the performance / throttling of getting messages out of the queue.
        public void HandlingManyMessages(int throttleMessageCount)
        {
            var locker = new object();
            var awsQueueClient = CreateMeABus.DefaultClientFactory().GetSqsClient(RegionEndpoint.EUWest1);
 
            var q = new SqsQueueByName(RegionEndpoint.EUWest1, "throttle_test", awsQueueClient, 1);
            if (!q.Exists())
            {
                q.Create(new SqsBasicConfiguration());
                Thread.Sleep(TimeSpan.FromMinutes(1));  // wait 60 secs for queue creation to be guaranteed completed by aws. :(
            }

            Assert.True(q.Exists());

            Console.WriteLine($"{DateTime.Now} - Adding {throttleMessageCount} messages to the queue.");

            var entriesAdded = 0;
            // Add some messages
            do
            {
                var entries = new List<SendMessageBatchRequestEntry>();
                for (var j = 0; j < 10; j++)
                {
                    var batchEntry = new SendMessageBatchRequestEntry
                                         {
                                             MessageBody = "{\"Subject\":\"GenericMessage\", \"Message\": \"" + entriesAdded.ToString() + "\"}",
                                             Id = Guid.NewGuid().ToString()
                                         };
                    entries.Add(batchEntry);
                    entriesAdded++;
                }
                awsQueueClient.SendMessageBatch(new SendMessageBatchRequest { QueueUrl = q.Url, Entries = entries });
            }
            while (entriesAdded < throttleMessageCount);

            Console.WriteLine($"{DateTime.Now} - Done adding messages.");
            
            var handleCount = 0;
            var serialisations = Substitute.For<IMessageSerialisationRegister>();
            var monitor = Substitute.For<IMessageMonitor>();
            var handler = Substitute.For<IHandlerAsync<GenericMessage>>();
            handler.Handle(null).ReturnsForAnyArgs(true).AndDoes(x => {lock (locker) { handleCount++; } });

            serialisations.DeserializeMessage(string.Empty).ReturnsForAnyArgs(new GenericMessage());
            var listener = new SqsNotificationListener(q, serialisations, monitor);
            listener.AddMessageHandler(() => handler);

            var stopwatch = new Stopwatch();
            stopwatch.Start();
            listener.Listen();
            var waitCount = 0;
            do
            {
                Thread.Sleep(TimeSpan.FromSeconds(5));
                Console.WriteLine($"{DateTime.Now} - Handled {handleCount} messages. Waiting for completion.");
                waitCount++;
            }
            while (handleCount < throttleMessageCount && waitCount < 100);

            listener.StopListening();
            stopwatch.Stop();

            Console.WriteLine($"{DateTime.Now} - Handled {handleCount} messages.");
            Console.WriteLine($"{DateTime.Now} - Took {stopwatch.ElapsedMilliseconds} ms");
            Console.WriteLine(
                $"{DateTime.Now} - Throughput {(float) handleCount/stopwatch.ElapsedMilliseconds*1000} msg/sec");
            Assert.AreEqual(throttleMessageCount, handleCount);
        }
        
    }
}