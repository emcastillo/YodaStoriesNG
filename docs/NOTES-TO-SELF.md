# Notes to self

Traps this format and this codebase set, each paid for once already. Read these before
touching the parser, the script engine or the release build.

---

## The file format

- **An IACT script item always has five argument slots.** There is no count field. Reading
  slot 0 as a count parses cleanly, shifts every argument by one, and turns dialogue into
  slices of the next section header - and the game still *mostly* runs. `--self-test`
  exists so it cannot come back. See [SCRIPTING.md](SCRIPTING.md).
- **The version field does not identify the game.** Retail `DESKTOP.DAW` reports 2.0,
  exactly like Yoda Stories. Trust the file name.
- **`TileAtIs` and `IsVariable` put the tile in slot 0** and the position after it, unlike
  every other tile opcode.
- **The zone count in the `ZONE` header is right; scanning for `IZON` markers is wrong.**
  The scan found 27 of Indiana Jones' 366 zones. Read by the counts.
  See [DATA-FORMAT.md](DATA-FORMAT.md).

## Indiana Jones

- **Indiana Jones is not Yoda Stories with other art.** Its script opcodes are numbered
  differently, its zones keep their scripts in separate sections, and the field its puzzles'
  documentation calls an item class is the puzzle type. See
  [WORLD-GENERATION.md](WORLD-GENERATION.md).

## Rendering and the capture harness

- **Read the backbuffer before `RenderPresent`, not after** - which is why every window
  flushes its own screenshot rather than the game loop doing it centrally. See
  [CAPTURING-SCREENSHOTS.md](CAPTURING-SCREENSHOTS.md).

## Releases

- **A single-file publish needs `IncludeAllContentForSelfExtract`, not just
  `IncludeNativeLibrariesForSelfExtract`.** Without it the binary parses a data file
  perfectly and then dies on the first SDL call, so "does it start" does not catch it.
  See [BUILDING.md](BUILDING.md).
