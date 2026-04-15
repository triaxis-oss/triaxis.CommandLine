namespace triaxis.CommandLine;

/// <summary>
/// Marks a static method as a service-registration hook to be invoked by the
/// source-generated entry point. The method must be static and take a single
/// <see cref="Microsoft.Extensions.DependencyInjection.IServiceCollection"/>
/// parameter.
/// </summary>
/// <remarks>
/// The generator emits a <c>.ConfigureServices(TypeName.MethodName)</c> call in the
/// generated <c>Main</c> chain for each marked method, letting projects register
/// services without having to hand-write an entry point just to call
/// <see cref="IToolBuilder.ConfigureServices"/>.
/// Multiple methods across the assembly are supported and invoked in a stable
/// order (ordinal by declaring type's fully qualified name, then by method name).
/// </remarks>
/// <example>
/// <code>
/// public static class Startup
/// {
///     [ConfigureServices]
///     public static void Register(IServiceCollection services)
///     {
///         services.AddSingleton&lt;IMyService, MyService&gt;();
///     }
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class ConfigureServicesAttribute : Attribute
{
}
