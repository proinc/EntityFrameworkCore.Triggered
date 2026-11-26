using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using EntityFrameworkCore.Triggered.Infrastructure.Internal;
using EntityFrameworkCore.Triggered.Transactions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EntityFrameworkCore.Triggered.Analyzers.Test;

public class TriggerRegistrationGeneratorTests
{
    private const string Triggers = @"
using EntityFrameworkCore.Triggered;
using EntityFrameworkCore.Triggered.Infrastructure;
using EntityFrameworkCore.Triggered.Transactions;
using Microsoft.Extensions.DependencyInjection;

public class Entity { }

namespace App.Triggers
{
    public class Beta : IBeforeSaveTrigger<Entity>
    {
        public void BeforeSave(ITriggerContext<Entity> context) { }
    }

    internal class Alpha : IAfterSaveTrigger<Entity>
    {
        public void AfterSave(ITriggerContext<Entity> context) { }
    }

    public abstract class AbstractTrigger : IBeforeSaveTrigger<Entity>
    {
        public void BeforeSave(ITriggerContext<Entity> context) { }
    }

    public class Inherited : AbstractTrigger { }

    public class Generic<TEntity> : IBeforeSaveTrigger<TEntity> where TEntity : class
    {
        public void BeforeSave(ITriggerContext<TEntity> context) { }

        public class NestedInGeneric : IBeforeSaveTrigger<Entity>
        {
            public void BeforeSave(ITriggerContext<Entity> context) { }
        }
    }

    public struct StructTrigger : IBeforeSaveTrigger<Entity>
    {
        public void BeforeSave(ITriggerContext<Entity> context) { }
    }

    public class Outer
    {
        internal class Nested : IBeforeSaveTrigger<Entity>
        {
            public void BeforeSave(ITriggerContext<Entity> context) { }
        }
    }

    public class Commit : IBeforeCommitTrigger<Entity>
    {
        public void BeforeCommit(ITriggerContext<Entity> context) { }
    }

    public class NotATrigger { }
}
";

    private const string OptionsBuilderRegistration = Triggers + @"
public static partial class Registration
{
    [GeneratedTriggerRegistration]
    public static partial TriggersContextOptionsBuilder AddAppTriggers(this TriggersContextOptionsBuilder builder);
}";

    private const string TriggerOnAPrivateEntity = @"
public class Container
{
    private class Row { }

    internal class RowTrigger : IBeforeSaveTrigger<Row>
    {
        void IBeforeSaveTrigger<Row>.BeforeSave(ITriggerContext<Row> context) { }
    }
}
";

    // Everything the test process can load, which includes the library the sources compile against.
    private static readonly MetadataReference[] References = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))
        .Split(Path.PathSeparator)
        .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
        .ToArray();

    private static (Compilation Output, ImmutableArray<Diagnostic> Diagnostics) Generate(string source)
    {
        var compilation = CSharpCompilation.Create(
            $"Generated{Guid.NewGuid():N}",
            new[] { CSharpSyntaxTree.ParseText(source) },
            References,
            // MSBuild turns the assembly version unification warnings off for every project too.
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary).WithSpecificDiagnosticOptions(new Dictionary<string, ReportDiagnostic> {
                ["CS1701"] = ReportDiagnostic.Suppress,
                ["CS1702"] = ReportDiagnostic.Suppress
            }));

        CSharpGeneratorDriver
            .Create(new TriggerRegistrationGenerator())
            .RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);

        return (output, diagnostics);
    }

    private static MethodInfo Compile(string source, string typeName, string methodName)
    {
        var (output, diagnostics) = Generate(source);
        Assert.Empty(diagnostics);

        using var stream = new MemoryStream();
        var result = output.Emit(stream);
        Assert.Empty(result.Diagnostics.Where(diagnostic => diagnostic.Severity >= DiagnosticSeverity.Warning));

        return Assembly.Load(stream.ToArray())
            .GetType(typeName, throwOnError: true)
            .GetMethod(methodName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
    }

    private static IEnumerable<(string Type, ServiceLifetime Lifetime)> RegisteredTriggers(MethodInfo registration, object lifetime = null, bool transactionTriggers = false)
    {
        var options = new DbContextOptionsBuilder()
            .UseTriggers(triggerOptions => {
                registration.Invoke(null, lifetime is null ? new object[] { triggerOptions } : new[] { triggerOptions, lifetime });

                if (transactionTriggers)
                {
                    triggerOptions.UseTransactionTriggers();
                }
            })
            .Options;

#pragma warning disable EF1001 // The extension is the only place the registered triggers can be read back.
        return options.FindExtension<TriggersOptionExtension>().Triggers.Select(trigger => (((Type)trigger.typeOrInstance).FullName, trigger.lifetime));
#pragma warning restore EF1001
    }

    [Fact]
    public void RegistersConcreteTriggerClassesInNameOrder()
    {
        var registration = Compile(OptionsBuilderRegistration, "Registration", "AddAppTriggers");

        Assert.Equal(
            new[] {
                ("App.Triggers.Alpha", ServiceLifetime.Scoped),
                ("App.Triggers.Beta", ServiceLifetime.Scoped),
                ("App.Triggers.Commit", ServiceLifetime.Scoped),
                ("App.Triggers.Inherited", ServiceLifetime.Scoped),
                ("App.Triggers.Outer+Nested", ServiceLifetime.Scoped)
            },
            RegisteredTriggers(registration, transactionTriggers: true));
    }

    // As with AddAssemblyTriggers, a class only counts for the trigger types the context has registered.
    [Fact]
    public void WithoutTransactionTriggers_LeavesOutClassesWithOnlyTransactionTriggers()
    {
        var registration = Compile(OptionsBuilderRegistration, "Registration", "AddAppTriggers");

        var triggers = RegisteredTriggers(registration).Select(trigger => trigger.Type);

        Assert.Contains("App.Triggers.Alpha", triggers);
        Assert.DoesNotContain("App.Triggers.Commit", triggers);
    }

    [Fact]
    public void LifetimeParameterWithADefault_Compiles()
    {
        var registration = Compile(Triggers + @"
public static partial class Registration
{
    [GeneratedTriggerRegistration]
    public static partial TriggersContextOptionsBuilder AddAppTriggers(this TriggersContextOptionsBuilder builder, ServiceLifetime lifetime = ServiceLifetime.Transient);
}", "Registration", "AddAppTriggers");

        Assert.NotEmpty(RegisteredTriggers(registration, lifetime: ServiceLifetime.Transient));
    }

    [Fact]
    public void LifetimeParameter_SetsTheLifetime()
    {
        var registration = Compile(Triggers + @"
namespace App
{
    public partial class Host
    {
        internal static partial class Inner
        {
            [GeneratedTriggerRegistration]
            internal static partial void Register(TriggersContextOptionsBuilder builder, ServiceLifetime lifetime);
        }
    }
}", "App.Host+Inner", "Register");

        var triggers = RegisteredTriggers(registration, lifetime: ServiceLifetime.Singleton);

        Assert.NotEmpty(triggers);
        Assert.All(triggers, trigger => Assert.Equal(ServiceLifetime.Singleton, trigger.Lifetime));
    }

    // AddAssemblyTriggers on a service collection doesn't register transaction triggers either.
    [Fact]
    public void OnServiceCollection_LeavesOutClassesWithOnlyTransactionTriggers()
    {
        var registration = Compile(Triggers + @"
public static partial class Registration
{
    [GeneratedTriggerRegistration]
    public static partial IServiceCollection AddAppTriggers(this IServiceCollection services);
}", "Registration", "AddAppTriggers");

        var services = new ServiceCollection();
        registration.Invoke(null, new object[] { services });

        var registered = services.Where(service => service.ImplementationType is not null).Select(service => service.ImplementationType.FullName);
        Assert.Contains("App.Triggers.Alpha", registered);
        Assert.DoesNotContain("App.Triggers.Commit", registered);
    }

    [Fact]
    public void MethodInATypeThatIsNotPartial_IsReported()
    {
        var (_, diagnostics) = Generate(Triggers + @"
public class Startup
{
    static partial class Registration
    {
        [GeneratedTriggerRegistration]
        internal static partial void Register(TriggersContextOptionsBuilder builder);
    }
}");

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("EFCT002", diagnostic.Id);
    }

    [Theory]
    [InlineData("public static void Register(TriggersContextOptionsBuilder builder) { }")]
    [InlineData("public partial void Register(TriggersContextOptionsBuilder builder);")]
    [InlineData("public static partial void Register(string builder);")]
    [InlineData("public static partial void Register(TriggersContextOptionsBuilder builder, int lifetime);")]
    [InlineData("public static partial IServiceCollection Register(TriggersContextOptionsBuilder builder);")]
    [InlineData("public static partial void Register<T>(TriggersContextOptionsBuilder builder);")]
    [InlineData("public static partial void Register(TriggersContextOptionsBuilder? builder);")]
    [InlineData("public static partial ref TriggersContextOptionsBuilder Register(TriggersContextOptionsBuilder builder);")]
    public void MethodThatCannotBeGenerated_IsReported(string declaration)
    {
        var (_, diagnostics) = Generate(Triggers + @"
public partial class Registration
{
    [GeneratedTriggerRegistration]
    " + declaration + @"
}");

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("EFCT002", diagnostic.Id);
    }

    private static void AssertReportedAndLeftOut(Compilation output, ImmutableArray<Diagnostic> diagnostics, string trigger)
    {
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("EFCT003", diagnostic.Id);
        Assert.Contains(trigger, diagnostic.GetMessage());
        Assert.Empty(output.GetDiagnostics().Where(x => x.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public void TriggerGeneratedCodeCannotReach_IsReportedAndLeftOut()
    {
        var (output, diagnostics) = Generate(Triggers + @"
public class Container
{
    private class Hidden : IBeforeSaveTrigger<Entity>
    {
        public void BeforeSave(ITriggerContext<Entity> context) { }
    }
}

public static partial class Registration
{
    [GeneratedTriggerRegistration]
    public static partial void Register(TriggersContextOptionsBuilder builder);
}");

        AssertReportedAndLeftOut(output, diagnostics, "Container.Hidden");
    }

    [Fact]
    public void TriggerOnAnEntityGeneratedCodeCannotReach_IsReportedAndLeftOut()
    {
        var (output, diagnostics) = Generate(Triggers + TriggerOnAPrivateEntity + @"
public static partial class Registration
{
    [GeneratedTriggerRegistration]
    public static partial void Register(IServiceCollection services);
}");

        AssertReportedAndLeftOut(output, diagnostics, "Container.RowTrigger");
    }

    [Fact]
    public void TriggerInATypeThatIsAnErrorToUse_IsReportedAndLeftOut()
    {
        var (output, diagnostics) = Generate(OptionsBuilderRegistration + @"
[System.Obsolete(""Gone"", true)]
public class Gone
{
    public class Inner : IBeforeSaveTrigger<Entity>
    {
        public void BeforeSave(ITriggerContext<Entity> context) { }
    }
}");

        AssertReportedAndLeftOut(output, diagnostics, "Gone.Inner");
    }

    [Fact]
    public void MethodsInPartsOfOneType_ShareItsCandidateList()
    {
        var (output, diagnostics) = Generate(Triggers + @"
public static partial class Registration<T, U>
{
    [GeneratedTriggerRegistration]
    public static partial void First(TriggersContextOptionsBuilder builder);
}

public static partial class Registration<T,U>
{
    [GeneratedTriggerRegistration]
    public static partial void Second(TriggersContextOptionsBuilder builder);
}");

        Assert.Empty(diagnostics);
        Assert.Empty(output.GetDiagnostics().Where(x => x.Severity == DiagnosticSeverity.Error));
    }

    // Only a service collection method writes the entity type into generated code.
    [Fact]
    public void OnOptionsBuilder_TriggerOnAnEntityGeneratedCodeCannotReach_IsRegistered()
    {
        var registration = Compile(OptionsBuilderRegistration + TriggerOnAPrivateEntity, "Registration", "AddAppTriggers");

        Assert.Contains("Container+RowTrigger", RegisteredTriggers(registration).Select(trigger => trigger.Type));
    }

    [Fact]
    public void WithBothKindsOfMethod_TriggerOnAnEntityGeneratedCodeCannotReach_IsLeftOutOfTheServiceCollectionOnly()
    {
        var (output, diagnostics) = Generate(OptionsBuilderRegistration + TriggerOnAPrivateEntity + @"
public static partial class Services
{
    [GeneratedTriggerRegistration]
    public static partial void Register(IServiceCollection services);
}");

        AssertReportedAndLeftOut(output, diagnostics, "Container.RowTrigger' is left out of the generated service collection registration");
    }

    // A service collection has nothing to register for a transaction trigger, reachable or not.
    [Fact]
    public void OnServiceCollection_TransactionTriggerGeneratedCodeCannotReach_IsNotReported()
    {
        var (_, diagnostics) = Generate(Triggers + @"
public class Container
{
    private class HiddenCommit : IBeforeCommitTrigger<Entity>
    {
        public void BeforeCommit(ITriggerContext<Entity> context) { }
    }
}

public static partial class Registration
{
    [GeneratedTriggerRegistration]
    public static partial void Register(IServiceCollection services);
}");

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void ObsoleteOrExperimentalTrigger_IsRegisteredWithoutWarnings()
    {
        var registration = Compile(Triggers + @"
namespace App.Triggers
{
    [System.Obsolete(""Replaced by Beta"")]
    public class Deprecated : IBeforeSaveTrigger<Entity>
    {
        public void BeforeSave(ITriggerContext<Entity> context) { }
    }

    [System.Obsolete(""Replaced by Beta"", DiagnosticId = ""APP001"")]
    public class DeprecatedWithAnId : IBeforeSaveTrigger<Entity>
    {
        public void BeforeSave(ITriggerContext<Entity> context) { }
    }

    [System.Diagnostics.CodeAnalysis.Experimental(""APP002"")]
    public class Trial : IBeforeSaveTrigger<Entity>
    {
        public void BeforeSave(ITriggerContext<Entity> context) { }
    }
}

public static partial class Registration
{
    [GeneratedTriggerRegistration]
    public static partial void @event(TriggersContextOptionsBuilder builder);

    [GeneratedTriggerRegistration]
    public static partial void Register(IServiceCollection services);
}", "Registration", "event");

        var triggers = RegisteredTriggers(registration).Select(trigger => trigger.Type);
        Assert.Contains("App.Triggers.Deprecated", triggers);
        Assert.Contains("App.Triggers.DeprecatedWithAnId", triggers);
        Assert.Contains("App.Triggers.Trial", triggers);
    }

    [Fact]
    public void WithoutARegistrationMethod_GeneratesNothing()
    {
        var (output, diagnostics) = Generate(Triggers);

        Assert.Empty(diagnostics);
        Assert.Single(output.SyntaxTrees);
    }

    // Fails when a trigger interface is added to the library and not to the analyzers' list.
    [Fact]
    public void KnowsEveryTriggerInterfaceInTheLibrary()
    {
        static IEnumerable<string> TriggerInterfaces(Assembly assembly)
            => assembly.GetExportedTypes()
                .Where(type => type.IsInterface && type.Name.Split('`')[0].EndsWith("Trigger", StringComparison.Ordinal))
                .Select(type => type.FullName)
                .OrderBy(name => name, StringComparer.Ordinal);

        Assert.Equal(
            TriggerInterfaces(typeof(IBeforeSaveTrigger<>).Assembly),
            TriggerMapping.SaveTriggerInterfaces.OrderBy(name => name, StringComparer.Ordinal));
        Assert.Equal(
            TriggerInterfaces(typeof(IBeforeCommitTrigger<>).Assembly),
            TriggerMapping.TransactionTriggerInterfaces.OrderBy(name => name, StringComparer.Ordinal));
    }
}
