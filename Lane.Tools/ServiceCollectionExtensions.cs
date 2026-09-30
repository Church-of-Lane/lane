using Lane.Core;
using Lane.Core.Presence;
using Lane.Tools.Identity;
using Lane.Tools.Images;
using Lane.Tools.Reading;
using Lane.Tools.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Lane.Tools;

public sealed class ToolsSetupOptions
{
    public BraveSearchOptions  Search   { get; set; } = new();
    public BookOptions         Books    { get; set; } = new();
    public IdentityToolOptions Identity { get; set; } = new();
}

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the built-in abilities and everything they depend on.
    ///
    /// The tools themselves are found by scanning for <c>[LaneTool]</c> — this method only
    /// supplies their dependencies, so a new ability added to this assembly needs no line
    /// here at all.
    /// </summary>
    public static IServiceCollection AddLaneBuiltinTools(
        this IServiceCollection services, ToolsSetupOptions options)
    {
        services.AddSingleton(options.Search);
        services.AddSingleton(options.Books);
        services.AddSingleton(options.Identity);

        services.TryAddSingleton<IPresenceSink, LoggingPresenceSink>();

        services.AddHttpClient<IWebSearch, BraveWebSearch>()
                .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(30));

        services.AddHttpClient(ImageShelf.HttpClientName)
                .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(20))
                .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
                {
                    ConnectCallback          = ImageShelf.ConnectPublicAsync,
                    MaxAutomaticRedirections = 3
                });

        services.AddLaneTools(typeof(ServiceCollectionExtensions).Assembly);

        return services;
    }
}
