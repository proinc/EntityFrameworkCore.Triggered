using Microsoft.CodeAnalysis;

namespace EntityFrameworkCore.Triggered.Analyzers;

internal static class DiagnosticDescriptors
{
    public static readonly DiagnosticDescriptor EFCT001_OldTriggerSignature = new(
        id: "EFCT001",
        title: "Trigger method has old async signature",
        messageFormat: "Class '{0}' implements '{1}' with the old async method signature. Migrate to '{2}' (async) or update to the new sync signature.",
        category: "Migration",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The trigger interface was split into sync and async variants. Methods returning Task with a CancellationToken parameter should use the async interface variant, or be converted to the new sync signature.");

    public static readonly DiagnosticDescriptor EFCT002_InvalidRegistrationMethod = new(
        id: "EFCT002",
        title: "Method can't be generated as a trigger registration",
        messageFormat: "{0} must be a static, non-generic partial method without a body, in a partial type. It takes a TriggersContextOptionsBuilder or an IServiceCollection, not nullable and by value, optionally followed by a ServiceLifetime, and returns its first parameter or void.",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor EFCT003_InaccessibleTrigger = new(
        id: "EFCT003",
        title: "Trigger is left out of the generated registration",
        messageFormat: "Trigger '{0}' is left out of {1} because generated code can't name it or an entity type it handles. Code can't name a type that is private, local to a file, internal to another assembly, or obsolete as an error. Change that, or register the trigger yourself and suppress this warning.",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);
}
