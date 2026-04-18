using Microsoft.AspNetCore.Builder;

namespace AdaptiveApi.Core.Plugins;

/// Extension point for out-of-tree features (SaaS, billing, SCIM, …).
/// Plugins are discovered by scanning assemblies in the app's base directory at startup.
/// Any assembly that ships a public parameterless class implementing this interface
/// is picked up; its `ConfigureServices` runs before `builder.Build()` and its `Map`
/// runs afterwards.
///
/// Keep plugin implementations minimal — they should delegate to real classes they
/// own, not inline behavior here. The public host never ships an IWebPlugin itself.
public interface IWebPlugin
{
    /// Human-readable identifier, surfaced in startup logs.
    string Name { get; }

    /// Register services. Called during host build; `builder.Services` and
    /// `builder.Configuration` are both available.
    void ConfigureServices(WebApplicationBuilder builder);

    /// Map endpoints and middleware. Called after `builder.Build()`.
    void Map(WebApplication app);
}
