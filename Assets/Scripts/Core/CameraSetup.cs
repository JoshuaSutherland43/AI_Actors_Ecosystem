using UnityEngine;
using System.Collections;

/// <summary>
/// Positions the orthographic camera to frame the simulation board
/// and the right-side UI panel. Call after board is initialised.
/// </summary>
[RequireComponent(typeof(Camera))]
public class CameraSetup : MonoBehaviour
{
    public SimulationConfig config;
    public TileGrid tileGrid;

    [Tooltip("Extra world units of padding around the board")]
    public float padding = 1.5f;

    private void Start()
    {
        if (tileGrid == null)
            tileGrid = FindAnyObjectByType<TileGrid>();

        if (config == null)
        {
            var board = FindAnyObjectByType<SimulationBoard>();
            if (board != null) config = board.config;
        }

        StartCoroutine(FrameWhenReady());
    }

    public void Frame()
    {
        var cam = GetComponent<Camera>();
        if (cam == null || !cam.orthographic || config == null || tileGrid == null) return;

        float boardW = config.gridWidth  * config.tileSize;
        float boardH = config.gridHeight * config.tileSize;

        // Position camera centered on the board (board is at world 0,0 by default)
        transform.position = new Vector3(
            tileGrid.transform.position.x,
            tileGrid.transform.position.y,
            -10f
        );

        // Fit the board vertically, then check horizontal
        float orthoH = boardH * 0.5f + padding;
        float orthoW = (boardW * 0.5f + padding) / cam.aspect;
        cam.orthographicSize = Mathf.Max(orthoH, orthoW);
    }

    private IEnumerator FrameWhenReady()
    {
        float timeout = 2f;
        float t = 0f;
        while (t < timeout && (config == null || tileGrid == null || tileGrid.Width <= 0 || tileGrid.Height <= 0))
        {
            t += Time.unscaledDeltaTime;
            yield return null;
        }

        Frame();
    }
}
