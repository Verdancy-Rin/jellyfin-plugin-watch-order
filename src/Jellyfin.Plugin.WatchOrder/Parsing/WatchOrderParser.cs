using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.WatchOrder.Models;

namespace Jellyfin.Plugin.WatchOrder.Parsing;

/// <summary>
/// Reads, validates and expands watch order JSON. Pure logic with no Jellyfin dependencies,
/// so it can be unit tested.
/// </summary>
public static partial class WatchOrderParser
{
    /// <summary>The highest <c>schemaVersion</c> this build understands.</summary>
    public const int SupportedSchemaVersion = 1;

    /// <summary>Gets the JSON options used for reading and writing order files.</summary>
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>
    /// Parses and validates order JSON.
    /// </summary>
    /// <param name="json">The JSON text.</param>
    /// <param name="definition">The parsed definition, when valid.</param>
    /// <param name="errors">Validation errors, empty when valid.</param>
    /// <returns><c>true</c> when the order is valid.</returns>
    public static bool TryParse(string json, out WatchOrderDefinition? definition, out IReadOnlyList<string> errors)
    {
        definition = null;
        try
        {
            definition = JsonSerializer.Deserialize<WatchOrderDefinition>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            errors = new[] { $"Invalid JSON: {ex.Message}" };
            return false;
        }

        if (definition is null)
        {
            errors = new[] { "The file is empty." };
            return false;
        }

        errors = Validate(definition);
        return errors.Count == 0;
    }

    /// <summary>
    /// Checks a definition for missing or contradictory fields.
    /// </summary>
    /// <param name="definition">The definition to check.</param>
    /// <returns>A list of human-readable errors; empty when valid.</returns>
    public static IReadOnlyList<string> Validate(WatchOrderDefinition definition)
    {
        var errors = new List<string>();

        if (definition.SchemaVersion > SupportedSchemaVersion)
        {
            errors.Add($"schemaVersion {definition.SchemaVersion} is newer than this plugin supports ({SupportedSchemaVersion}). Update the plugin.");
        }

        if (!IdPattern().IsMatch(definition.Id ?? string.Empty))
        {
            errors.Add("id must be 1-64 characters of lowercase letters, digits and hyphens, e.g. \"star-wars-chronological\".");
        }

        if (string.IsNullOrWhiteSpace(definition.Name))
        {
            errors.Add("name is required.");
        }

        if (definition.Version is < 1)
        {
            errors.Add("version must be a whole number, 1 or more.");
        }

        if (definition.Source is { } source && !IsWebLink(source))
        {
            errors.Add("source must be an https:// or http:// link to where the order is published.");
        }

        if (definition.Image is { } image)
        {
            ValidateImage(image, errors);
        }

        if (definition.Entries is null || definition.Entries.Count == 0)
        {
            errors.Add("entries must contain at least one entry.");
            return errors;
        }

        for (var i = 0; i < definition.Entries.Count; i++)
        {
            var e = definition.Entries[i];
            var at = $"entries[{i}]";
            var hasIds = e.ProviderIds.Count > 0;

            switch (e.Type)
            {
                case WatchOrderEntryType.Movie:
                    if (string.IsNullOrWhiteSpace(e.Title) && !hasIds)
                    {
                        errors.Add($"{at}: a movie needs a title or providerIds.");
                    }

                    break;

                case WatchOrderEntryType.Episode:
                    RequireSeries(e, at, hasIds, errors);
                    if (e.Season is null || e.Episode is null)
                    {
                        errors.Add($"{at}: an episode needs season and episode.");
                    }

                    break;

                case WatchOrderEntryType.Episodes:
                    RequireSeries(e, at, hasIds, errors);
                    if (e.Season is null || e.From is null || e.To is null)
                    {
                        errors.Add($"{at}: an episode range needs season, from and to.");
                    }
                    else if (e.From > e.To)
                    {
                        errors.Add($"{at}: from ({e.From}) is after to ({e.To}).");
                    }

                    break;

                case WatchOrderEntryType.Season:
                    RequireSeries(e, at, hasIds, errors);
                    if (e.Season is null)
                    {
                        errors.Add($"{at}: a season entry needs season.");
                    }

                    break;
            }

            if (e.Recommended && !e.Optional)
            {
                errors.Add($"{at}: recommended only applies to optional entries; add \"optional\": true, or remove recommended.");
            }

            if (e.Section is not null && string.IsNullOrWhiteSpace(e.Section))
            {
                errors.Add($"{at}: section needs a heading, or remove it.");
            }

            if (e.Season is < 0 || e.Episode is < 0 || e.From is < 0)
            {
                errors.Add($"{at}: season and episode numbers cannot be negative.");
            }
        }

        return errors;
    }

    /// <summary>
    /// Expands range entries into one step per episode. Season entries are kept as one step.
    /// </summary>
    /// <param name="definition">A valid definition.</param>
    /// <returns>The steps, in order.</returns>
    public static IReadOnlyList<WatchOrderStep> Expand(WatchOrderDefinition definition)
    {
        var steps = new List<WatchOrderStep>();

        for (var i = 0; i < definition.Entries.Count; i++)
        {
            var e = definition.Entries[i];
            var ids = e.ProviderIds;

            switch (e.Type)
            {
                case WatchOrderEntryType.Movie:
                    steps.Add(new WatchOrderStep(i, WatchOrderEntryType.Movie, e.Title, e.Year, null, null, ids, e.Note));
                    break;

                case WatchOrderEntryType.Episode:
                    steps.Add(new WatchOrderStep(i, WatchOrderEntryType.Episode, e.Series, e.Year, e.Season, e.Episode, ids, e.Note));
                    break;

                case WatchOrderEntryType.Episodes:
                    for (var ep = e.From!.Value; ep <= e.To!.Value; ep++)
                    {
                        steps.Add(new WatchOrderStep(i, WatchOrderEntryType.Episode, e.Series, e.Year, e.Season, ep, ids, e.Note));
                    }

                    break;

                case WatchOrderEntryType.Season:
                    steps.Add(new WatchOrderStep(i, WatchOrderEntryType.Season, e.Series, e.Year, e.Season, null, ids, e.Note));
                    break;
            }
        }

        return steps;
    }

    /// <summary>Whether text is an absolute http(s) link.</summary>
    /// <param name="text">The text.</param>
    /// <returns><c>true</c> for an http or https link.</returns>
    public static bool IsWebLink(string text)
        => Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    private static void ValidateImage(OrderImage image, List<string> errors)
    {
        if (image.Type is not null && image.Type is not ("movie" or "series"))
        {
            errors.Add("image.type must be \"movie\" or \"series\".");
        }

        var namesItem = !string.IsNullOrWhiteSpace(image.Title) || image.ProviderIds is { Count: > 0 };
        if (image.Type is not null && !namesItem)
        {
            errors.Add("image needs a title or providerIds to find the film or show.");
        }
        else if (image.Type is null && namesItem)
        {
            errors.Add("image needs a type (\"movie\" or \"series\") to find the film or show.");
        }

        if (image.Url is { } url && !IsWebLink(url))
        {
            errors.Add("image.url must be an https:// or http:// link to a picture.");
        }

        if (image.Type is null && !namesItem && string.IsNullOrWhiteSpace(image.Url))
        {
            errors.Add("image needs a film or show (type with title or providerIds), a url, or both.");
        }
    }

    private static void RequireSeries(WatchOrderEntry e, string at, bool hasIds, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(e.Series) && !hasIds)
        {
            errors.Add($"{at}: needs a series name or the series' providerIds.");
        }
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$")]
    private static partial Regex IdPattern();
}
