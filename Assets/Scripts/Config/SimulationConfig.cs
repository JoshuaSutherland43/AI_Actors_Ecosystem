using UnityEngine;

/// <summary>
/// Central configuration asset. Create via Assets > Create > Ecosystem > SimulationConfig
/// All tunable values live here — no magic numbers scattered across scripts.
/// </summary>
[CreateAssetMenu(fileName = "SimulationConfig", menuName = "Ecosystem/SimulationConfig")]
public class SimulationConfig : ScriptableObject
{
    [Header("Board")]
    public int gridWidth = 60;
    public int gridHeight = 40;
    public float tileSize = 0.5f;

    [Header("Entities")]
    public int initialHerbivoreCount = 40;
    public int maxHerbivores = 120;
    [Tooltip("Make all herbivores bright yellow for clear visibility during demos")]
    public bool forceBrightYellowHerbivores = true;
    [Tooltip("Extra uniform size multiplier for herbivore sprites")]
    public float herbivoreSizeMultiplier = 1.05f;

    [Header("Fungus")]
    [Tooltip("How many simulation ticks a fungus tile persists before decaying")]
    public int fungusTileDurationTicks = 2000;

    [Tooltip("Radius (in tiles) around a dead infected entity that spawns fungus")]
    public int deathFungusRadius = 2;

    [Tooltip("Chance per tick that a fungus tile spreads to a neighbour grass tile")]
    [Range(0f, 1f)]
    public float fungusSpreadChance = 0.0012f;

    [Tooltip("Extra local spread attempts per fungus tile when evaluating strategic growth")]
    [Range(1, 12)]
    public int fungusStrategicSpreadAttempts = 4;

    [Tooltip("Search radius (in tiles) used by fungus when looking for herd density targets")]
    [Range(1, 12)]
    public int fungusStrategicSearchRadius = 4;

    [Tooltip("How strongly fungus prefers spreading toward clustered healthy herds")]
    [Range(0f, 5f)]
    public float fungusHerdAttractionWeight = 0.85f;

    [Tooltip("How many starter fungus clusters are seeded at simulation start")]
    public int initialFungusPatches = 1;

    [Tooltip("Minimum nearby biomass tiles (grass or fertile) needed to sustain a fungus tile")]
    [Range(0, 8)]
    public int fungusBiomassThreshold = 1;

    [Tooltip("Extra TTL loss per tick when local biomass is exhausted")]
    [Range(0, 20)]
    public int fungusStarvationTtlPenalty = 3;

    [Tooltip("Extra survival fraction while starved (0.2 = 20% longer before collapse)")]
    [Range(0f, 1f)]
    public float fungusStarvationGraceFraction = 0.5f;

    [Tooltip("Maximum successful spread events each fungus tile can perform")]
    [Range(0, 20)]
    public int fungusSpreadEventsPerTile = 10;

    [Header("Infection")]
    [Tooltip("Chance per tick of infecting a healthy herbivore on a fungus tile")]
    [Range(0f, 1f)]
    public float infectionChancePerTick = 0.009f;

    [Tooltip("Chance per tick of infecting a healthy herbivore by close proximity to infected herbivores")]
    [Range(0f, 1f)]
    public float proximityInfectionChancePerTick = 0.0035f;

    [Tooltip("Multiplier on infection aura radius for close-proximity infection checks")]
    [Range(0.1f, 2f)]
    public float proximityInfectionRadiusMultiplier = 0.45f;

    [Tooltip("Infection radius used by infected herd to corral others")]
    public float infectionAuraRadius = 3.5f;

    [Tooltip("Ticks until an infected entity dies")]
    public int infectionDeathTicks = 600;

    [Header("Movement")]
    public float baseSpeed = 1.2f;
    public float infectedSpeedMultiplier = 1.35f;   // infected move faster early
    public float dyingSpeedMultiplier = 0.4f;        // slow near death
    public float steeringSmoothing = 6f;             // higher = smoother turns

    [Header("Grass Regrowth")]
    [Tooltip("Chance per tick that a dead-soil tile becomes grass")]
    [Range(0f, 1f)]
    public float grassRegrowthChance = 0.002f;

    [Tooltip("Multiplier applied to dead-soil regrowth once rain-recovery delay has passed")]
    [Range(1f, 30f)]
    public float postRainGrassRegrowthMultiplier = 8f;

    [Tooltip("Chance per tick that acid soil recovers back to grass after rain-recovery delay")]
    [Range(0f, 1f)]
    public float acidSoilRegrowthChance = 0.04f;

    [Tooltip("Real-time seconds after rain ends before rapid terrain recovery begins")]
    [Range(0f, 60f)]
    public float rainRecoveryDelaySeconds = 10f;

    [Tooltip("Ticks dead soil remains brown before it can regrow grass")]
    [Range(0, 4000)]
    public int deadSoilRecoveryDelayTicks = 180;

    [Header("Quadtree")]
    public int quadtreeCapacity = 5;   
    public int quadtreeMaxDepth = 12;

    [Header("Simulation Time")]
    public float tickInterval = 0.05f; 

    [Header("Breeding")]
    public bool enableBreeding = true;

    [Tooltip("Base chance per tick to breed when two compatible healthy herbivores are close")]
    [Range(0f, 0.5f)]
    public float breedingBaseChancePerTick = 0.05f;

    [Tooltip("Maximum distance between two healthy herbivores to attempt breeding")]
    [Range(0.01f, 4f)]
    public float breedingRange = 0.9f;

    [Tooltip("Minimum age in ticks before a herbivore can breed")]
    [Range(1, 5000)]
    public int breedingMinAgeTicks = 120;

    [Tooltip("Cooldown ticks after breeding before the same herbivore can breed again")]
    [Range(1, 5000)]
    public int breedingCooldownTicks = 260;

    [Tooltip("Herbivores must be below this hunger value to be eligible for breeding")]
    [Range(0f, 100f)]
    public float breedingMaxHunger = 45f;

    [Tooltip("Maximum births allowed each simulation tick")]
    [Range(1, 20)]
    public int maxBirthsPerTick = 2;

    [Header("Shelter")]
    [Tooltip("Number of permanent shelter clusters (grey tiles) on the board")]
    [Range(1, 20)]
    public int shelterClusterCount = 5;

    [Tooltip("Radius of each shelter cluster in tiles")]
    [Range(1, 8)]
    public int shelterClusterRadius = 2;

    [Tooltip("Minimum healthy herbivores spawned directly into shelter at startup")]
    [Range(0, 40)]
    public int initialShelterResidents = 6;

    [Header("Acid Rain Cycle")]
    public bool enableAcidRain = true;

    [Tooltip("Ticks between acid rain waves")]
    [Range(100, 10000)]
    public int acidRainCycleTicks = 6000;

    [Tooltip("Ticks rain remains active once it starts")]
    [Range(20, 9000)]
    public int acidRainDurationTicks = 6000;

    [Tooltip("Ticks before rain where perceptive animals start seeking shelter")]
    [Range(10, 2000)]
    public int acidRainWarningTicks = 340;

    [Tooltip("Fraction of herbivores that can detect incoming rain before it starts")]
    [Range(0f, 1f)]
    public float acidRainEarlyAwarenessChance = 0.16f;

    [Tooltip("Per tick chance grass is destroyed by acid rain")]
    [Range(0f, 1f)]
    public float acidRainGrassDestroyChance = 0.3f;

    [Tooltip("Per tick chance fungus is destroyed by acid rain")]
    [Range(0f, 1f)]
    public float acidRainFungusDestroyChance = 0.55f;

    [Tooltip("Per tick chance fertile soil is destroyed by acid rain")]
    [Range(0f, 1f)]
    public float acidRainFertileDestroyChance = 0.4f;

    [Tooltip("Chance each tick an exposed herbivore dies during active acid rain")]
    [Range(0f, 1f)]
    public float acidRainExposedDeathChance = 0.18f;

    [Tooltip("Radius of the moving acid-rain front in tiles")]
    [Range(1f, 30f)]
    public float acidRainFrontRadiusTiles = 8f;

    [Tooltip("Soft falloff edge around rain front in tiles")]
    [Range(0.2f, 12f)]
    public float acidRainFrontFeatherTiles = 2f;

    [Tooltip("How fast the acid-rain front drifts across the map (tiles per simulated second)")]
    [Range(0.1f, 1000f)]
    public float acidRainFrontDriftTilesPerSecond = 1.2f;

    [Tooltip("If enabled, guarantees at least one infected herbivore is sheltered before rain starts")]
    public bool ensureShelteredInfectedBeforeRain = true;

    [Header("Fertility")]
    [Tooltip("Radius around non-infected corpse that becomes fertile soil")]
    [Range(1, 6)]
    public int fertiliseRadius = 5;

    [Tooltip("How long fertile soil persists before reverting")]
    [Range(20, 4000)]
    public int fertileSoilDurationTicks = 420;
}
