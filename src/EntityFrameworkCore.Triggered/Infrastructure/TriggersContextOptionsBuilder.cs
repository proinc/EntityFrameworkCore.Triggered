using System;
using System.Reflection;
using EntityFrameworkCore.Triggered.Infrastructure.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace EntityFrameworkCore.Triggered.Infrastructure
{
    public class TriggersContextOptionsBuilder
    {
        readonly DbContextOptionsBuilder _optionsBuilder;

        public TriggersContextOptionsBuilder(DbContextOptionsBuilder optionsBuilder)
        {
            _optionsBuilder = optionsBuilder ?? throw new ArgumentNullException(nameof(optionsBuilder));
        }

        public TriggersContextOptionsBuilder AddTrigger<TTrigger>()
            => AddTrigger<TTrigger>(ServiceLifetime.Transient);

        public TriggersContextOptionsBuilder AddTrigger<TTrigger>(ServiceLifetime lifetime)
            => AddTrigger(typeof(TTrigger), lifetime);

        public TriggersContextOptionsBuilder AddTrigger(Type triggerType, ServiceLifetime lifetime)
            => WithOption(e => e.WithAdditionalTrigger(triggerType, lifetime));

        public TriggersContextOptionsBuilder AddTrigger(object trigger)
            => WithOption(e => e.WithAdditionalTrigger(trigger));

        /// <summary>
        ///     Adds every concrete class in the assembly that implements a registered trigger type.
        /// </summary>
        /// <remarks>
        ///     The assembly is read when EF builds the context's services, which it does once and then caches, so
        ///     trigger types registered after this call still count.
        /// </remarks>
        public TriggersContextOptionsBuilder AddTriggersFromAssembly(Assembly assembly, ServiceLifetime lifetime)
            => WithOption(e => e.WithAdditionalTriggerAssembly(assembly, lifetime));

        /// <summary>
        ///     Adds each of the given classes that implements a registered trigger type, and leaves out the rest.
        /// </summary>
        /// <remarks>
        ///     For a list that discovery produced, such as the one the source generator writes for
        ///     <c>[GeneratedTriggerRegistration]</c>. <see cref="AddTrigger(Type, ServiceLifetime)" /> is for a
        ///     class you name yourself, and rejects one that isn't a trigger. The array must not change afterwards.
        /// </remarks>
        public TriggersContextOptionsBuilder AddTriggerCandidates(Type[] candidates, ServiceLifetime lifetime)
            => WithOption(e => e.WithAdditionalTriggerCandidates(candidates, lifetime));

        public TriggersContextOptionsBuilder CascadeBehavior(CascadeBehavior cascadeBehavior = Infrastructure.CascadeBehavior.EntityAndType)
            => WithOption(e => e.WithCascadeBehavior(cascadeBehavior));

        public TriggersContextOptionsBuilder MaxCascadeCycles(int maxCascadingCycles = 100)
            => WithOption(e => e.WithMaxCascadeCycles(maxCascadingCycles));

        public TriggersContextOptionsBuilder AddTriggerType(Type triggerType)
            => WithOption(e => e.WithAdditionalTriggerType(triggerType));

        public TriggersContextOptionsBuilder UseApplicationScopedServiceProviderAccessor(Func<IServiceProvider, IServiceProvider> serviceProviderTransform)
            => WithOption(e => e.WithApplicationScopedServiceProviderAccessor(serviceProviderTransform));

        /// <summary>
        ///     Sets an option by cloning the extension used to store the settings. This ensures the builder
        ///     does not modify options that are already in use elsewhere.
        /// </summary>
        /// <param name="setAction"> An action to set the option. </param>
        /// <returns> The same builder instance so that multiple calls can be chained. </returns>
        protected virtual TriggersContextOptionsBuilder WithOption(Func<TriggersOptionExtension, TriggersOptionExtension> setAction)
        {
            ((IDbContextOptionsBuilderInfrastructure)_optionsBuilder).AddOrUpdateExtension(
                setAction(_optionsBuilder.Options.FindExtension<TriggersOptionExtension>() ?? new TriggersOptionExtension()));

            return this;
        }
    }
}
