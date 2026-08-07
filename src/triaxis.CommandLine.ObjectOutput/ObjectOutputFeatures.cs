namespace triaxis.CommandLine.ObjectOutput;

/// <summary>
/// Trimming feature switches for the output pipeline.
/// </summary>
static class ObjectOutputFeatures
{
    internal const string ReflectionFallbackSwitch = "triaxis.CommandLine.ObjectOutput.EnableReflectionFallback";

    /// <summary>
    /// Whether a type without a generated descriptor may be described by reflecting over
    /// it at run time. On by default.
    /// </summary>
    /// <remarks>
    /// Merely referencing the reflective path keeps it alive: the trimmer cannot prove
    /// the generated lookup always hits, so it preserves the branch and warns. Setting
    /// <c>EnableObjectOutputReflectionFallback=false</c> lets
    /// <c>ILLink.Substitutions.xml</c> stub this to <see langword="false"/>, which makes
    /// the branch dead and removes the reflective code along with its warnings.
    /// </remarks>
    public static bool ReflectionFallbackEnabled
        => !AppContext.TryGetSwitch(ReflectionFallbackSwitch, out var enabled) || enabled;
}
