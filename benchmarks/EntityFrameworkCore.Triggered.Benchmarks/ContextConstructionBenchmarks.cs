using System;
using System.Reflection;
using System.Reflection.Emit;
using BenchmarkDotNet.Attributes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EntityFrameworkCore.Triggered.Benchmarks
{
    // What a new DbContext costs when triggers come from an assembly scan. The scanned assembly
    // holds AssemblyClasses classes that aren't triggers, next to this assembly's two that are.
    [MemoryDiagnoser]
    public class ContextConstructionBenchmarks
    {
        DbContextOptions<TriggeredApplicationContext> _sharedOptions;
        IServiceProvider _serviceProvider;

        [Params(10, 1_000, 10_000)]
        public int AssemblyClasses;

        [GlobalSetup]
        public void GlobalSetup()
        {
            var assemblies = new[] { CreateAssembly(AssemblyClasses), typeof(ContextConstructionBenchmarks).Assembly };

            void Configure(DbContextOptionsBuilder options) => options
                .UseInMemoryDatabase(nameof(ContextConstructionBenchmarks))
                .UseTriggers(triggerOptions => triggerOptions.AddAssemblyTriggers(assemblies));

            var optionsBuilder = new DbContextOptionsBuilder<TriggeredApplicationContext>();
            Configure(optionsBuilder);
            _sharedOptions = optionsBuilder.Options;

            _serviceProvider = new ServiceCollection()
                .AddDbContext<TriggeredApplicationContext>(Configure)
                .BuildServiceProvider();
        }

        static Assembly CreateAssembly(int classes)
        {
            var module = AssemblyBuilder
                .DefineDynamicAssembly(new AssemblyName($"Synthetic{classes}"), AssemblyBuilderAccess.Run)
                .DefineDynamicModule("Synthetic");

            Type created = null;
            for (var i = 0; i < classes; i++)
            {
                created = module.DefineType($"Synthetic.Class{i}", TypeAttributes.Public | TypeAttributes.Class).CreateType();
            }

            return created.Assembly;
        }

        // One set of options for every context, as a context pool or factory has when it constructs a new instance.
        [Benchmark]
        public object SharedOptions()
        {
            using var context = new TriggeredApplicationContext(_sharedOptions);

            return context.Model;
        }

        // AddDbContext builds the options again for each scope.
        [Benchmark]
        public object OptionsPerScope()
        {
            using var scope = _serviceProvider.CreateScope();

            return scope.ServiceProvider.GetRequiredService<TriggeredApplicationContext>().Model;
        }
    }
}
