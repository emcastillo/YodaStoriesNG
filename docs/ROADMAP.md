# Roadmap

What is done, what is broken, what is next, and what is deliberately not being done.

---

## Done

The original roadmap was a list of WebFun-style development tools. All of it shipped:

- Save game inspector and editor (`F8`)
- Zone editor with tile placement (`F9`)
- Asset viewer with flag filtering (`F4`)
- Script editor with disassembly, world highlighting and teleport (`F3`)
- Map viewer (`F2`), debug overlay (`F1`), console inspector (`I`)
- Zone teleportation, item spawning through the save editor, a debug menu

Beyond that: both games parse and play, all 36 conditions and 38 instructions are
implemented, palette animation is authentic, there is a mission bot, a score system, save
and load, controller support with a pause menu, mixed sound effects, a native menu bar, and a screenshot harness that regenerates
every image in these docs from a real run.

CI builds and self-tests every push on GitHub Actions, and a `v*` tag publishes
self-contained builds for Windows, Linux and both macOS architectures. The old GitLab
pipeline, which deployed to a network share on a server that no longer exists, is gone.

---

## Known problems

Ordered by how much they actually matter.

### Indiana Jones: what is still open

Indiana Jones now generates the original's worlds, runs its scripts under its own opcode
table, trades through the original's hotspot puzzles (`Game/SectorPuzzles.cs`), draws in its
own palette, starts in the town where Marcus hands over the first artefact, and has a bot
that follows the chain. Still open:

- **Two instructions are undecoded** (`0x14`, `0x23`) and two conditions are the least
  certain of the table - see [SCRIPTING.md](SCRIPTING.md#indiana-jones-opcode-numbers).
- **The bot solves puzzles but has not finished a whole chain yet.** On the reference world
  (`YSNG_SEED=5`) it takes the briefing, fetches dynamite and blasts a blockade, solves a
  ledge-and-floor-switch room, pulls a crate off the Nitro, and reaches step 2 of 5.
  What stops it next: islands (vehicle travel planning is in, untested end to end), boulder
  rows over pressure plates (a greedy push/pull heuristic, not a Sokoban solver), and zones
  whose layout dice are not seeded, so the same seed can differ between runs.
  `YSNG_SEED=5 YSNG_BOT_SECONDS=600` reproduces it.
- **Handing the artefact to Marcus** is scripted in his office but nothing requires it:
  the game is won when the artefact is taken (`WinGame` fires there).
- **Everything learned here is Indiana Jones-only for now** - the character-type field,
  the NPC-hotspot fix, `SectorPuzzles`, the generator. Yoda Stories gets them when it moves
  to the original generator.

### The bot cannot find R2-D2 when he is a zone monster

**Where:** `src/YodaStoriesNG.Engine/Bot/MissionSolver.cs`, `FindR2D2InDagobah`

It searches Dagobah zones' hotspots for R2-D2 but not their `IZAX` monsters, which is where
he usually is, so most runs log `Could not find R2D2 anywhere on Dagobah!` and set off
without the locator.

### Opcodes `0x19` and `0x1A` are evaluated as the wrong condition

**Where:** `src/YodaStoriesNG.Engine/Data/Action.cs`,
`src/YodaStoriesNG.Engine/Game/ActionExecutor.cs`

`ConditionOpcode` declares `NpcIs` and `HasNpc` with the same values as `SectorCounterIs`
(`0x19`) and `SectorCounterIsLessThan` (`0x1A`). C# cannot switch on two names for one
value, so those opcodes are currently evaluated as NPC-interaction checks. Real scripts use
them as sector-counter comparisons.

Fixing it means also reconciling how sector counters are stored: this engine keys them as
`variable[arg0 + 3000]` compared against `arg1`, where the original keeps one counter per
zone compared against `arg0`. See
[SCRIPTING.md](SCRIPTING.md#known-divergences-from-the-original).

### Capture harness loses the D3D device after the tool windows

**Where:** `src/YodaStoriesNG.Engine/Dev/Capture.cs`

After the capture timeline opens and closes the tool windows, every later capture of the
main window fails with `RenderReadPixels failed: GetRenderTargetData(): DEVICELOST`. That
is why `r2d2-help.png` has not regenerated. The pause-menu shot is taken before the tool
windows for this reason. The game itself keeps rendering; only readback breaks.

### `Wait` does not wait

**Where:** `src/YodaStoriesNG.Engine/Game/ActionExecutor.cs`

Instruction `0x08` should suspend script execution for a tick. The executor runs an action's
instructions to completion within one frame, so scripted pauses do not pause. Visible in
cutscene-like sequences that were written expecting them.

### No biplane on the Indiana Jones title screen

**Where:** `src/YodaStoriesNG.Engine/UI/TitleScreen.cs`

The flyby animation needs four tile IDs. Yoda Stories' X-Wing is 948-951; the equivalent
biplane tiles have not been identified in the Indiana Jones atlas, so its title screen is
static. Finding them is an afternoon with the Asset Viewer.

### There are no automated tests

There is one self-check, covering the IACT binary layout because that is where the most
expensive bug lived:

```bash
dotnet run --project src/YodaStoriesNG.Engine -- --self-test
```

CI runs it on every push and pull request, so the layout cannot regress silently. Nothing
else is covered. The natural next targets are the rest of the file format and the world
generator's solvability guarantee - both are pure functions over inputs the repository can
synthesise.

---

## Next

Roughly in order of value per unit of work.

1. **Teach the bot the Indiana Jones chain.** The world is there; the bot is the last
   thing between Indiana Jones and a game that plays itself.
2. **Reconcile the sector-counter opcodes.** Removes the last known scripting divergence.
3. **Decide whether Yoda Stories moves to the original generator.** The port already
   generates Yoda worlds; it would make them the original's and retire the heuristic one.
4. **Extend the self-test to the whole container format.** `SelfTest.cs` already builds a
   data file by hand; extending it to tiles, characters and puzzles is more of the same, and
   would have caught both the IACT bug and the `TNAM` one.
5. **Find the biplane tiles.**

## Deliberately not doing

- **A content authoring pipeline.** The Zone Editor is for understanding zones, not for
  shipping new ones. This is a reimplementation of an engine, not a level editor.
- **Networked or multiplayer anything.** The original is a single-player game that generates
  a private world.
- **A rewrite of `GameEngine.cs` for its own sake.** It is 4,900 lines and that is not
  comfortable, but every split attempted so far produced two classes sharing the same
  private state. It gets refactored when a feature needs it to be, not before.
