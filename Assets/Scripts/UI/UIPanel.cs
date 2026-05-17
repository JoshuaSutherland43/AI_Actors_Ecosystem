using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// Simple toggleable simulation overlay with stats and controls.
/// Inspired by the referenced debug HUD patterns (runtime metrics + keybind legend).
/// </summary>
public class UIPanel : MonoBehaviour
{
    [Header("References")]
    public SimulationBoard board;

    [Header("Simulation Metrics")]
    public TextMeshProUGUI labelFPS;
    public TextMeshProUGUI labelSpeed;
    public TextMeshProUGUI labelEntities;
    public TextMeshProUGUI labelInfected;
    public TextMeshProUGUI labelDead;

    [Header("Quadtree Metrics")]
    public TextMeshProUGUI labelQTNodes;
    public TextMeshProUGUI labelQTDepth;
    public TextMeshProUGUI labelQTSaved;
    public TextMeshProUGUI labelAcidTimer;
    public TextMeshProUGUI labelPhase;

    [Header("Environment Metrics")]
    public TextMeshProUGUI labelGrass;
    public TextMeshProUGUI labelFungus;
    public TextMeshProUGUI labelDead_soil;
    public TextMeshProUGUI labelShelter;
    public TextMeshProUGUI labelAcidSoil;
    public TextMeshProUGUI labelFertileSoil;

    [Header("Selected Entity Panel")]
    public GameObject selectedPanel;
    public TextMeshProUGUI selectedInfo;

    [Header("Personality Legend")]
    public TextMeshProUGUI legendText;

    [Header("Speed Slider")]
    public Slider speedSlider;
    public TextMeshProUGUI speedLabel;

    [Header("Lecturer Snapshot")]
    public TextMeshProUGUI lecturerInfo;

    private readonly float[] speedPresets = { 0.25f, 0.5f, 1f, 2f, 5f };
    private GameObject _panelRoot;

    private void Start()
    {
        if (board == null)
            board = FindAnyObjectByType<SimulationBoard>();

        if (board == null)
        {
            Debug.LogError("UIPanel could not find SimulationBoard.", this);
            enabled = false;
            return;
        }

        EnsureCanvasAndPanel();

        board.OnStatsUpdated.AddListener(UpdateMetrics);
        board.OnAgentSelected.AddListener(UpdateSelectedPanel);

        if (speedSlider != null)
        {
            speedSlider.minValue = 0;
            speedSlider.maxValue = speedPresets.Length - 1;
            speedSlider.wholeNumbers = true;
            speedSlider.value = 2;
            speedSlider.onValueChanged.RemoveListener(OnSpeedChanged);
            speedSlider.onValueChanged.AddListener(OnSpeedChanged);
            OnSpeedChanged(speedSlider.value);
        }

        BuildLegend();
        BuildLecturerInfo();

        if (selectedPanel != null)
            selectedPanel.SetActive(false);

        SetOverlayVisible(true);
    }

    private void Update()
    {
        HandleHotkeys();

        if (board != null)
        {
            UpdateMetrics(board.Stats);
            BuildLecturerInfo();
        }

        if (selectedPanel != null && selectedPanel.activeSelf && selectedInfo != null)
        {
            var selected = board.GetSelectedAgent();
            if (selected != null && !selected.IsDead)
                selectedInfo.text = selected.GetPanelInfo();
        }
    }

    private void OnSpeedChanged(float val)
    {
        int idx = Mathf.Clamp(Mathf.RoundToInt(val), 0, speedPresets.Length - 1);
        float s = speedPresets[idx];
        board.SetSpeed(s);
        if (speedLabel != null)
            speedLabel.text = $"{s}x";
    }

    private void UpdateMetrics(SimStats s)
    {
        SetLabel(labelFPS, $"{s.fps}");
        SetLabel(labelSpeed, $"{s.speed:0.##}x");
        SetLabel(labelEntities, $"{s.totalEntities}");
        SetLabel(labelInfected, $"{s.infectedCount}");
        SetLabel(labelDead, $"{s.deadCount}");
        SetLabel(labelQTNodes, $"{s.qtNodes}");
        SetLabel(labelQTDepth, $"{s.qtMaxDepth}");
        SetLabel(labelQTSaved, $"{s.candidatesSaved}");
        SetLabel(
            labelAcidTimer,
            s.acidRainActive
                ? $"ACTIVE {s.acidRainSecondsRemaining:0.0}s ({s.acidRainTicksRemaining}t)"
                : $"{s.acidRainSecondsUntilNext:0.0}s ({s.acidRainTicksUntilNext}t)");
        SetLabel(labelPhase, s.ecosystemPhase ?? "N/A");
        SetLabel(labelGrass, $"{s.grassTiles}");
        SetLabel(labelFungus, $"{s.fungusTiles}");
        SetLabel(labelDead_soil, $"{s.deadSoilTiles}");
        SetLabel(labelShelter, $"{s.shelterTiles}");
        SetLabel(labelAcidSoil, $"{s.acidSoilTiles}");
        SetLabel(labelFertileSoil, $"{s.fertileSoilTiles}");
    }

    private static void SetLabel(TextMeshProUGUI tmp, string text)
    {
        if (tmp != null)
            tmp.text = text;
    }

    private void UpdateSelectedPanel(HerbivoreAgent agent)
    {
        if (selectedPanel == null)
            return;

        if (agent == null)
        {
            selectedPanel.SetActive(false);
            return;
        }

        selectedPanel.SetActive(true);
        if (selectedInfo != null)
            selectedInfo.text = agent.GetPanelInfo();
    }

    private void BuildLegend()
    {
        if (legendText == null)
            return;

        var types = System.Enum.GetValues(typeof(PersonalityType));
        var sb = new System.Text.StringBuilder();

        foreach (PersonalityType t in types)
        {
            var p = HerbivorePersonality.FromType(t);
            string hex = ColorUtility.ToHtmlStringRGB(p.healthyColor);
            sb.AppendLine($"<color=#{hex}>■</color> {t}");
        }

        legendText.text = sb.ToString();
    }

    private void BuildLecturerInfo()
    {
        if (board == null || board.config == null || lecturerInfo == null)
            return;

        float infectedPct = board.Stats.totalEntities > 0
            ? (100f * board.Stats.infectedCount / board.Stats.totalEntities)
            : 0f;

        lecturerInfo.text =
            "F1 Panel  F2 Pause  F3 Quadtree  F4 Decision Tree  F5 Scene Stats  F6 Speed+  F7 Speed-  F8 Paths\n" +
            $"Time: {board.Stats.simulatedSeconds:0.0}s  Ticks: {board.Stats.tickCount}  State: {(board.Stats.isPaused ? "Paused" : "Running")}\n" +
            $"Tick: {board.config.tickInterval:0.000}s  Speed: {board.Stats.speed:0.##}x  FPS: {board.Stats.fps}\n" +
            $"Acid Rain: {(board.Stats.acidRainActive ? $"ACTIVE ({board.Stats.acidRainSecondsRemaining:0.0}s / {board.Stats.acidRainTicksRemaining} ticks left)" : $"in {board.Stats.acidRainSecondsUntilNext:0.0}s / {board.Stats.acidRainTicksUntilNext} ticks")}  Phase: {board.Stats.ecosystemPhase}\n" +
            $"Infection: {board.Stats.infectedCount}/{board.Stats.totalEntities} ({infectedPct:0.0}%)\n" +
            $"Terrain: G {board.Stats.grassTiles} | F {board.Stats.fungusTiles} | D {board.Stats.deadSoilTiles} | S {board.Stats.shelterTiles} | A {board.Stats.acidSoilTiles}\n" +
            $"Quadtree: Nodes {board.Stats.qtNodes} | Depth {board.Stats.qtMaxDepth} | Saved/tick {board.Stats.candidatesSaved}\n" +
            $"Queries: {board.Stats.queriesPerFrame}/tick | Total Queries: {board.Stats.totalQueries} | Total Saved: {board.Stats.totalSavedChecks}\n" +
            "Lifecycle: fungus->infection->death->ring spread->dead soil->regrowth";
    }

    private void EnsureCanvasAndPanel()
    {
        Canvas canvas = GetComponentInParent<Canvas>();
        if (canvas == null)
            canvas = FindAnyObjectByType<Canvas>();

        if (canvas == null)
        {
            var go = new GameObject("Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;

            var scaler = go.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 1f;
        }

        if (transform.parent != canvas.transform)
            transform.SetParent(canvas.transform, false);

        var oldPanel = transform.Find("RightPanel");
        if (oldPanel != null)
            Destroy(oldPanel.gameObject);

        var panel = EnsurePanelRoot(transform);
        _panelRoot = panel.gameObject;

        AddSectionHeader(panel, "SIMULATION");
        labelFPS = AddMetricRow(panel, "FPS");
        labelSpeed = AddMetricRow(panel, "Speed");
        labelEntities = AddMetricRow(panel, "Entities");
        labelInfected = AddMetricRow(panel, "Infected");
        labelDead = AddMetricRow(panel, "Dead");

        AddSectionHeader(panel, "QUADTREE");
        labelQTNodes = AddMetricRow(panel, "Nodes");
        labelQTDepth = AddMetricRow(panel, "Max Depth");
        labelQTSaved = AddMetricRow(panel, "Saved Checks");
        labelAcidTimer = AddMetricRow(panel, "Acid Rain Timer");
        labelPhase = AddMetricRow(panel, "Ecosystem Phase");

        AddSectionHeader(panel, "ENVIRONMENT");
        labelGrass = AddMetricRow(panel, "Grass");
        labelFungus = AddMetricRow(panel, "Fungus");
        labelDead_soil = AddMetricRow(panel, "Dead Soil");
        labelShelter = AddMetricRow(panel, "Shelter");
        labelAcidSoil = AddMetricRow(panel, "Acid Soil");
        labelFertileSoil = AddMetricRow(panel, "Fertile Soil");

        AddSectionHeader(panel, "CONTROLS");
        CreateButton(panel, "Play", OnPlay);
        CreateButton(panel, "Pause", OnPause);
        CreateButton(panel, "Toggle Pause", OnTogglePause);
        CreateButton(panel, "Reset", OnReset);
        CreateButton(panel, "Speed +", OnSpeedUp);
        CreateButton(panel, "Speed -", OnSpeedDown);
        CreateButton(panel, "Quadtree", OnToggleQuadtree);
        CreateButton(panel, "Decision Tree", OnToggleDecisionTree);
        CreateButton(panel, "Scene Stats", OnToggleSceneStats);
        CreateButton(panel, "Paths", OnTogglePaths);
        CreateButton(panel, "Infect", OnSpawnInfected);

        var speedRow = CreateRow(panel, 28f);
        speedSlider = CreateSimpleSlider(speedRow);
        speedLabel = CreateValueText(speedRow, "1x", 18);

        AddSectionHeader(panel, "LEGEND");
        legendText = CreateBodyText(panel, 18);

        AddSectionHeader(panel, "SELECTED");
        selectedPanel = new GameObject("SelectedPanel", typeof(RectTransform), typeof(VerticalLayoutGroup));
        selectedPanel.transform.SetParent(panel, false);
        var selectedRt = selectedPanel.GetComponent<RectTransform>();
        selectedRt.sizeDelta = new Vector2(0f, 260f);
        selectedInfo = CreateBodyText(selectedPanel.transform, 14);

        AddSectionHeader(panel, "ASSESSMENT SNAPSHOT");
        lecturerInfo = CreateBodyText(panel, 15);
    }

    private static Transform EnsurePanelRoot(Transform parent)
    {
        var existing = parent.Find("RightPanel");
        if (existing != null)
            return existing;

        var panel = new GameObject("RightPanel", typeof(RectTransform), typeof(Image), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
        panel.transform.SetParent(parent, false);

        var rt = panel.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(1f, 0f);
        rt.anchorMax = new Vector2(1f, 1f);
        rt.pivot = new Vector2(1f, 0.5f);
        rt.sizeDelta = new Vector2(360f, 0f);
        rt.anchoredPosition = Vector2.zero;

        var img = panel.GetComponent<Image>();
        img.color = new Color32(16, 20, 34, 228);

        var layout = panel.GetComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(14, 14, 14, 14);
        layout.spacing = 8f;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;

        var fitter = panel.GetComponent<ContentSizeFitter>();
        fitter.verticalFit = ContentSizeFitter.FitMode.Unconstrained;

        return panel.transform;
    }

    private static void AddSectionHeader(Transform parent, string title)
    {
        var t = CreateBodyText(parent, 19);
        t.text = title;
        t.fontStyle = FontStyles.Bold;
        t.color = new Color32(221, 228, 255, 255);
    }

    private static TextMeshProUGUI AddMetricRow(Transform parent, string label)
    {
        var row = CreateRow(parent, 26f);
        CreateValueText(row, label, 17).alignment = TextAlignmentOptions.Left;
        return CreateValueText(row, "-", 17);
    }

    private static Transform CreateRow(Transform parent, float height)
    {
        var row = new GameObject("Row", typeof(RectTransform), typeof(HorizontalLayoutGroup));
        row.transform.SetParent(parent, false);

        var rt = row.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(0f, height);

        var layout = row.GetComponent<HorizontalLayoutGroup>();
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = true;
        layout.spacing = 8f;

        return row.transform;
    }

    private static TextMeshProUGUI CreateValueText(Transform parent, string text, float size)
    {
        var go = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
        go.transform.SetParent(parent, false);
        var tmp = go.GetComponent<TextMeshProUGUI>();
        tmp.text = text;
        tmp.fontSize = size;
        tmp.color = new Color32(230, 235, 255, 255);
        tmp.alignment = TextAlignmentOptions.Right;
        tmp.textWrappingMode = TextWrappingModes.NoWrap;
        return tmp;
    }

    private static TextMeshProUGUI CreateBodyText(Transform parent, float size)
    {
        var go = new GameObject("BodyText", typeof(RectTransform), typeof(TextMeshProUGUI));
        go.transform.SetParent(parent, false);
        var tmp = go.GetComponent<TextMeshProUGUI>();
        tmp.text = string.Empty;
        tmp.fontSize = size;
        tmp.color = new Color32(220, 225, 245, 255);
        tmp.alignment = TextAlignmentOptions.TopLeft;
        tmp.textWrappingMode = TextWrappingModes.Normal;
        return tmp;
    }

    private static void CreateButton(Transform parent, string label, UnityAction callback)
    {
        var go = new GameObject(label + "Button", typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
        go.transform.SetParent(parent, false);

        var img = go.GetComponent<Image>();
        img.color = new Color32(55, 70, 100, 210);

        var btn = go.GetComponent<Button>();
        btn.onClick.AddListener(callback);

        var layout = go.GetComponent<LayoutElement>();
        layout.preferredHeight = 30f;

        var txt = CreateBodyText(go.transform, 16);
        txt.text = label;
        txt.alignment = TextAlignmentOptions.Center;
    }

    private static Slider CreateSimpleSlider(Transform parent)
    {
        var root = new GameObject("SpeedSlider", typeof(RectTransform), typeof(Slider), typeof(LayoutElement));
        root.transform.SetParent(parent, false);
        root.GetComponent<LayoutElement>().preferredHeight = 22f;

        var bg = new GameObject("Background", typeof(RectTransform), typeof(Image));
        bg.transform.SetParent(root.transform, false);
        bg.GetComponent<Image>().color = new Color32(40, 45, 65, 255);

        var fillArea = new GameObject("Fill Area", typeof(RectTransform));
        fillArea.transform.SetParent(root.transform, false);

        var fill = new GameObject("Fill", typeof(RectTransform), typeof(Image));
        fill.transform.SetParent(fillArea.transform, false);
        fill.GetComponent<Image>().color = new Color32(85, 170, 255, 255);

        var handleArea = new GameObject("Handle Slide Area", typeof(RectTransform));
        handleArea.transform.SetParent(root.transform, false);

        var handle = new GameObject("Handle", typeof(RectTransform), typeof(Image));
        handle.transform.SetParent(handleArea.transform, false);
        var handleImg = handle.GetComponent<Image>();
        handleImg.color = new Color32(245, 245, 255, 255);

        var slider = root.GetComponent<Slider>();
        slider.targetGraphic = handleImg;
        slider.fillRect = fill.GetComponent<RectTransform>();
        slider.handleRect = handle.GetComponent<RectTransform>();
        slider.direction = Slider.Direction.LeftToRight;

        Stretch(bg.GetComponent<RectTransform>(), 0, 0, 0, 0);
        Stretch(fillArea.GetComponent<RectTransform>(), 8, 8, 7, 7);
        Stretch(handleArea.GetComponent<RectTransform>(), 8, 8, 0, 0);
        Stretch(fill.GetComponent<RectTransform>(), 0, 0, 0, 0);

        var handleRt = handle.GetComponent<RectTransform>();
        handleRt.sizeDelta = new Vector2(14f, 22f);
        handleRt.anchorMin = new Vector2(0.5f, 0.5f);
        handleRt.anchorMax = new Vector2(0.5f, 0.5f);
        handleRt.pivot = new Vector2(0.5f, 0.5f);

        return slider;
    }

    private static void Stretch(RectTransform rt, float left, float right, float top, float bottom)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = new Vector2(left, bottom);
        rt.offsetMax = new Vector2(-right, -top);
    }

    private void HandleHotkeys()
    {
        if (board == null) return;

        if (TryHotkeyDown(KeyCode.F1))
            SetOverlayVisible(_panelRoot == null || !_panelRoot.activeSelf);
        if (TryHotkeyDown(KeyCode.F2))
            board.TogglePause();
        if (TryHotkeyDown(KeyCode.F3))
            board.ToggleQuadtree();
        if (TryHotkeyDown(KeyCode.F4))
            board.ToggleDecisionTreeOverlay();
        if (TryHotkeyDown(KeyCode.F5))
            board.ToggleSceneStatsOverlay();
        if (TryHotkeyDown(KeyCode.F6))
            board.AdjustSpeed(0.25f);
        if (TryHotkeyDown(KeyCode.F7))
            board.AdjustSpeed(-0.25f);
        if (TryHotkeyDown(KeyCode.F8))
            board.TogglePaths();
    }

    private void SetOverlayVisible(bool visible)
    {
        if (_panelRoot != null)
            _panelRoot.SetActive(visible);
    }

    private static bool TryHotkeyDown(KeyCode key)
    {
#if ENABLE_INPUT_SYSTEM
        if (Keyboard.current != null)
        {
            switch (key)
            {
                case KeyCode.F1: return Keyboard.current.f1Key.wasPressedThisFrame;
                case KeyCode.F2: return Keyboard.current.f2Key.wasPressedThisFrame;
                case KeyCode.F3: return Keyboard.current.f3Key.wasPressedThisFrame;
                case KeyCode.F4: return Keyboard.current.f4Key.wasPressedThisFrame;
                case KeyCode.F5: return Keyboard.current.f5Key.wasPressedThisFrame;
                case KeyCode.F6: return Keyboard.current.f6Key.wasPressedThisFrame;
                case KeyCode.F7: return Keyboard.current.f7Key.wasPressedThisFrame;
                case KeyCode.F8: return Keyboard.current.f8Key.wasPressedThisFrame;
            }
        }
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
        return Input.GetKeyDown(key);
#else
        return false;
#endif
    }

    public void OnPlay() => board.Play();
    public void OnPause() => board.Pause();
    public void OnTogglePause() => board.TogglePause();
    public void OnSpeedUp() => board.AdjustSpeed(0.25f);
    public void OnSpeedDown() => board.AdjustSpeed(-0.25f);
    public void OnReset() => board.Reset();
    public void OnToggleQuadtree() => board.ToggleQuadtree();
    public void OnToggleDecisionTree() => board.ToggleDecisionTreeOverlay();
    public void OnToggleSceneStats() => board.ToggleSceneStatsOverlay();
    public void OnTogglePaths() => board.TogglePaths();
    public void OnToggleDepthColor() => board.ToggleDepthColor();
    public void OnSpawnInfected() => board.SpawnInfected();
}
