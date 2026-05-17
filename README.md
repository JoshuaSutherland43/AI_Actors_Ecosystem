# AI-Driven Ecosystem Simulation Report

## Module / Assignment
**Assignment Task:** Build a Simple AI-Driven Simulation  
**Project:** `Latest_VR_A3`  
**Engine:** Unity (2D simulation, rendered in world-space with UI overlays)

---

## 1. Core Idea and Concept

This project implements a **dynamic survival ecosystem** where herbivore agents must survive in a changing environment shaped by:

- spreading fungal infection,
- terrain decay/regrowth cycles,
- periodic acid-rain fronts,
- shelter migration behavior,
- social and personality-driven decision making.

To strengthen coordinated behavior and uniqueness, the simulation includes a second actor type, **Shelter Wardens**, which patrol shelter zones, detect infection threats, and trigger herd shelter coordination alarms for healthy herbivores.

The design intention is to model an RTS/simulation-like world where local decisions create emergent global behavior: clustering, panic migration, infection collapse, ecosystem recovery, and repeated survival cycles.

---

## 2. Environment and Entities

## 2.1 World Model
The world is a 2D tile-based board managed by `TileGrid`, with tile states:

- `Grass`
- `Fungus`
- `DeadSoil`
- `Shelter`
- `AcidSoil`
- `FertileSoil`

Each tile type has different gameplay effects (nutrition, infection risk, survivability, regrowth behavior).

## 2.2 Actor Types
### A) Herbivore Agents (primary population)
- Autonomous entities with internal state:
  - hunger
  - health
  - infection progression
  - age
  - breeding cooldown/readiness
- Personality profiles affect movement, risk tolerance, social behavior, infection susceptibility, and response to danger.

### B) Shelter Warden Agents (second actor type)
- Patrol shelter regions.
- Detect nearby infected herbivores.
- Enter response mode and raise an alarm influencing healthy herbivore behavior.
- Apply local anti-fungus pressure by converting nearby fungus to dead soil.

## 2.3 AI-Controlled Environment Objects
- **Fungus**: spreads strategically, has TTL, decays into dead soil, influenced by local biomass.
- **Acid Rain Front**: moving environmental hazard band that transforms terrain and kills exposed actors.
- **Terrain Recovery**: dead/acid soil can probabilistically recover to grass over time.

---

## 3. System Loop (Simulation Tick Pipeline)

The simulation uses a fixed-step tick system (`tickInterval`) with speed scaling.  
At each simulation tick:

1. Advance acid rain cycle and front state.
2. Update quadtree memberships for moving/spawned/dead actors.
3. Tick all wardens (patrol/respond/alarm).
4. Tick all herbivores (state updates, steering, movement, infection, hunger, death checks).
5. Execute breeding logic.
6. Tick terrain systems (fungus spread/decay, regrowth, acid recovery).
7. Handle collapse phase transitions.
8. Remove dead agents from active list.
9. Aggregate statistics and refresh visual overlays (paths, quadtree lines, UI metrics).

This deterministic loop ensures all systems remain synchronized and analyzable.

---

## 4. Actor AI and Algorithm Implementations

## 4.1 Herbivore Decision Architecture
Herbivores use a **finite-state machine (FSM)**:

- `Wandering`
- `SeekingGrass`
- `Fleeing`
- `SeekingShelter`
- `Infected_Wandering`
- `Infected_Herding`
- `Dying`
- `Dead`

State transitions are driven by:

- internal needs (hunger, infection progression),
- local context (tile type, nearby infected/healthy counts),
- global environment phase (acid rain active/imminent, land collapse),
- social signals (leader clustering, warden alarms).

## 4.2 Steering and Movement Algorithms
Movement is force-blended steering:

- Wander noise (personality-scaled)
- Seek grass/shelter/fungus target vectors
- Flee vectors from fungus/infected/herd pressure
- Social cohesion
- Edge avoidance and board clamping
- Route refinement via multi-angle risk sampling (hazard-aware path adjustment)

For healthy agents, candidate directions are scored using hazard risk (fungus, acid soil, infected proximity, edge pressure), then safer steering directions are selected.

## 4.3 Infection and Health Algorithms
- **Tile-based infection**: standing on fungus gives per-tick infection probability.
- **Proximity infection**: chance scales with nearby infected pressure and distance.
- **Infection progression**: infected entities accumulate infection ticks until death threshold.
- **Hunger-health coupling**: hunger rises over time; starvation drains health.

## 4.4 Coordinated Healthy-Herd Behavior (new implementation)
Healthy herbivores now coordinate toward shelter through:

- **leader-following migration**: healthy `SeekingShelter` agents influence nearby peers,
- **warden alarm response**: nearby warden alarms trigger early shelter movement,
- **vulnerable escort tendency**: agents bias movement to support vulnerable nearby herd members (timid, low health, high hunger).

This creates explicit **healthy actor collaboration toward a common survival objective**.

## 4.5 Infected Herd Coordination Algorithms
Infected agents coordinate via:

- infected cohesion,
- healthy-target interception staging,
- fungal funneling bias,
- containment arc construction around healthy clusters.

This creates pack-like pressure and corralling behavior rather than random chase.

## 4.6 Shelter Warden Algorithms (second actor class)
Wardens implement a two-state controller:

- **PatrollingShelter**: random shelter-ring patrol target sampling.
- **RespondingToInfected**: nearest-infected pursuit inside detection radius, alarm activation, and local fungal suppression.

Warden alarms are queried by herbivores to influence decision transitions into coordinated shelter migration.

---

## 5. Environment and World-System Algorithms

## 5.1 Fungus Lifecycle and Strategic Spread
Each fungus tile has:

- a lifetime counter (TTL),
- spread event budget,
- starvation penalty when local biomass is low.

Spread uses candidate scoring:

- local adjacency opportunities,
- strategic random samples in a search radius,
- attraction toward healthy herd density,
- distance-weighted target preference.

This produces directional, context-aware spread instead of naive random flood fill.

## 5.2 Acid Rain System
Acid rain runs as periodic waves with:

- warning phase,
- active phase,
- moving front direction and drift velocity,
- core radius + feathered falloff exposure.

Consequences:

- terrain conversion (grass/fungus/fertile → acid soil),
- exposed actor mortality unless sheltered.

## 5.3 Terrain Recovery and Collapse
- Dead soil can regrow to grass probabilistically.
- Acid soil can recover after rain-recovery delay.
- Fertile soil is temporary and decays.
- If all living ground is lost, collapse mode can trigger mass exposure effects.

---

## 6. Quad-Tree Data Structure and Spatial Query System

The project includes a **custom self-implemented quadtree** (no external library):

- dynamic insertion of entities,
- removal by known/unknown prior position,
- incremental update for moved entities,
- subtree collapse when capacity conditions allow,
- radius querying for nearby-entity lookup.

### Dynamic Maintenance
`SimulationBoard` tracks previous positions per agent and updates quadtree membership each tick:

- moved agents: remove + reinsert (incremental update),
- dead/despawned agents: removal from quadtree and tracking map,
- new agents: insertion on spawn.

### AI Usage
Herbivores query nearby entities through quadtree radius search each tick, reducing broad-phase checks and improving scalability.

---

## 7. Quad-Tree Visualization and Debug Compliance

To satisfy visualization and rubric robustness, quadtree rendering is implemented in multiple forms:

1. **GL line overlay** (runtime visual layer).
2. **Unity `Debug.DrawLine`** per tick (explicit debug draw channel).
3. **Unity Gizmos** (`OnDrawGizmos`) for scene visualization.

All are controlled through the same quadtree visibility toggle (`F3` / UI button), ensuring clear real-time structural feedback as entities move/spawn/die.

---

## 8. Interaction Coverage

## 8.1 Actor–Object Interactions
- Herbivores consume nutrition effects from grass/fertile/shelter.
- Fungus tiles create infection pressure.
- Shelter tiles provide safety from rain and strategic regrouping.
- Acid regions impose severe environmental risk.

## 8.2 Actor–Actor Interactions
- Healthy social cohesion.
- Infection proximity contagion.
- Infected pack corralling of healthy agents.
- Breeding between compatible healthy agents.
- Warden-to-herbivore alarm-driven coordination.

---

## 9. Adaptive Behavior Coverage

Behavior changes continuously with:

- hunger thresholds,
- infection stage progression,
- shelter presence,
- acid rain active/imminent states,
- local infected density,
- landscape phase transitions,
- personality multipliers.

This adaptation appears both at individual level (FSM transitions) and ecosystem level (population spread/collapse/recovery cycles).

---

## 10. Technical Functionality and Stability

- System compiles successfully (`Assembly-CSharp` build pass).
- Runtime architecture is modular (`Config`, `Core`, `Entities`, `Terrain`, `UI`, `Quadtree`).
- Simulation metrics and overlays provide continuous diagnostics:
  - population counts,
  - infection/death statistics,
  - quadtree node/depth/query savings,
  - terrain composition,
  - acid-rain timing and phase.

---

## 11. Creativity and Uniqueness

This simulation avoids the standard predator-prey template and instead explores a **biohazard survival ecology** with:

- fungal strategic expansion,
- hazard-wave weather dynamics,
- shelter governance via wardens,
- social migration and escort behavior,
- infection herding as an antagonistic emergent force.

The combination produces a distinctive and cohesive systems-driven simulation.

---

## 12. Rubric Mapping Summary

| Criterion | Coverage |
|---|---|
| Quad-Tree Data Structure | Fully implemented, dynamic update/insert/remove/query |
| Quad-Tree Visualisation | GL overlay + Debug.DrawLine + Gizmos + toggle |
| AI Behaviours for Entities | FSM + personality + needs/goals + infection lifecycle |
| Coordinated Movement | Cohesion, fleeing, infected corralling, shelter migration |
| Adaptive Behaviour | Internal states + weather + terrain phase adaptation |
| Actor–Actor & Actor–Object Interactions | Infection, breeding, social grouping, tile effects, warden alarms |
| Cohesion of Ecosystem | Integrated terrain/actors/weather/quadtree loops |
| Technical Functionality | Structured codebase, successful build, debug metrics |
| Creativity & Uniqueness | Biohazard shelter-ecology with emergent multi-system behavior |

---

## 13. Controls and Demonstration Notes

### UI/Hotkeys
- `F1` Toggle panel
- `F2` Pause/Resume
- `F3` Toggle quadtree (GL + debug draw + gizmos)
- `F4` Toggle decision tree overlay
- `F5` Toggle scene stats overlay
- `F6/F7` Speed up/down
- `F8` Toggle path rendering

### Developer Brushes
- Hold `F` + mouse drag: fungus paint
- Hold `R` + mouse drag: manual acid rain brush

These controls are useful for demonstrating specific rubric behaviors in the video.

---

## 14. Suggested 5-Minute Video Structure

1. **Concept intro** (world, actors, objectives).
2. **Quadtree** (toggle on/off, show dynamic updates while actors move/spawn/die).
3. **Herbivore AI** (state changes from hunger/threat/infection).
4. **Coordination** (healthy shelter migration + escort; infected herd corralling).
5. **Second actor** (warden patrol, detection, alarm influence).
6. **Environmental AI** (fungus spread, acid rain front, regrowth/collapse).
7. **Closing metrics** (stats panel and cohesion summary).

---

## 15. Conclusion

The submitted simulation satisfies the assignment’s core AI and systems requirements with:

- custom dynamic spatial indexing (quadtree),
- multi-actor ecosystem behavior,
- coordinated and adaptive agent logic,
- integrated environmental AI systems,
- real-time visualization/debug instrumentation.

The final result is a cohesive, technically grounded, and creative AI-driven simulation suitable for assessment under the provided rubric.

