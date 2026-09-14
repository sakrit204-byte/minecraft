# SakritCraft

A survival and crafting game with Minecraft's rules and Skyrim's world, written from
scratch in C# on Vulkan.

The terrain is a continuous signed-distance field rather than a grid of cubes, so every
tunnel you dig has a real slope and every cliff can genuinely overhang. The systems that
make Minecraft work are kept almost intact in their logic: darkness spawns monsters,
tools have tiers, materials have hardness, crops advance on a tick, a lit portal takes
you to a compressed hell dimension. What changes is the fidelity.

The full technical design is in [`docs/MASTER-PLAN.html`](docs/MASTER-PLAN.html).

## State

| Milestone | Status |
|---|---|
| M0 · Vulkan renderer foundation | Complete |
| M1 · Seed to triangles | Complete |
| M2 · Walking on it | Physics complete, renderer in progress |
| M3 · Mining and placing | Not started |
| M4 · Light, both kinds | Not started |

Running now: a deterministic world generator, a surface-nets mesher, a capsule character
controller colliding against the density field, and a Vulkan 1.3 renderer drawing
streamed terrain chunks at 60 fps.

Not yet built: mining, materials and textures, the gameplay light grid, creatures,
building, farming, the Nether.

## Building

Requires the .NET 9 SDK and a Vulkan 1.3 driver. No Vulkan SDK install is needed; GLSL is
compiled to SPIR-V through the Shaderc NuGet package at run time.

```
dotnet build SakritCraft.sln
dotnet test tests/SakritCraft.Tests
dotnet run -c Release --project src/SakritCraft.Client
```

Useful flags: `--seed <text>`, `--gpu <index>`, `--fps <n>`, `--fifo`, `--exit-after <s>`,
`--no-hot-reload`, `--trace`.

Editing any file under `shaders/` recompiles and swaps the pipeline without restarting.
A shader that fails to compile logs the error and keeps the last working version.

## Layout

```
src/SakritCraft.Core         math, hashing, ECS, jobs
src/SakritCraft.World        seed, noise, density field, caves, biomes
src/SakritCraft.Mesh         surface nets, QEF solve, level of detail
src/SakritCraft.Render       Vulkan device, frame graph, terrain streaming
src/SakritCraft.Physics      signed-distance collision, character controller
src/SakritCraft.Sim          entities, AI, crops, light grid
src/SakritCraft.Content      materials, items, recipes, creatures
src/SakritCraft.Net          message channel, snapshots, prediction
src/SakritCraft.Client       entry point, input, console
tools/SakritCraft.TextureForge   procedural texture baker
tests/SakritCraft.Tests      determinism, meshing, physics
```

## The one invariant

Generation is a pure function of the seed and a position. The save format stores only the
difference between what the seed produced and what the player changed, so a one-bit drift
in the generator silently corrupts every existing world.

Two golden-hash tests guard this. If one fails, the generator's output has moved. That is
sometimes intended, but it always requires bumping `GeneratorVersion` and keeping the old
code path reachable, never quietly updating the expected hash.

## Verification

`docs/verification/` holds images produced while building each milestone: an 8 km relief
map, a vertical cross-section showing caves and overhangs, the meshed geometry, and the
renderer output.
