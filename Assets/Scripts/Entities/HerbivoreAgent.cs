using UnityEngine;
using System.Collections.Generic;

public enum HerbivoreState
{
    Wandering,
    SeekingGrass,
    Fleeing,
    SeekingShelter,
    Infected_Wandering,
    Infected_Herding,   // infected herding behaviour: corralling healthy animals
    Dying,
    Dead
}

/// <summary>
/// Core herbivore agent. Driven by personality-weighted steering forces.
/// Infection progresses over ticks; death spawns fungus.
/// </summary>
[RequireComponent(typeof(SpriteRenderer))]
public class HerbivoreAgent : MonoBehaviour
{
    // ── Identity ─────────────────────────────────
    public int EntityId { get; private set; }
    public HerbivorePersonality Personality { get; private set; }

    // ── State ─────────────────────────────────────
    public HerbivoreState State { get; private set; } = HerbivoreState.Wandering;
    public bool IsInfected => _infectionTicks > 0;
    public bool IsDead => State == HerbivoreState.Dead;
    public float InfectionProgress => _infectionTicks / (float)_config.infectionDeathTicks;

    // ── Stats (visible in inspector / side panel) ──
    public float Health { get; private set; } = 100f;
    public float Hunger { get; private set; } = 0f;
    public TileType CurrentTile { get; private set; }
    public int NearbyCount { get; private set; }
    public int QuadtreeDepth { get; private set; }
    public int LastSavedCandidates { get; private set; }
    public int AgeTicks { get; private set; }
    public bool IsBreedReady => !IsDead && !IsInfected && AgeTicks >= _config.breedingMinAgeTicks && _breedCooldownTicks <= 0 && Hunger <= _config.breedingMaxHunger;

    // ── Position ──────────────────────────────────
    public Vector2 Position => (Vector2)transform.position;

    // ── Internal ──────────────────────────────────
    private SimulationConfig _config;
    private TileGrid _grid;
    private SimulationBoard _board;
    private SpriteRenderer _sr;

    private Vector2 _velocity;
    private Vector2 _steeringTarget;
    private int _infectionTicks;
    private float _wanderAngle;
    private float _freezeTimer;      // Timid freeze behaviour
    private int _breedCooldownTicks;
    private string _currentAction = "Idle";
    private string _lastDecisionReason = "No decision yet.";
    private int _lastInfectedNearby;
    private bool _lastThreatenedByHerd;
    private bool _canSenseRainEarly;

    // Path trail
    private readonly Queue<Vector2> _pathHistory = new();
    private const int PathLength = 30;

    private static int _nextId = 0;

    // ── Colors ────────────────────────────────────
    private static readonly Color InfectedColor  = new(0.7f, 0.1f, 0.9f);
    private static readonly Color DyingColor     = new(0.4f, 0.05f, 0.5f);
    private static readonly Color DemoVisibleYellow = new(1f, 0.95f, 0.15f);
    private const string RenderSortingLayer = "Terrain";
    private const int BaseSortOrder = 50;
    private const int SelectedSortOrder = 60;

    // ──────────────────────────────────────────────
    public void Initialise(SimulationConfig config, TileGrid grid, SimulationBoard board, PersonalityType? forcedType = null)
    {
        _config = config;
        _grid   = grid;
        _board  = board;
        _sr     = GetComponent<SpriteRenderer>();

        EntityId    = _nextId++;
        Personality = forcedType.HasValue
            ? HerbivorePersonality.FromType(forcedType.Value)
            : HerbivorePersonality.Random();

        _wanderAngle = Random.Range(0f, 360f);
        _velocity    = Random.insideUnitCircle * config.baseSpeed;
        float perceptionFactor = Mathf.Lerp(0.45f, 1.55f, Mathf.Clamp01(Personality.rainPerception / 3f));
        float awarenessChance = Mathf.Clamp01(config.acidRainEarlyAwarenessChance * perceptionFactor);
        _canSenseRainEarly = Random.value < awarenessChance;

        ApplyPersonalityVisuals();
    }

    private void ApplyPersonalityVisuals()
    {
        _sr.color = GetHealthyVisualColor();
        _sr.drawMode = SpriteDrawMode.Sliced;
        _sr.size = new Vector2(0.5f, 0.5f);
        transform.localScale = Vector3.one;
        _sr.sortingLayerName = RenderSortingLayer;
        _sr.sortingOrder = BaseSortOrder;
        UpdateActionTelemetry();
    }

    // ──────────────────────────────────────────────
    // Called every simulation tick by SimulationBoard
    // ──────────────────────────────────────────────
    public void SimulationTick(Quadtree qt, List<HerbivoreAgent> all, int totalCount)
    {
        if (IsDead) return;

        AgeTicks++;
        if (_breedCooldownTicks > 0) _breedCooldownTicks--;

        CurrentTile = _grid.GetTileAtWorld(Position);

        // ── Infection check ──────────────────────
        if (!IsInfected && CurrentTile == TileType.Fungus)
        {
            float chance = _config.infectionChancePerTick * Personality.infectionSusceptibility;
            if (Random.value < chance)
                Infect();
        }

        // ── Infection progression ─────────────────
        if (IsInfected)
        {
            bool sheltered = CurrentTile == TileType.Shelter;
            bool pauseInfectionInShelter = sheltered;
            if (!pauseInfectionInShelter)
                _infectionTicks++;
            Health = Mathf.Max(0f, 100f - (InfectionProgress * 100f));

            if (_infectionTicks >= _config.infectionDeathTicks)
            {
                Die();
                return;
            }
        }

        // ── Hunger ───────────────────────────────
        Hunger = Mathf.Min(100f, Hunger + 0.13f);
        if (CurrentTile == TileType.Grass) Hunger = Mathf.Max(0f, Hunger - 0.45f);
        if (CurrentTile == TileType.FertileSoil) Hunger = Mathf.Max(0f, Hunger - 0.35f);
        if (CurrentTile == TileType.Shelter)
            Hunger = Mathf.Max(0f, Hunger - (_board.IsAcidRainActive ? 0.2f : 0.08f));

        if (Hunger >= 100f)
        {
            Health = Mathf.Max(0f, Health - 2.25f);
            if (Health <= 0f)
            {
                Die();
                return;
            }
        }

        // ── Freeze (Timid) ───────────────────────
        if (_freezeTimer > 0f)
        {
            _freezeTimer -= _config.tickInterval;
            return;
        }

        // ── Quadtree queries ─────────────────────
        int saved = 0;
        var nearby = qt.QueryRadius(Position, _config.infectionAuraRadius * 2f, totalCount, ref saved);
        LastSavedCandidates = saved;
        NearbyCount = nearby.Count;
        TryProximityInfection(nearby);

        // ── State machine ────────────────────────
        UpdateState(nearby);
        UpdateActionTelemetry();

        // ── Steering ─────────────────────────────
        Vector2 force = ComputeSteering(nearby);
        if (force.sqrMagnitude < 0.0001f)
            force = Wander();

        float speed = _config.baseSpeed;
        if (IsInfected)
            speed *= InfectionProgress < 0.7f ? _config.infectedSpeedMultiplier : _config.dyingSpeedMultiplier;
        Vector2 desiredVelocity = force.normalized * speed;
        _velocity = Vector2.Lerp(_velocity, desiredVelocity, Time.deltaTime * _config.steeringSmoothing);
        Vector2 edgeDeflection = AvoidBoardEdges();
        if (edgeDeflection.sqrMagnitude > 0.0001f)
            _velocity = Vector2.Lerp(_velocity, (_velocity + edgeDeflection.normalized * speed).normalized * speed, 0.65f);

        // Clamp to board
        Vector2 nextPos = Position + _velocity * _config.tickInterval;
        if (!_grid.IsInsideBoard(nextPos))
        {
            Vector2 boardCenter = (Vector2)_grid.transform.position;
            _velocity = (boardCenter - Position).normalized * speed;
            nextPos = ClampInsideBoard(Position + _velocity * _config.tickInterval);
        }
        else
        {
            nextPos = ClampInsideBoard(nextPos);
        }

        transform.position = (Vector3)nextPos;

        // Store path
        _pathHistory.Enqueue(Position);
        if (_pathHistory.Count > PathLength) _pathHistory.Dequeue();

        UpdateVisuals();
    }

    private void UpdateState(List<HerbivoreAgent> nearby)
    {
        bool acidActive = _board.IsAcidRainActive;
        bool acidImminent = _board.IsAcidRainImminent;
        bool shelterNow = CurrentTile == TileType.Shelter;
        int earlyShelterThreshold = Mathf.RoundToInt(Mathf.Lerp(40f, _config.acidRainWarningTicks, Mathf.Clamp01(Personality.rainPerception / 3f)));
        bool shouldSeekShelterEarly = _canSenseRainEarly && acidImminent && _board.TicksUntilAcidRain <= earlyShelterThreshold;

        if ((acidActive && !shelterNow) || shouldSeekShelterEarly)
        {
            State = HerbivoreState.SeekingShelter;
            _lastDecisionReason = acidActive
                ? "Acid rain is active, moving to nearest shelter tile."
                : "Perceived acid rain threat, seeking shelter early.";
            _lastThreatenedByHerd = false;
            _lastInfectedNearby = 0;
            return;
        }

        if (shelterNow && (acidActive || acidImminent))
        {
            State = HerbivoreState.SeekingShelter;
            _lastDecisionReason = acidActive
                ? "Staying under shelter while acid rain is active."
                : "Holding shelter position until the rain front arrives.";
            _lastThreatenedByHerd = false;
            _lastInfectedNearby = 0;
            return;
        }

        if (IsInfected)
        {
            // Check if other infected animals are nearby to form a herd
            int infectedNearby = 0;
            foreach (var n in nearby)
                if (n != this && n.IsInfected && !n.IsDead) infectedNearby++;

            _lastInfectedNearby = infectedNearby;
            _lastThreatenedByHerd = false;

            State = infectedNearby >= 2
                ? HerbivoreState.Infected_Herding
                : HerbivoreState.Infected_Wandering;

            _lastDecisionReason = infectedNearby >= 2
                ? "Detected 2+ infected neighbors, switching to herd behavior."
                : "Not enough infected neighbors, staying in infected wandering mode.";

            if (InfectionProgress > 0.8f)
            {
                State = HerbivoreState.Dying;
                _lastDecisionReason = "Infection exceeded 80%, entering dying state.";
            }
        }
        else
        {
            // Check for infected herds corralling nearby
            bool threatenedByHerd = false;
            int infectedHerdNearby = 0;
            int anyInfectedNearby = 0;
            foreach (var n in nearby)
            {
                if (n != this && n.IsInfected && !n.IsDead)
                    anyInfectedNearby++;

                if (n.State == HerbivoreState.Infected_Herding)
                {
                    infectedHerdNearby++;
                    float corralInfluence = Personality.corralVulnerability;
                    float threatChance = Mathf.Clamp01(0.06f + corralInfluence * 0.08f + Personality.infectedAvoidance * 0.05f);
                    if (Random.value < threatChance)
                    {
                        threatenedByHerd = true;
                        // Timid freezes
                        if (Personality.type == PersonalityType.Timid)
                            _freezeTimer = 0.5f;
                        break;
                    }
                }
            }

            _lastInfectedNearby = infectedHerdNearby;
            _lastThreatenedByHerd = threatenedByHerd;
            bool fungusDanger = CurrentTile == TileType.Fungus || Personality.fungusAvoidance > 1.7f && IsFungusNearby(2);
            bool infectedDanger = threatenedByHerd || anyInfectedNearby >= Mathf.CeilToInt(Mathf.Lerp(3f, 1f, Mathf.Clamp01(Personality.infectedAvoidance / 3f)));
            bool urgentFlee = fungusDanger || infectedDanger;

            if (urgentFlee)
            {
                State = HerbivoreState.Fleeing;
                if (CurrentTile == TileType.Fungus)
                {
                    _lastDecisionReason = "Standing on fungus, fleeing to safer ground.";
                }
                else if (infectedDanger)
                {
                    _lastDecisionReason = "Infected neighbors/herd pressure detected, fleeing.";
                }
                else
                {
                    _lastDecisionReason = "Fungus detected nearby, personality triggered early flee.";
                }
            }
            else if (Hunger > 60f)
            {
                bool hostileWorld = _board.IsLandDestroyed || (_grid.GrassTiles <= Mathf.Max(1, _grid.Width / 10));
                State = hostileWorld ? HerbivoreState.Wandering : HerbivoreState.SeekingGrass;
                _lastDecisionReason = hostileWorld
                    ? "Food scarcity after land collapse, roaming for any safe forage."
                    : "Hunger is high (>60), seeking grass.";
            }
            else
            {
                State = HerbivoreState.Wandering;
                _lastDecisionReason = "No immediate threat and hunger is manageable, wandering.";
            }
        }
    }

    private void UpdateActionTelemetry()
    {
        switch (State)
        {
            case HerbivoreState.Wandering:
                _currentAction = "Wandering and loosely grouping with healthy neighbors.";
                break;
            case HerbivoreState.SeekingGrass:
                _currentAction = "Seeking nearest grass to lower hunger.";
                break;
            case HerbivoreState.Fleeing:
                _currentAction = "Fleeing away from fungus or infected-herd pressure.";
                break;
            case HerbivoreState.SeekingShelter:
                _currentAction = "Navigating to grey shelter tiles before/while acid rain falls.";
                break;
            case HerbivoreState.Infected_Wandering:
                _currentAction = "Infected roaming and drifting toward fungal tiles.";
                break;
            case HerbivoreState.Infected_Herding:
                _currentAction = "Coordinating with infected herd and corralling healthy herbivores.";
                break;
            case HerbivoreState.Dying:
                _currentAction = "Slowed movement while infection reaches lethal stage.";
                break;
            case HerbivoreState.Dead:
                _currentAction = "Inactive (dead).";
                break;
        }
    }

    private string GetUpcomingDecisionPreview()
    {
        if (State == HerbivoreState.Dead)
            return "- No upcoming decisions (entity is dead).";

        if (_board.IsAcidRainActive || _board.IsAcidRainImminent)
        {
            return
                $"- Acid rain active: {_board.IsAcidRainActive} / imminent: {_board.IsAcidRainImminent} (ticks remaining {_board.TicksUntilAcidRain}).\n" +
                "- If exposed, prioritize shelter tile pathing immediately.\n" +
                "- Under shelter, remain stable and attempt breeding if partner available.";
        }

        if (IsInfected)
        {
            return
                $"- Death check: if infection > 80%, switch to Dying (current {InfectionProgress * 100f:F0}%).\n" +
                $"- Herd check: if infected neighbors >= 2, switch to Infected_Herding (current {_lastInfectedNearby}).\n" +
                "- Otherwise continue infected wandering and seek fungal regions.";
        }

        return
            $"- Threat check: if on fungus or herd pressure rises, switch to Fleeing (herd nearby {_lastInfectedNearby}, threatened {_lastThreatenedByHerd}).\n" +
            $"- Hunger check: if hunger > 60, switch to SeekingGrass (current {Hunger:F0}).\n" +
            "- Otherwise continue wandering and social cohesion.";
    }

    private Vector2 ComputeSteering(List<HerbivoreAgent> nearby)
    {
        Vector2 force = Vector2.zero;

        switch (State)
        {
            case HerbivoreState.Wandering:
            case HerbivoreState.SeekingGrass:
                force += Wander();
                if (State == HerbivoreState.SeekingGrass)
                    force += SeekGrass() * 2f;
                force += SocialCohesion(nearby) * Personality.socialAttraction;
                break;

            case HerbivoreState.Fleeing:
                force += Flee() * Personality.fleeStrength * 3f;
                force += Wander() * 0.3f;
                break;

            case HerbivoreState.SeekingShelter:
                force += SeekShelter() * 3f;
                if (CurrentTile != TileType.Shelter)
                    force += Wander() * 0.15f;
                break;

            case HerbivoreState.Infected_Wandering:
                force += Wander() * (1f + Personality.wanderNoise);
                force += SeekFungus() * 0.8f;
                break;

            case HerbivoreState.Infected_Herding:
                force += InfectedHerdSteering(nearby);
                force += CorralHealthy(nearby) * Personality.corralVulnerability;
                break;

            case HerbivoreState.Dying:
                force += Wander() * 0.2f;
                break;
        }

        force += AvoidBoardEdges() * (IsInfected ? 0.9f : 1.8f);
        if (!IsInfected)
        {
            force += AvoidLocalHazards(nearby) * 1.6f;
            force = RefineHealthyRoute(force, nearby);
        }

        return force;
    }

    // ── Steering behaviours ───────────────────────

    private Vector2 Wander()
    {
        _wanderAngle += Random.Range(-1f, 1f) * 30f * Personality.wanderNoise;
        return new Vector2(Mathf.Cos(_wanderAngle * Mathf.Deg2Rad), Mathf.Sin(_wanderAngle * Mathf.Deg2Rad));
    }

    private Vector2 SeekGrass()
    {
        Vector2 target = _grid.NearestGrassWorld(Position, 8);
        if (_board.IsLandDestroyed || target == Position)
            target = _grid.NearestFertileWorld(Position, 10);
        return (target - Position).normalized;
    }

    private Vector2 SeekShelter()
    {
        Vector2 target = _grid.NearestShelterWorld(Position, 16);
        Vector2 dir = target - Position;
        return dir.sqrMagnitude < 0.0001f ? Vector2.zero : dir.normalized;
    }

    private Vector2 Flee()
    {
        // Flee away from fungus tiles and infected herds
        Vector2 away = Vector2.zero;
        Vector2 nearestFungus = FindNearestTileWorld(TileType.Fungus, 8);
        if (nearestFungus != Vector2.zero)
            away += (Position - nearestFungus).normalized * (1.5f * Personality.fungusAvoidance);

        Vector2 nearestInfected = FindNearestInfectedVector(6f);
        if (nearestInfected != Vector2.zero)
            away += nearestInfected.normalized * (1.25f * Personality.infectedAvoidance);

        if (CurrentTile == TileType.Fungus)
        {
            Vector2 grassTarget = _grid.NearestGrassWorld(Position, 10);
            away += (grassTarget - Position).normalized * 2.2f;
        }

        return away + Wander() * Mathf.Lerp(0.25f, 0.6f, Personality.wanderNoise * 0.5f);
    }

    private Vector2 SocialCohesion(List<HerbivoreAgent> nearby)
    {
        Vector2 center = Vector2.zero;
        int count = 0;
        foreach (var n in nearby)
        {
            if (n == this || n.IsInfected || n.IsDead) continue;
            center += n.Position;
            count++;
        }
        if (count == 0) return Vector2.zero;
        center /= count;
        return (center - Position).normalized * 0.5f;
    }

    private Vector2 SeekFungus()
    {
        // Infected animals drift toward fungal regions
        _grid.WorldToGrid(Position, out int cx, out int cy);
        for (int dx = -4; dx <= 4; dx++)
        for (int dy = -4; dy <= 4; dy++)
        {
            if (_grid.GetTile(cx + dx, cy + dy) == TileType.Fungus)
            {
                Vector2 target = _grid.GridToWorld(cx + dx, cy + dy);
                return (target - Position).normalized;
            }
        }
        return Vector2.zero;
    }

    private Vector2 InfectedHerdSteering(List<HerbivoreAgent> nearby)
    {
        // Move together with infected peers and take interception lines that pressure healthy groups into fungal zones.
        Vector2 center = Vector2.zero;
        int count = 0;
        foreach (var n in nearby)
        {
            if (n == this || !n.IsInfected || n.IsDead) continue;
            center += n.Position;
            count++;
        }
        Vector2 cohesion = count > 0 ? (center / count - Position).normalized : Vector2.zero;
        Vector2 fungusBias = SeekFungus();
        Vector2 intercept = CorralHealthy(nearby) * 1.55f;
        Vector2 containment = BuildContainmentArc(nearby) * 1.2f;
        float fungusBiasScale = CountNearbyHealthy(nearby) > 0 ? 1.4f : 0.85f;
        return cohesion * 1.2f + fungusBias * fungusBiasScale + intercept + containment + Wander() * 0.12f;
    }

    private Vector2 CorralHealthy(List<HerbivoreAgent> nearby)
    {
        // Infected units prefer moving to "behind" healthy targets relative to nearby fungus, creating better funneling.
        Vector2 toward = Vector2.zero;
        float weightSum = 0f;
        foreach (var n in nearby)
        {
            if (n == this || n.IsInfected || n.IsDead) continue;

            Vector2 toHealthyVector = n.Position - Position;
            float dist = Mathf.Max(0.001f, toHealthyVector.magnitude);
            Vector2 toHealthy = toHealthyVector / dist;

            Vector2 fungusNearHealthy = _grid.NearestFungusWorld(n.Position, 10);
            bool hasFungusAnchor = (fungusNearHealthy - n.Position).sqrMagnitude > _config.tileSize * _config.tileSize * 0.25f;
            Vector2 pushTowardFungus = hasFungusAnchor
                ? (fungusNearHealthy - n.Position).normalized
                : toHealthy;

            Vector2 stagingPoint = n.Position - pushTowardFungus * Mathf.Lerp(0.9f, 1.8f, Mathf.Clamp01(Personality.corralVulnerability / 2.5f));
            Vector2 interceptDir = (stagingPoint - Position).sqrMagnitude > 0.0001f
                ? (stagingPoint - Position).normalized
                : toHealthy;

            float herdWeight = Mathf.Lerp(0.85f, 1.35f, Mathf.Clamp01(n.Personality.corralVulnerability / 2.5f));
            float distWeight = 1f / (1f + dist);
            float weight = herdWeight * distWeight;

            toward += ((interceptDir * 1.55f) + (pushTowardFungus * (hasFungusAnchor ? 1.25f : 0.55f)) + (toHealthy * 0.35f)) * weight;
            weightSum += weight;
        }
        return weightSum > 0f ? toward / weightSum : Vector2.zero;
    }

    private int CountNearbyHealthy(List<HerbivoreAgent> nearby)
    {
        int count = 0;
        foreach (var n in nearby)
        {
            if (n == null || n == this || n.IsDead || n.IsInfected) continue;
            count++;
        }
        return count;
    }

    private Vector2 BuildContainmentArc(List<HerbivoreAgent> nearby)
    {
        Vector2 healthyCenter = Vector2.zero;
        int healthyCount = 0;
        foreach (var n in nearby)
        {
            if (n == null || n == this || n.IsDead || n.IsInfected) continue;
            healthyCenter += n.Position;
            healthyCount++;
        }

        if (healthyCount == 0) return Vector2.zero;
        healthyCenter /= healthyCount;

        Vector2 fungusAnchor = _grid.NearestFungusWorld(healthyCenter, 12);
        if ((fungusAnchor - healthyCenter).sqrMagnitude < _config.tileSize * _config.tileSize * 0.25f)
            return Vector2.zero;

        Vector2 funnelDir = (fungusAnchor - healthyCenter).normalized;
        Vector2 arcPoint = healthyCenter - funnelDir * 1.4f;
        Vector2 toArc = arcPoint - Position;
        return toArc.sqrMagnitude > 0.0001f ? toArc.normalized : Vector2.zero;
    }

    private void TryProximityInfection(List<HerbivoreAgent> nearby)
    {
        if (IsInfected || IsDead || nearby == null || nearby.Count == 0) return;

        float radiusMultiplier = Mathf.Max(0.1f, _config.proximityInfectionRadiusMultiplier);
        float infectionRadius = Mathf.Max(_config.tileSize * 0.75f, _config.infectionAuraRadius * radiusMultiplier);
        float radiusSq = infectionRadius * infectionRadius;
        float pressure = 0f;
        int closeInfected = 0;

        foreach (var n in nearby)
        {
            if (n == null || n == this || n.IsDead || !n.IsInfected) continue;

            float d2 = (n.Position - Position).sqrMagnitude;
            if (d2 > radiusSq) continue;

            closeInfected++;
            float dist = Mathf.Sqrt(d2);
            float closeness = 1f - Mathf.Clamp01(dist / infectionRadius);
            float herdBonus = n.State == HerbivoreState.Infected_Herding ? 1.35f : 1f;
            pressure += closeness * herdBonus;
        }

        if (closeInfected == 0) return;

        float susceptibility = Mathf.Clamp(Personality.infectionSusceptibility, 0.2f, 3f);
        float baseChance = Mathf.Clamp01(_config.proximityInfectionChancePerTick * susceptibility);
        float chance = 1f - Mathf.Pow(1f - baseChance, Mathf.Max(1f, pressure));
        if (Random.value < chance)
        {
            Infect();
            _lastDecisionReason = "Infected by close contact with infected herd pressure.";
        }
    }

    private Vector2 AvoidLocalHazards(List<HerbivoreAgent> nearby)
    {
        Vector2 avoidance = Vector2.zero;

        _grid.WorldToGrid(Position, out int cx, out int cy);
        for (int dx = -2; dx <= 2; dx++)
        {
            for (int dy = -2; dy <= 2; dy++)
            {
                if (dx == 0 && dy == 0) continue;
                int gx = cx + dx;
                int gy = cy + dy;
                if (gx < 0 || gx >= _grid.Width || gy < 0 || gy >= _grid.Height) continue;

                TileType tile = _grid.GetTile(gx, gy);
                float tileWeight = tile switch
                {
                    TileType.Fungus => 1.4f,
                    TileType.AcidSoil => 1.1f,
                    TileType.DeadSoil => 0.25f,
                    _ => 0f
                };
                if (tileWeight <= 0f) continue;

                Vector2 tileWorld = _grid.GridToWorld(gx, gy);
                Vector2 delta = Position - tileWorld;
                float d2 = Mathf.Max(0.01f, delta.sqrMagnitude);
                avoidance += delta.normalized * (tileWeight / d2);
            }
        }

        foreach (var n in nearby)
        {
            if (n == null || n == this || n.IsDead || !n.IsInfected) continue;
            Vector2 delta = Position - n.Position;
            float d2 = Mathf.Max(0.04f, delta.sqrMagnitude);
            if (d2 > _config.infectionAuraRadius * _config.infectionAuraRadius * 2.5f) continue;
            float herdWeight = n.State == HerbivoreState.Infected_Herding ? 2.2f : 1.5f;
            avoidance += delta.normalized * (herdWeight / d2);
        }

        return avoidance;
    }

    private Vector2 RefineHealthyRoute(Vector2 preferredForce, List<HerbivoreAgent> nearby)
    {
        if (preferredForce.sqrMagnitude < 0.0001f)
            return preferredForce;

        Vector2 baseDir = preferredForce.normalized;
        float lookAhead = Mathf.Max(_config.tileSize * 1.6f, _config.baseSpeed * _config.tickInterval * 6f);
        Vector2 bestDir = baseDir;
        float bestRisk = EvaluateRouteRisk(baseDir, lookAhead, nearby);

        for (int i = 1; i <= 6; i++)
        {
            float angle = 22.5f * i;
            Vector2 left = Rotate(baseDir, angle);
            float leftRisk = EvaluateRouteRisk(left, lookAhead, nearby);
            if (leftRisk < bestRisk)
            {
                bestRisk = leftRisk;
                bestDir = left;
            }

            Vector2 right = Rotate(baseDir, -angle);
            float rightRisk = EvaluateRouteRisk(right, lookAhead, nearby);
            if (rightRisk < bestRisk)
            {
                bestRisk = rightRisk;
                bestDir = right;
            }
        }

        return bestDir * preferredForce.magnitude;
    }

    private float EvaluateRouteRisk(Vector2 direction, float lookAhead, List<HerbivoreAgent> nearby)
    {
        if (direction.sqrMagnitude < 0.0001f) return float.MaxValue;
        Vector2 dir = direction.normalized;
        float risk = 0f;

        for (int step = 1; step <= 3; step++)
        {
            float t = step / 3f;
            Vector2 sample = Position + dir * lookAhead * t;
            risk += SampleHazardRisk(sample, nearby) * (0.8f + t);
        }

        return risk;
    }

    private float SampleHazardRisk(Vector2 sampleWorld, List<HerbivoreAgent> nearby)
    {
        if (!_grid.IsInsideBoard(sampleWorld))
            return 999f;

        float risk = 0f;
        TileType tile = _grid.GetTileAtWorld(sampleWorld);
        risk += tile switch
        {
            TileType.Fungus => 9f,
            TileType.AcidSoil => 6.5f,
            TileType.DeadSoil => 1.2f,
            TileType.FertileSoil => 0.35f,
            TileType.Shelter => 0.1f,
            _ => 0f
        };

        foreach (var n in nearby)
        {
            if (n == null || n.IsDead || !n.IsInfected) continue;
            float dist = Vector2.Distance(sampleWorld, n.Position);
            float threatRadius = _config.infectionAuraRadius * 1.25f;
            if (dist >= threatRadius) continue;

            float closeness = 1f - Mathf.Clamp01(dist / threatRadius);
            float herdBonus = n.State == HerbivoreState.Infected_Herding ? 1.7f : 1f;
            risk += closeness * 7f * herdBonus;
        }

        risk += EdgePressure(sampleWorld) * 4.5f;
        return risk;
    }

    private float EdgePressure(Vector2 worldPos)
    {
        float halfW = _grid.Width * _config.tileSize * 0.5f;
        float halfH = _grid.Height * _config.tileSize * 0.5f;
        Vector2 center = _grid.transform.position;
        float minX = center.x - halfW;
        float maxX = center.x + halfW;
        float minY = center.y - halfH;
        float maxY = center.y + halfH;

        float safeMargin = Mathf.Max(_config.tileSize * 2.2f, 0.1f);
        float left = Mathf.Clamp01((safeMargin - (worldPos.x - minX)) / safeMargin);
        float right = Mathf.Clamp01((safeMargin - (maxX - worldPos.x)) / safeMargin);
        float bottom = Mathf.Clamp01((safeMargin - (worldPos.y - minY)) / safeMargin);
        float top = Mathf.Clamp01((safeMargin - (maxY - worldPos.y)) / safeMargin);
        return left + right + bottom + top;
    }

    private Vector2 AvoidBoardEdges()
    {
        float halfW = _grid.Width * _config.tileSize * 0.5f;
        float halfH = _grid.Height * _config.tileSize * 0.5f;
        Vector2 center = _grid.transform.position;
        float minX = center.x - halfW;
        float maxX = center.x + halfW;
        float minY = center.y - halfH;
        float maxY = center.y + halfH;
        float margin = Mathf.Max(_config.tileSize * 2f, 0.1f);

        Vector2 push = Vector2.zero;
        float leftDist = Position.x - minX;
        float rightDist = maxX - Position.x;
        float bottomDist = Position.y - minY;
        float topDist = maxY - Position.y;

        if (leftDist < margin) push += Vector2.right * (1f - Mathf.Clamp01(leftDist / margin));
        if (rightDist < margin) push += Vector2.left * (1f - Mathf.Clamp01(rightDist / margin));
        if (bottomDist < margin) push += Vector2.up * (1f - Mathf.Clamp01(bottomDist / margin));
        if (topDist < margin) push += Vector2.down * (1f - Mathf.Clamp01(topDist / margin));

        return push;
    }

    private Vector2 ClampInsideBoard(Vector2 worldPos)
    {
        float halfW = _grid.Width * _config.tileSize * 0.5f;
        float halfH = _grid.Height * _config.tileSize * 0.5f;
        Vector2 center = _grid.transform.position;
        float margin = Mathf.Max(_config.tileSize * 0.35f, 0.01f);

        return new Vector2(
            Mathf.Clamp(worldPos.x, center.x - halfW + margin, center.x + halfW - margin),
            Mathf.Clamp(worldPos.y, center.y - halfH + margin, center.y + halfH - margin)
        );
    }

    private static Vector2 Rotate(Vector2 v, float degrees)
    {
        float rad = degrees * Mathf.Deg2Rad;
        float sin = Mathf.Sin(rad);
        float cos = Mathf.Cos(rad);
        return new Vector2(v.x * cos - v.y * sin, v.x * sin + v.y * cos);
    }

    private bool IsFungusNearby(int searchRadius)
    {
        _grid.WorldToGrid(Position, out int cx, out int cy);
        for (int dx = -searchRadius; dx <= searchRadius; dx++)
        {
            for (int dy = -searchRadius; dy <= searchRadius; dy++)
            {
                if (_grid.GetTile(cx + dx, cy + dy) == TileType.Fungus)
                    return true;
            }
        }

        return false;
    }

    private Vector2 FindNearestTileWorld(TileType type, int searchRadius)
    {
        _grid.WorldToGrid(Position, out int cx, out int cy);
        int bestDist = int.MaxValue;
        int bestX = cx;
        int bestY = cy;
        bool found = false;

        for (int dx = -searchRadius; dx <= searchRadius; dx++)
        {
            for (int dy = -searchRadius; dy <= searchRadius; dy++)
            {
                int gx = cx + dx;
                int gy = cy + dy;
                if (gx < 0 || gx >= _grid.Width || gy < 0 || gy >= _grid.Height)
                    continue;

                if (_grid.GetTile(gx, gy) != type)
                    continue;

                int dist = dx * dx + dy * dy;
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestX = gx;
                    bestY = gy;
                    found = true;
                }
            }
        }

        return found ? _grid.GridToWorld(bestX, bestY) : Vector2.zero;
    }

    private Vector2 FindNearestInfectedVector(float radius)
    {
        var agents = _board.GetAgents();
        if (agents == null) return Vector2.zero;

        float bestDist = radius * radius;
        Vector2 closest = Vector2.zero;
        bool found = false;

        foreach (var a in agents)
        {
            if (a == null || a == this || a.IsDead || !a.IsInfected) continue;
            Vector2 delta = a.Position - Position;
            float d2 = delta.sqrMagnitude;
            if (d2 < bestDist)
            {
                bestDist = d2;
                closest = delta;
                found = true;
            }
        }

        return found ? -closest : Vector2.zero;
    }

    // ──────────────────────────────────────────────

    public void Infect()
    {
        if (IsInfected) return;
        _infectionTicks = 1;
        State = HerbivoreState.Infected_Wandering;
        _lastDecisionReason = "Forced infection event triggered.";
        UpdateActionTelemetry();
    }

    public void ForceEnvironmentalDeath(string reason)
    {
        if (IsDead) return;
        _lastDecisionReason = reason;
        Die();
    }

    public void ForceRelocate(Vector2 worldPos)
    {
        transform.position = worldPos;
        _velocity = Vector2.zero;
        _pathHistory.Clear();
        _pathHistory.Enqueue(worldPos);
    }

    private void Die()
    {
        State = HerbivoreState.Dead;
        _lastDecisionReason = "Infection reached death threshold.";
        UpdateActionTelemetry();
        // Notify board to handle fungus spawn and cleanup
        _board.OnEntityDied(this);
        gameObject.SetActive(false);
    }

    private void UpdateVisuals()
    {
        if (!IsInfected)
        {
            _sr.color = GetHealthyVisualColor();
        }
        else
        {
            float t = InfectionProgress;
            Color c = Color.Lerp(GetHealthyVisualColor(), InfectedColor, t);
            if (State == HerbivoreState.Dying) c = Color.Lerp(InfectedColor, DyingColor, (t - 0.8f) / 0.2f);
            _sr.color = c;
        }
    }

    private Color GetHealthyVisualColor()
    {
        if (_config != null && _config.forceBrightYellowHerbivores)
            return DemoVisibleYellow;
        return Personality.healthyColor;
    }

    // ── Selection highlight ───────────────────────
    private bool _selected;
    public void SetSelected(bool sel)
    {
        _selected = sel;
        _sr.sortingLayerName = RenderSortingLayer;
        _sr.sortingOrder = sel ? SelectedSortOrder : BaseSortOrder;
    }

    public void DrawPath(LineRenderer lr)
    {
        var pts = _pathHistory.ToArray();
        lr.positionCount = pts.Length;
        for (int i = 0; i < pts.Length; i++)
            lr.SetPosition(i, (Vector3)pts[i]);
    }

    // ── Inspector/Panel info ──────────────────────
    public string GetPanelInfo()
    {
        return $"ID: {EntityId}\n" +
               $"Type: {Personality.type}\n" +
               $"State: {State}\n" +
               $"Health: {Health:F0}\n" +
               $"Hunger: {Hunger:F0}\n" +
               $"Age Ticks: {AgeTicks}\n" +
               $"Breed Ready: {IsBreedReady}\n" +
               $"Infected: {IsInfected}\n" +
               $"Infect %: {InfectionProgress * 100f:F0}%\n" +
               $"Tile: {CurrentTile}\n" +
               $"Nearby: {NearbyCount}\n\n" +
               $"Current Action:\n{_currentAction}\n\n" +
               $"Last Decision:\n{_lastDecisionReason}\n\n" +
               $"Upcoming Decisions:\n{GetUpcomingDecisionPreview()}";
    }

    public string GetDecisionTreeDiagram()
    {
        var header =
            $"Entity {EntityId} ({Personality.type})\n" +
            $"State: {State} | Tile: {CurrentTile} | Hunger: {Hunger:F0} | Infection: {InfectionProgress * 100f:F0}% | BreedReady: {IsBreedReady}\n" +
            $"Current Action: {_currentAction}\n" +
            $"Reason: {_lastDecisionReason}\n";

        if (State == HerbivoreState.Dead)
        {
            return header + "\nDecision Tree\n" +
                   "[Start]\n" +
                   "  +-- Is Dead? yes\n" +
                   "      +-- Action: inactive";
        }

        if (IsInfected)
        {
            return header + "\nDecision Tree\n" +
                   "[Start]\n" +
                   "  +-- Is Infected? yes\n" +
                   $"      +-- Infection > 80%? {(InfectionProgress > 0.8f ? "yes" : "no")} ({InfectionProgress * 100f:F0}%)\n" +
                   $"      |   +-- yes -> Dying\n" +
                   $"      +-- Infected neighbors >= 2? {(_lastInfectedNearby >= 2 ? "yes" : "no")} ({_lastInfectedNearby})\n" +
                   "          +-- yes -> Infected_Herding\n" +
                   "          +-- no  -> Infected_Wandering";
        }

        bool onFungus = CurrentTile == TileType.Fungus;
        return header + "\nDecision Tree\n" +
               "[Start]\n" +
               "  +-- Is Infected? no\n" +
               $"      +-- Acid rain active/imminent? {(_board.IsAcidRainActive || _board.IsAcidRainImminent ? "yes" : "no")}\n" +
               "      |   +-- yes -> Seek Shelter\n" +
               $"      +-- On Fungus tile? {(onFungus ? "yes" : "no")}\n" +
               $"      +-- Herd threat? {(_lastThreatenedByHerd ? "yes" : "no")} (infected herd nearby: {_lastInfectedNearby})\n" +
               $"      |   +-- yes -> Fleeing\n" +
               $"      +-- Hunger > 60? {(Hunger > 60f ? "yes" : "no")} ({Hunger:F0})\n" +
               "      |   +-- yes -> SeekingGrass\n" +
               "      +-- otherwise -> Wandering";
    }

    public bool CanBreedWith(HerbivoreAgent other)
    {
        if (other == null || other == this) return false;
        if (!IsBreedReady || !other.IsBreedReady) return false;
        if (Personality.type == PersonalityType.Timid && other.Personality.type == PersonalityType.Timid) return false;
        return true;
    }

    public float GetBreedingChanceModifier()
    {
        return Mathf.Clamp(Personality.breedingDrive * Mathf.Lerp(1.2f, 0.7f, Hunger / 100f), 0.1f, 2.5f);
    }

    public void MarkBred()
    {
        _breedCooldownTicks = _config.breedingCooldownTicks;
        Hunger = Mathf.Clamp(Hunger + 12f, 0f, 100f);
    }

    public static void ResetIdCounter() => _nextId = 0;
}
