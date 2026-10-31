using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.WatchOrder.WebClient;

/// <summary>
/// Registers <see cref="ScriptInjector"/> with the File Transformation plugin at startup, which adds the
/// user page to the web app without modifying jellyfin-web's files. File Transformation is optional:
/// without it the plugin still works, but the page only exists for admins in the dashboard.
/// </summary>
public sealed class FileTransformationRegistrar : IHostedService
{
    /// <summary>Stable id for this plugin's transformation, so re-registering replaces it.</summary>
    private static readonly Guid TransformationId = Guid.Parse("4b0a7c5e-0d0b-4d61-9b4b-7f2c1d9e6a31");

    private readonly ILogger<FileTransformationRegistrar> _logger;

    public FileTransformationRegistrar(ILogger<FileTransformationRegistrar> logger)
    {
        _logger = logger;
    }

    /// <summary>Gets a value indicating whether the File Transformation plugin was found and accepted the registration.</summary>
    public static bool IsRegistered { get; private set; }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            Register();
        }
        catch (Exception ex)
        {
            // Never stop Jellyfin starting because of an optional integration.
            _logger.LogWarning(ex, "Could not register the Watch Order web page with File Transformation");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void Register()
    {
        // File Transformation lives in its own load context, so it is reached by reflection
        // rather than a package reference (this is the approach its README documents).
        var assembly = AssemblyLoadContext.All
            .SelectMany(context => context.Assemblies)
            .FirstOrDefault(a => a.GetName().Name == "Jellyfin.Plugin.FileTransformation");

        var register = assembly?
            .GetType("Jellyfin.Plugin.FileTransformation.PluginInterface")?
            .GetMethod("RegisterTransformation", BindingFlags.Public | BindingFlags.Static);

        if (register is null || register.GetParameters() is not [var payloadParameter])
        {
            _logger.LogInformation(
                "File Transformation plugin not found. Install it to show the Watch orders page to users in the web app");
            return;
        }

        var json = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["id"] = TransformationId.ToString(),
            // Must be exactly "index.html": File Transformation groups transformations by pattern text and,
            // when a group with that exact name exists (other plugins use it), runs only that group.
            ["fileNamePattern"] = "index.html",
            ["callbackAssembly"] = typeof(ScriptInjector).Assembly.FullName!,
            ["callbackClass"] = typeof(ScriptInjector).FullName!,
            ["callbackMethod"] = nameof(ScriptInjector.Transform)
        });

        // The method takes a Newtonsoft JObject from File Transformation's own copy of Newtonsoft,
        // so build it with that type's Parse method instead of referencing Newtonsoft here.
        var parse = payloadParameter.ParameterType.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static, new[] { typeof(string) });
        if (parse is null)
        {
            _logger.LogWarning("Unrecognised File Transformation version; the Watch orders page won't be added to the web app");
            return;
        }

        register.Invoke(null, new[] { parse.Invoke(null, new object[] { json }) });
        IsRegistered = true;
        _logger.LogInformation("Watch orders page registered with File Transformation");
    }
}
