using System;

namespace EntityFrameworkCore.Triggered
{
    /// <summary>
    /// Marks a partial method that the source generator implements to register every trigger class in the assembly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The method is static and partial. It takes a <c>TriggersContextOptionsBuilder</c> or an <c>IServiceCollection</c>,
    /// optionally followed by a <c>ServiceLifetime</c>, and returns its first parameter or nothing. Triggers are
    /// scoped when the method takes no lifetime.
    /// </para>
    /// <para>
    /// A trigger class is a concrete, non-generic class that implements one of the library's trigger interfaces.
    /// Classes are listed in ordinal order of their full names. As with <c>AddAssemblyTriggers</c>, a context
    /// registers the ones that implement a trigger type it uses.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// static partial class TriggerRegistration
    /// {
    ///     [GeneratedTriggerRegistration]
    ///     public static partial TriggersContextOptionsBuilder AddApplicationTriggers(this TriggersContextOptionsBuilder builder);
    /// }
    /// </code>
    /// </example>
    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    public sealed class GeneratedTriggerRegistrationAttribute : Attribute
    {
    }
}
