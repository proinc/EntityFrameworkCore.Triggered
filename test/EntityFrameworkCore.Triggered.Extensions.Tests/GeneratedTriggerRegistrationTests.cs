using System;
using System.Linq;
using EntityFrameworkCore.Triggered.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EntityFrameworkCore.Triggered.Extensions.Tests
{
    public static partial class GeneratedTriggers
    {
        [GeneratedTriggerRegistration]
        public static partial TriggersContextOptionsBuilder AddGeneratedTriggers(this TriggersContextOptionsBuilder builder);

        [GeneratedTriggerRegistration]
        public static partial IServiceCollection AddGeneratedTriggers(this IServiceCollection services, ServiceLifetime lifetime);
    }

    public class GeneratedTriggerRegistrationTests
    {
        [Fact]
        public void OnOptionsBuilder_RegistersTheTriggersTheAssemblyScanFinds_InNameOrder()
        {
            var scanned = TriggerContextOptionsBuilderExtensionsTests.RegisteredTriggers(builder => builder.AddAssemblyTriggers());
            var generated = TriggerContextOptionsBuilderExtensionsTests.RegisteredTriggers(builder => builder.AddGeneratedTriggers());

            Assert.NotEmpty(generated);
            Assert.Equal(scanned.OrderBy(x => ((Type)x.typeOrInstance).FullName, StringComparer.Ordinal), generated);
        }

        [Theory]
        [InlineData(ServiceLifetime.Transient)]
        [InlineData(ServiceLifetime.Singleton)]
        public void OnServiceCollection_RegistersTheServicesTheAssemblyScanDoes(ServiceLifetime lifetime)
        {
            static string[] Describe(IServiceCollection services)
                => services
                    .Select(x => $"{x.ServiceType} {x.Lifetime} {x.ImplementationType}")
                    .OrderBy(x => x, StringComparer.Ordinal)
                    .ToArray();

            var scanned = new ServiceCollection().AddAssemblyTriggers(lifetime);
            var generated = new ServiceCollection().AddGeneratedTriggers(lifetime);

            Assert.NotEmpty(generated);
            Assert.Equal(Describe(scanned), Describe(generated));
        }
    }
}
