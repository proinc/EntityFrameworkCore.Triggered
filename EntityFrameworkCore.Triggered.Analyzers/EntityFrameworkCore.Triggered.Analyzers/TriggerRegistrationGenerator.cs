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
                static (node, _) => node is MethodDeclarationSyntax,
                static (attributeContext, _) => ReadMethod(attributeContext))
            .Collect();

        // The package's analyzers reach every project that references it, so a project that marks no method
        // shouldn't pay for looking through its classes.
        var triggers = methods
            .Combine(context.CompilationProvider)
            .Select(static (source, cancellationToken) => source.Left.Any(method => method.Signature is not null)
                ? FindTriggers(source.Right.Assembly.GlobalNamespace, cancellationToken)
                : default);

        context.RegisterSourceOutput(methods.Combine(triggers), static (output, source) => Generate(output, source.Left, source.Right));
    }

    private static RegistrationMethod ReadMethod(GeneratorAttributeSyntaxContext context)
    {
        var syntax = (MethodDeclarationSyntax)context.TargetNode;
        var method = (IMethodSymbol)context.TargetSymbol;

        var firstParameterType = method.Parameters.Length > 0 ? FullName(method.Parameters[0].Type) : null;
        var containingTypeDeclarations = syntax.Ancestors().OfType<TypeDeclarationSyntax>().Reverse().ToList();

        var isValid = firstParameterType is OptionsBuilderType or ServiceCollectionType
            // The implementation goes in another file, in another part of each containing type.
            && containingTypeDeclarations.All(type => type.Modifiers.Any(SyntaxKind.PartialKeyword))
            && !method.ContainingType.IsFileLocal
            && method.IsStatic
            && method.IsPartialDefinition
            && !method.IsGenericMethod
            && method.Parameters.All(parameter => parameter.RefKind == RefKind.None)
            && (method.Parameters.Length == 1 || (method.Parameters.Length == 2 && FullName(method.Parameters[1].Type) == ServiceLifetimeType))
            && (method.ReturnsVoid || FullName(method.ReturnType) == firstParameterType)
            // The implementation is written without annotations, and the compiler wants the two parts to agree.
            && method.Parameters[0].NullableAnnotation != NullableAnnotation.Annotated
            && method.ReturnNullableAnnotation != NullableAnnotation.Annotated;

        // The name comes from syntax so that an escaped keyword keeps its @.
        var name = syntax.Identifier.Text;

        if (!isValid)
        {
            return new RegistrationMethod(name, Signature: null, syntax.Identifier.GetLocation());
        }

        var containingTypes = containingTypeDeclarations
            .Select(type => {
                var keyword = type is RecordDeclarationSyntax record
                    ? $"{record.Keyword.Text} {record.ClassOrStructKeyword.Text}".TrimEnd()
                    : type.Keyword.Text;

                return $"partial {keyword} {type.Identifier.Text}{type.TypeParameterList}";
            })
            .ToImmutableArray();

        var signature = new MethodSignature(
            FullName(method.ContainingType),
            method.ContainingNamespace.IsGlobalNamespace ? null : method.ContainingNamespace.ToDisplayString(NamespaceFormat),
            new EquatableArray<string>(containingTypes),
            string.Join(" ", syntax.Modifiers.Select(modifier => modifier.Text)),
            firstParameterType!,
            method.ReturnsVoid,
            method.IsExtensionMethod,
            syntax.ParameterList.Parameters[0].Identifier.Text,
            method.Parameters.Length == 2 ? syntax.ParameterList.Parameters[1].Identifier.Text : null);

        return new RegistrationMethod(name, signature, Location: null);
    }

    private static EquatableArray<TriggerClass> FindTriggers(INamespaceSymbol globalNamespace, CancellationToken cancellationToken)
    {
        var triggers = ImmutableArray.CreateBuilder<TriggerClass>();

        void Visit(INamespaceOrTypeSymbol container)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Asking a type for its nested types alone keeps the compiler from binding its other members.
            var members = container is INamespaceSymbol @namespace ? @namespace.GetMembers() : container.GetTypeMembers();

            foreach (var member in members)
            {
                if (member is INamedTypeSymbol type && ReadTrigger(type) is { } trigger)
                {
                    triggers.Add(trigger);
                }

                Visit(member);
            }
        }

        Visit(globalNamespace);
        triggers.Sort(static (left, right) => string.CompareOrdinal(left.FullName, right.FullName));

        return new EquatableArray<TriggerClass>(triggers.ToImmutable());
    }

    private static TriggerClass? ReadTrigger(INamedTypeSymbol type)
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
                areInterfacesAccessible &= IsAccessible(@interface);
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

        // Code can't name a class whose use is an error, though reflection can still find it.
        var isAccessible = IsAccessible(type) && !type.GetAttributes().Any(attribute =>
            attribute.AttributeClass?.ToDisplayString() == "System.ObsoleteAttribute"
            && attribute.ConstructorArguments.Length == 2
            && attribute.ConstructorArguments[1].Value is true);

        return new TriggerClass(
            FullName(type),
            new EquatableArray<string>(saveTriggerInterfaces?.ToImmutable() ?? ImmutableArray<string>.Empty),
            isAccessible,
            areInterfacesAccessible,
            isAccessible && areInterfacesAccessible ? null : type.Locations.FirstOrDefault(location => location.IsInSource) ?? Location.None);
    }

    private static string FullName(ITypeSymbol type) => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    // Whether generated code can name the type. It sits in the same assembly, so internal is enough at every
    // level of nesting.
    private static bool IsAccessible(ITypeSymbol type)
    {
        if (type is IArrayTypeSymbol array)
        {
            return IsAccessible(array.ElementType);
        }

        if (type is not INamedTypeSymbol named)
        {
            return true;
        }

        for (INamedTypeSymbol? current = named; current is not null; current = current.ContainingType)
        {
            if (current.IsFileLocal
                || current.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal)
                || !current.TypeArguments.All(IsAccessible))
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
                context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.EFCT002_InvalidRegistrationMethod, method.Location, method.Name));
            }
        }

        var validMethods = methods.Where(method => method.Signature is not null).Select(method => (method.Name, Signature: method.Signature!)).ToList();
        if (validMethods.Count == 0)
        {
            return;
        }

        // Only a service collection method names the trigger interfaces, so only then does it matter whether
        // generated code can reach them.
        var namesInterfaces = validMethods.Any(method => method.Signature.FirstParameterType == ServiceCollectionType);

        foreach (var trigger in triggers)
        {
            if (!trigger.IsAccessible || (namesInterfaces && !trigger.AreInterfacesAccessible))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    DiagnosticDescriptors.EFCT003_InaccessibleTrigger,
                    trigger.Location,
                    trigger.FullName.Substring("global::".Length)));
            }
        }

        var accessibleTriggers = triggers.Where(trigger => trigger.IsAccessible).ToList();

        var source = new StringBuilder();
        source.AppendLine("// <auto-generated/>");
        source.AppendLine("#nullable enable");
        // Naming an obsolete trigger warns in this file, where its author can't suppress it.
        source.AppendLine("#pragma warning disable CS0612, CS0618");

        // Methods that share a containing type share one declaration of it, and one candidate list.
        foreach (var group in validMethods.GroupBy(method => method.Signature.ContainingType))
        {
            source.AppendLine();
            AppendContainingType(source, group.ToList(), accessibleTriggers);
        }

        context.AddSource("TriggerRegistration.g.cs", SourceText.From(source.ToString(), Encoding.UTF8));
    }

    private static void AppendContainingType(StringBuilder source, List<(string Name, MethodSignature Signature)> methods, List<TriggerClass> triggers)
    {
        var (@namespace, containingTypes) = (methods[0].Signature.Namespace, methods[0].Signature.ContainingTypes);
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

        foreach (var containingType in containingTypes)
        {
            Open(containingType);
        }

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
                line($"{DescriptorExtensionsType}.Add({services}, new {ServiceDescriptorType}(typeof({@interface}), static serviceProvider => global::Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService(serviceProvider, typeof({trigger.FullName})), {ServiceLifetimeType}.Transient));");
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
        EquatableArray<string> ContainingTypes,
        string Modifiers,
        string FirstParameterType,
        bool ReturnsVoid,
        bool IsExtension,
        string FirstParameterName,
        string? LifetimeParameterName);

    // Location is set only when generated code can't reach the class or one of its interfaces, for the diagnostic.
    private sealed record TriggerClass(
        string FullName,
        EquatableArray<string> SaveTriggerInterfaces,
        bool IsAccessible,
        bool AreInterfacesAccessible,
        Location? Location);
}
