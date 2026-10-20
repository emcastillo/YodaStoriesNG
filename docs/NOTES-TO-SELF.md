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

## Moving through a zone

- **Anything in the object layer stops the hero.** Not just tiles flagged Object: a pilot is
  a Character tile, Pacal an Item tile, and walking onto them skipped the bump scripts that
  fly the plane and win the game. Items stay walk-over pickups here, except where a bump
  script is waiting for that tile.
- **A block only ever moves into an empty square.** Pushing a crate onto an item used to
  overwrite the item. The original checks for no tile at all (WebFun `hero.ts` `_doDrag`).
- **The hero moves diagonally, with no corner check,** and slides diagonally round an
  obstacle. Some doorways are only reachable that way, boxed in by tree trunks.
- **A vehicle takes you when you stand on it, not when you walk onto it.** Flight scripts
  end by putting the hero on the plane's square; that is the take-off. Remember the square
  the script chose - input can move the hero before the speech is dismissed.

## Rendering and the capture harness

- **Read the backbuffer before `RenderPresent`, not after** - which is why every window
  flushes its own screenshot rather than the game loop doing it centrally. See
  [CAPTURING-SCREENSHOTS.md](CAPTURING-SCREENSHOTS.md).

## Releases

- **A single-file publish needs `IncludeAllContentForSelfExtract`, not just
  `IncludeNativeLibrariesForSelfExtract`.** Without it the binary parses a data file
  perfectly and then dies on the first SDL call, so "does it start" does not catch it.
  See [BUILDING.md](BUILDING.md).
