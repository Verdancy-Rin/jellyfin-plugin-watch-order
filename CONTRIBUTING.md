# Contributing to Watch Order

Thanks for helping. Watch Order is maintained in spare time, so pull requests and issues are looked at when time allows, not on a schedule. Clear reports and small, focused pull requests are the quickest to get in.

## Reporting a bug

Open an issue with the **Bug report** form. It asks for what's needed to look into it: your Jellyfin and plugin versions, where it happened (web app, which browser or app), what you expected and what happened, and any lines from the Jellyfin log that start with `Watch Order`.

Please check the open issues first, in case it's already known.

## Suggesting an order

Use the **Order request** form for an order you'd like to see, or better, add it yourself (below). Say where the order comes from (an official timeline, Wikipedia, a well-known fan order), because there's often more than one way to watch a franchise.

## Adding an order

Orders live in [`orders/`](orders/), one JSON file per order. The format is described in [`docs/order-format.md`](docs/order-format.md), and the visual builder in the Dashboard writes it for you (build the order, then **Export**).

1. Put the file at `orders/<franchise>/<id>.json`, where `<id>` matches the order's `id`.
2. Use `providerIds` for every entry: TMDb for films, TVDb or TMDb for series (IMDb as well where you have it). Titles vary between metadata sources; ids don't. Please check each id is the right one.
3. Give it `"version": 1`. If you change an order that's already published, raise its version so people who added it are offered the update.
4. Credit the source of the order in `author` or `description`, and only submit orders that are yours to share.
5. Run `dotnet test`. The tests check every file in `orders/`, so a broken order fails before anyone has to review it.

Orders are added as files to import by link. Bundling one with the plugin (so it appears under **Ready-made orders**) is for orders most people would want offered; say in your pull request if you think yours should be, and it can be added to `orders/ready-made.json`.

## Changing the code

1. Install the .NET 10 SDK, then run `dotnet build -c Release` and `dotnet test`. Both should pass with no warnings.
2. Keep changes focused: one fix or feature per pull request.
3. Add or update tests for what you change. Matching rules are tested against an in-memory library (`tests/.../InMemoryLibrary.cs`); see the README's Development section.
4. If you add a field to the order format, follow the checklist in [`docs/order-format.md`](docs/order-format.md#adding-a-field-for-developers) so older and newer versions keep working together.
5. Describe in the pull request what you changed and how you tested it, ideally on a real Jellyfin server.

## Licence

Watch Order is licensed under the [GNU GPL v3.0](LICENSE). By contributing code or orders, you agree that your contribution is licensed under it too.
