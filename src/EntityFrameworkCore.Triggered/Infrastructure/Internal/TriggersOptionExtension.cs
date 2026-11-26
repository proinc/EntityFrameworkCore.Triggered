using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using EntityFrameworkCore.Triggered.Internal;
using EntityFrameworkCore.Triggered.Internal.CascadeStrategies;
using EntityFrameworkCore.Triggered.Lifecycles;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EntityFrameworkCore.Triggered.Infrastructure.Internal
{
    public class TriggersOptionExtension : IDbContextOptionsExtension
    {
        sealed class ExtensionInfo : DbContextOptionsExtensionInfo
        {
            private string? _logFragment;
            private readonly int _serviceProviderHash;

            public ExtensionInfo(TriggersOptionExtension extension) : base(extension)
            {
                // EF asks for the hash for every context it constructs. The extension no longer changes once it
                // hands out its info, so the hash is worked out once.
                _serviceProviderHash = extension.ComputeServiceProviderHashCode();
            }

            public override bool IsDatabaseProvider => false;
            public override string LogFragment
            {
                get
                {
                    if (_logFragment == null)
                    {
                        _logFragment = string.Empty;
                    }

                    return _logFragment;
                }
            }

            public new TriggersOptionExtension Extension => (TriggersOptionExtension)base.Extension;

            public override void PopulateDebugInfo(IDictionary<string, string> debugInfo)
            {
                if (debugInfo == null)
                {
                    throw new ArgumentNullException(nameof(debugInfo));
                }

                debugInfo["Triggers:TriggersCount"] = Extension.ResolveTriggers().Length.ToString();
                debugInfo["Triggers:TriggerTypesCount"] = Extension._triggerTypes.Length.ToString();
                debugInfo["Triggers:MaxCascadeCycles"] = Extension._maxCascadeCycles.ToString();
                debugInfo["Triggers:CascadeBehavior"] = Extension._cascadeBehavior.ToString();
            }

            public override int GetServiceProviderHashCode() => _serviceProviderHash;

            public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other)
                => other is ExtensionInfo otherInfo
                    && (ReferenceEquals(Extension, otherInfo.Extension) || Extension.RegistersSameServicesAs(otherInfo.Extension));
        }

        // One entry of the trigger list. A trigger is a type or an instance, and has to implement a trigger type.
        // A discovery is an assembly or an array of classes, and contributes the classes that do.
        private readonly record struct Registration(object Source, ServiceLifetime Lifetime, bool IsDiscovery)
        {
            public Type TriggerType => Source as Type ?? Source.GetType();
            public object? TriggerInstance => Source is Type ? null : Source;
            public IEnumerable<Type> GetDiscoveredTypes() => Source as Type[] ?? ((Assembly)Source).GetTypes();

            // Arrays compare by content, so a caller that builds a new array for each set of options still shares
            // a service provider.
            public bool Equals(Registration other)
                => Lifetime == other.Lifetime
                    && IsDiscovery == other.IsDiscovery
                    && (Source is Type[] types
                        ? other.Source is Type[] otherTypes && (ReferenceEquals(types, otherTypes) || types.SequenceEqual(otherTypes))
                        : Source.Equals(other.Source));

            // An array hashes by its length, which agrees with Equals and saves walking it for each new set of options.
            public override int GetHashCode()
                => HashCode.Combine(Lifetime, IsDiscovery, Source is Type[] types ? types.Length : Source.GetHashCode());
        }

        private sealed record ResolvedTrigger(Type Type, object? Instance, ServiceLifetime Lifetime, Type[] Implementations);

        private static readonly Type[] _defaultTriggerTypes = new[] {
            typeof(IBeforeSaveTrigger<>),
            typeof(IBeforeSaveAsyncTrigger<>),
            typeof(IAfterSaveTrigger<>),
            typeof(IAfterSaveAsyncTrigger<>),
            typeof(IAfterSaveFailedTrigger<>),
            typeof(IAfterSaveFailedAsyncTrigger<>),
            typeof(IBeforeSaveStartingTrigger),
            typeof(IBeforeSaveStartingAsyncTrigger),
            typeof(IBeforeSaveCompletedTrigger),
            typeof(IBeforeSaveCompletedAsyncTrigger),
            typeof(IAfterSaveFailedStartingTrigger),
            typeof(IAfterSaveFailedStartingAsyncTrigger),
            typeof(IAfterSaveFailedCompletedTrigger),
            typeof(IAfterSaveFailedCompletedAsyncTrigger),
            typeof(IAfterSaveStartingTrigger),
            typeof(IAfterSaveStartingAsyncTrigger),
            typeof(IAfterSaveCompletedTrigger),
            typeof(IAfterSaveCompletedAsyncTrigger)
        };

        private ExtensionInfo? _info;
        // Arrays are replaced, never changed, so clones can share them.
        private Registration[] _registrations = Array.Empty<Registration>();
        private Type[] _triggerTypes = _defaultTriggerTypes;
        private ResolvedTrigger[]? _resolvedTriggers;
        private bool _validated;
        private int _maxCascadeCycles = 100;
        private CascadeBehavior _cascadeBehavior = CascadeBehavior.EntityAndType;
        private Func<IServiceProvider, IServiceProvider>? _serviceProviderTransform;

        public TriggersOptionExtension()
        {
        }

        public TriggersOptionExtension(TriggersOptionExtension copyFrom)
        {
            _registrations = copyFrom._registrations;
            _triggerTypes = copyFrom._triggerTypes;
            _maxCascadeCycles = copyFrom._maxCascadeCycles;
            _cascadeBehavior = copyFrom._cascadeBehavior;
            _serviceProviderTransform = copyFrom._serviceProviderTransform;
        }

        public DbContextOptionsExtensionInfo Info
            => _info ??= new ExtensionInfo(this);

        public int MaxCascadeCycles => _maxCascadeCycles;
        public CascadeBehavior CascadeBehavior => _cascadeBehavior;

        /// <summary>
        /// The triggers these options register, with the triggers found in each added assembly in place of the assembly.
        /// </summary>
        public IEnumerable<(object typeOrInstance, ServiceLifetime lifetime)> Triggers
            => ResolveTriggers().Select(trigger => (trigger.Instance ?? trigger.Type, trigger.Lifetime));

        public void ApplyServices(IServiceCollection services)
        {
            services.AddScoped(serviceProvider => new ApplicationTriggerServiceProviderAccessor(serviceProvider, _serviceProviderTransform));
            services.AddScoped<IResettableService>(serviceProvider => serviceProvider.GetRequiredService<ApplicationTriggerServiceProviderAccessor>());
            services.TryAddScoped<ITriggerServiceProviderAccessor>(serviceProvider => serviceProvider.GetRequiredService<ApplicationTriggerServiceProviderAccessor>());

            services.TryAddSingleton<ITriggerTypeRegistryService, TriggerTypeRegistryService>();

            services.AddScoped<TriggerDiscoveryService>();
            services.AddScoped<IResettableService>(serviceProvider => serviceProvider.GetRequiredService<TriggerDiscoveryService>());
            services.TryAddScoped<ITriggerDiscoveryService>(serviceProvider => serviceProvider.GetRequiredService<TriggerDiscoveryService>());

            services.AddScoped<TriggerService>();
            services.AddScoped<IResettableService>(serviceProvider => serviceProvider.GetRequiredService<TriggerService>());
            services.TryAddScoped<ITriggerService>(serviceProvider => serviceProvider.GetRequiredService<TriggerService>());

            services.AddScoped<TriggerFactory>();

            services.AddScoped<TriggerSessionSaveChangesInterceptor>();
            services.AddScoped<IResettableService>(serviceProvider => serviceProvider.GetRequiredService<TriggerSessionSaveChangesInterceptor>());
            services.TryAddScoped<IInterceptor>(serviceProvider => serviceProvider.GetRequiredService<TriggerSessionSaveChangesInterceptor>());

            services.Configure<TriggerOptions>(triggerServiceOptions => {
                triggerServiceOptions.MaxCascadeCycles = _maxCascadeCycles;
            });

            var cascadeStrategyType = _cascadeBehavior switch {
                CascadeBehavior.None => typeof(NoCascadeStrategy),
                CascadeBehavior.EntityAndType => typeof(EntityAndTypeCascadeStrategy),
                _ => throw new InvalidOperationException("Unsupported cascading mode")
            };

            services.TryAddTransient(typeof(ICascadeStrategy), cascadeStrategyType);

            foreach (var trigger in ResolveTriggers())
            {
                if (trigger.Implementations.Length == 0)
                {
                    continue;
                }

                var instanceParamExpression = Expression.Parameter(typeof(object), "object");

                var triggerInstanceFactoryBuilder =
                    Expression.Lambda<Func<object?, object>>(
                            Expression.New(
                                typeof(TriggerInstanceFactory<>).MakeGenericType(trigger.Type).GetConstructor(new[] { typeof(object) })!,
                                instanceParamExpression
                            ),
                            instanceParamExpression
                    )
                    .Compile();

                foreach (var triggerTypeImplementation in trigger.Implementations)
                {
                    var triggerTypeImplementationFactoryType = typeof(ITriggerInstanceFactory<>).MakeGenericType(triggerTypeImplementation);
                    services.Add(new ServiceDescriptor(triggerTypeImplementationFactoryType, _ => triggerInstanceFactoryBuilder(trigger.Instance), trigger.Lifetime));
                    services.AddScoped(typeof(IResettableService), serviceProvider => serviceProvider.GetRequiredService(triggerTypeImplementationFactoryType));
                }
            }
        }

        // Checked here rather than in AddTrigger because a trigger can be added before the trigger type it implements,
        // as when UseTransactionTriggers follows AddTrigger. Discoveries are skipped: they only contribute classes that
        // are triggers, and reading an assembly is too slow for the setups where EF calls this for every context.
        public void Validate(IDbContextOptions options)
        {
            if (_validated)
            {
                return;
            }

            foreach (var registration in _registrations)
            {
                if (!registration.IsDiscovery && FindTriggerImplementations(registration.TriggerType).Length == 0)
                {
                    throw new InvalidOperationException(
                        $"'{registration.TriggerType}' was added as a trigger but implements none of the registered trigger types. "
                        + "A trigger implements an interface such as IBeforeSaveTrigger<TEntity>, or one added with AddTriggerType. "
                        + "UseTransactionTriggers adds the transaction trigger types.");
                }
            }

            _validated = true;
        }

        protected TriggersOptionExtension Clone() => new(this);

        private ResolvedTrigger[] ResolveTriggers()
        {
            if (_resolvedTriggers is not null)
            {
                return _resolvedTriggers;
            }

            var triggers = new List<ResolvedTrigger>();

            foreach (var registration in _registrations)
            {
                if (registration.IsDiscovery)
                {
                    foreach (var type in registration.GetDiscoveredTypes())
                    {
                        if (type.IsClass && !type.IsAbstract && FindTriggerImplementations(type) is { Length: > 0 } implementations)
                        {
                            triggers.Add(new ResolvedTrigger(type, null, registration.Lifetime, implementations));
                        }
                    }
                }
                else
                {
                    triggers.Add(new ResolvedTrigger(
                        registration.TriggerType,
                        registration.TriggerInstance,
                        registration.Lifetime,
                        FindTriggerImplementations(registration.TriggerType)));
                }
            }

            return _resolvedTriggers = triggers.ToArray();
        }

        // The interfaces of the type that are trigger types, grouped by trigger type in the order those were registered.
        private Type[] FindTriggerImplementations(Type type)
        {
            var interfaces = type.GetInterfaces();
            if (interfaces.Length == 0)
            {
                return Array.Empty<Type>();
            }

            var implementations = new List<Type>();

            foreach (var triggerType in _triggerTypes)
            {
                foreach (var @interface in interfaces)
                {
                    var implementsTriggerType = triggerType.IsGenericTypeDefinition
                        ? @interface.IsGenericType && @interface.GetGenericTypeDefinition() == triggerType
                        : @interface == triggerType;

                    if (implementsTriggerType)
                    {
                        implementations.Add(@interface);
                    }
                }
            }

            return implementations.ToArray();
        }

        private int ComputeServiceProviderHashCode()
        {
            var hashCode = new HashCode();

            foreach (var registration in _registrations)
            {
                hashCode.Add(registration);
            }

            foreach (var triggerType in _triggerTypes)
            {
                hashCode.Add(triggerType);
            }

            hashCode.Add(_maxCascadeCycles);
            hashCode.Add(_cascadeBehavior);

            if (_serviceProviderTransform != null)
            {
                hashCode.Add(_serviceProviderTransform);
            }

            return hashCode.ToHashCode();
        }

        private bool RegistersSameServicesAs(TriggersOptionExtension other)
            => _registrations.SequenceEqual(other._registrations)
                && _triggerTypes.SequenceEqual(other._triggerTypes)
                && _maxCascadeCycles == other._maxCascadeCycles
                && _cascadeBehavior == other._cascadeBehavior
                && _serviceProviderTransform == other._serviceProviderTransform;

        public TriggersOptionExtension WithCascadeBehavior(CascadeBehavior cascadeBehavior)
        {
            var clone = Clone();

            clone._cascadeBehavior = cascadeBehavior;

            return clone;
        }

        public TriggersOptionExtension WithMaxCascadeCycles(int maxCascadeCycles)
        {
            var clone = Clone();

            clone._maxCascadeCycles = maxCascadeCycles;

            return clone;
        }

        public TriggersOptionExtension WithAdditionalTrigger(Type triggerType, ServiceLifetime lifetime)
        {
            if (triggerType == null)
            {
                throw new ArgumentNullException(nameof(triggerType));
            }

            return WithAdditionalRegistration(new Registration(triggerType, lifetime, IsDiscovery: false));
        }

        public TriggersOptionExtension WithAdditionalTrigger(object instance)
        {
            if (instance == null)
            {
                throw new ArgumentNullException(nameof(instance));
            }

            return WithAdditionalRegistration(new Registration(instance, ServiceLifetime.Singleton, IsDiscovery: false));
        }

        public TriggersOptionExtension WithAdditionalTriggerAssembly(Assembly assembly, ServiceLifetime lifetime)
        {
            if (assembly == null)
            {
                throw new ArgumentNullException(nameof(assembly));
            }

            return WithAdditionalRegistration(new Registration(assembly, lifetime, IsDiscovery: true));
        }

        public TriggersOptionExtension WithAdditionalTriggerCandidates(Type[] candidates, ServiceLifetime lifetime)
        {
            if (candidates == null)
            {
                throw new ArgumentNullException(nameof(candidates));
            }

            return WithAdditionalRegistration(new Registration(candidates, lifetime, IsDiscovery: true));
        }

        private TriggersOptionExtension WithAdditionalRegistration(Registration registration)
        {
            var clone = Clone();
            clone._registrations = [.. _registrations, registration];

            return clone;
        }

        public TriggersOptionExtension WithAdditionalTriggerType(Type triggerType)
        {
            if (triggerType == null)
            {
                throw new ArgumentNullException(nameof(triggerType));
            }

            // Kept distinct, since a trigger type listed twice would register each of its triggers twice.
            if (_triggerTypes.Contains(triggerType))
            {
                return this;
            }

            var clone = Clone();
            clone._triggerTypes = [.. _triggerTypes, triggerType];

            return clone;
        }

        public TriggersOptionExtension WithApplicationScopedServiceProviderAccessor(Func<IServiceProvider, IServiceProvider> serviceProviderTransform)
        {
            var clone = Clone();
            clone._serviceProviderTransform = serviceProviderTransform;

            return clone;
        }
    }
}
