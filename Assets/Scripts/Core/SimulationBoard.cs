using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif
#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// Master controller. Owns the simulation loop, quadtree, entity spawning,
/// and all statistics. Communicates with UIPanel via events.
/// </summary>
public class SimulationBoard : MonoBehaviour
{
    [Header("Config")]
    public SimulationConfig config;

    [Header("Scene References")]
    public TileGrid tileGrid;
    public Transform entityParent;
    public GameObject herbivorePrefab;
    public LineRenderer pathLineRenderer;

    [Header("Toggles")]
    public bool showQuadtree = true;
    public bool showPaths    = false;
    public bool depthColor   = true;
    public bool showSceneStatsOverlay = true;
    public bool showDecisionTreeOverlay = true;

    // ── Simulation state ──────────────────────────
    private bool _running;
    private float _speed = 1f;
    private float _tickTimer;
    private int _totalDeaths;
    private int _tickCount;
    private float _simulatedSeconds;
    private long _totalQueryCount;
    private long _totalCandidateChecks;
    private int _acidRainCycleTicksRemaining;
    private int _acidRainTicksRemaining;
    private bool _isLandDestroyed;
    private float _lastRainEndRealtime;
    private Vector2 _acidRainFrontCenter;
    private Vector2 _acidRainFrontVelocity;
    private Vector2 _acidRainFrontDirection;
    private bool _acidRainFrontActive;

    // ── Entities ──────────────────────────────────
    private List<HerbivoreAgent> _agents = new();
    private HerbivoreAgent _selectedAgent;

    // ── Quadtree ──────────────────────────────────
    private Quadtree _qt;
    private Rect _boardRect;

    // ── Stats ─────────────────────────────────────
    public SimStats Stats { get; private set; } = new();
    public bool IsPaused => !_running;
    public float Speed => _speed;
    public bool IsAcidRainActive => _acidRainTicksRemaining > 0;
    public int TicksUntilAcidRain => IsAcidRainActive ? 0 : Mathf.Max(0, _acidRainCycleTicksRemaining);
    public float SecondsUntilAcidRain => TicksUntilAcidRain * config.tickInterval / Mathf.Max(0.01f, _speed);
    public float SecondsRemainingInAcidRain => _acidRainTicksRemaining * config.tickInterval / Mathf.Max(0.01f, _speed);
    public bool IsAcidRainImminent => config != null && config.enableAcidRain && !IsAcidRainActive && TicksUntilAcidRain <= config.acidRainWarningTicks;
    public bool IsLandDestroyed => _isLandDestroyed;

    // ── Events ────────────────────────────────────
    public UnityEvent<HerbivoreAgent> OnAgentSelected = new();
    public UnityEvent<SimStats>       OnStatsUpdated  = new();

    // ── GL material ───────────────────────────────
    private Material _lineMaterial;
    private List<(Vector2 a, Vector2 b, Color col)> _qtLines = new();

    // ──────────────────────────────────────────────
    private void Awake()
    {
        AutoResolveReferences();

        _lineMaterial = new Material(Shader.Find("Hidden/Internal-Colored"))
        {
            hideFlags = HideFlags.HideAndDontSave
        };
        _lineMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        _lineMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        _lineMaterial.SetInt("_Cull",     (int)UnityEngine.Rendering.CullMode.Off);
        _lineMaterial.SetInt("_ZWrite",   0);
    }

    private void Start()
    {
        Initialise();
    }

    public void Initialise()
    {
        StopAllCoroutines();
        _running = false;
        _totalDeaths = 0;
        _tickCount = 0;
        _simulatedSeconds = 0f;
        _totalQueryCount = 0;
        _totalCandidateChecks = 0;
        _acidRainTicksRemaining = 0;
        _acidRainCycleTicksRemaining = config != null && config.enableAcidRain ? Mathf.Max(1, config.acidRainCycleTicks) : int.MaxValue;
        _isLandDestroyed = false;
        _lastRainEndRealtime = -10000f;
        _acidRainFrontActive = false;
        _acidRainFrontCenter = Vector2.zero;
        _acidRainFrontVelocity = Vector2.zero;
        _acidRainFrontDirection = Vector2.right;

        // Setup grid
        if (tileGrid == null || config == null || herbivorePrefab == null) return;
        tileGrid.Initialise(config.gridWidth, config.gridHeight);

        // Board bounds in world space
        float hw = config.gridWidth  * config.tileSize * 0.5f;
        float hh = config.gridHeight * config.tileSize * 0.5f;
        Vector2 origin = (Vector2)tileGrid.transform.position;
        _boardRect = new Rect(origin.x - hw, origin.y - hh, hw * 2f, hh * 2f);

        // Clear old entities
        foreach (Transform t in entityParent) Destroy(t.gameObject);
        _agents.Clear();
        HerbivoreAgent.ResetIdCounter();
        _selectedAgent = null;

        // Spawn initial entities
        for (int i = 0; i < config.initialHerbivoreCount; i++)
            SpawnHerbivore();
        SeedShelterResidents();

        // Seed a few fungal patches so terrain interactions are visible immediately.
        int starterFungus = Mathf.Max(1, config.initialFungusPatches);
        for (int i = 0; i < starterFungus; i++)
        {
            Vector2 fungalSeed = new Vector2(
                Random.Range(_boardRect.xMin, _boardRect.xMax),
                Random.Range(_boardRect.yMin, _boardRect.yMax)
            );
            tileGrid.SpawnDeathFungus(fungalSeed, 1);
        }

        // Start running
        _tickTimer = 0f;
        _running = true;

        PopulateStatsAndNotify();
    }

    private void SpawnHerbivore(PersonalityType? type = null)
    {
        if (_agents.Count >= config.maxHerbivores) return;

        Vector2 pos = new Vector2(
            Random.Range(_boardRect.xMin + 0.5f, _boardRect.xMax - 0.5f),
            Random.Range(_boardRect.yMin + 0.5f, _boardRect.yMax - 0.5f)
        );

        var go    = Instantiate(herbivorePrefab, (Vector3)pos, Quaternion.identity, entityParent);
        var agent = go.GetComponent<HerbivoreAgent>();
        agent.Initialise(config, tileGrid, this, type);
        _agents.Add(agent);
    }

    private void SpawnHerbivoreAt(Vector2 pos, PersonalityType? type = null)
    {
        if (_agents.Count >= config.maxHerbivores) return;
        if (!_boardRect.Contains(pos)) return;

        var go = Instantiate(herbivorePrefab, (Vector3)pos, Quaternion.identity, entityParent);
        var agent = go.GetComponent<HerbivoreAgent>();
        agent.Initialise(config, tileGrid, this, type);
        _agents.Add(agent);
    }

    // ──────────────────────────────────────────────
    // Update / Tick
    // ──────────────────────────────────────────────
    private void Update()
    {
        if (TryGetPointerDown(out Vector2 pointerScreen))
            HandleClick(pointerScreen);

        if (!_running) return;

        float interval = config.tickInterval / _speed;
        _tickTimer += Time.deltaTime;

        while (_tickTimer >= interval)
        {
            _tickTimer -= interval;
            SimulationTick();
        }
    }

    private void SimulationTick()
    {
        AdvanceAcidRainCycle();

        // Rebuild quadtree
        _qt = new Quadtree(_boardRect, config.quadtreeCapacity, config.quadtreeMaxDepth);
        foreach (var a in _agents)
            if (!a.IsDead)
                _qt.Insert(a);

        // Tick entities
        int savedChecksThisTick = 0;
        int queriesThisTick = 0;
        foreach (var a in _agents)
            if (!a.IsDead)
            {
                a.SimulationTick(_qt, _agents, _agents.Count);
                savedChecksThisTick += a.LastSavedCandidates;
                queriesThisTick++;
            }

        if (IsAcidRainActive)
        {
            AdvanceAcidRainFront();
            tileGrid.ApplyAcidRainFrontTick(
                _acidRainFrontCenter,
                _acidRainFrontDirection,
                config.acidRainFrontRadiusTiles * config.tileSize,
                config.acidRainFrontFeatherTiles * config.tileSize,
                config.acidRainGrassDestroyChance,
                config.acidRainFungusDestroyChance,
                config.acidRainFertileDestroyChance);
            KillExposedAgentsDuringAcidRain();
        }

        TryHandleBreeding();

        // Tick terrain (strategic spread can use live entity data).
        tileGrid.SimulationTick(_agents, IsRainRecoveryUnlocked());

        UpdateLandDestroyedPhase();

        // Remove dead agents
        _agents.RemoveAll(a => a.IsDead);

        // Collect stats
        _qt.AggregateStats(out int totalNodes, out int maxDepth);
        Stats.fps            = Mathf.RoundToInt(1f / Time.deltaTime);
        Stats.speed          = _speed;
        Stats.totalEntities  = _agents.Count;
        Stats.infectedCount  = _agents.FindAll(a => a.IsInfected).Count;
        Stats.deadCount      = _totalDeaths;
        Stats.qtNodes        = totalNodes;
        Stats.qtMaxDepth     = maxDepth;
        Stats.candidatesSaved = savedChecksThisTick;
        Stats.queriesPerFrame = queriesThisTick;
        Stats.grassTiles     = tileGrid.GrassTiles;
        Stats.fungusTiles    = tileGrid.FungusTiles;
        Stats.deadSoilTiles  = tileGrid.DeadSoilTiles;
        Stats.shelterTiles   = tileGrid.ShelterTiles;
        Stats.acidSoilTiles  = tileGrid.AcidSoilTiles;
        Stats.fertileSoilTiles = tileGrid.FertileSoilTiles;
        _tickCount++;
        _simulatedSeconds += config.tickInterval;
        _totalQueryCount += queriesThisTick;
        _totalCandidateChecks += savedChecksThisTick;
        Stats.tickCount = _tickCount;
        Stats.simulatedSeconds = _simulatedSeconds;
        Stats.totalQueries = _totalQueryCount;
        Stats.totalSavedChecks = _totalCandidateChecks;
        Stats.isPaused = !_running;
        Stats.acidRainActive = IsAcidRainActive;
        Stats.acidRainTicksRemaining = _acidRainTicksRemaining;
        Stats.acidRainTicksUntilNext = TicksUntilAcidRain;
        Stats.acidRainSecondsRemaining = SecondsRemainingInAcidRain;
        Stats.acidRainSecondsUntilNext = SecondsUntilAcidRain;
        Stats.landDestroyed = _isLandDestroyed;
        Stats.ecosystemPhase = _isLandDestroyed ? "Collapse / Survival" : (IsAcidRainActive ? "Acid Rain" : (IsAcidRainImminent ? "Acid Warning" : "Growth / Infection"));

        OnStatsUpdated.Invoke(Stats);

        // Rebuild GL line cache
        if (showQuadtree)
        {
            _qtLines.Clear();
            _qt.CollectLines(_qtLines, depthColor);
        }

        // Path renderer
        if (showPaths && _selectedAgent != null && !_selectedAgent.IsDead && pathLineRenderer != null)
            _selectedAgent.DrawPath(pathLineRenderer);
        else if (pathLineRenderer != null)
            pathLineRenderer.positionCount = 0;
    }

    // ──────────────────────────────────────────────
    // Called by HerbivoreAgent on death
    // ──────────────────────────────────────────────
    public void OnEntityDied(HerbivoreAgent agent)
    {
        _totalDeaths++;
        if (agent.IsInfected)
        {
            tileGrid.SpawnDeathFungus(agent.Position, config.deathFungusRadius);
        }
        else
        {
            tileGrid.SpawnFertileSoil(agent.Position, config.fertiliseRadius, config.fertileSoilDurationTicks);
        }
    }

    private void AdvanceAcidRainCycle()
    {
        if (config == null || !config.enableAcidRain) return;

        if (_acidRainTicksRemaining > 0)
        {
            _acidRainTicksRemaining--;
            if (_acidRainTicksRemaining <= 0)
            {
                _acidRainCycleTicksRemaining = Mathf.Max(1, config.acidRainCycleTicks);
                _lastRainEndRealtime = Time.realtimeSinceStartup;
                _acidRainFrontActive = false;
            }
            return;
        }

        if (_acidRainCycleTicksRemaining <= Mathf.Max(1, config.acidRainWarningTicks))
            EnsureInfectedShelteredBeforeRain();

        _acidRainCycleTicksRemaining--;
        if (_acidRainCycleTicksRemaining <= 0)
        {
            EnsureInfectedShelteredBeforeRain();
            _acidRainTicksRemaining = Mathf.Max(1, config.acidRainDurationTicks);
            _acidRainCycleTicksRemaining = 0;
            BeginAcidRainFront();
        }
    }

    private void BeginAcidRainFront()
    {
        if (config == null) return;

        float radiusWorld = Mathf.Max(config.tileSize, config.acidRainFrontRadiusTiles * config.tileSize);
        float startX;
        float startY;
        Vector2 target;

        int edge = Random.Range(0, 4);
        switch (edge)
        {
            case 0: // left -> right
                startX = _boardRect.xMin - radiusWorld;
                startY = Random.Range(_boardRect.yMin, _boardRect.yMax);
                target = new Vector2(_boardRect.xMax + radiusWorld, Random.Range(_boardRect.yMin, _boardRect.yMax));
                break;
            case 1: // right -> left
                startX = _boardRect.xMax + radiusWorld;
                startY = Random.Range(_boardRect.yMin, _boardRect.yMax);
                target = new Vector2(_boardRect.xMin - radiusWorld, Random.Range(_boardRect.yMin, _boardRect.yMax));
                break;
            case 2: // bottom -> top
                startX = Random.Range(_boardRect.xMin, _boardRect.xMax);
                startY = _boardRect.yMin - radiusWorld;
                target = new Vector2(Random.Range(_boardRect.xMin, _boardRect.xMax), _boardRect.yMax + radiusWorld);
                break;
            default: // top -> bottom
                startX = Random.Range(_boardRect.xMin, _boardRect.xMax);
                startY = _boardRect.yMax + radiusWorld;
                target = new Vector2(Random.Range(_boardRect.xMin, _boardRect.xMax), _boardRect.yMin - radiusWorld);
                break;
        }

        _acidRainFrontCenter = new Vector2(startX, startY);
        Vector2 dir = (target - _acidRainFrontCenter).normalized;
        if (dir.sqrMagnitude < 0.0001f) dir = Vector2.right;
        _acidRainFrontDirection = dir;

        float pathDistance = Vector2.Distance(_acidRainFrontCenter, target);
        float rainDurationSeconds = Mathf.Max(0.1f, config.acidRainDurationTicks * config.tickInterval);
        float requiredSpeed = pathDistance / rainDurationSeconds;
        float requestedSpeed = Mathf.Max(0.1f, config.acidRainFrontDriftTilesPerSecond) * config.tileSize;
        float speedWorldPerSecond = Mathf.Max(requiredSpeed, requestedSpeed);
        _acidRainFrontVelocity = dir * speedWorldPerSecond;
        _acidRainFrontActive = true;
    }

    private void AdvanceAcidRainFront()
    {
        if (!_acidRainFrontActive || config == null) return;
        _acidRainFrontCenter += _acidRainFrontVelocity * config.tickInterval;
    }

    private bool IsRainRecoveryUnlocked()
    {
        if (config == null) return true;
        return Time.realtimeSinceStartup - _lastRainEndRealtime >= Mathf.Max(0f, config.rainRecoveryDelaySeconds);
    }

    private void SeedShelterResidents()
    {
        if (config == null || tileGrid == null) return;
        int targetResidents = Mathf.Clamp(config.initialShelterResidents, 0, config.maxHerbivores);
        if (targetResidents <= 0) return;

        int shelteredNow = 0;
        foreach (var a in _agents)
        {
            if (a == null || a.IsDead) continue;
            if (tileGrid.IsShelterWorld(a.Position)) shelteredNow++;
        }

        int toAdd = Mathf.Max(0, targetResidents - shelteredNow);
        for (int i = 0; i < toAdd; i++)
        {
            if (_agents.Count >= config.maxHerbivores) break;
            if (!tileGrid.TryGetRandomShelterWorld(out Vector2 shelterPos)) break;

            Vector2 jitter = Random.insideUnitCircle * config.tileSize * 0.25f;
            SpawnHerbivoreAt(shelterPos + jitter);
        }
    }

    private void EnsureInfectedShelteredBeforeRain()
    {
        if (config == null || tileGrid == null || !config.ensureShelteredInfectedBeforeRain) return;

        foreach (var a in _agents)
        {
            if (a == null || a.IsDead || !a.IsInfected) continue;
            if (tileGrid.IsShelterWorld(a.Position)) return;
        }

        HerbivoreAgent shelteredCandidate = null;
        foreach (var a in _agents)
        {
            if (a == null || a.IsDead || a.IsInfected) continue;
            if (!tileGrid.IsShelterWorld(a.Position)) continue;
            shelteredCandidate = a;
            break;
        }

        if (shelteredCandidate != null)
        {
            shelteredCandidate.Infect();
            return;
        }

        HerbivoreAgent closestHealthy = null;
        Vector2 closestShelter = Vector2.zero;
        float bestDistSq = float.MaxValue;
        foreach (var a in _agents)
        {
            if (a == null || a.IsDead || a.IsInfected) continue;
            Vector2 shelter = tileGrid.NearestShelterWorld(a.Position, 24);
            float d2 = (shelter - a.Position).sqrMagnitude;
            if (d2 < bestDistSq)
            {
                bestDistSq = d2;
                closestHealthy = a;
                closestShelter = shelter;
            }
        }

        if (closestHealthy != null)
        {
            closestHealthy.ForceRelocate(closestShelter);
            closestHealthy.Infect();
        }
    }

    private void KillExposedAgentsDuringAcidRain()
    {
        foreach (var a in _agents)
        {
            if (a == null || a.IsDead) continue;
            if (tileGrid.IsShelterWorld(a.Position)) continue;
            if (!IsUnderAcidRainFront(a.Position)) continue;
            if (Random.value <= config.acidRainExposedDeathChance)
                a.ForceEnvironmentalDeath("Acid rain exposure");
        }
    }

    private bool IsUnderAcidRainFront(Vector2 worldPos)
    {
        if (!_acidRainFrontActive || config == null) return false;

        float radius = Mathf.Max(config.tileSize, config.acidRainFrontRadiusTiles * config.tileSize);
        float feather = Mathf.Max(config.tileSize * 0.25f, config.acidRainFrontFeatherTiles * config.tileSize);
        float maxRadius = radius + feather;
        Vector2 normal = Vector2.Perpendicular(_acidRainFrontDirection.sqrMagnitude < 0.0001f ? Vector2.right : _acidRainFrontDirection.normalized);
        float dist = Mathf.Abs(Vector2.Dot(worldPos - _acidRainFrontCenter, normal));
        return dist <= maxRadius;
    }

    private void UpdateLandDestroyedPhase()
    {
        bool noLivingGround = tileGrid.GrassTiles <= 0 && tileGrid.FungusTiles <= 0;
        if (noLivingGround && !_isLandDestroyed)
        {
            _isLandDestroyed = true;
            tileGrid.ConvertAllLivingGroundToAcid();
            KillAgentsOutsideShelterImmediate();
            return;
        }

        if (!noLivingGround && _isLandDestroyed)
        {
            _isLandDestroyed = false;
        }
    }

    private void KillAgentsOutsideShelterImmediate()
    {
        foreach (var a in _agents)
        {
            if (a == null || a.IsDead) continue;
            if (tileGrid.IsShelterWorld(a.Position)) continue;
            a.ForceEnvironmentalDeath("Land collapse exposure");
        }
    }

    // ──────────────────────────────────────────────
    // GL Quadtree rendering
    // ──────────────────────────────────────────────
    private void OnPostRender() => DrawQT();

    // For Scene view support
    private void OnRenderObject()
    {
        if (!Application.isPlaying) return;
        DrawQT();
    }

    private void DrawQT()
    {
        if (!showQuadtree || _lineMaterial == null || _qtLines == null) return;

        _lineMaterial.SetPass(0);
        GL.PushMatrix();
        GL.Begin(GL.LINES);

        foreach (var (a, b, col) in _qtLines)
        {
            GL.Color(col);
            GL.Vertex3(a.x, a.y, 0);
            GL.Vertex3(b.x, b.y, 0);
        }

        GL.End();
        GL.PopMatrix();
    }

#if UNITY_EDITOR
    private void OnDrawGizmos()
    {
        if (!Application.isPlaying || config == null || tileGrid == null) return;
        if (!showSceneStatsOverlay && !showDecisionTreeOverlay) return;

        Handles.BeginGUI();
        if (showSceneStatsOverlay)
            DrawSceneStatsGizmoPanel();
        if (showDecisionTreeOverlay)
            DrawDecisionTreeGizmoPanel();
        Handles.EndGUI();
    }

    private void DrawSceneStatsGizmoPanel()
    {
        Vector3 anchor = tileGrid.transform.position + new Vector3(
            config.gridWidth * config.tileSize * 0.52f,
            config.gridHeight * config.tileSize * 0.48f,
            0f
        );

        Vector2 guiPoint = HandleUtility.WorldToGUIPoint(anchor);
        var rect = new Rect(guiPoint.x, guiPoint.y, 300f, 230f);
        GUI.Box(rect, "Scene Stats (F5)");
        GUI.Label(new Rect(rect.x + 8f, rect.y + 24f, rect.width - 16f, rect.height - 28f),
            $"Entities: {Stats.totalEntities}\\n" +
            $"Infected: {Stats.infectedCount}\\n" +
            $"Dead: {Stats.deadCount}\\n" +
            $"Grass: {Stats.grassTiles}\\n" +
            $"Fungus: {Stats.fungusTiles}\\n" +
            $"Dead Soil: {Stats.deadSoilTiles} | Acid: {Stats.acidSoilTiles}\\n" +
            $"Shelter: {Stats.shelterTiles} | Fertile: {Stats.fertileSoilTiles}\\n" +
            $"Acid Rain: {(Stats.acidRainActive ? $"ACTIVE ({Stats.acidRainSecondsRemaining:0.0}s / {Stats.acidRainTicksRemaining}t)" : $"in {Stats.acidRainSecondsUntilNext:0.0}s / {Stats.acidRainTicksUntilNext}t")}\\n" +
            $"Phase: {Stats.ecosystemPhase}\\n" +
            $"QT Nodes: {Stats.qtNodes}\\n" +
            $"Saved Checks: {Stats.candidatesSaved}");
    }

    private void DrawDecisionTreeGizmoPanel()
    {
        var rect = new Rect(16f, 16f, 500f, 360f);
        GUI.Box(rect, "Selected Entity Decision Tree (F4)");
        string text = "No entity selected. Click a herbivore to inspect decisions.";
        if (_selectedAgent != null)
            text = _selectedAgent.GetDecisionTreeDiagram();

        var style = new GUIStyle(GUI.skin.label)
        {
            wordWrap = true,
            richText = false,
            fontSize = 11
        };

        GUI.Label(new Rect(rect.x + 8f, rect.y + 24f, rect.width - 16f, rect.height - 32f), text, style);
    }
#endif

    // ──────────────────────────────────────────────
    // Input
    // ──────────────────────────────────────────────
    private void HandleClick(Vector2 screenPosition)
    {
        if (Camera.main == null) return;
        Vector2 world = Camera.main.ScreenToWorldPoint(screenPosition);
        HerbivoreAgent closest = null;
        float best = 0.4f;
        foreach (var a in _agents)
        {
            if (a.IsDead) continue;
            float d = Vector2.Distance(a.Position, world);
            if (d < best) { best = d; closest = a; }
        }

        if (_selectedAgent != null) _selectedAgent.SetSelected(false);
        _selectedAgent = closest;
        if (_selectedAgent != null) _selectedAgent.SetSelected(true);
        OnAgentSelected.Invoke(_selectedAgent);
    }

    // ──────────────────────────────────────────────
    // Public controls (called by UI buttons)
    // ──────────────────────────────────────────────
    public void Play()  => _running = true;
    public void Pause() => _running = false;
    public void TogglePause() => _running = !_running;
    public void Reset() => Initialise();
    public void SetSpeed(float s) => _speed = Mathf.Clamp(s, 0.25f, 5f);
    public void AdjustSpeed(float delta) => SetSpeed(_speed + delta);
    public void ToggleQuadtree() => showQuadtree = !showQuadtree;
    public void TogglePaths()    => showPaths    = !showPaths;
    public void ToggleDepthColor() => depthColor = !depthColor;
    public void ToggleSceneStatsOverlay() => showSceneStatsOverlay = !showSceneStatsOverlay;
    public void ToggleDecisionTreeOverlay() => showDecisionTreeOverlay = !showDecisionTreeOverlay;

    public void SpawnInfected()
    {
        if (_agents.Count == 0) return;
        var rnd = _agents[Random.Range(0, _agents.Count)];
        rnd.Infect();
    }

    public HerbivoreAgent GetSelectedAgent() => _selectedAgent;
    public IReadOnlyList<HerbivoreAgent> GetAgents() => _agents;
    public List<(Vector2 a, Vector2 b, Color col)> GetQTLines() => _qtLines;

    private void TryHandleBreeding()
    {
        if (!config.enableBreeding) return;
        if (_agents.Count >= config.maxHerbivores) return;

        int birthsThisTick = 0;
        int maxBirthsPerTick = Mathf.Max(1, config.maxBirthsPerTick);
        float range = config.breedingRange;
        float rangeSq = range * range;

        for (int i = 0; i < _agents.Count; i++)
        {
            if (_agents.Count >= config.maxHerbivores || birthsThisTick >= maxBirthsPerTick) break;
            var a = _agents[i];
            if (a == null || a.IsDead || !a.IsBreedReady) continue;
            if (!tileGrid.IsShelterWorld(a.Position)) continue;

            for (int j = i + 1; j < _agents.Count; j++)
            {
                if (_agents.Count >= config.maxHerbivores || birthsThisTick >= maxBirthsPerTick) break;
                var b = _agents[j];
                if (b == null || b.IsDead || !b.IsBreedReady) continue;
                if (!tileGrid.IsShelterWorld(b.Position)) continue;
                if (!a.CanBreedWith(b)) continue;

                if ((a.Position - b.Position).sqrMagnitude > rangeSq) continue;

                float chance =
                    config.breedingBaseChancePerTick *
                    0.5f * (a.GetBreedingChanceModifier() + b.GetBreedingChanceModifier());
                if (IsAcidRainActive || IsAcidRainImminent) chance *= 1.6f;

                if (Random.value > chance) continue;

                Vector2 midpoint = (a.Position + b.Position) * 0.5f;
                Vector2 spawnPos = midpoint + Random.insideUnitCircle * 0.15f;
                if (!_boardRect.Contains(spawnPos)) continue;

                // Avoid birthing directly into fungus if possible.
                if (tileGrid.GetTileAtWorld(spawnPos) == TileType.Fungus)
                    spawnPos = tileGrid.NearestGrassWorld(spawnPos, 6);

                PersonalityType childType = Random.value < 0.5f ? a.Personality.type : b.Personality.type;
                SpawnHerbivoreAt(spawnPos, childType);
                a.MarkBred();
                b.MarkBred();
                birthsThisTick++;
            }
        }
    }

    private void PopulateStatsAndNotify()
    {
        _qt = new Quadtree(_boardRect, config.quadtreeCapacity, config.quadtreeMaxDepth);
        foreach (var a in _agents)
        {
            if (!a.IsDead) _qt.Insert(a);
        }

        _qt.AggregateStats(out int totalNodes, out int maxDepth);

        Stats.fps            = 0;
        Stats.speed          = _speed;
        Stats.totalEntities  = _agents.Count;
        Stats.infectedCount  = _agents.FindAll(a => a.IsInfected).Count;
        Stats.deadCount      = _totalDeaths;
        Stats.qtNodes        = totalNodes;
        Stats.qtMaxDepth     = maxDepth;
        Stats.grassTiles     = tileGrid.GrassTiles;
        Stats.fungusTiles    = tileGrid.FungusTiles;
        Stats.deadSoilTiles  = tileGrid.DeadSoilTiles;
        Stats.shelterTiles   = tileGrid.ShelterTiles;
        Stats.acidSoilTiles  = tileGrid.AcidSoilTiles;
        Stats.fertileSoilTiles = tileGrid.FertileSoilTiles;
        Stats.candidatesSaved = 0;
        Stats.queriesPerFrame = 0;
        Stats.tickCount = _tickCount;
        Stats.simulatedSeconds = _simulatedSeconds;
        Stats.totalQueries = _totalQueryCount;
        Stats.totalSavedChecks = _totalCandidateChecks;
        Stats.isPaused = !_running;
        Stats.acidRainActive = IsAcidRainActive;
        Stats.acidRainTicksRemaining = _acidRainTicksRemaining;
        Stats.acidRainTicksUntilNext = TicksUntilAcidRain;
        Stats.acidRainSecondsRemaining = SecondsRemainingInAcidRain;
        Stats.acidRainSecondsUntilNext = SecondsUntilAcidRain;
        Stats.landDestroyed = _isLandDestroyed;
        Stats.ecosystemPhase = _isLandDestroyed ? "Collapse / Survival" : (IsAcidRainActive ? "Acid Rain" : (IsAcidRainImminent ? "Acid Warning" : "Growth / Infection"));

        if (showQuadtree)
        {
            _qtLines.Clear();
            _qt.CollectLines(_qtLines, depthColor);
        }

        OnStatsUpdated.Invoke(Stats);
    }

    private void AutoResolveReferences()
    {
        if (tileGrid == null)
            tileGrid = FindAnyObjectByType<TileGrid>();

        if (config == null && tileGrid != null)
            config = tileGrid.config;

        if (entityParent == null)
        {
            var parentGo = GameObject.Find("EntityParent");
            if (parentGo == null)
            {
                parentGo = new GameObject("EntityParent");
                parentGo.transform.SetParent(transform, true);
            }
            entityParent = parentGo.transform;
        }

        if (pathLineRenderer == null)
        {
            var pathGo = GameObject.Find("PathLine");
            if (pathGo != null)
                pathLineRenderer = pathGo.GetComponent<LineRenderer>();
        }
    }

    private static bool TryGetPointerDown(out Vector2 screenPos)
    {
        screenPos = default;

#if ENABLE_INPUT_SYSTEM
        if (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame)
        {
            screenPos = Mouse.current.position.ReadValue();
            return true;
        }

        if (Touchscreen.current != null && Touchscreen.current.primaryTouch.press.wasPressedThisFrame)
        {
            screenPos = Touchscreen.current.primaryTouch.position.ReadValue();
            return true;
        }
#endif

#if ENABLE_LEGACY_INPUT_MANAGER
        if (Input.GetMouseButtonDown(0))
        {
            screenPos = Input.mousePosition;
            return true;
        }
#endif

        return false;
    }
}

[System.Serializable]
public class SimStats
{
    public int   fps;
    public float speed;
    public bool  isPaused;
    public int   tickCount;
    public float simulatedSeconds;
    public int   totalEntities;
    public int   infectedCount;
    public int   deadCount;
    public int   qtNodes;
    public int   qtMaxDepth;
    public int   grassTiles;
    public int   fungusTiles;
    public int   deadSoilTiles;
    public int   shelterTiles;
    public int   acidSoilTiles;
    public int   fertileSoilTiles;
    public int   queriesPerFrame;
    public int   candidatesSaved;
    public long  totalQueries;
    public long  totalSavedChecks;
    public bool  acidRainActive;
    public int   acidRainTicksRemaining;
    public int   acidRainTicksUntilNext;
    public float acidRainSecondsRemaining;
    public float acidRainSecondsUntilNext;
    public bool  landDestroyed;
    public string ecosystemPhase;
}
