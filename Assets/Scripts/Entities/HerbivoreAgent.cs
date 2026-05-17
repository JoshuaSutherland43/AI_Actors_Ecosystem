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

        // ── State machine ────────────────────────
        UpdateState(nearby);
        UpdateActionTelemetry();

        // ── Steering ─────────────────────────────
        Vector2 force = ComputeSteering(nearby);

        float speed = _config.baseSpeed;
        if (IsInfected)
            speed *= InfectionProgress < 0.7f ? _config.infectedSpeedMultiplier : _config.dyingSpeedMultiplier;
        _velocity = Vector2.Lerp(_velocity, force.normalized * speed, Time.deltaTime * _config.steeringSmoothing);

        // Clamp to board
        Vector2 nextPos = Position + _velocity * _config.tickInterval;
        if (!_grid.IsInsideBoard(nextPos))
        {
            Vector2 boardCenter = (Vector2)_grid.transform.position;
            _velocity = (boardCenter - Position).normalized * speed;
            nextPos = Position + _velocity * _config.tickInterval;
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
        // Move together with other infected and orbit active fungal regions to trap healthy herds.
        Vector2 center = Vector2.zero;
        int count = 0;
        foreach (var n in nearby)
        {
            if (n == this || !n.IsInfected || n.IsDead) continue;
            center += n.Position;
            count++;
        }
        Vector2 cohesion = count > 0 ? (center / count - Position).normalized : Vector2.zero;
        Vector2 fungusBias = SeekFungus() * 1.1f;
        Vector2 intercept = CorralHealthy(nearby) * 1.35f;
        return cohesion * 1.35f + fungusBias + intercept + Wander() * 0.2f;
    }

    private Vector2 CorralHealthy(List<HerbivoreAgent> nearby)
    {
        // Infected herd steers toward healthy animals and angles pushes toward fungus clusters.
        Vector2 toward = Vector2.zero;
        int count = 0;
        foreach (var n in nearby)
        {
            if (n == this || n.IsInfected || n.IsDead) continue;
            Vector2 toHealthy = (n.Position - Position).normalized;
            Vector2 fungusNearHealthy = _grid.NearestFungusWorld(n.Position, 6);
            Vector2 pushTowardFungus = (fungusNearHealthy - n.Position).sqrMagnitude > 0.0001f
                ? (fungusNearHealthy - n.Position).normalized
                : Vector2.zero;
            toward += (toHealthy * 1.2f) + (pushTowardFungus * 0.9f);
            count++;
        }
        return count > 0 ? toward / count : Vector2.zero;
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
