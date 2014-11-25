// Copyright 2007-2014 Chris Patterson, Dru Sellers, Travis Smith, et. al.
//  
// Licensed under the Apache License, Version 2.0 (the "License"); you may not use
// this file except in compliance with the License. You may obtain a copy of the 
// License at 
// 
//     http://www.apache.org/licenses/LICENSE-2.0 
// 
// Unless required by applicable law or agreed to in writing, software distributed
// under the License is distributed on an "AS IS" BASIS, WITHOUT WARRANTIES OR 
// CONDITIONS OF ANY KIND, either express or implied. See the License for the 
// specific language governing permissions and limitations under the License.
namespace MassTransit.Transports
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Logging;
    using Pipeline;
    using Subscriptions;


    /// <summary>
    /// Support in-memory message queue that is not durable, but supports parallel delivery of messages
    /// based on TPL usage.
    /// </summary>
    public class InMemoryTransport :
        IReceiveTransport,
        ISendTransport,
        IDisposable
    {
        static readonly ILog _log = Logger.Get<InMemoryTransport>();

        readonly BlockingCollection<InMemoryTransportMessage> _collection;
        readonly Uri _inputAddress;
        readonly Connectable<ISendObserver> _observers;

        public InMemoryTransport(Uri inputAddress)
        {
            _inputAddress = inputAddress;

            _observers = new Connectable<ISendObserver>();

            var queue = new ConcurrentQueue<InMemoryTransportMessage>();
            _collection = new BlockingCollection<InMemoryTransportMessage>(queue);
        }

        public void Dispose()
        {
            if (_collection != null)
                _collection.Dispose();
        }

        public Uri InputAddress
        {
            get { return _inputAddress; }
        }

        async Task IReceiveTransport.Start(IPipe<ReceiveContext> receivePipe, CancellationToken stopReceive)
        {
            Task receiveTask = Task.Run(() =>
            {
                using (RegisterShutdown(stopReceive))
                {
                    Parallel.ForEach(GetConsumingPartitioner(_collection), async message =>
                    {
                        if (stopReceive.IsCancellationRequested)
                            return;

                        var context = new InMemoryReceiveContext(_inputAddress, message);

                        try
                        {
                            await receivePipe.Send(context);
                        }
                        catch (Exception ex)
                        {
                            message.DeliveryCount++;
                            _log.Error(string.Format("Receive Fault: {0}", message.MessageId), ex);

                            _collection.Add(message, stopReceive);
                        }
                    });
                }
            }, stopReceive);

            await receiveTask;
        }

        async Task ISendTransport.Send<T>(T message, IPipe<SendContext<T>> pipe, CancellationToken cancelSend)
        {
            var context = new InMemorySendContext<T>(message, cancelSend);

            try
            {
                await pipe.Send(context);

                Guid messageId = context.MessageId ?? NewId.NextGuid();

                await _observers.ForEach(x => x.PreSend(context));

                var transportMessage = new InMemoryTransportMessage(messageId, context.Body, context.ContentType.MediaType);

                _collection.Add(transportMessage, cancelSend);

                await _observers.ForEach(x => x.PostSend(context));
            }
            catch (Exception ex)
            {
                _observers.ForEach(x => x.SendFault(context, ex))
                    .Wait(cancelSend);
            }
        }

        public ConnectHandle Connect(ISendObserver observer)
        {
            return _observers.Connect(observer);
        }

        CancellationTokenRegistration RegisterShutdown(CancellationToken cancellationToken)
        {
            return cancellationToken.Register(() =>
            {
                // signal collection that no more messages will be added, ending it
                _collection.CompleteAdding();
            });
        }

        Partitioner<T> GetConsumingPartitioner<T>(BlockingCollection<T> collection)
        {
            return new BlockingCollectionPartitioner<T>(collection);
        }


        class BlockingCollectionPartitioner<T> :
            Partitioner<T>
        {
            readonly BlockingCollection<T> _collection;

            internal BlockingCollectionPartitioner(BlockingCollection<T> collection)
            {
                if (collection == null)
                    throw new ArgumentNullException("collection");
                _collection = collection;
            }

            public override bool SupportsDynamicPartitions
            {
                get { return true; }
            }

            public override IList<IEnumerator<T>> GetPartitions(int partitionCount)
            {
                if (partitionCount < 1)
                    throw new ArgumentOutOfRangeException("partitionCount");

                IEnumerable<T> dynamicPartitioner = GetDynamicPartitions();

                return Enumerable.Range(0, partitionCount).Select(_ => dynamicPartitioner.GetEnumerator()).ToArray();
            }

            public override IEnumerable<T> GetDynamicPartitions()
            {
                return _collection.GetConsumingEnumerable();
            }
        }
    }
}