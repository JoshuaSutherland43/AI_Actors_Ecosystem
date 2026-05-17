using UnityEngine;

/// <summary>
/// Attach to the Main Camera. Renders quadtree lines via GL in OnPostRender
/// so they always draw on top of the board but below the UI.
/// </summary>
[RequireComponent(typeof(Camera))]
public class QuadtreeOverlayRenderer : MonoBehaviour
{
    public SimulationBoard board;

    private Material _mat;

    private void Awake()
    {
        _mat = new Material(Shader.Find("Hidden/Internal-Colored"))
        {
            hideFlags = HideFlags.HideAndDontSave
        };
        _mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        _mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        _mat.SetInt("_Cull",     (int)UnityEngine.Rendering.CullMode.Off);
        _mat.SetInt("_ZWrite",   0);
    }

    private void OnPostRender()
    {
        if (board == null || !board.showQuadtree) return;

        _mat.SetPass(0);
        GL.PushMatrix();
        GL.LoadProjectionMatrix(GetComponent<Camera>().projectionMatrix);
        GL.modelview = GetComponent<Camera>().worldToCameraMatrix;
        GL.Begin(GL.LINES);

        // board exposes _qtLines through a public accessor
        foreach (var (a, b, col) in board.GetQTLines())
        {
            GL.Color(col);
            GL.Vertex3(a.x, a.y, 0);
            GL.Vertex3(b.x, b.y, 0);
        }

        GL.End();
        GL.PopMatrix();
    }
}
