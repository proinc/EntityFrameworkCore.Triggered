using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace EntityFrameworkCore.Triggered.Analyzers;

/// <summary>
/// Implements the partial methods marked with <c>[GeneratedTriggerRegistration]</c>, so an assembly's triggers are
/// listed at compile time instead of found by reflection when the application starts.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class TriggerRegistrationGenerator : IIncrementalGenerator
{
    private const string AttributeMetadataName = "EntityFrameworkCore.Triggered.GeneratedTriggerRegistrationAttribute";
    private const string OptionsBuilderType = "global::EntityFrameworkCore.Triggered.Infrastructure.TriggersContextOptionsBuilder";
    private const string ServiceCollectionType = "global::Microsoft.Extensions.DependencyInjection.IServiceCollection";
    private const string ServiceLifetimeType = "global::Microsoft.Extensions.DependencyInjection.ServiceLifetime";
    private const string ServiceDescriptorType = "global::Microsoft.Extensions.DependencyInjection.ServiceDescriptor";
    private const string DescriptorExtensionsType = "global::Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions";
    private const string CandidatesField = "__triggerCandidates";

    private static readonly SymbolDisplayFormat NamespaceFormat = SymbolDisplayFormat.FullyQualifiedFormat
        .WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var methods = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                AttributeMetadataName,
                static (_, _) => true,
                static (attributeContext, _) => ReadMethod(attributeContext))
            .Collect();

        // The package's analyzers reach every project that references it, so a project that marks no method
        // shouldn't pay for looking through its classes. One that does pays on each compilation either way: a
        // syntax provider reruns its transform for every class too, and measured no faster.
        var triggers = methods
            .Combine(context.CompilationProvider)
            .Select(static (source, cancellationToken) => source.Left.Any(method => method.Signature is not null)
                ? FindTriggers(source.Right, cancellationToken)
                : default);

        context.RegisterSourceOutput(methods.Combine(triggers), static (output, source) => Generate(output, source.Left, source.Right));
    }

    private static RegistrationMethod ReadMethod(GeneratorAttributeSyntaxContext context)
    {
        // The attribute can also sit on a local function or an accessor, neither of which can be partial.
        if (context.TargetNode is not MethodDeclarationSyntax syntax)
        {
            var identifier = (context.TargetNode as LocalFunctionStatementSyntax)?.Identifier.GetLocation();

            return new RegistrationMethod(context.TargetSymbol.Name, Signature: null, identifier ?? context.TargetNode.GetLocation());
        }

        var method = (IMethodSymbol)context.TargetSymbol;

        var firstParameterType = method.Parameters.Length > 0 ? FullName(method.Parameters[0].Type) : null;
        var containingClass = syntax.Parent as ClassDeclarationSyntax;

        var isValid = firstParameterType is OptionsBuilderType or ServiceCollectionType
            // The implementation goes in another part of the class, in another file. Only a top-level, non-generic
            // class is accepted: an extension method needs one anyway, and the other part is then just its name.
            && containingClass is { Parent: BaseNamespaceDeclarationSyntax or CompilationUnitSyntax, TypeParameterList: null }
            && containingClass.Modifiers.Any(SyntaxKind.PartialKeyword)
            && !method.ContainingType.IsFileLocal
            && method.IsStatic
            && method.IsPartialDefinition
            && method.PartialImplementationPart is null
            && !method.IsGenericMethod
            && method.Parameters.All(parameter => parameter.RefKind == RefKind.None)
            && (method.Parameters.Length == 1 || (method.Parameters.Length == 2 && FullName(method.Parameters[1].Type) == ServiceLifetimeType))
            && (method.ReturnsVoid || FullName(method.ReturnType) == firstParameterType)
            && method.RefKind == RefKind.None
            // The implementation is written without annotations, and the compiler wants the two parts to agree.
            && method.Parameters[0].NullableAnnotation != NullableAnnotation.Annotated
            && method.ReturnNullableAnnotation != NullableAnnotation.Annotated;

        // The name comes from syntax so that an escaped keyword keeps its @.
        var name = syntax.Identifier.Text;

        if (!isValid)
        {
            return new RegistrationMethod(name, Signature: null, syntax.Identifier.GetLocation());
        }

        var signature = new MethodSignature(
            FullName(method.ContainingType),
            method.ContainingNamespace.IsGlobalNamespace ? null : method.ContainingNamespace.ToDisplayString(NamespaceFormat),
            containingClass!.Identifier.Text,
            string.Join(" ", syntax.Modifiers.Select(modifier => modifier.Text)),
            firstParameterType!,
            method.ReturnsVoid,
            method.IsExtensionMethod,
            syntax.ParameterList.Parameters[0].Identifier.Text,
            method.Parameters.Length == 2 ? syntax.ParameterList.Parameters[1].Identifier.Text : null);

        return new RegistrationMethod(name, signature, Location: null);
    }

    private static EquatableArray<TriggerClass> FindTriggers(Compilation compilation, CancellationToken cancellationToken)
    {
        var triggers = ImmutableArray.CreateBuilder<TriggerClass>();

        void Visit(INamespaceOrTypeSymbol container)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Asking a type for its nested types alone keeps the compiler from binding its other members.
            var members = container is INamespaceSymbol @namespace ? @namespace.GetMembers() : container.GetTypeMembers();

            foreach (var member in members)
            {
                if (member is INamedTypeSymbol type && ReadTrigger(type, compilation) is { } trigger)
                {
                    triggers.Add(trigger);
                }

                Visit(member);
            }
        }

        Visit(compilation.Assembly.GlobalNamespace);
        triggers.Sort(static (left, right) => string.CompareOrdinal(left.SortName, right.SortName));

        return new EquatableArray<TriggerClass>(triggers.ToImmutable());
    }

    private static TriggerClass? ReadTrigger(INamedTypeSymbol type, Compilation compilation)
    {
        // Generic classes are left out: the runtime scan can't register an open generic type either.
        if (type is not { TypeKind: TypeKind.Class, IsAbstract: false, IsGenericType: false })
        {
            return null;
        }

        ImmutableArray<string>.Builder? saveTriggerInterfaces = null;
        var implementsTransactionTrigger = false;
        var areInterfacesAccessible = true;

        foreach (var @interface in type.AllInterfaces)
        {
            if (!TriggerMapping.HasTriggerInterfaceName(@interface))
            {
                continue;
            }

            var name = TriggerMapping.MetadataName(@interface);

            if (TriggerMapping.SaveTriggerInterfaces.Contains(name))
            {
                (saveTriggerInterfaces ??= ImmutableArray.CreateBuilder<string>()).Add(FullName(@interface));

                // A service collection registers the interface itself, entity type included.
                areInterfacesAccessible &= CanName(@interface, compilation);
            }
            else if (TriggerMapping.TransactionTriggerInterfaces.Contains(name))
            {
                implementsTransactionTrigger = true;
            }
        }

        if (saveTriggerInterfaces is null && !implementsTransactionTrigger)
        {
            return null;
        }

        var isAccessible = CanName(type, compilation);
        var canNameInterfacesToo = isAccessible && areInterfacesAccessible;

        return new TriggerClass(
            FullName(type),
            new EquatableArray<string>(saveTriggerInterfaces?.ToImmutable() ?? ImmutableArray<string>.Empty),
            isAccessible,
            canNameInterfacesToo,
            canNameInterfacesToo ? null : type.Locations.FirstOrDefault(location => location.IsInSource) ?? Location.None);
    }

    private static string FullName(ITypeSymbol type) => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    private static bool IsObsoleteError(INamedTypeSymbol type)
        => type.GetAttributes().Any(attribute =>
            attribute.AttributeClass?.ToDisplayString() == "System.ObsoleteAttribute"
            && attribute.ConstructorArguments.Length == 2
            && attribute.ConstructorArguments[1].Value is true);

    // Whether generated code can name the type. Reflection finds types that code can't: ones that are private,
    // local to a file, internal to another assembly, or an error to use.
    private static bool CanName(ITypeSymbol type, Compilation compilation)
    {
        if (type is IArrayTypeSymbol array)
        {
            return CanName(array.ElementType, compilation);
        }

        if (type is not INamedTypeSymbol { TypeKind: not TypeKind.Error } named)
        {
            return true;
        }

        if (!compilation.IsSymbolAccessibleWithin(named, compilation.Assembly))
        {
            return false;
        }

        // The rest holds for the type and for each type it's nested in.
        for (INamedTypeSymbol? current = named; current is not null; current = current.ContainingType)
        {
            if (current.IsFileLocal || IsObsoleteError(current) || !current.TypeArguments.All(argument => CanName(argument, compilation)))
            {
                return false;
            }
        }

        return true;
    }

    private static void Generate(SourceProductionContext context, ImmutableArray<RegistrationMethod> methods, EquatableArray<TriggerClass> triggers)
    {
        foreach (var method in methods)
        {
            if (method.Location is not null)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    DiagnosticDescriptors.EFCT002_InvalidRegistrationMethod,
                    method.Location,
                    // A lambda can carry the attribute too, and has no name.
                    method.Name.Length > 0 ? $"'{method.Name}'" : "A lambda"));
            }
        }

        var validMethods = methods.Where(method => method.Signature is not null).Select(method => (method.Name, Signature: method.Signature!)).ToList();
        if (validMethods.Count == 0)
        {
            return;
        }

        var hasOptionsBuilderMethod = validMethods.Any(method => method.Signature.FirstParameterType == OptionsBuilderType);
        var hasServiceCollectionMethod = validMethods.Any(method => method.Signature.FirstParameterType == ServiceCollectionType);

        foreach (var trigger in triggers)
        {
            // An options builder method names the class. A service collection method names its save trigger
            // interfaces too, and has nothing to register for a class without any.
            var leftOutOfOptionsBuilder = hasOptionsBuilderMethod && !trigger.IsAccessible;
            var leftOutOfServiceCollection = hasServiceCollectionMethod
                && trigger.SaveTriggerInterfaces.Count > 0
                && !trigger.AreInterfacesAccessible;

            if (leftOutOfOptionsBuilder || leftOutOfServiceCollection)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    DiagnosticDescriptors.EFCT003_InaccessibleTrigger,
                    trigger.Location,
                    trigger.FullName.Substring("global::".Length),
                    hasOptionsBuilderMethod && trigger.IsAccessible ? "the generated service collection registration" : "the generated registration"));
            }
        }

        var accessibleTriggers = triggers.Where(trigger => trigger.IsAccessible).ToList();

        var source = new StringBuilder();
        source.AppendLine("// <auto-generated/>");
        // Naming a trigger marked obsolete or experimental is reported in this file, where its author can't suppress it.
        source.AppendLine("#pragma warning disable");

        // Methods that share a class share one declaration of it, and one candidate list.
        foreach (var group in validMethods.GroupBy(method => method.Signature.ContainingType))
        {
            source.AppendLine();
            AppendClass(source, group.ToList(), accessibleTriggers);
        }

        context.AddSource("TriggerRegistration.g.cs", SourceText.From(source.ToString(), Encoding.UTF8));
    }

    private static void AppendClass(StringBuilder source, List<(string Name, MethodSignature Signature)> methods, List<TriggerClass> triggers)
    {
        var (@namespace, className) = (methods[0].Signature.Namespace, methods[0].Signature.ClassName);
        var depth = 0;

        void Line(string text) => source.Append(' ', depth * 4).AppendLine(text);

        void Open(string declaration)
        {
            Line(declaration);
            Line("{");
            depth++;
        }

        void Close(string suffix = "")
        {
            depth--;
            Line("}" + suffix);
        }

        if (@namespace is not null)
        {
            Open($"namespace {@namespace}");
        }

        Open($"partial class {className}");

        if (methods.Any(method => method.Signature.FirstParameterType == OptionsBuilderType))
        {
            // One array for every call, so options built again for each scope compare equal without a walk.
            Open($"private static readonly global::System.Type[] {CandidatesField} = new global::System.Type[]");
            foreach (var trigger in triggers)
            {
                Line($"typeof({trigger.FullName}),");
            }

            Close(";");
            source.AppendLine();
        }

        foreach (var (name, signature) in methods)
        {
            var returnType = signature.ReturnsVoid ? "void" : signature.FirstParameterType;
            var parameters = $"{(signature.IsExtension ? "this " : "")}{signature.FirstParameterType} {signature.FirstParameterName}";
            if (signature.LifetimeParameterName is not null)
            {
                parameters += $", {ServiceLifetimeType} {signature.LifetimeParameterName}";
            }

            var lifetime = signature.LifetimeParameterName ?? $"{ServiceLifetimeType}.Scoped";

            Open($"{signature.Modifiers} {returnType} {name}({parameters})");

            if (signature.FirstParameterType == OptionsBuilderType)
            {
                Line($"{signature.FirstParameterName}.AddTriggerCandidates({CandidatesField}, {lifetime});");
            }
            else
            {
                AppendServiceRegistrations(Line, signature.FirstParameterName, lifetime, triggers);
            }

            if (!signature.ReturnsVoid)
            {
                Line($"return {signature.FirstParameterName};");
            }

            Close();
        }

        while (depth > 0)
        {
            Close();
        }
    }

    // What AddAssemblyTriggers does on a service collection: the class under its own type, and each save trigger
    // interface resolving to it. Transaction triggers aren't registered there either.
    private static void AppendServiceRegistrations(Action<string> line, string services, string lifetime, List<TriggerClass> triggers)
    {
        foreach (var trigger in triggers.Where(trigger => trigger.AreInterfacesAccessible && trigger.SaveTriggerInterfaces.Count > 0))
        {
            line($"{DescriptorExtensionsType}.TryAdd({services}, new {ServiceDescriptorType}(typeof({trigger.FullName}), typeof({trigger.FullName}), {lifetime}));");

            foreach (var @interface in trigger.SaveTriggerInterfaces)
            {
                line($"{DescriptorExtensionsType}.Add({services}, new {ServiceDescriptorType}(typeof({@interface}), serviceProvider => global::Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService(serviceProvider, typeof({trigger.FullName})), {ServiceLifetimeType}.Transient));");
            }
        }
    }

    // Signature is null when the method can't be generated. Location is set only then, because one on every method
    // would change with each edit above it and defeat the generator's cache. It keeps its syntax tree so that a
    // pragma around the declaration can silence the diagnostic.
    private sealed record RegistrationMethod(string Name, MethodSignature? Signature, Location? Location);

    private sealed record MethodSignature(
        string ContainingType,
        string? Namespace,
        string ClassName,
        string Modifiers,
        string FirstParameterType,
        bool ReturnsVoid,
        bool IsExtension,
        string FirstParameterName,
        string? LifetimeParameterName);

    // IsAccessible is whether generated code can name the class, and AreInterfacesAccessible whether it can name the
    // class's trigger interfaces as well. Location is set only when it can't do both, for the diagnostic.
    private sealed record TriggerClass(
        string FullName,
        EquatableArray<string> SaveTriggerInterfaces,
        bool IsAccessible,
        bool AreInterfacesAccessible,
        Location? Location)
    {
        // The name as metadata has it. FullName escapes a keyword with an @, which would sort it first.
        public string SortName => FullName.Replace("@", "");
    }
}
