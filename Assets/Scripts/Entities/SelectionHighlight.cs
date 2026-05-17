using UnityEngine;

/// <summary>
/// Attach to a child of HerbivoreAgent prefab.
/// Shows a pulsing ring when the entity is selected.
/// Uses a simple Line Renderer in a circle shape.
/// </summary>
[RequireComponent(typeof(LineRenderer))]
public class SelectionHighlight : MonoBehaviour
{
    [Range(8, 32)] public int segments = 20;
    public float radius = 0.28f;
    public Color ringColor = new Color(1f, 1f, 0f, 0.85f);
    public float pulseSpeed = 3f;
    public float pulseAmount = 0.05f;

    private LineRenderer _lr;
    private bool _visible;

    private void Awake()
    {
        _lr = GetComponent<LineRenderer>();
        _lr.loop = true;
        _lr.useWorldSpace = false;
        _lr.startWidth = 0.03f;
        _lr.endWidth   = 0.03f;
        _lr.material   = new Material(Shader.Find("Sprites/Default"));
        _lr.startColor = ringColor;
        _lr.endColor   = ringColor;
        _lr.positionCount = segments;
        SetVisible(false);
        BuildCircle(radius);
    }

    private void Update()
    {
        if (!_visible) return;
        float r = radius + Mathf.Sin(Time.time * pulseSpeed) * pulseAmount;
        BuildCircle(r);
    }

    private void BuildCircle(float r)
    {
        for (int i = 0; i < segments; i++)
        {
            float angle = (float)i / segments * Mathf.PI * 2f;
            _lr.SetPosition(i, new Vector3(Mathf.Cos(angle) * r, Mathf.Sin(angle) * r, 0));
        }
    }

    public void SetVisible(bool vis)
    {
        _visible = vis;
        _lr.enabled = vis;
    }
}
