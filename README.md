# CupkekGames Combat Hex Grids

Combat on a hex grid: a `CombatSpace` for `com.cupkekgames.combat` built on `com.cupkekgames.hexgrids`. One combat unit is one tile and one unit stands on each tile. Units glide tile to tile on fixed ticks, and areas are tile patterns. Combat depends on no space package: a game gives each fight its space.

## What's inside

**Runtime** (`CupkekGames.Combat.HexGrids`)

- `HexCombatSpace`: the component a fight carries beside its unit manager, pointing at a `HexFieldView`. Distances and ranges are in tiles (a range is rounded to the nearest tile), areas are tile patterns (a circle is every tile within its radius; arcs and lines test tile centres), and a unit acts only while standing on its tile. Each frame it runs every tick the units' times made due, one tick per unit in turn, in the order they started, so the same fight plays out the same at any frame rate or speed (`FixedStepClock` from `com.cupkekgames.timesystem`).
- `HexCombatMover`: one unit's movement. It can be lifted off the field and set down on a free tile (formation drags), and it is settled when it stands on its tile, not walking or pushed. It glides between tiles playing `CombatUnitView.MoveAnimationKind` and turns towards where it walks or, standing, its target. A dash slides it along its line on the board at once and stops before a taken tile, while the model follows over the dash's time. A navmesh agent on the same body is switched off.
- `HexCombatTileMarks`: a ring in each team's colour under every fighting unit's tile (the one it is stepping to while it walks), rebuilt each frame, so the grid's rules read in play.
- `HexCombatBoard`: the rules in plain C#, by integer id: placement on the nearest free tile, reservation, one-tile steps towards a target until it is in reach, hold, root, dash, and areas. It is tested without Unity objects.

**Tests** (`CupkekGames.Combat.HexGrids.Tests`, EditMode): `HexCombatBoardTests`

## Settings

- `HexFieldView` tile size: the only world number (one combat unit, as metres, is the tile spacing).
- `HexCombatSpace`: ticks per second (20), seconds per tile (0.6), range tolerance in tiles (0.5), most ticks per frame (8).
