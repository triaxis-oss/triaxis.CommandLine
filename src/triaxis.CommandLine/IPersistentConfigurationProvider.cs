namespace triaxis.CommandLine;

using Microsoft.Extensions.Configuration;

/// <summary>
/// An <see cref="IConfigurationProvider"/> whose values can be mutated and written
/// back to durable storage. The base package defines only the contract; a concrete
/// writer (JSON, YAML, …) is supplied by the application or a higher-level package.
/// </summary>
/// <remarks>
/// A provider deriving from <see cref="ConfigurationProvider"/> already exposes a
/// matching public <c>Set</c>, so implementing this interface adds only
/// <see cref="Save"/> plus the interface declaration.
/// </remarks>
public interface IPersistentConfigurationProvider : IConfigurationProvider
{
    /// <summary>
    /// Persists the current values to durable storage and raises the provider's
    /// reload token, so a live <see cref="IConfiguration"/> built on top of it
    /// reflects the change.
    /// </summary>
    void Save();
}
