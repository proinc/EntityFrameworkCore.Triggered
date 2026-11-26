using System;
using System.Collections.Generic;
using System.Linq;
using EntityFrameworkCore.Triggered.Infrastructure;
using EntityFrameworkCore.Triggered.Infrastructure.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EntityFrameworkCore.Triggered.Extensions.Tests
{
    public interface ILateTrigger
    {
    }

    public class LateTrigger : ILateTrigger
    {
    }

    public class TriggerContextOptionsBuilderExtensionsTests
    {
        internal static IEnumerable<(object typeOrInstance, ServiceLifetime lifetime)> RegisteredTriggers(Action<TriggersContextOptionsBuilder> configure)
        {
            var context = new DbContextOptionsBuilder();
            configure(new TriggersContextOptionsBuilder(context));

            return context.Options.Extensions.OfType<TriggersOptionExtension>().Single().Triggers;
        }

        [Fact]
        public void AddAssemblyTriggers_AbstractTrigger_GetsIgnored()
        {
            var triggers = RegisteredTriggers(builder => builder.AddAssemblyTriggers());

            // Ensure that we did not register the AbstractTrigger
            Assert.DoesNotContain(triggers, x => ReferenceEquals(x.typeOrInstance, typeof(AbstractTrigger)));
        }

        [Fact]
        public void AddAssemblyTriggers_RegistersOnlyClassesThatImplementATriggerType()
        {
            var triggers = RegisteredTriggers(builder => builder.AddAssemblyTriggers(ServiceLifetime.Singleton));

            Assert.Equal(
                new (object, ServiceLifetime)[] { (typeof(SampleTrigger), ServiceLifetime.Singleton), (typeof(SampleTriggerClone), ServiceLifetime.Singleton) },
                triggers.OrderBy(x => ((Type)x.typeOrInstance).FullName, StringComparer.Ordinal));
        }

        [Fact]
        public void AddAssemblyTriggers_TriggerTypeAddedAfterwards_IsStillFound()
        {
            var triggers = RegisteredTriggers(builder => builder.AddAssemblyTriggers().AddTriggerType(typeof(ILateTrigger)));

            Assert.Contains((typeof(LateTrigger), ServiceLifetime.Scoped), triggers);
        }
    }
}
