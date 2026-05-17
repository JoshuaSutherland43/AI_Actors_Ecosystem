using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Generic 2D Quadtree for spatial queries.
/// Rebuilt from scratch each tick for correctness and simplicity.
/// </summary>
public class Quadtree
{
    public Rect Bounds { get; private set; }
    public int Depth { get; private set; }

    private readonly int _capacity;
    private readonly int _maxDepth;

    private List<HerbivoreAgent> _entities = new();
    private Quadtree[] _children; // null when leaf

    // Statistics (aggregated from root)
    public int TotalNodes { get; private set; }
    public int MaxDepthReached { get; private set; }
    public int QueriesThisFrame { get; private set; }
    public int CandidatesSaved { get; private set; }

    public bool IsLeaf => _children == null;

    public Quadtree(Rect bounds, int capacity, int maxDepth, int depth = 0)
    {
        Bounds = bounds;
        _capacity = capacity;
        _maxDepth = maxDepth;
        Depth = depth;
        TotalNodes = 1;
        MaxDepthReached = depth;
    }

    // ──────────────────────────────────────────────
    // Insert
    // ──────────────────────────────────────────────
    public void Insert(HerbivoreAgent entity)
    {
        if (!Bounds.Contains(entity.Position))
            return;

        if (IsLeaf && (_entities.Count < _capacity || Depth >= _maxDepth))
        {
            _entities.Add(entity);
            return;
        }

        if (IsLeaf)
            Subdivide();

        foreach (var child in _children)
            child.Insert(entity);
    }

    private void Subdivide()
    {
        float hw = Bounds.width * 0.5f;
        float hh = Bounds.height * 0.5f;
        float x = Bounds.x;
        float y = Bounds.y;

        _children = new Quadtree[4]
        {
            new(new Rect(x,      y,      hw, hh), _capacity, _maxDepth, Depth + 1),
            new(new Rect(x + hw, y,      hw, hh), _capacity, _maxDepth, Depth + 1),
            new(new Rect(x,      y + hh, hw, hh), _capacity, _maxDepth, Depth + 1),
            new(new Rect(x + hw, y + hh, hw, hh), _capacity, _maxDepth, Depth + 1),
        };

        // Move existing entities into children
        var existing = _entities;
        _entities = new List<HerbivoreAgent>();
        foreach (var e in existing)
            foreach (var child in _children)
                child.Insert(e);

        // Aggregate stats
        TotalNodes = 1;
        MaxDepthReached = Depth;
        foreach (var child in _children)
        {
            TotalNodes += child.TotalNodes;
            MaxDepthReached = Mathf.Max(MaxDepthReached, child.MaxDepthReached);
        }
    }

    // ──────────────────────────────────────────────
    // Query
    // ──────────────────────────────────────────────
    public List<HerbivoreAgent> QueryRadius(Vector2 center, float radius, int totalEntityCount, ref int saved)
    {
        var result = new List<HerbivoreAgent>();
        QueryRadius(center, radius, result, totalEntityCount, ref saved);
        return result;
    }

    private void QueryRadius(Vector2 center, float radius, List<HerbivoreAgent> result, int totalCount, ref int saved)
    {
        if (!CircleOverlapsRect(center, radius, Bounds))
        {
            // All entities in this node skipped
            saved += CountAll();
            return;
        }

        if (IsLeaf)
        {
            float r2 = radius * radius;
            foreach (var e in _entities)
                if ((e.Position - center).sqrMagnitude <= r2)
                    result.Add(e);
        }
        else
        {
            foreach (var child in _children)
                child.QueryRadius(center, radius, result, totalCount, ref saved);
        }
    }

    private int CountAll()
    {
        if (IsLeaf) return _entities.Count;
        int n = 0;
        foreach (var c in _children) n += c.CountAll();
        return n;
    }

    private static bool CircleOverlapsRect(Vector2 center, float radius, Rect rect)
    {
        float nearX = Mathf.Clamp(center.x, rect.xMin, rect.xMax);
        float nearY = Mathf.Clamp(center.y, rect.yMin, rect.yMax);
        float dx = center.x - nearX;
        float dy = center.y - nearY;
        return dx * dx + dy * dy <= radius * radius;
    }

    // ──────────────────────────────────────────────
    // Draw gizmos (called from SimulationBoard in OnDrawGizmos)
    // ──────────────────────────────────────────────
    public void DrawGizmos(bool depthColor)
    {
        Color c;
        if (depthColor)
        {
            float t = Depth / 8f;
            c = Color.Lerp(new Color(0.4f, 0.8f, 1f, 0.25f), new Color(1f, 0.3f, 0.3f, 0.55f), t);
        }
        else
        {
            c = new Color(0.6f, 0.9f, 1f, 0.3f);
        }

        Gizmos.color = c;
        Gizmos.DrawWireCube(Bounds.center, new Vector3(Bounds.width, Bounds.height, 0));

        if (!IsLeaf)
            foreach (var child in _children)
                child.DrawGizmos(depthColor);
    }

    // For runtime GL drawing
    public void CollectLines(List<(Vector2 a, Vector2 b, Color col)> lines, bool depthColor)
    {
        Color c;
        if (depthColor)
        {
            float t = Mathf.Clamp01(Depth / 8f);
            c = Color.Lerp(new Color(0.4f, 0.9f, 1f, 0.25f), new Color(1f, 0.35f, 0.35f, 0.6f), t);
        }
        else
        {
            c = new Color(0.7f, 0.95f, 1f, 0.35f);
        }

        // Four edges of this node
        var r = Bounds;
        lines.Add((new Vector2(r.xMin, r.yMin), new Vector2(r.xMax, r.yMin), c));
        lines.Add((new Vector2(r.xMax, r.yMin), new Vector2(r.xMax, r.yMax), c));
        lines.Add((new Vector2(r.xMax, r.yMax), new Vector2(r.xMin, r.yMax), c));
        lines.Add((new Vector2(r.xMin, r.yMax), new Vector2(r.xMin, r.yMin), c));

        if (!IsLeaf)
            foreach (var child in _children)
                child.CollectLines(lines, depthColor);
    }

    // Aggregate stats up from children
    public void AggregateStats(out int totalNodes, out int maxDepth)
    {
        totalNodes = 0;
        maxDepth = 0;
        AggregateStatsRecursive(ref totalNodes, ref maxDepth);
    }

    private void AggregateStatsRecursive(ref int totalNodes, ref int maxDepth)
    {
        totalNodes++;
        if (Depth > maxDepth) maxDepth = Depth;
        if (!IsLeaf)
            foreach (var c in _children)
                c.AggregateStatsRecursive(ref totalNodes, ref maxDepth);
    }
}
