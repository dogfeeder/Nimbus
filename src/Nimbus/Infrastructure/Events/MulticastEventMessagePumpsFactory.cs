﻿using System;
using System.Collections.Generic;
using System.Linq;
using Nimbus.Configuration.PoorMansIocContainer;
using Nimbus.Configuration.Settings;
using Nimbus.Handlers;
using Nimbus.Infrastructure.Filtering;
using Nimbus.Routing;

namespace Nimbus.Infrastructure.Events
{
    internal class MulticastEventMessagePumpsFactory : MessagePumpFactory
    {
        private readonly ApplicationNameSetting _applicationName;
        private readonly InstanceNameSetting _instanceName;
        private readonly ILogger _logger;
        private readonly IMessageDispatcherFactory _messageDispatcherFactory;
        private readonly IHandlerMapper _handlerMapper;
        private readonly ITypeProvider _typeProvider;
        private readonly PoorMansIoC _container;
        private readonly INimbusTransport _transport;
        private readonly IRouter _router;
        private readonly IFilterConditionProvider _filterConditionProvider;

        internal MulticastEventMessagePumpsFactory(ApplicationNameSetting applicationName,
                                                   InstanceNameSetting instanceName,
                                                   IFilterConditionProvider filterConditionProvider,
                                                   IHandlerMapper handlerMapper,
                                                   ILogger logger,
                                                   IMessageDispatcherFactory messageDispatcherFactory,
                                                   INimbusTransport transport,
                                                   IRouter router,
                                                   ITypeProvider typeProvider,
                                                   PoorMansIoC container)
        {
            _applicationName = applicationName;
            _instanceName = instanceName;
            _handlerMapper = handlerMapper;
            _logger = logger;
            _messageDispatcherFactory = messageDispatcherFactory;
            _transport = transport;
            _router = router;
            _typeProvider = typeProvider;
            _container = container;
            _filterConditionProvider = filterConditionProvider;
        }

        public IEnumerable<IMessagePump> CreateAll()
        {
            var openGenericHandlerType = typeof(IHandleMulticastEvent<>);
            var handlerTypes = _typeProvider.MulticastEventHandlerTypes.ToArray();

            // Events are routed to Topics and we'll create a subscription per instance of the logical endpoint to enable multicast behaviour
            var allMessageTypesHandledByThisEndpoint = _handlerMapper.GetMessageTypesHandledBy(openGenericHandlerType, handlerTypes);
            var bindings = allMessageTypesHandledByThisEndpoint
                .Select(m => new {MessageType = m, TopicPath = _router.Route(m, QueueOrTopic.Topic)})
                .GroupBy(b => b.TopicPath)
                .Select(g => new
                             {
                                 TopicPath = g.Key,
                                 MessageTypes = g.Select(x => x.MessageType),
                                 HandlerTypes = g.SelectMany(x => _handlerMapper.GetHandlerTypesFor(openGenericHandlerType, x.MessageType))
                             })
                .ToArray();

            if (bindings.Any(b => b.MessageTypes.Count() > 1))
                throw new NotSupportedException("Routing multiple message types through a single Topic is not supported.");

            foreach (var binding in bindings)
            {
                foreach (var handlerType in binding.HandlerTypes)
                {
                    var messageType = binding.MessageTypes.Single();
                    var subscriptionName = PathFactory.SubscriptionNameFor(_applicationName, _instanceName, handlerType);
                    var filterCondition = _filterConditionProvider.GetFilterConditionFor(handlerType);

                    _logger.Debug("Creating message pump for multicast event subscription '{0}/{1}' handling {2} with filter {3}",
                                  binding.TopicPath,
                                  subscriptionName,
                                  messageType,
                                  filterCondition);

                    var messageReceiver = _transport.GetTopicReceiver(binding.TopicPath, subscriptionName, filterCondition);
                    var handlerMap = new Dictionary<Type, Type[]> {{messageType, new[] {handlerType}}};
                    var messageDispatcher = _messageDispatcherFactory.Create(openGenericHandlerType, handlerMap);

                    var pump = _container.ResolveWithOverrides<MessagePump>(messageReceiver, messageDispatcher);
                    GarbageMan.Add(pump);
                    yield return pump;
                }
            }
        }
    }
}