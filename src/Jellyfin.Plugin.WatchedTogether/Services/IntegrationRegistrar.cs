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
    /// Registers our shelves with Home Screen Sections: the recently-watched shelf and the two
    /// Most Popular shelves (unless switched off).
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

        MethodInfo? register = pluginInterface.GetMethod("RegisterSection");
        if (register == null)
        {
            logger.LogWarning("[WatchedTogether] This version of Home Screen Sections has no RegisterSection method. Please update it.");
            return false;
        }

        Configuration.PluginConfiguration config = Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration();

        Register(register, pluginInterface.Assembly, Plugin.SectionId,
            Title(config.SectionTitle, "Recently Watched on This Server"), nameof(ResultsHandler.GetResults), logger);

        if (config.PopularMoviesEnabled)
        {
            Register(register, pluginInterface.Assembly, PopularityService.MoviesSectionId,
                Title(config.PopularMoviesTitle, "Most Popular Movies"), nameof(ResultsHandler.GetPopularMovies), logger);
        }

        if (config.PopularShowsEnabled)
        {
            Register(register, pluginInterface.Assembly, PopularityService.ShowsSectionId,
                Title(config.PopularShowsTitle, "Most Popular Shows"), nameof(ResultsHandler.GetPopularShows), logger);
        }

        return true;
    }

    private static string Title(string? configured, string fallback) =>
        string.IsNullOrWhiteSpace(configured) ? fallback : configured.Trim();

    private static void Register(MethodInfo register, Assembly homeScreenSectionsAssembly, string sectionId, string title, string resultsMethod, ILogger logger)
    {
        JObject payload = new JObject
        {
            { "id", sectionId },
            { "displayText", title },
            { "limit", 1 },
            { "additionalData", string.Empty },
            { "resultsAssembly", typeof(ResultsHandler).Assembly.FullName },
            { "resultsClass", typeof(ResultsHandler).FullName },
            { "resultsMethod", resultsMethod }
        };

        register.Invoke(null, new object?[] { payload });
        logger.LogInformation("[WatchedTogether] Registered home screen section '{Title}'", title);

        RegisterSectionName(homeScreenSectionsAssembly, sectionId, title, logger);
    }

    /// <summary>
    /// Home Screen Sections looks up every section's name in its translation packs. With no entry for
    /// our section id, 3.0.x falls back to the literal text "Genre Section" in its admin table.
    /// Adding our title to its English pack (which every language falls back to) fixes the label.
    /// </summary>
    private static void RegisterSectionName(Assembly homeScreenSectionsAssembly, string sectionId, string title, ILogger logger)
    {
        try
        {
            Type? pluginType = homeScreenSectionsAssembly.GetType("Jellyfin.Plugin.HomeScreenSections.HomeScreenSectionsPlugin");
            Type? translationManagerType = homeScreenSectionsAssembly.GetType("Jellyfin.Plugin.HomeScreenSections.Library.ITranslationManager");

            object? plugin = pluginType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
            IServiceProvider? services = pluginType?.GetProperty("ServiceProvider", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(plugin) as IServiceProvider;
            object? translationManager = translationManagerType == null ? null : services?.GetService(translationManagerType);
            MethodInfo? update = translationManagerType?.GetMethod("UpdateTranslationPack");

            if (translationManager == null || update == null)
            {
                logger.LogInformation("[WatchedTogether] Home Screen Sections has no translation manager to name our section; its admin table may show a generic label");
                return;
            }

            JObject pack = new JObject { { sectionId, title } };
            update.Invoke(translationManager, new object?[] { "en", pack });
            logger.LogInformation("[WatchedTogether] Registered section name '{Title}' with Home Screen Sections", title);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[WatchedTogether] Could not register the section name with Home Screen Sections");
        }
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
