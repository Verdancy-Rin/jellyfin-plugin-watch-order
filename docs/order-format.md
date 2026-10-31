# Watch order format

Most people will build orders with the visual builder (Dashboard → Watch Order → New order), which writes this format for you. This page is for writing or sharing order files by hand.

An order is a JSON file with a list of entries, watched top to bottom. Comments (`//`) and trailing commas are allowed. Add `"$schema"` to get autocomplete and validation in VS Code and other editors.

```jsonc
{
  "$schema": "../../schema/watch-order.schema.json",
  "schemaVersion": 1,
  "id": "my-franchise",            // lowercase letters, digits, hyphens; also the file name
  "name": "My Franchise (chronological)",
  "description": "Optional text shown in the UI",
  "author": "Who made this order",
  "version": 1,                    // raise when you publish a change
  "source": "https://example.com/my-franchise.json",  // optional: where the latest copy lives
  "entries": [ ... ]
}
```

`version` is the order's own version number. Start at 1 and raise it each time you publish a changed copy, so the plugin can tell people with an older copy that a newer one exists. Files without it count as version 1. `source` is an optional link to where the latest copy of the file is published.

## Entry types

| `type` | Required fields | Expands to |
| --- | --- | --- |
| `movie` | `title` or `providerIds` | one film |
| `episode` | `series` or `providerIds`, `season`, `episode` | one episode |
| `episodes` | `series` or `providerIds`, `season`, `from`, `to` | every episode from `from` to `to` (inclusive) |
| `season` | `series` or `providerIds`, `season` | every episode in that season that's in your library |

Every entry can also have `year` (used to tell remakes and reboots apart), `note` (shown beside it) and `optional: true` for filler, spin-offs or side stories. Each user chooses per order whether to skip optional entries; when they do, those entries don't count towards progress and are never queued or autoplayed.

Season `0` is Specials.

## Matching

Each entry is matched to your library in this order:

1. **Provider ids.** `providerIds` belong to the film, or to the *series* for episode entries. Keys are case-insensitive: `Tmdb`, `Imdb`, `Tvdb` (plus any other provider Jellyfin knows). This is the reliable option, so use it in orders you share. Collection ids such as `TmdbCollection` are ignored, because every film in a collection shares one and it would match the wrong film.
2. **Title and year.** Used if no id matches and *Fall back to title and year* is on. The year may be off by one, because sources disagree on release years. These matches show as "matched by title" in the admin view so you can check them.

Episode numbers follow your library's numbering, which comes from your metadata provider. If you use an alternative episode order (DVD, absolute), write the order using that numbering.

## Example

```json
{
  "id": "example",
  "name": "Example",
  "entries": [
    { "type": "movie", "title": "Some Film", "year": 2008, "providerIds": { "Tmdb": "12345" } },
    { "type": "season", "series": "Some Show", "season": 1 },
    { "type": "episodes", "series": "Some Show", "season": 2, "from": 1, "to": 4 },
    { "type": "episode", "series": "Crossover Show", "season": 3, "episode": 8, "note": "Crossover part 2", "optional": true }
  ]
}
```

See [`orders/examples/`](../orders/examples/) for a complete file.

## Compatibility

The format is stable at `schemaVersion: 1`. These rules keep files working across plugin versions:

- **New fields are optional and don't change `schemaVersion`.** A file that doesn't use a new field means exactly what it did before, so old files keep working in new plugins.
- **Unknown fields are kept, not dropped.** An older plugin reading a file from a newer one ignores fields it doesn't understand, but keeps them when it saves, when the order is edited in the builder, and when it's exported. Nothing is lost by passing a file through an older plugin.
- **`schemaVersion` only goes up for a change an older plugin would get wrong:** a field that changes what an entry means, a new entry type, or a new required field. A plugin refuses a file with a newer `schemaVersion` and says to update, rather than misreading it. Newer plugins keep reading older versions.
- **Fields are never renamed or given a new meaning.** A replacement gets a new name, and the old one keeps working.

### Adding a field (for developers)

A new field touches these places, so check each one:

| Where | What to do |
| --- | --- |
| `Models/WatchOrderDefinition.cs` or `WatchOrderEntry.cs` | Add the property, optional (nullable or with a default that means "as before"). |
| `Parsing/WatchOrderParser.cs` | Validate it if it has rules. Never make an existing file invalid. |
| `schema/watch-order.schema.json` | Add it, with a description. |
| This page | Document it. |
| Builder (`configPage.js`) | Add it to `KNOWN_ORDER_FIELDS` or `KNOWN_ENTRY_FIELDS` and to the form if people should edit it. Until then the builder keeps it untouched. |
| Anything that reads orders | Resolver, progress, autoplay, playlists, item pages, stats: decide whether the field changes their behaviour. If it changes what an entry means for an older plugin, that's a `schemaVersion` change. |
| Tests | Add a round-trip test, and run the tests that read every file in `orders/`. |
| Ready-made orders | Raise each order's `version` if its content changed, so admins are offered the update. |
