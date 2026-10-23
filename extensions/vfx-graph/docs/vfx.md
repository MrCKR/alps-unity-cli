# VFX Tools

The extension contributes independent tools for Visual Effect Graph components and Unity ParticleSystem/LineRenderer/TrailRenderer workflows.

## Visual Effect Graph

| Tool | Description |
| --- | --- |
| `vfx.graph-info` | Read one existing Visual Effect Graph asset. |
| `vfx.graph-create` | Create or overwrite one Visual Effect Graph asset. |
| `vfx.effect-info` | Read the VisualEffect component and assigned graph. |
| `vfx.effect-add` | Ensure a target has a VisualEffect component. |
| `vfx.effect-assign` | Assign an existing graph to a VisualEffect component. |
| `vfx.parameter` | Set one exposed VisualEffect parameter and verify the matching Has*/Get* typed readback. |
| `vfx.control-play` | Play a VisualEffect. |
| `vfx.control-stop` | Stop a VisualEffect. |
| `vfx.control-pause` | Toggle VisualEffect pause state. |
| `vfx.control-reinitialize` | Reinitialize a VisualEffect. |
| `vfx.control-set-rate` | Set VisualEffect playback rate and return the public-property readback. |
| `vfx.control-set-seed` | Set VisualEffect random seed and return the public-property readback. |
| `vfx.template-list` | List installed graph templates. |
| `vfx.template-create` | Create a graph from a supported template. |

These tools require `com.unity.visualeffectgraph`.

## Particle And Renderer Tools

The extension also contributes `vfx.particle-create`, `vfx.particle-info`, `vfx.particle-main`, `vfx.particle-emission`, `vfx.particle-shape`, `vfx.particle-play`, `vfx.particle-stop`, `vfx.particle-pause`, `vfx.particle-clear`, `vfx.line-create`, `vfx.line-positions`, `vfx.trail-create`, and `vfx.trail-set`.
