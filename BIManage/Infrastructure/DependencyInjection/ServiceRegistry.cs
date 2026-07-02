using System;
using System.Collections.Generic;

namespace BIManage.Infrastructure.DependencyInjection
{
    /// <summary>
    ///     Lightweight service registry for dependency injection and lifecycle management.
    ///     Manages service registration, resolution, and disposal in correct order.
    /// </summary>
    /// <example>
    /// <code>
    /// var services = new ServiceRegistry();
    /// services.RegisterSingleton&lt;ILogger&gt;(new FileLogger());
    /// services.RegisterSingleton&lt;IFeatureToggleService, FeatureToggleService&gt;();
    ///
    /// var logger = services.GetRequiredService&lt;ILogger&gt;();
    /// services.Dispose(); // Disposes services in reverse registration order
    /// </code>
    /// </example>
    public class ServiceRegistry : IDisposable
    {
        private readonly Dictionary<Type, object> _singletons = new();
        private readonly Dictionary<Type, Type> _transientMappings = new();
        private readonly List<object> _registrationOrder = new();
        private readonly object _lock = new();
        private bool _disposed;

        /// <summary>
        ///     Registers a singleton service with its implementation type.
        ///     The implementation will be instantiated on first resolution.
        /// </summary>
        /// <typeparam name="TInterface">The service interface type</typeparam>
        /// <typeparam name="TImplementation">The implementation type</typeparam>
        /// <returns>This ServiceRegistry for fluent chaining</returns>
        public ServiceRegistry RegisterSingleton<TInterface, TImplementation>()
            where TImplementation : TInterface, new()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(ServiceRegistry));

            lock (_lock)
            {
                var interfaceType = typeof(TInterface);

                if (_singletons.ContainsKey(interfaceType))
                    throw new InvalidOperationException($"Service {interfaceType.Name} is already registered.");

                var instance = new TImplementation();
                _singletons[interfaceType] = instance!;
                _registrationOrder.Add(instance!);
            }

            return this;
        }

        /// <summary>
        ///     Registers an existing singleton instance.
        /// </summary>
        /// <typeparam name="TInterface">The service interface type</typeparam>
        /// <param name="instance">The service instance</param>
        /// <returns>This ServiceRegistry for fluent chaining</returns>
        public ServiceRegistry RegisterSingleton<TInterface>(TInterface instance)
            where TInterface : class
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(ServiceRegistry));

            if (instance == null)
                throw new ArgumentNullException(nameof(instance));

            lock (_lock)
            {
                var interfaceType = typeof(TInterface);

                if (_singletons.ContainsKey(interfaceType))
                    throw new InvalidOperationException($"Service {interfaceType.Name} is already registered.");

                _singletons[interfaceType] = instance;
                _registrationOrder.Add(instance);
            }

            return this;
        }

        /// <summary>
        ///     Registers a transient service mapping.
        ///     A new instance will be created on each resolution.
        /// </summary>
        /// <typeparam name="TInterface">The service interface type</typeparam>
        /// <typeparam name="TImplementation">The implementation type</typeparam>
        /// <returns>This ServiceRegistry for fluent chaining</returns>
        public ServiceRegistry RegisterTransient<TInterface, TImplementation>()
            where TImplementation : TInterface, new()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(ServiceRegistry));

            lock (_lock)
            {
                var interfaceType = typeof(TInterface);
                var implementationType = typeof(TImplementation);

                if (_transientMappings.ContainsKey(interfaceType))
                    throw new InvalidOperationException($"Service {interfaceType.Name} is already registered as transient.");

                _transientMappings[interfaceType] = implementationType;
            }

            return this;
        }

        /// <summary>
        ///     Resolves a required service. Throws if not found.
        /// </summary>
        /// <typeparam name="TInterface">The service interface type</typeparam>
        /// <returns>The service instance</returns>
        /// <exception cref="InvalidOperationException">If service is not registered</exception>
        public TInterface GetRequiredService<TInterface>()
            where TInterface : class
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(ServiceRegistry));

            var service = GetService<TInterface>();

            if (service == null)
            {
                var interfaceType = typeof(TInterface);
                throw new InvalidOperationException(
                    $"Required service {interfaceType.Name} is not registered. " +
                    $"Register it using RegisterSingleton<{interfaceType.Name}>() or RegisterTransient<{interfaceType.Name}>().");
            }

            return service;
        }

        /// <summary>
        ///     Resolves an optional service. Returns null if not found.
        /// </summary>
        /// <typeparam name="TInterface">The service interface type</typeparam>
        /// <returns>The service instance or null if not registered</returns>
        public TInterface? GetService<TInterface>()
            where TInterface : class
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(ServiceRegistry));

            var interfaceType = typeof(TInterface);

            // Check for singleton first
            if (_singletons.TryGetValue(interfaceType, out var singleton))
            {
                return (TInterface)singleton;
            }

            // Check for transient mapping
            if (_transientMappings.TryGetValue(interfaceType, out var implementationType))
            {
                return (TInterface?)Activator.CreateInstance(implementationType);
            }

            return null;
        }

        /// <summary>
        ///     Disposes all registered services in reverse registration order.
        ///     Only services implementing IDisposable will be disposed.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
                return;

            lock (_lock)
            {
                if (_disposed)
                    return;

                // Dispose in reverse registration order
                for (int i = _registrationOrder.Count - 1; i >= 0; i--)
                {
                    var service = _registrationOrder[i];

                    if (service is IDisposable disposable)
                    {
                        try
                        {
                            disposable.Dispose();
                        }
                        catch
                        {
                            // Swallow exceptions during disposal to ensure all services are disposed
                            // Logging not available here as logger may already be disposed
                        }
                    }
                }

                _singletons.Clear();
                _transientMappings.Clear();
                _registrationOrder.Clear();

                _disposed = true;
            }
        }
    }
}
