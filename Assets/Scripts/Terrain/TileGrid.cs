using System;
using System.Collections.Generic;
using UnityEngine;

public enum TileType
{
    Grass,
    Fungus,
    DeadSoil,
    Shelter,
    AcidSoil,
    FertileSoil
}

/// <summary>
/// Manages terrain tile state and rendering as a generated texture.
/// Supports both MeshRenderer (quad) and SpriteRenderer scene setups.
/// </summary>
public class TileGrid : MonoBehaviour
{
    [Header("References")]
    public SimulationConfig config;

    public int Width { get; private set; }
    public int Height { get; private set; }

    private TileType[] _tiles;
    private int[] _fungusTtl;
    private int[] _fertileTtl;
    private int[] _deadSoilDelay;
    private int[] _fungusSpreadRemaining;

    private static readonly Color32 GrassColor = new(88, 176, 96, 255);
    private static readonly Color32 GrassColor2 = new(106, 196, 114, 255);
    private static readonly Color32 FungusColor = new(30, 40, 170, 255);
    private static readonly Color32 FungusColor2 = new(55, 20, 210, 255);
    private static readonly Color32 DeadSoilColor = new(90, 60, 35, 255);
    private static readonly Color32 DeadSoilColor2 = new(72, 48, 28, 255);
    private static readonly Color32 ShelterColor = new(135, 135, 145, 255);
    private static readonly Color32 ShelterColor2 = new(116, 116, 130, 255);
    private static readonly Color32 AcidSoilColor = new(56, 34, 18, 255);
    private static readonly Color32 AcidSoilColor2 = new(47, 28, 15, 255);
    private static readonly Color32 FertileSoilColor = new(118, 85, 52, 255);
    private static readonly Color32 FertileSoilColor2 = new(104, 74, 44, 255);

    private Texture2D _texture;
    private Sprite _runtimeSprite;
    private SpriteRenderer _visualSpriteRenderer;
    private Color32[] _pixels;
    private bool _dirty;

    public int GrassTiles { get; private set; }
    public int FungusTiles { get; private set; }
    public int DeadSoilTiles { get; private set; }
    public int ShelterTiles { get; private set; }
    public int AcidSoilTiles { get; private set; }
    public int FertileSoilTiles { get; private set; }

    private readonly System.Random _rng = new();

    public void Initialise(int width, int height)
    {
        if (config == null)
        {
            Debug.LogError("TileGrid initialisation failed: SimulationConfig is not assigned.", this);
            return;
        }

        Width = width;
        Height = height;

        _tiles = new TileType[width * height];
        _fungusTtl = new int[width * height];
        _fertileTtl = new int[width * height];
        _deadSoilDelay = new int[width * height];
        _fungusSpreadRemaining = new int[width * height];

        for (int i = 0; i < _tiles.Length; i++)
            _tiles[i] = TileType.Grass;

        SeedShelters();

        RecalculateTileCounts();
        BuildTexture();
        RebuildPixels();
        ApplyTexture();
        _dirty = false;
    }

    private void BuildTexture()
    {
        if (_runtimeSprite != null)
        {
            Destroy(_runtimeSprite);
            _runtimeSprite = null;
        }

        _texture = new Texture2D(Width, Height, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp
        };
        _pixels = new Color32[Width * Height];

        EnsureVisualSpriteRenderer();

        if (_visualSpriteRenderer != null)
        {
            float pixelsPerUnit = Mathf.Max(0.0001f, 1f / config.tileSize);
            _runtimeSprite = Sprite.Create(
                _texture,
                new Rect(0f, 0f, Width, Height),
                new Vector2(0.5f, 0.5f),
                pixelsPerUnit
            );
            _runtimeSprite.name = "TileGridRuntimeSprite";

            _visualSpriteRenderer.sprite = _runtimeSprite;
            _visualSpriteRenderer.drawMode = SpriteDrawMode.Sliced;
            _visualSpriteRenderer.size = new Vector2(Width * config.tileSize, Height * config.tileSize);
            _visualSpriteRenderer.color = Color.white;
            _visualSpriteRenderer.enabled = true;
            transform.localScale = Vector3.one;
        }

        var meshRenderer = GetComponent<Renderer>();
        if (meshRenderer != null)
        {
            if (meshRenderer.sharedMaterial == null)
            {
                Shader shader = Shader.Find("Unlit/Texture");
                if (shader == null) shader = Shader.Find("Universal Render Pipeline/Unlit");
                if (shader != null) meshRenderer.sharedMaterial = new Material(shader);
            }

            var mat = meshRenderer.material;
            if (mat.HasProperty("_MainTex")) mat.SetTexture("_MainTex", _texture);
            if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", _texture);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", Color.white);
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", Color.white);
            meshRenderer.enabled = false;
        }

        if (_visualSpriteRenderer == null)
        {
            float worldW = Width * config.tileSize;
            float worldH = Height * config.tileSize;
            transform.localScale = new Vector3(worldW, worldH, 1f);
        }
    }

    public TileType GetTile(int x, int y)
    {
        if (x < 0 || x >= Width || y < 0 || y >= Height) return TileType.DeadSoil;
        return _tiles[y * Width + x];
    }

    public TileType GetTileAtWorld(Vector2 worldPos)
    {
        WorldToGrid(worldPos, out int x, out int y);
        return GetTile(x, y);
    }

    public void SetTile(int x, int y, TileType type, int ttl = 0)
    {
        if (x < 0 || x >= Width || y < 0 || y >= Height) return;

        int idx = y * Width + x;
        _tiles[idx] = type;
        _fungusTtl[idx] = type == TileType.Fungus ? ttl : 0;
        _fertileTtl[idx] = type == TileType.FertileSoil ? ttl : 0;
        _deadSoilDelay[idx] = type == TileType.DeadSoil ? Mathf.Max(0, ttl) : 0;
        _fungusSpreadRemaining[idx] = type == TileType.Fungus ? Mathf.Max(0, config.fungusSpreadEventsPerTile) : 0;

        _dirty = true;
        RecalculateTileCounts();
    }

    public Vector2 GridToWorld(int x, int y)
    {
        float wx = transform.position.x - (Width * config.tileSize * 0.5f) + (x + 0.5f) * config.tileSize;
        float wy = transform.position.y - (Height * config.tileSize * 0.5f) + (y + 0.5f) * config.tileSize;
        return new Vector2(wx, wy);
    }

    public void WorldToGrid(Vector2 world, out int gx, out int gy)
    {
        float ox = transform.position.x - Width * config.tileSize * 0.5f;
        float oy = transform.position.y - Height * config.tileSize * 0.5f;
        gx = Mathf.FloorToInt((world.x - ox) / config.tileSize);
        gy = Mathf.FloorToInt((world.y - oy) / config.tileSize);
    }

    public void SimulationTick(IReadOnlyList<HerbivoreAgent> agents = null, bool enableRainRecovery = true)
    {
        if (_tiles == null || _tiles.Length == 0) return;

        var fungusCandidates = new List<int>();
        var deadCandidates = new List<int>();
        var acidCandidates = new List<int>();

        for (int i = 0; i < _tiles.Length; i++)
        {
            switch (_tiles[i])
            {
                case TileType.Fungus:
                    int fx = i % Width;
                    int fy = i / Width;
                    int nearbyBiomass = CountNearbyBiomassTiles(fx, fy);
                    if (nearbyBiomass <= config.fungusBiomassThreshold)
                        _fungusTtl[i] -= GetEffectiveStarvationPenalty();
                    _fungusTtl[i]--;
                    if (_fungusTtl[i] <= 0)
                    {
                        _tiles[i] = TileType.DeadSoil;
                        _deadSoilDelay[i] = Mathf.Max(0, config.deadSoilRecoveryDelayTicks);
                        _fungusSpreadRemaining[i] = 0;
                        _dirty = true;
                    }
                    else
                    {
                        fungusCandidates.Add(i);
                    }
                    break;

                case TileType.DeadSoil:
                    if (_deadSoilDelay[i] > 0)
                    {
                        _deadSoilDelay[i]--;
                    }
                    else
                    {
                        deadCandidates.Add(i);
                    }
                    break;

                case TileType.AcidSoil:
                    acidCandidates.Add(i);
                    break;

                case TileType.FertileSoil:
                    _fertileTtl[i]--;
                    if (_fertileTtl[i] <= 0)
                    {
                        _tiles[i] = TileType.DeadSoil;
                        _deadSoilDelay[i] = Mathf.Max(0, config.deadSoilRecoveryDelayTicks);
                        _dirty = true;
                    }
                    break;
            }
        }

        RunStrategicFungusSpread(fungusCandidates, agents);

        float deadSoilRegrowthChance = Mathf.Clamp01(
            config.grassRegrowthChance *
            (enableRainRecovery ? config.postRainGrassRegrowthMultiplier : 1f));
        foreach (int idx in deadCandidates)
        {
            if (_rng.NextDouble() < deadSoilRegrowthChance)
            {
                _tiles[idx] = TileType.Grass;
                _dirty = true;
            }
        }

        if (enableRainRecovery)
        {
            float acidRegrowthChance = Mathf.Clamp01(config.acidSoilRegrowthChance);
            foreach (int idx in acidCandidates)
            {
                if (_rng.NextDouble() < acidRegrowthChance)
                {
                    _tiles[idx] = TileType.Grass;
                    _dirty = true;
                }
            }
        }

        RecalculateTileCounts();

        if (_dirty)
        {
            RebuildPixels();
            ApplyTexture();
            _dirty = false;
        }
    }

    public void ApplyAcidRainTick(float grassToAcidChance, float fungusToAcidChance, float fertileToAcidChance)
    {
        if (_tiles == null || _tiles.Length == 0) return;

        float grassChance = Mathf.Clamp01(grassToAcidChance);
        float fungusChance = Mathf.Clamp01(fungusToAcidChance);
        float fertileChance = Mathf.Clamp01(fertileToAcidChance);

        for (int i = 0; i < _tiles.Length; i++)
        {
            switch (_tiles[i])
            {
                case TileType.Grass:
                    if (_rng.NextDouble() < grassChance)
                    {
                        _tiles[i] = TileType.AcidSoil;
                        _dirty = true;
                    }
                    break;
                case TileType.Fungus:
                    if (_rng.NextDouble() < fungusChance)
                    {
                        _tiles[i] = TileType.AcidSoil;
                        _fungusTtl[i] = 0;
                        _fungusSpreadRemaining[i] = 0;
                        _dirty = true;
                    }
                    break;
                case TileType.FertileSoil:
                    if (_rng.NextDouble() < fertileChance)
                    {
                        _tiles[i] = TileType.AcidSoil;
                        _fertileTtl[i] = 0;
                        _dirty = true;
                    }
                    break;
            }
        }

        if (_dirty)
        {
            RecalculateTileCounts();
            RebuildPixels();
            ApplyTexture();
            _dirty = false;
        }
    }

    public void ApplyAcidRainFrontTick(
        Vector2 frontCenterWorld,
        Vector2 frontDirectionWorld,
        float frontRadiusWorld,
        float frontFeatherWorld,
        float grassToAcidChance,
        float fungusToAcidChance,
        float fertileToAcidChance)
    {
        if (_tiles == null || _tiles.Length == 0) return;

        float coreRadius = Mathf.Max(config.tileSize * 0.5f, frontRadiusWorld);
        float feather = Mathf.Max(config.tileSize * 0.25f, frontFeatherWorld);
        float maxRadius = coreRadius + feather;

        float grassChance = Mathf.Clamp01(grassToAcidChance);
        float fungusChance = Mathf.Clamp01(fungusToAcidChance);
        float fertileChance = Mathf.Clamp01(fertileToAcidChance);
        Vector2 sweepDir = frontDirectionWorld.sqrMagnitude < 0.0001f ? Vector2.right : frontDirectionWorld.normalized;
        Vector2 bandNormal = Vector2.Perpendicular(sweepDir);

        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int idx = y * Width + x;
                TileType tile = _tiles[idx];
                if (tile != TileType.Grass && tile != TileType.Fungus && tile != TileType.FertileSoil) continue;

                Vector2 world = GridToWorld(x, y);
                Vector2 delta = world - frontCenterWorld;
                float distance = Mathf.Abs(Vector2.Dot(delta, bandNormal));
                if (distance > maxRadius) continue;
                float exposure = EvaluateRainExposure(distance, coreRadius, feather);
                if (exposure <= 0.0001f) continue;

                switch (tile)
                {
                    case TileType.Grass:
                        if (_rng.NextDouble() < grassChance * exposure)
                        {
                            _tiles[idx] = TileType.AcidSoil;
                            _dirty = true;
                        }
                        break;
                    case TileType.Fungus:
                        if (_rng.NextDouble() < fungusChance * exposure)
                        {
                            _tiles[idx] = TileType.AcidSoil;
                            _fungusTtl[idx] = 0;
                            _fungusSpreadRemaining[idx] = 0;
                            _dirty = true;
                        }
                        break;
                    case TileType.FertileSoil:
                        if (_rng.NextDouble() < fertileChance * exposure)
                        {
                            _tiles[idx] = TileType.AcidSoil;
                            _fertileTtl[idx] = 0;
                            _dirty = true;
                        }
                        break;
                }
            }
        }

        if (_dirty)
        {
            RecalculateTileCounts();
            RebuildPixels();
            ApplyTexture();
            _dirty = false;
        }
    }

    public void ApplyAcidRainBrush(
        Vector2 centerWorld,
        float radiusWorld,
        float grassToAcidChance,
        float fungusToAcidChance,
        float fertileToAcidChance)
    {
        if (_tiles == null || _tiles.Length == 0) return;

        float radius = Mathf.Max(config.tileSize * 0.5f, radiusWorld);
        float radiusSq = radius * radius;
        float grassChance = Mathf.Clamp01(grassToAcidChance);
        float fungusChance = Mathf.Clamp01(fungusToAcidChance);
        float fertileChance = Mathf.Clamp01(fertileToAcidChance);

        WorldToGrid(centerWorld - Vector2.one * radius, out int minX, out int minY);
        WorldToGrid(centerWorld + Vector2.one * radius, out int maxX, out int maxY);

        if (minX > maxX) (minX, maxX) = (maxX, minX);
        if (minY > maxY) (minY, maxY) = (maxY, minY);

        minX = Mathf.Clamp(minX, 0, Width - 1);
        maxX = Mathf.Clamp(maxX, 0, Width - 1);
        minY = Mathf.Clamp(minY, 0, Height - 1);
        maxY = Mathf.Clamp(maxY, 0, Height - 1);

        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                Vector2 tileWorld = GridToWorld(x, y);
                if ((tileWorld - centerWorld).sqrMagnitude > radiusSq) continue;

                int idx = y * Width + x;
                switch (_tiles[idx])
                {
                    case TileType.Grass:
                        if (_rng.NextDouble() < grassChance)
                        {
                            _tiles[idx] = TileType.AcidSoil;
                            _dirty = true;
                        }
                        break;
                    case TileType.Fungus:
                        if (_rng.NextDouble() < fungusChance)
                        {
                            _tiles[idx] = TileType.AcidSoil;
                            _fungusTtl[idx] = 0;
                            _fungusSpreadRemaining[idx] = 0;
                            _dirty = true;
                        }
                        break;
                    case TileType.FertileSoil:
                        if (_rng.NextDouble() < fertileChance)
                        {
                            _tiles[idx] = TileType.AcidSoil;
                            _fertileTtl[idx] = 0;
                            _dirty = true;
                        }
                        break;
                }
            }
        }

        if (_dirty)
        {
            RecalculateTileCounts();
            RebuildPixels();
            ApplyTexture();
            _dirty = false;
        }
    }

    public void ConvertAllLivingGroundToAcid()
    {
        if (_tiles == null || _tiles.Length == 0) return;
        for (int i = 0; i < _tiles.Length; i++)
        {
            if (_tiles[i] == TileType.Grass || _tiles[i] == TileType.Fungus || _tiles[i] == TileType.FertileSoil)
            {
                _tiles[i] = TileType.AcidSoil;
                _fungusTtl[i] = 0;
                _fertileTtl[i] = 0;
                _deadSoilDelay[i] = 0;
                _fungusSpreadRemaining[i] = 0;
                _dirty = true;
            }
        }

        if (_dirty)
        {
            RecalculateTileCounts();
            RebuildPixels();
            ApplyTexture();
            _dirty = false;
        }
    }

    private void RunStrategicFungusSpread(List<int> fungusCandidates, IReadOnlyList<HerbivoreAgent> agents)
    {
        int strategicRadius = Mathf.Max(1, config.fungusStrategicSearchRadius);
        int attempts = Mathf.Max(1, config.fungusStrategicSpreadAttempts);
        float attraction = Mathf.Max(0f, config.fungusHerdAttractionWeight);

        foreach (int idx in fungusCandidates)
        {
            if (_fungusSpreadRemaining[idx] <= 0)
                continue;
            if (_rng.NextDouble() >= config.fungusSpreadChance)
                continue;

            int x = idx % Width;
            int y = idx / Width;

            int bestX = int.MinValue;
            int bestY = int.MinValue;
            float bestScore = float.MinValue;

            for (int i = 0; i < attempts; i++)
            {
                int tx = x + _rng.Next(-1, 2);
                int ty = y + _rng.Next(-1, 2);
                float score = EvaluateFungusTargetScore(tx, ty, agents, attraction, strategicRadius, localBonus: 0.25f);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestX = tx;
                    bestY = ty;
                }
            }

            for (int i = 0; i < attempts; i++)
            {
                int tx = x + _rng.Next(-strategicRadius, strategicRadius + 1);
                int ty = y + _rng.Next(-strategicRadius, strategicRadius + 1);
                float score = EvaluateFungusTargetScore(tx, ty, agents, attraction, strategicRadius, localBonus: 0f);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestX = tx;
                    bestY = ty;
                }
            }

            if (bestX != int.MinValue && SpreadFungusTo(bestX, bestY))
                _fungusSpreadRemaining[idx] = Mathf.Max(0, _fungusSpreadRemaining[idx] - 1);
        }
    }

    private float EvaluateFungusTargetScore(
        int x,
        int y,
        IReadOnlyList<HerbivoreAgent> agents,
        float herdAttraction,
        int strategicRadius,
        float localBonus)
    {
        if (x < 0 || x >= Width || y < 0 || y >= Height) return float.MinValue;
        int idx = y * Width + x;
        if (_tiles[idx] != TileType.Grass) return float.MinValue;

        float score = localBonus;

        if (agents != null)
        {
            foreach (var a in agents)
            {
                if (a == null || a.IsDead || a.IsInfected) continue;
                WorldToGrid(a.Position, out int ax, out int ay);
                int dx = ax - x;
                int dy = ay - y;
                int d2 = dx * dx + dy * dy;
                if (d2 > strategicRadius * strategicRadius) continue;

                score += herdAttraction * (1f / (1f + d2));
                if (a.Hunger > 55f) score += 0.12f;
                if (d2 >= 2 && d2 <= strategicRadius * strategicRadius)
                    score += 0.18f * herdAttraction;
            }
        }

        int adjacentFungus = CountAdjacentType(x, y, TileType.Fungus);
        score += adjacentFungus * 0.08f;
        return score;
    }

    private int CountAdjacentType(int x, int y, TileType type)
    {
        int count = 0;
        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                if (dx == 0 && dy == 0) continue;
                int tx = x + dx;
                int ty = y + dy;
                if (tx < 0 || tx >= Width || ty < 0 || ty >= Height) continue;
                if (GetTile(tx, ty) == type) count++;
            }
        }

        return count;
    }

    private int CountNearbyBiomassTiles(int x, int y)
    {
        int count = 0;
        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                if (dx == 0 && dy == 0) continue;
                int tx = x + dx;
                int ty = y + dy;
                if (tx < 0 || tx >= Width || ty < 0 || ty >= Height) continue;
                TileType t = GetTile(tx, ty);
                if (t == TileType.Grass || t == TileType.FertileSoil)
                    count++;
            }
        }

        return count;
    }

    private static float EvaluateRainExposure(float distance, float coreRadius, float feather)
    {
        if (distance <= coreRadius) return 1f;
        if (distance >= coreRadius + feather) return 0f;
        float t = 1f - ((distance - coreRadius) / feather);
        return Mathf.Clamp01(t);
    }

    private int GetEffectiveStarvationPenalty()
    {
        int penalty = Mathf.Max(0, config.fungusStarvationTtlPenalty);
        float survivalBonus = Mathf.Clamp01(config.fungusStarvationGraceFraction);
        return Mathf.Max(0, Mathf.RoundToInt(penalty * (1f - survivalBonus)));
    }

    private bool SpreadFungusTo(int x, int y)
    {
        if (x < 0 || x >= Width || y < 0 || y >= Height) return false;

        int idx = y * Width + x;
        if (_tiles[idx] == TileType.Grass)
        {
            _tiles[idx] = TileType.Fungus;
            _fungusTtl[idx] = config.fungusTileDurationTicks;
            _fungusSpreadRemaining[idx] = Mathf.Max(0, config.fungusSpreadEventsPerTile - 1);
            _deadSoilDelay[idx] = 0;
            _dirty = true;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Called when an infected entity dies. Spawns fungus in a radius.
    /// </summary>
    public void SpawnDeathFungus(Vector2 worldPos, int radius)
    {
        WorldToGrid(worldPos, out int cx, out int cy);

        for (int dx = -radius; dx <= radius; dx++)
        {
            for (int dy = -radius; dy <= radius; dy++)
            {
                if (dx * dx + dy * dy > radius * radius) continue;

                int x = cx + dx;
                int y = cy + dy;
                if (x < 0 || x >= Width || y < 0 || y >= Height) continue;

                int idx = y * Width + x;
                if (_tiles[idx] != TileType.Shelter && _tiles[idx] != TileType.Fungus)
                {
                    _tiles[idx] = TileType.Fungus;
                    _fungusTtl[idx] = config.fungusTileDurationTicks;
                    _fungusSpreadRemaining[idx] = Mathf.Max(0, config.fungusSpreadEventsPerTile);
                    _deadSoilDelay[idx] = 0;
                    _dirty = true;
                }
            }
        }

        if (_dirty)
        {
            RecalculateTileCounts();
            RebuildPixels();
            ApplyTexture();
            _dirty = false;
        }
    }

    public void SpawnFertileSoil(Vector2 worldPos, int radius, int ttl)
    {
        WorldToGrid(worldPos, out int cx, out int cy);
        int clampedTtl = Mathf.Max(10, ttl);

        for (int dx = -radius; dx <= radius; dx++)
        {
            for (int dy = -radius; dy <= radius; dy++)
            {
                if (dx * dx + dy * dy > radius * radius) continue;
                int x = cx + dx;
                int y = cy + dy;
                if (x < 0 || x >= Width || y < 0 || y >= Height) continue;

                int idx = y * Width + x;
                if (_tiles[idx] == TileType.Shelter) continue;

                _tiles[idx] = TileType.FertileSoil;
                _fertileTtl[idx] = clampedTtl;
                _deadSoilDelay[idx] = 0;
                _dirty = true;
            }
        }

        if (_dirty)
        {
            RecalculateTileCounts();
            RebuildPixels();
            ApplyTexture();
            _dirty = false;
        }
    }

    private void RebuildPixels()
    {
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int idx = y * Width + x;
                bool checker = (x + y) % 2 == 0;

                _pixels[idx] = _tiles[idx] switch
                {
                    TileType.Grass => checker ? GrassColor : GrassColor2,
                    TileType.Fungus => checker ? FungusColor : FungusColor2,
                    TileType.DeadSoil => checker ? DeadSoilColor : DeadSoilColor2,
                    TileType.Shelter => checker ? ShelterColor : ShelterColor2,
                    TileType.AcidSoil => checker ? AcidSoilColor : AcidSoilColor2,
                    TileType.FertileSoil => checker ? FertileSoilColor : FertileSoilColor2,
                    _ => GrassColor
                };
            }
        }
    }

    private void ApplyTexture()
    {
        _texture.SetPixels32(_pixels);
        _texture.Apply();
    }

    public Vector2 NearestGrassWorld(Vector2 worldPos, int searchRadius = 5)
    {
        WorldToGrid(worldPos, out int cx, out int cy);
        int best = int.MaxValue;
        int bx = cx;
        int by = cy;

        for (int dx = -searchRadius; dx <= searchRadius; dx++)
        {
            for (int dy = -searchRadius; dy <= searchRadius; dy++)
            {
                int d2 = dx * dx + dy * dy;
                if (d2 >= best) continue;

                int x = cx + dx;
                int y = cy + dy;
                if (x < 0 || x >= Width || y < 0 || y >= Height) continue;

                if (GetTile(x, y) == TileType.Grass)
                {
                    best = d2;
                    bx = x;
                    by = y;
                }
            }
        }

        return GridToWorld(bx, by);
    }

    public Vector2 NearestFungusWorld(Vector2 worldPos, int searchRadius = 5)
    {
        WorldToGrid(worldPos, out int cx, out int cy);
        int best = int.MaxValue;
        int bx = cx;
        int by = cy;
        bool found = false;

        for (int dx = -searchRadius; dx <= searchRadius; dx++)
        {
            for (int dy = -searchRadius; dy <= searchRadius; dy++)
            {
                int d2 = dx * dx + dy * dy;
                if (d2 >= best) continue;

                int x = cx + dx;
                int y = cy + dy;
                if (x < 0 || x >= Width || y < 0 || y >= Height) continue;

                if (GetTile(x, y) == TileType.Fungus)
                {
                    best = d2;
                    bx = x;
                    by = y;
                    found = true;
                }
            }
        }

        return found ? GridToWorld(bx, by) : worldPos;
    }

    public Vector2 NearestShelterWorld(Vector2 worldPos, int searchRadius = 12)
    {
        return FindNearestTileWorld(worldPos, TileType.Shelter, searchRadius);
    }

    public Vector2 NearestFertileWorld(Vector2 worldPos, int searchRadius = 10)
    {
        return FindNearestTileWorld(worldPos, TileType.FertileSoil, searchRadius);
    }

    public bool IsShelterWorld(Vector2 worldPos)
    {
        return GetTileAtWorld(worldPos) == TileType.Shelter;
    }

    public bool TryGetRandomShelterWorld(out Vector2 worldPos)
    {
        worldPos = default;
        if (_tiles == null || _tiles.Length == 0) return false;

        const int maxAttempts = 128;
        for (int i = 0; i < maxAttempts; i++)
        {
            int x = _rng.Next(0, Width);
            int y = _rng.Next(0, Height);
            if (GetTile(x, y) != TileType.Shelter) continue;
            worldPos = GridToWorld(x, y);
            return true;
        }

        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                if (GetTile(x, y) != TileType.Shelter) continue;
                worldPos = GridToWorld(x, y);
                return true;
            }
        }

        return false;
    }

    private Vector2 FindNearestTileWorld(Vector2 worldPos, TileType tileType, int searchRadius)
    {
        WorldToGrid(worldPos, out int cx, out int cy);
        int best = int.MaxValue;
        int bx = cx;
        int by = cy;
        bool found = false;

        for (int dx = -searchRadius; dx <= searchRadius; dx++)
        {
            for (int dy = -searchRadius; dy <= searchRadius; dy++)
            {
                int d2 = dx * dx + dy * dy;
                if (d2 >= best) continue;

                int x = cx + dx;
                int y = cy + dy;
                if (x < 0 || x >= Width || y < 0 || y >= Height) continue;
                if (GetTile(x, y) != tileType) continue;

                best = d2;
                bx = x;
                by = y;
                found = true;
            }
        }

        return found ? GridToWorld(bx, by) : worldPos;
    }

    public bool IsInsideBoard(Vector2 worldPos)
    {
        WorldToGrid(worldPos, out int x, out int y);
        return x >= 0 && x < Width && y >= 0 && y < Height;
    }

    private void RecalculateTileCounts()
    {
        int grass = 0;
        int fungus = 0;
        int deadSoil = 0;
        int shelter = 0;
        int acidSoil = 0;
        int fertileSoil = 0;

        for (int i = 0; i < _tiles.Length; i++)
        {
            switch (_tiles[i])
            {
                case TileType.Grass:
                    grass++;
                    break;
                case TileType.Fungus:
                    fungus++;
                    break;
                case TileType.DeadSoil:
                    deadSoil++;
                    break;
                case TileType.Shelter:
                    shelter++;
                    break;
                case TileType.AcidSoil:
                    acidSoil++;
                    break;
                case TileType.FertileSoil:
                    fertileSoil++;
                    break;
            }
        }

        GrassTiles = grass;
        FungusTiles = fungus;
        DeadSoilTiles = deadSoil;
        ShelterTiles = shelter;
        AcidSoilTiles = acidSoil;
        FertileSoilTiles = fertileSoil;
    }

    private void SeedShelters()
    {
        if (_tiles == null || _tiles.Length == 0 || config == null) return;

        int clusterCount = Mathf.Max(1, config.shelterClusterCount);
        int radius = Mathf.Max(1, config.shelterClusterRadius);

        for (int i = 0; i < clusterCount; i++)
        {
            int cx = _rng.Next(radius, Mathf.Max(radius + 1, Width - radius));
            int cy = _rng.Next(radius, Mathf.Max(radius + 1, Height - radius));

            for (int dx = -radius; dx <= radius; dx++)
            {
                for (int dy = -radius; dy <= radius; dy++)
                {
                    if (dx * dx + dy * dy > radius * radius) continue;
                    int x = cx + dx;
                    int y = cy + dy;
                    if (x < 0 || x >= Width || y < 0 || y >= Height) continue;
                    _tiles[y * Width + x] = TileType.Shelter;
                }
            }
        }
    }

    private void OnDestroy()
    {
        if (_runtimeSprite != null)
        {
            Destroy(_runtimeSprite);
            _runtimeSprite = null;
        }
    }

    private void EnsureVisualSpriteRenderer()
    {
        if (_visualSpriteRenderer != null) return;

        _visualSpriteRenderer = GetComponent<SpriteRenderer>();
        if (_visualSpriteRenderer != null)
        {
            _visualSpriteRenderer.sortingLayerName = "Terrain";
            _visualSpriteRenderer.sortingOrder = -5;
            _visualSpriteRenderer.drawMode = SpriteDrawMode.Sliced;
            return;
        }

        Transform existing = transform.Find("TileGridVisual");
        GameObject visualGo;
        if (existing != null)
        {
            visualGo = existing.gameObject;
        }
        else
        {
            visualGo = new GameObject("TileGridVisual");
            visualGo.transform.SetParent(transform, false);
        }

        visualGo.transform.localPosition = Vector3.zero;
        visualGo.transform.localRotation = Quaternion.identity;
        visualGo.transform.localScale = Vector3.one;

        _visualSpriteRenderer = visualGo.GetComponent<SpriteRenderer>();
        if (_visualSpriteRenderer == null)
            _visualSpriteRenderer = visualGo.AddComponent<SpriteRenderer>();

        _visualSpriteRenderer.sortingLayerName = "Terrain";
        _visualSpriteRenderer.sortingOrder = -5;
        _visualSpriteRenderer.drawMode = SpriteDrawMode.Sliced;
    }
}
