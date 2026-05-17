namespace triaxis.CommandLine;

/// <summary>
/// Marks a static method as a builder-customization hook invoked by the
/// source-generated entry point, so projects can register services or customize
/// the host/builder without hand-writing a <c>Main</c>.
/// </summary>
/// <remarks>
/// <para>
/// The method must be <c>static</c>, return <c>void</c>, and take any combination
/// (each at most once, in any order) of <see cref="IToolBuilder"/>,
/// <see cref="Microsoft.Extensions.Hosting.IHostBuilder"/>, and
/// <see cref="Microsoft.Extensions.DependencyInjection.IServiceCollection"/> — the
/// same signatures a per-command <c>Configure</c> method accepts. The generator
/// folds every matching method into the generated entry point in a stable order
/// (ordinal by declaring type's fully-qualified name, then by method name).
/// Non-matching shapes are ignored, mirroring <see cref="ConfigureServicesAttribute"/>.
/// </para>
/// <para>
/// Because the hook owns the opinionated builder setup, its presence makes the
/// generated entry point <b>skip the logging and default-configuration helpers</b>
/// (<c>UseSerilog</c>, <c>UseVerbosityOptions</c>, <c>UseDefaultConfiguration</c>).
/// Command discovery and <c>UseObjectOutput</c> are structural (driven by the
/// discovered commands and their return types) and are still emitted. A hook
/// restores logging/configuration with <c>builder.UseDefaultLogging()</c> (the
/// combined <c>UseSerilog</c> + <c>UseVerbosityOptions</c> one-liner) and
/// <c>builder.UseDefaultConfiguration()</c> rather than <c>UseDefaults()</c>, which
/// would register commands a second time. The simpler
/// <see cref="ConfigureServicesAttribute">[ConfigureServices]</see> hook keeps the
/// default stack and is the better choice when you only need to register services.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// public static class Startup
/// {
///     [Configure]
///     public static void Setup(IToolBuilder builder, IServiceCollection services)
///     {
///         builder.UseDefaultLogging();
///         builder.UseDefaultConfiguration();
///         services.AddSingleton&lt;IMyService, MyService&gt;();
///     }
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class ConfigureAttribute : Attribute
{
}
