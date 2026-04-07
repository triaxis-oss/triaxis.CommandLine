namespace triaxis.CommandLine;

/// <summary>
/// Holds generated command registration delegates set by source-generated module initializers.
/// When present, <see cref="ToolBuilderExtensions.AddCommandsFromAssembly(IToolBuilder)"/>
/// uses the generated registration instead of reflection-based discovery.
/// </summary>
public static class GeneratedCommandRegistration
{
    private static readonly Dictionary<string, Func<Func<IServiceProvider>, CommandTreeNode>> s_registrations = new();

    /// <summary>
    /// Registers a generated command tree factory for the given assembly.
    /// Called from source-generated module initializers.
    /// </summary>
    public static void Register(string assemblyName, Func<Func<IServiceProvider>, CommandTreeNode> factory)
    {
        s_registrations[assemblyName] = factory;
    }

    /// <summary>
    /// Tries to get a generated registration for the given assembly.
    /// </summary>
    public static bool TryGet(string assemblyName, out Func<Func<IServiceProvider>, CommandTreeNode> factory)
    {
        return s_registrations.TryGetValue(assemblyName, out factory!);
    }
}
