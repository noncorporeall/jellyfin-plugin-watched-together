using System.Reflection;
using System.Runtime.Loader;
using Jellyfin.Plugin.WatchedTogether.Helpers;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace Jellyfin.Plugin.WatchedTogether.Services;

/// <summary>
/// Talks to the Home Screen Sections and File Transformation plugins.
/// Both expose static "PluginInterface" classes that must be reached by reflection, because
/// Jellyfin loads each plugin into its own assembly load context.
/// </summary>
internal static class IntegrationRegistrar
{
    /// <summary>Stable id for our index.html transformation (lets File Transformation replace rather than duplicate it).</summary>
    private const string IndexTransformationId = "c949e7b8-d47c-4312-a4f4-ea3820c4ce87";

    /// <summary>
    /// Registers the shelf with Home Screen Sections.
    /// </summary>
    /// <returns>True when registration succeeded.</returns>
    public static bool RegisterSection(ILogger logger)
    {
        Type? pluginInterface = FindPluginInterface(".HomeScreenSections", "Jellyfin.Plugin.HomeScreenSections.PluginInterface");
        if (pluginInterface == null)
        {
            logger.LogWarning("[WatchedTogether] Home Screen Sections is not installed or not loaded yet; the shelf cannot be registered.");
            return false;
        }

        string? title = Plugin.Instance?.Configuration.SectionTitle;
        if (string.IsNullOrWhiteSpace(title))
        {
            title = "Recently Watched on This Server";
        }

        JObject payload = new JObject
        {
            { "id", Plugin.SectionId },
            { "displayText", title },
            { "limit", 1 },
            { "additionalData", string.Empty },
            { "resultsAssembly", typeof(ResultsHandler).Assembly.FullName },
            { "resultsClass", typeof(ResultsHandler).FullName },
            { "resultsMethod", nameof(ResultsHandler.GetResults) }
        };

        MethodInfo? register = pluginInterface.GetMethod("RegisterSection");
        if (register == null)
        {
            logger.LogWarning("[WatchedTogether] This version of Home Screen Sections has no RegisterSection method. Please update it.");
            return false;
        }

        register.Invoke(null, new object?[] { payload });
        logger.LogInformation("[WatchedTogether] Registered home screen section '{Title}'", title);
        return true;
    }

    /// <summary>
    /// Asks File Transformation to inject our script and stylesheet into jellyfin-web's index.html.
    /// </summary>
    /// <returns>True when registration succeeded.</returns>
    public static bool RegisterClientInjection(ILogger logger)
    {
        Type? pluginInterface = FindPluginInterface(".FileTransformation", "Jellyfin.Plugin.FileTransformation.PluginInterface");
        if (pluginInterface == null)
        {
            logger.LogWarning("[WatchedTogether] File Transformation is not installed or not loaded yet; avatar badges will not appear.");
            return false;
        }

        JObject payload = new JObject
        {
            { "id", IndexTransformationId },
            { "fileNamePattern", "index.html" },
            { "callbackAssembly", typeof(IndexTransformation).Assembly.FullName },
            { "callbackClass", typeof(IndexTransformation).FullName },
            { "callbackMethod", nameof(IndexTransformation.Transform) }
        };

        MethodInfo? register = pluginInterface.GetMethod("RegisterTransformation");
        if (register == null)
        {
            logger.LogWarning("[WatchedTogether] This version of File Transformation has no RegisterTransformation method. Please update it.");
            return false;
        }

        register.Invoke(null, new object?[] { payload });
        logger.LogInformation("[WatchedTogether] Registered index.html injection for avatar badges");
        return true;
    }

    private static Type? FindPluginInterface(string assemblyNameFragment, string typeName)
    {
        Assembly? assembly = AssemblyLoadContext.All
            .SelectMany(x => x.Assemblies)
            .FirstOrDefault(x => x.FullName?.Contains(assemblyNameFragment, StringComparison.Ordinal) ?? false);

        return assembly?.GetType(typeName);
    }
}
