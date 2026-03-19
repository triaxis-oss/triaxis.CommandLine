namespace triaxis.CommandLine;

using System;

/// <summary>
/// Holds a generated command registration delegate set by source-generated module initializers.
/// When present, <see cref="ToolBuilderExtensions.AddCommandsFromAssembly(IToolBuilder)"/> uses
/// the generated registration instead of reflection-based discovery.
/// </summary>
public static class GeneratedCommandRegistration
{
    /// <summary>
    /// Registers a generated command registration action for the given assembly.
    /// Called from source-generated module initializers.
    /// </summary>
    public static void Register(string assemblyName, Action<IToolBuilder> registration)
    {
        Registrations[assemblyName] = registration;
    }

    /// <summary>
    /// Tries to get a generated registration for the given assembly.
    /// </summary>
    public static bool TryGet(string assemblyName, out Action<IToolBuilder> registration)
    {
        return Registrations.TryGetValue(assemblyName, out registration);
    }

    private static readonly Dictionary<string, Action<IToolBuilder>> Registrations = new();
}
