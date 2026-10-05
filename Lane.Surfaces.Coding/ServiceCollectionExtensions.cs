using Lane.Core;
using Lane.Core.Memory;
using Lane.Surfaces.Coding.Git;
using Lane.Surfaces.Coding.Permissions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lane.Surfaces.Coding;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the coding surfaces, the permission server Claude Code asks through, and the
    /// coding tools. Needs an <see cref="IKeyValueStore"/>, so call it with memory configured.
    /// </summary>
    public static IServiceCollection AddLaneCoding(this IServiceCollection services, CodingOptions options)
    {
        services.AddSingleton(options);

        services.AddSingleton(sp => new PermissionBroker(options.PermissionTimeout, sp.GetService<TimeProvider>()));

        services.AddSingleton(sp => new PermissionMcpServer(
            sp.GetRequiredService<PermissionBroker>(),
            options.PermissionPort,
            sp.GetRequiredService<ILogger<PermissionMcpServer>>()));
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<PermissionMcpServer>());

        services.AddHttpClient(GitHubClient.HttpClientName)
                .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(30));

        services.AddSingleton(sp => new CodingWorkspaceManager(
            options,
            sp,
            sp.GetRequiredService<IKeyValueStore>(),
            sp.GetRequiredService<PermissionBroker>(),
            sp.GetRequiredService<PermissionMcpServer>(),
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<ILoggerFactory>()));
        services.AddSingleton<ICodingWorkspaces>(sp => sp.GetRequiredService<CodingWorkspaceManager>());
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<CodingWorkspaceManager>());

        services.AddLaneTools(typeof(ServiceCollectionExtensions).Assembly);

        return services;
    }
}
