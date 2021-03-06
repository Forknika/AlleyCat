﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using AlleyCat.Common;
using AlleyCat.Game;
using AlleyCat.Logging;
using EnsureThat;
using Godot;
using LanguageExt;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static LanguageExt.Prelude;

namespace AlleyCat.Autowire
{
    public class AutowireContext : IAutowireContext
    {
        public string Key => Node.GetPath();

        public Node Node { get; }

        public Option<IAutowireContext> Parent
        {
            get
            {
                if (Node.GetTree().Root == Node) return None;

                Option<Node> FindParent(Node node)
                {
                    var parent = Optional(node.GetParent());

                    return parent.Bind(p => p.FindDelegate()).Contains(node) ? parent.Bind(FindParent) : parent;
                }

                return FindParent(Node).Map(n => n.GetAutowireContext());
            }
        }

        public LanguageExt.HashSet<Type> Requires { get; private set; } = HashSet<Type>();

        public LanguageExt.HashSet<Type> Provides { get; private set; } = HashSet<Type>();

        public static LanguageExt.HashSet<INodeProcessorFactory> ProcessorFactories => _processorFactories;

        private readonly IServiceCollection _services = new ServiceCollection();

        private Option<ServiceProvider> _provider;

        private readonly DependencyChain _queue = new DependencyChain();

        private bool _built;

        private bool _disposed;

        private static readonly NodeStore<AutowireContext> Store = new NodeStore<AutowireContext>();

        private static readonly IMemoryCache Cache = new MemoryCache(new MemoryCacheOptions());

        private static LanguageExt.HashSet<INodeProcessorFactory> _processorFactories =
            HashSet<INodeProcessorFactory>(
                new NodeAttributeProcessorFactory(),
                new ServiceAttributeProcessorFactory(),
                new AncestorAttributeProcessorFactory(),
                new ServiceDefinitionProviderProcessorFactory(),
                new SingletonAttributeProcessorFactory(),
                new PostConstructAttributeProcessorFactory());

        public static void RegisterProcessorFactory(INodeProcessorFactory factory)
        {
            _processorFactories = _processorFactories.TryAdd(factory);
        }

        public ILogger Logger
        {
            get
            {
                if (_logger.IsNone)
                {
                    _logger = _loggerFactory.Map(f => f.CreateLogger(this.GetLogCategory()));
                }

                return _logger.IfNone(new PrintLogger(this.GetLogCategory(), LogLevel.Information));
            }
        }

        private Option<ILogger> _logger;

        private static Option<ILoggerFactory> _loggerFactory;

        private AutowireContext(Node node)
        {
            Debug.Assert(node != null, "node != null");

            Node = node;
        }

        public Option<T> FindService<T>() => FindService(typeof(T)).OfType<T>().HeadOrNone();

        public Option<object> FindService(Type serviceType)
        {
            CheckDisposed();

            return _provider.Bind(p => Optional(p.GetService(serviceType)));
        }

        public void AddService(Action<IServiceCollection> provider)
        {
            Ensure.That(provider, nameof(provider)).IsNotNull();

            CheckDisposed();

            provider.Invoke(_services);

            _provider = _services.BuildServiceProvider();

            if (_loggerFactory.IsNone)
            {
                _loggerFactory = FindService<ILoggerFactory>();
            }
        }

        internal void Register(Node node)
        {
            Ensure.That(node, nameof(node)).IsNotNull();

            if (node == node.GetTree().Root)
            {
                return;
            }

            var definition = node is Binding
                ? CreateDefinition(node)
                : Cache.GetOrCreate(node.GetType(), _ => CreateDefinition(node));

            Register(new DependencyNode(node, definition));
        }

        internal void Register(AutowireContext context) => Register(new DependencyNode(context));

        private void Register(DependencyNode node)
        {
            CheckDisposed();

            if (_built)
            {
                node.Process(this);
            }
            else
            {
                _queue.Add(node);
            }

            this.LogDebug("Registering a node: '{}'.", node.Instance);
        }

        internal void Initialize()
        {
            CheckDisposed();

            Requires = Requires.Clear();
            Provides = Provides.Clear();

            var provided = HashSet<Type>();

            foreach (var dependency in _queue)
            {
                Requires = Requires.Union(dependency.Requires);

                if (Node == dependency.Instance)
                {
                    Provides = Provides.Union(dependency.Provides);
                }

                provided = provided.Union(dependency.Provides);
            }

            Requires = Requires.Except(provided);

            this.LogDebug("Calculating dependencies.");

            Provides.Iter(type => this.LogDebug("- Provides: {}", type));
            Requires.Iter(type => this.LogDebug("- Requires: {}", type));

            Parent.BiIter(p => ((AutowireContext) p).Register(this), _ => Build());
        }

        internal void Build()
        {
            CheckDisposed();

            this.LogDebug("Started building autowire context.");

            _built = true;

            _queue.UpdateDependencies();

            if (Logger.IsEnabled(LogLevel.Debug))
            {
                this.LogDebug("Dependency build order:");

                _queue.Iter(d => this.LogDebug(" - {}", d.Instance.GetPath()));
            }

            _queue.Iter(d => d.Process(this));
            _queue.Iter(d => d.ProcessDeferred(this));

            _queue.Clear();

            this.LogDebug("Finished building autowire context.");
        }

        private ServiceDefinition CreateDefinition(Node node)
        {
            Debug.Assert(node != null, "node != null");

            var processors = CreateProcessors(node).ToSeq();

            var provides = HashSet<Type>();
            var requires = HashSet<Type>();

            if (node is IServiceDefinitionProvider p)
            {
                provides = provides.Union(p.ProvidedTypes);
            }

            foreach (var processor in processors)
            {
                if (processor is IDependencyConsumer consumer)
                {
                    requires = requires.Union(consumer.Requires);
                }

                if (processor is IDependencyProvider provider)
                {
                    provides = provides.Union(provider.Provides);
                }
            }

            return new ServiceDefinition(node.GetType(), provides, requires, processors);
        }

        private static IEnumerable<INodeProcessor> CreateProcessors(Node node)
        {
            Debug.Assert(node != null, "node != null");

            var type = node.GetType();
            var processors = ProcessorFactories.Bind(f => f.Create(type)).ToList();

            processors.Sort();

            return processors;
        }

        public void Dispose()
        {
            if (_disposed) return;

            _services.Clear();
            _queue.Clear();

            _provider.Iter(p => p.DisposeQuietly());
            _provider = None;

            _disposed = true;
        }

        private void CheckDisposed()
        {
            if (_disposed)
            {
                throw new InvalidOperationException($"{this} is already disposed.");
            }
        }

        public override string ToString() => $"ApplicationContext({Node.Name})";

        internal static AutowireContext GetOrCreate(Node node) => Store.Get(node, _ => new AutowireContext(node));

        internal static void Shutdown() => Store.DisposeQuietly();
    }
}
