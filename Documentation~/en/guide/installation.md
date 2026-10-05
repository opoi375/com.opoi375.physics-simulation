# Installation

## From git URL (recommended)

1. **Window → Package Manager**
2. Click **＋ → Add package from git URL**
3. Paste:

```
https://github.com/opoi375/com.opoi375.physics-simulation.git
```

The package's only dependency is `com.unity.test-framework`, resolved automatically. It will **not** drag in URP, `Unity.Mathematics` or Burst.

## From disk

**Add package from disk** and pick this repository's `package.json`.

Embedded usage: drop the whole folder into `Packages/com.opoi375.physics-simulation/` and Unity treats it as an embedded package. If the embedded package carries its own git repository, ignore it in the host project's root `.gitignore`, otherwise the host repo swallows it as a plain directory:

```gitignore
# --- Embedded packages with their own repos -----------------------------
/Packages/com.opoi375.physics-simulation/
```

## Requirements

| Item | Requirement |
| --- | --- |
| Unity | **6000.5 or newer** (`"unity": "6000.5"`, `"unityRelease": "6f1"`) |
| Render pipeline | none (no pipeline dependency at all) |
| Tests | Unity Test Framework 1.7.0+ (installed automatically as a dependency) |

## Referencing from your own assembly

```json
{
  "references": [
    "PhysicsSimulation.Runtime"
  ]
}
```

Editor scripts additionally reference `PhysicsSimulation.Editor`.

| Assembly | Contents | Platforms |
| --- | --- | --- |
| `PhysicsSimulation.Runtime` | pure logic layer + `MassSpringBehaviour` / `MassSpringParticleLink` | all |
| `PhysicsSimulation.Editor` | `Tools > Physics Simulation` menus | Editor |
| `PhysicsSimulation.Editor.Tests` | EditMode tests (`autoReferenced: false`, `UNITY_INCLUDE_TESTS`) | Editor |

## Verify the install

1. The menu **Tools → Physics Simulation → Create Demo Scene** appears
2. Click it → a new scene is created and saved as `Assets/Scenes/PhysicsDemo.unity`
3. Press **Play**; run **Tools → Physics Simulation → Dump State** to see particle/spring counts, max speed and the NaN flag

::: warning The demo scene overwrites its target file
`Create Demo Scene` saves **silently** (`EditorSceneManager.SaveScene`) to the fixed path `Assets/Scenes/PhysicsDemo.unity`. If you have edited a scene at that path, save it elsewhere first.
:::
