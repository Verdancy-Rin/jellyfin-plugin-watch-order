# Orders

Shareable watch order files. Each file is one order; the format is described in [`docs/order-format.md`](../docs/order-format.md).

- `mcu/`: the Marvel Cinematic Universe in release order. One order per phase (`mcu-phase-1` to `mcu-phase-6`), The Infinity Saga (Phases One to Three), The Multiverse Saga (Phases Four to Six), and `mcu-complete` with everything. Disney+ series are included as whole seasons; animated alternate-universe series, shorts and standalone specials are marked optional.
- `star-wars/`: `star-wars-skywalker-saga` (Episodes I to IX) and `star-wars-complete` (every canon film and series in story order, following StarWars.com's timeline). The Tales anthologies and Resistance are optional.
- `cardcaptor-sakura/`: the 1998 series and Clear Card with their films and specials placed in story order (a three-season layout for the 1998 series, one season for Clear Card, plus specials).
- `naruto/`: `naruto` (Naruto and Naruto Shippuden) and `naruto-and-boruto` (continuing into Boruto: Naruto Next Generations), in story order with all eleven films. Filler and most films are optional, and the ones worth watching are marked recommended.
- `steven-universe/`: the series, the film and Steven Universe Future, in story order.
- `urusei-yatsura/`: the 1981 series with its OVAs and films placed in story order (episode numbers follow a four-season layout plus specials).
- `examples/` holds small orders that show the format.

Every file here is checked by the test suite, so a broken order fails CI. Films and series are matched by TMDb id, so they match whatever your files are called. Titles and years are there as a fallback and for reading. Entries that aren't released yet say so in their note and show as "not in your library" until they are.

The MCU, Star Wars, Cardcaptor Sakura, Naruto and Urusei Yatsura orders come with the plugin: open Dashboard → Watch Order → **Ready-made orders** and add the ones you want. [`ready-made.json`](ready-made.json) lists which orders are bundled, and in what order they're shown. For any other file here, use **Import** with the file, its link, or its contents.

## Contributing an order

See [CONTRIBUTING.md](../CONTRIBUTING.md) for the full guide. In short:


1. Add `orders/<franchise>/<id>.json`, where `<id>` matches the `id` field.
2. Prefer `providerIds` (TMDb for films, TVDb or TMDb for series) over title matching. Titles vary between metadata sources; ids don't.
3. Credit the source of the order in `author` if it comes from somewhere else, and include it only if it's yours to share.
4. Give it `"version": 1`, and raise the version whenever you change an order that's already published, so people who added it are offered the update.
5. To bundle it with the plugin, add it to `ready-made.json`. Only bundle orders that most people would want to be offered; personal or niche orders can stay as files here, to import by link.
6. Run `dotnet test` before opening a PR.
