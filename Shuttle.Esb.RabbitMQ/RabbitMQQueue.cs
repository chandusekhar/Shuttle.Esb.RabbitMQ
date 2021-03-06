﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Shuttle.Core.Contract;
using Shuttle.Core.Streams;

namespace Shuttle.Esb.RabbitMQ
{
    public class RabbitMQQueue : IQueue, ICreateQueue, IDropQueue, IDisposable, IPurgeQueue
    {
        private static readonly object ConnectionLock = new object();
        private static readonly object QueueLock = new object();

        [ThreadStatic] private static ConditionalWeakTable<IConnection, Channel> _threadChannels;

        private readonly Dictionary<string, object> _arguments = new Dictionary<string, object>();
        private readonly Dictionary<Channel, WeakReference<Thread>> _channels = new Dictionary<Channel, WeakReference<Thread>>();
        private readonly List<Channel> _channelsToRemove = new List<Channel>();
        private readonly IRabbitMQConfiguration _configuration;
        
        private readonly ConnectionFactory _factory;

        private readonly int _operationRetryCount;

        private readonly RabbitMQUriParser _parser;
        private volatile IConnection _connection;

        public RabbitMQQueue(Uri uri, IRabbitMQConfiguration configuration)
        {
            Guard.AgainstNull(uri, nameof(uri));
            Guard.AgainstNull(configuration, nameof(configuration));

            _parser = new RabbitMQUriParser(uri);

            Uri = _parser.Uri;

            if (_parser.Priority != 0)
            {
                _arguments.Add("x-max-priority", (int) _parser.Priority);
            }

            _configuration = configuration;

            _operationRetryCount = _configuration.OperationRetryCount;

            if (_operationRetryCount < 1)
            {
                _operationRetryCount = 3;
            }

            _factory = new ConnectionFactory
            {
                UserName = _parser.Username,
                Password = _parser.Password,
                HostName = _parser.Host,
                VirtualHost = _parser.VirtualHost,
                Port = _parser.Port,
                RequestedHeartbeat = configuration.RequestedHeartbeat
            };
        }

        public bool HasUserInfo => !string.IsNullOrEmpty(_parser.Username) && !string.IsNullOrEmpty(_parser.Password);

        public void Create()
        {
            AccessQueue(() => QueueDeclare(GetChannel().Model));
        }

        public void Dispose()
        {
            CloseConnection();
        }

        private void CloseConnection()
        {
            lock (QueueLock)
            {
                foreach (var channel in _channels.Keys)
                {
                    channel.Dispose();
                }

                _channels.Clear();

                lock (ConnectionLock)
                {
                    if (_connection != null)
                    {
                        if (_connection.IsOpen)
                        {
                            _connection.Close(_configuration.ConnectionCloseTimeoutMilliseconds);
                        }

                        try
                        {
                            _connection.Dispose();
                        }
                        catch
                        {
                            // ignored
                        }

                        _connection = null;
                    }
                }
            }
        }

        public void Drop()
        {
            AccessQueue(() => { GetChannel().Model.QueueDelete(_parser.Queue); });
        }

        public void Purge()
        {
            AccessQueue(() => { GetChannel().Model.QueuePurge(_parser.Queue); });
        }

        public Uri Uri { get; }

        public bool IsEmpty()
        {
            return AccessQueue(() =>
            {
                var result = GetChannel().Model.BasicGet(_parser.Queue, false);

                if (result == null)
                {
                    return true;
                }

                GetChannel().Model.BasicReject(result.DeliveryTag, true);

                return false;
            });
        }

        public void Enqueue(TransportMessage transportMessage, Stream stream)
        {
            Guard.AgainstNull(transportMessage, nameof(transportMessage));
            Guard.AgainstNull(stream, nameof(stream));

            if (transportMessage.HasExpired())
            {
                return;
            }

            AccessQueue(() =>
            {
                var model = GetChannel().Model;

                var properties = model.CreateBasicProperties();

                properties.Persistent = _parser.Persistent;
                properties.CorrelationId = transportMessage.MessageId.ToString();

                if (transportMessage.HasExpiryDate())
                {
                    var milliseconds = (long) (transportMessage.ExpiryDate - DateTime.Now).TotalMilliseconds;

                    if (milliseconds < 1)
                    {
                        return;
                    }

                    properties.Expiration = milliseconds.ToString();
                }

                if (transportMessage.HasPriority())
                {
                    if (transportMessage.Priority > 255)
                    {
                        transportMessage.Priority = 255;
                    }

                    properties.Priority = (byte) transportMessage.Priority;
                }

                byte[] data = null;

                if (stream is MemoryStream ms && ms.TryGetBuffer(out var segment))
                {
                    data = segment.Array;

                    var length = (int) ms.Length;

                    if (segment.Offset != 0 || data.Length != length)
                    {
                        // we can't use any buffer pool since Rabbit needs the exact size buffer, so copy the data to a new array
                        var destinationData = new byte[length];
                        Buffer.BlockCopy(data, segment.Offset, destinationData, 0, length);
                        data = destinationData;
                    }
                }

                if (data == null)
                {
                    data = stream.ToBytes();
                }

                model.BasicPublish(string.Empty, _parser.Queue, false, properties, data);
            });
        }

        public ReceivedMessage GetMessage()
        {
            return AccessQueue(() =>
            {
                var result = GetChannel().Next();

                if (result == null)
                {
                    return null;
                }

                var body = result.Body;
                return new ReceivedMessage(new MemoryStream(body, 0, body.Length, false, true), result);
            });
        }

        public void Acknowledge(object acknowledgementToken)
        {
            AccessQueue(() => GetChannel().Acknowledge((BasicDeliverEventArgs) acknowledgementToken));
        }

        public void Release(object acknowledgementToken)
        {
            AccessQueue(() =>
            {
                var basicDeliverEventArgs = (BasicDeliverEventArgs) acknowledgementToken;

                GetChannel()
                    .Model.BasicPublish(string.Empty, _parser.Queue, false, basicDeliverEventArgs.BasicProperties,
                        basicDeliverEventArgs.Body);
                GetChannel().Acknowledge(basicDeliverEventArgs);
            });
        }

        private void QueueDeclare(IModel model)
        {
            model.QueueDeclare(_parser.Queue, _parser.Durable, false, false, _arguments);
        }

        private IConnection GetConnection()
        {
            if (_connection != null && _connection.IsOpen)
            {
                return _connection;
            }

            lock (ConnectionLock)
            {
                // double checked locking
                if (_connection != null)
                {
                    if (_connection.IsOpen)
                    {
                        return _connection;
                    }

                    // close all channels and dispose the existing connection
                    CloseConnection();
                }

                _connection = _factory.CreateConnection(_parser.Name);
            }

            return _connection;
        }

        private Channel GetChannel()
        {
            if (_threadChannels == null)
            {
                _threadChannels = new ConditionalWeakTable<IConnection, Channel>();
            }

            var connection = _connection;
            Channel channel = null;
            if (connection != null && _threadChannels.TryGetValue(connection, out channel) && channel.Model.IsOpen)
            {
                return channel;
            }

            if (channel != null)
            {
                // existing channel is not open
                _threadChannels.Remove(connection);
                channel.Dispose();
            }
            
            var retry = 0;
            connection = null;

            while (connection == null && retry < _operationRetryCount)
            {
                try
                {
                    connection = GetConnection();
                }
                catch 
                {
                    retry++;
                }
            }

            if (connection == null)
            {
                throw new ConnectionException(string.Format(Resources.ConnectionException, Uri.Secured()));
            }

            var model = connection.CreateModel();

            model.BasicQos(0,
                (ushort) (_parser.PrefetchCount == 0 ? _configuration.DefaultPrefetchCount : _parser.PrefetchCount),
                false);

            QueueDeclare(model);

            channel = new Channel(model, _parser, _configuration);

            _threadChannels.Add(connection, channel);

            lock (QueueLock)
            {
                _channels.Add(channel, new WeakReference<Thread>(Thread.CurrentThread));

                // remove the dead channels
                var channelsToRemove = _channelsToRemove;
                foreach (var pair in _channels)
                {
                    if (!pair.Value.TryGetTarget(out _))
                    {
                        channelsToRemove.Add(pair.Key);
                    }
                }

                foreach (var channelToRemove in channelsToRemove)
                {
                    _channels.Remove(channelToRemove);
                    channelToRemove.Dispose();
                }

                _channelsToRemove.Clear();
            }

            return channel;
        }

        private void AccessQueue(Action action, int retry = 0)
        {
            try
            {
                action.Invoke();
            }
            catch (ConnectionException)
            {
                if (retry == 3)
                {
                    throw;
                }

                CloseConnection();

                AccessQueue(action, retry + 1);
            }
        }

        private T AccessQueue<T>(Func<T> action, int retry = 0)
        {
            try
            {
                return action.Invoke();
            }
            catch (ConnectionException)
            {
                if (retry == 3)
                {
                    throw;
                }

                CloseConnection();

                return AccessQueue(action, retry + 1);
            }
        }
    }
}