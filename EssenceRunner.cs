using System;
using System.IO;
using System.Linq;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Components.Actions;
using GamesByMalcs.Melvor2.Components.ArtisanAction;
using GamesByMalcs.Melvor2.Entities.Runecrafting;
using GamesByMalcs.Melvor2.GameMenus;
using Godot;
using MelvorModLoader;

namespace EssenceRunner;

// While runecrafting, switches between the recipes of the current category so every
// product's bank count climbs in steps of the batch size.
// Order: fill every recipe up to the highest count's step, then raise all of them one step,
// in the game's recipe order. Products at or above the max are skipped; when every
// craftable product is capped, runecrafting stops.
// Offline progress runs the same tick loop on a background thread, so balancing applies there too.
public class EssenceRunnerMod : IMod
{
    const int spinBoxLimit = 1_000_000_000;
    const int nodeCheckInterval = 30; // frames between checks for the footer icon and a new save.
    const int buttonSpacing = 3;
    static readonly Vector2 toggleButtonSize = new Vector2(46, 30);
    static readonly Vector2 spinBoxSize = new Vector2(130, 30);
    static readonly Vector2 labelSize = new Vector2(110, 30);

    ModContext ctx;
    bool enabled;
    int batch = 1000; // craft each product up to the next multiple of this before switching.
    int max; // 0 means no cap.

    Button footerIcon;
    Control popup;
    Button toggleButton;

    Game hookedGame;
    ArtisanActionComponent action;
    volatile bool recountNeeded = true; // counts only change when a recipe completes, so skip the scan otherwise.

    string settingsFile => Path.Combine(ctx.Directory, "settings.txt");

    public void OnLoad(ModContext ctx)
    {
        this.ctx = ctx;
        LoadSettings();
        ctx.Frame += Update;
        ctx.Tree.NodeAdded += NodeAddedHandler;
    }

    void LoadSettings()
    {
        if (!File.Exists(settingsFile)) return;
        string[] parts = File.ReadAllText(settingsFile).Trim().Split(',');
        if (parts.Length != 3) return;
        if (bool.TryParse(parts[0], out bool savedEnabled)) enabled = savedEnabled;
        if (int.TryParse(parts[1], out int savedBatch) && savedBatch >= 1) batch = savedBatch;
        if (int.TryParse(parts[2], out int savedMax) && savedMax >= 0) max = savedMax;
    }

    void SaveSettings() => File.WriteAllText(settingsFile, enabled + "," + batch + "," + max);

    void Update()
    {
        if (popup == null) BuildPopup();

        var game = GameAccess.CurrentGame;
        if (game == null || !game.IsSaveLoaded)
        {
            popup.Visible = false;
            return;
        }

        // Fallback for a game that was already loaded when the mod started.
        if (game != hookedGame) Hook(game);

        if (Engine.GetProcessFrames() % nodeCheckInterval != 0) return;

        // Sits left of TimeSkip's icon if that mod is installed.
        if (!GodotObject.IsInstanceValid(footerIcon))
        {
            footerIcon = Footer.AddIcon(ctx.Tree, "EssenceRunnerIcon", "res://Assets/Items/VolatileFireRune.png", "TimeSkipIcon", goldTint: false);
            if (footerIcon != null) footerIcon.Pressed += () => Footer.Toggle(popup, footerIcon);
        }
    }

    // Loading a save adds a new GameView to the tree, and offline progress for the time away
    // starts right after that AddChild. Hooking here gets in before the first offline tick.
    void NodeAddedHandler(Node node)
    {
        if (node is not GameView) return;
        // GameView.Game is not public; GameAccess reads it from the view now in the tree.
        var game = GameAccess.CurrentGame;
        if (game != null && game != hookedGame) Hook(game);
    }

    void Hook(Game game)
    {
        if (action != null)
        {
            action.Ticked -= TickedHandler;
            action.RecipeCreated -= RecipeCreatedHandler;
            action.Started -= StartedHandler;
        }
        hookedGame = game;
        var runecrafting = game.Entities.GetAllObjects().OfType<RunecraftingEntity>().FirstOrDefault();
        action = runecrafting?.Action;
        if (action == null)
        {
            ctx.Log("runecrafting entity not found");
            return;
        }
        action.Ticked += TickedHandler;
        action.RecipeCreated += RecipeCreatedHandler;
        action.Started += StartedHandler;
        recountNeeded = true;
    }

    void RecipeCreatedHandler(object sender, EventArgs e) => recountNeeded = true;
    void StartedHandler(object sender, EventArgs e) => recountNeeded = true;

    // Ticked fires at the start of each tick, before the action timer advances,
    // so a switch here lands right after the previous craft finished.
    void TickedHandler(object sender, ActionTickedEventArgs e)
    {
        if (!enabled || !recountNeeded) return;
        recountNeeded = false;

        var current = action.CreatingRecipe;
        if (current == null) return;

        var next = PickRecipe(current, out bool allCapped);
        if (allCapped)
        {
            action.TryStop(false);
            ctx.Log($"every rune at max {max}, stopped");
            return;
        }
        if (next == null || next == current) return;

        // Same path as clicking a recipe while the action runs: StartCreating() swaps the
        // creating recipe when the selected one differs from it.
        action.SelectRecipe(next);
        action.StartCreating();
    }

    ActiveArtisanRecipe PickRecipe(ActiveArtisanRecipe current, out bool allCapped)
    {
        allCapped = false;
        int Count(ActiveArtisanRecipe r) => action.Inventory.GetQuantity(r.Recipe.Products[0].Item);

        var craftable = action.ActiveRecipes.Values
            .Where(r => r.Category == current.Category)
            .Where(r => r.Requirements == null || r.Requirements.AreMet)
            .Where(r => r.Recipe.Products.Count > 0)
            .Where(r => action.CanCreateRecipe(r))
            .ToList();
        var candidates = craftable.Where(r => max == 0 || Count(r) < max).ToList();
        if (candidates.Count == 0)
        {
            allCapped = craftable.Count > 0;
            return null;
        }

        long Step(ActiveArtisanRecipe r) => Count(r) / batch;

        var steps = candidates.Select(Step).ToList();
        long highest = steps.Max();
        bool allLevel = steps.All(s => s == highest);
        long target = allLevel ? highest + 1 : highest;

        for (int i = 0; i < candidates.Count; i++)
            if (steps[i] < target) return candidates[i];
        return null;
    }

    void BuildPopup()
    {
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", buttonSpacing);

        toggleButton = new Button();
        toggleButton.ToggleMode = true;
        toggleButton.ButtonPressed = enabled;
        toggleButton.Text = enabled ? "On" : "Off";
        toggleButton.CustomMinimumSize = toggleButtonSize;
        Footer.Style(toggleButton);
        toggleButton.Toggled += SetEnabled;
        column.AddChild(toggleButton);

        column.AddChild(NumberRow("Craft at once", 1, batch, SetBatch));
        column.AddChild(NumberRow("Max (0 = none)", 0, max, SetMax));

        popup = Footer.MakePopup(ctx.Tree, column);
    }

    HBoxContainer NumberRow(string text, int minValue, int value, Action<int> onChanged)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", buttonSpacing);

        var label = new Label();
        label.Text = text;
        label.CustomMinimumSize = labelSize;
        label.VerticalAlignment = VerticalAlignment.Center;
        row.AddChild(label);

        var spinBox = new SpinBox();
        spinBox.MinValue = minValue;
        spinBox.MaxValue = spinBoxLimit;
        spinBox.Step = 1;
        spinBox.Rounded = true;
        spinBox.Value = value;
        spinBox.CustomMinimumSize = spinBoxSize;
        spinBox.ValueChanged += newValue => onChanged((int)newValue);
        row.AddChild(spinBox);
        return row;
    }

    void SetEnabled(bool isEnabled)
    {
        enabled = isEnabled;
        toggleButton.Text = isEnabled ? "On" : "Off";
        recountNeeded = true;
        SaveSettings();
        ctx.Log(isEnabled ? "enabled" : "disabled");
    }

    void SetBatch(int newBatch)
    {
        batch = newBatch;
        recountNeeded = true;
        SaveSettings();
        ctx.Log($"craft at once {newBatch}");
    }

    void SetMax(int newMax)
    {
        max = newMax;
        recountNeeded = true;
        SaveSettings();
        ctx.Log(newMax == 0 ? "max off" : $"max {newMax}");
    }
}
