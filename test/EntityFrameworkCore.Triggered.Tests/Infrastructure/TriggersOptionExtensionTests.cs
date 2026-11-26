using System;
using EntityFrameworkCore.Triggered.Infrastructure;
using EntityFrameworkCore.Triggered.Infrastructure.Internal;
using EntityFrameworkCore.Triggered.Internal;
using EntityFrameworkCore.Triggered.Tests.Stubs;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EntityFrameworkCore.Triggered.Tests.Infrastructure
{
    public class TriggersOptionExtensionTests
    {
        public class TestModel
        {
            public Guid Id { get; set; }
        }

        class TestDbContext : DbContext
        {
            public TestDbContext(DbContextOptions<TestDbContext> options)
                : base(options)
            {
            }

            public DbSet<TestModel> TestModels { get; set; }
        }

        public interface ICustomTrigger
        {
        }

        public class CustomTrigger : ICustomTrigger
        {
        }

        public class OtherTrigger : IBeforeSaveTrigger<TestModel>
        {
            public void BeforeSave(ITriggerContext<TestModel> context)
            {
            }
        }

        static TestDbContext CreateContext(Action<TriggersContextOptionsBuilder> configure)
            => new(new DbContextOptionsBuilder<TestDbContext>()
                .UseInMemoryDatabase(nameof(TriggersOptionExtensionTests))
                // Each test here builds its own service provider, and EF objects once a process has built twenty.
                .ConfigureWarnings(warnings => warnings.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
                .UseTriggers(configure)
                .Options);

        // A singleton of EF's internal service provider, so two contexts return the same one only when they share that provider.
        static object ServiceProviderMarker(DbContext context)
            => context.GetService<ITriggerTypeRegistryService>();

        [Fact]
        public void AddTrigger_TypeThatImplementsNoTriggerType_IsRejected()
        {
            using var context = CreateContext(triggerOptions => triggerOptions.AddTrigger(typeof(string), ServiceLifetime.Transient));

            var exception = Assert.Throws<InvalidOperationException>(() => context.SaveChanges());

            Assert.Contains(typeof(string).FullName, exception.Message);
        }

        [Fact]
        public void AddTrigger_InstanceThatImplementsNoTriggerType_IsRejected()
        {
            using var context = CreateContext(triggerOptions => triggerOptions.AddTrigger(new Uri("https://example.com")));

            var exception = Assert.Throws<InvalidOperationException>(() => context.SaveChanges());

            Assert.Contains(typeof(Uri).FullName, exception.Message);
        }

        [Fact]
        public void AddTrigger_OpenGenericType_IsRejected()
        {
            using var context = CreateContext(triggerOptions => triggerOptions.AddTrigger(typeof(TriggerStub<>), ServiceLifetime.Transient));

            var exception = Assert.Throws<InvalidOperationException>(() => context.SaveChanges());

            Assert.Contains("open generic", exception.Message);
        }

        [Fact]
        public void AddTriggersFromAssembly_SkipsOpenGenericTriggerClasses()
        {
            // This assembly holds TriggerStub<TEntity>, a concrete trigger class that can't be created until it's closed.
            using var context = CreateContext(triggerOptions => triggerOptions
                .AddTriggersFromAssembly(typeof(TriggersOptionExtensionTests).Assembly, ServiceLifetime.Scoped));

            Assert.Equal(0, context.SaveChanges());
        }

        [Fact]
        public void AddTrigger_TypeThatImplementsNoTriggerType_IsRejectedByEveryContext()
        {
            using var first = CreateContext(triggerOptions => triggerOptions.AddTrigger(typeof(string), ServiceLifetime.Transient));
            Assert.Throws<InvalidOperationException>(() => first.SaveChanges());

            using var second = CreateContext(triggerOptions => triggerOptions.AddTrigger(typeof(string), ServiceLifetime.Transient));

            Assert.Throws<InvalidOperationException>(() => second.SaveChanges());
            Assert.Throws<InvalidOperationException>(() => second.SaveChanges());
        }

        class ContextThatAddsATriggerTypeInOnConfiguring : DbContext
        {
            public ContextThatAddsATriggerTypeInOnConfiguring(DbContextOptions<ContextThatAddsATriggerTypeInOnConfiguring> options)
                : base(options)
            {
            }

            protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
                => optionsBuilder.UseTriggers(triggerOptions => triggerOptions.AddTriggerType(typeof(ICustomTrigger)));
        }

        // EF builds services from the constructor's options before it calls OnConfiguring.
        [Fact]
        public void AddTrigger_WhenOnConfiguringAddsTheTriggerTypeItImplements_IsAccepted()
        {
            using var context = new ContextThatAddsATriggerTypeInOnConfiguring(new DbContextOptionsBuilder<ContextThatAddsATriggerTypeInOnConfiguring>()
                .UseInMemoryDatabase(nameof(TriggersOptionExtensionTests))
                .ConfigureWarnings(warnings => warnings.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
                .UseTriggers(triggerOptions => triggerOptions.AddTrigger<CustomTrigger>())
                .Options);

            Assert.Equal(0, context.SaveChanges());
        }

        [Fact]
        public void AddTrigger_BeforeTheTriggerTypeItImplements_IsAccepted()
        {
            using var context = CreateContext(triggerOptions => triggerOptions
                .AddTrigger<CustomTrigger>()
                .AddTriggerType(typeof(ICustomTrigger)));

            Assert.Equal(0, context.SaveChanges());
        }

        [Fact]
        public void AddTriggerCandidates_LeavesOutClassesThatImplementNoTriggerType()
        {
            var optionsBuilder = new DbContextOptionsBuilder();
            optionsBuilder.UseTriggers(triggerOptions => triggerOptions
                .AddTriggerCandidates(new[] { typeof(string), typeof(OtherTrigger), typeof(CustomTrigger) }, ServiceLifetime.Singleton));

            var triggers = optionsBuilder.Options.FindExtension<TriggersOptionExtension>().Triggers;

            Assert.Equal(new (object, ServiceLifetime)[] { (typeof(OtherTrigger), ServiceLifetime.Singleton) }, triggers);
        }

        [Fact]
        public void OptionsBuiltTheSameWayTwice_ShareAServiceProvider()
        {
            static void Configure(TriggersContextOptionsBuilder triggerOptions) => triggerOptions
                .AddTrigger<TriggerStub<TestModel>>()
                .AddTriggersFromAssembly(typeof(TriggersOptionExtension).Assembly, ServiceLifetime.Scoped)
                .AddTriggerCandidates(new[] { typeof(OtherTrigger) }, ServiceLifetime.Scoped)
                .AddTriggerType(typeof(ICustomTrigger));

            using var first = CreateContext(Configure);
            using var second = CreateContext(Configure);

            Assert.Same(ServiceProviderMarker(first), ServiceProviderMarker(second));
        }

        [Fact]
        public void OptionsWithDifferentTriggers_GetTheirOwnServiceProvider()
        {
            using var first = CreateContext(triggerOptions => triggerOptions.AddTrigger<TriggerStub<TestModel>>());
            using var second = CreateContext(triggerOptions => triggerOptions.AddTrigger<OtherTrigger>());

            Assert.NotSame(ServiceProviderMarker(first), ServiceProviderMarker(second));
        }

        [Fact]
        public void TriggerAddedTwice_RunsTwice()
        {
            var trigger = new TriggerStub<TestModel>();
            using var context = CreateContext(triggerOptions => triggerOptions.AddTrigger(trigger).AddTrigger(trigger));

            context.Add(new TestModel { Id = Guid.NewGuid() });
            context.SaveChanges();

            Assert.Equal(2, trigger.BeforeSaveInvocations.Count);
        }
    }
}
