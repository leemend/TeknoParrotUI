using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Layout;
using TeknoParrotUi.Common;

namespace TeknoParrotUi.Avalonia.Views;

/// <summary>
/// Golden Tee 2019 local player-profile editor.
///
/// This intentionally lives outside the generic settings/control views. Local
/// Golden Tee remains one physical RawInputTrackball control set; this component
/// only manages per-player identity and pre-launch appearance data.
/// </summary>
internal sealed class GoldenTeePlayerProfileEditor
{
    private const string ProfileName = "GoldenTeeLive2019";
    private const string DefaultLabel = "Default";
    private const string CreateLabel = "Create New Profile...";


    private readonly GameProfile _profile;
    private readonly StackPanel _host;
    private readonly TextBlock _status = new() { Opacity = 0.8, TextWrapping = global::Avalonia.Media.TextWrapping.Wrap };
    private readonly Dictionary<int, PlayerEditor> _players = new();
    private readonly Dictionary<FieldInformation, string> _baseline = new();
    private readonly GameProfile? _stockProfile;
    private FieldInformation? _rodAnchor;
    private CheckBox? _rodCheckBox;
    private bool _applyingRodPreferredSetup;

    private sealed class PlayerEditor
    {
        public required int Player { get; init; }
        public required ComboBox ProfileCombo { get; init; }
        public required TextBox ProfileNameBox { get; init; }
        public required TextBox InitialsBox { get; init; }
        public required Button SaveButton { get; init; }
        public required Button DeleteButton { get; init; }
        public required Dictionary<string, Control> ValueEditors { get; init; }
        public string? LoadedProfileName { get; set; }
    }

    public static bool Supports(GameProfile? profile) =>
        profile != null &&
        string.Equals(profile.ProfileName, ProfileName, StringComparison.OrdinalIgnoreCase);

    public GoldenTeePlayerProfileEditor(
        GameProfile profile,
        StackPanel host)
    {
        _profile = profile;
        _host = host;
        _stockProfile = LoadStockProfile(profile);
        _rodAnchor = _profile.ConfigValues.FirstOrDefault(field =>
            string.Equals(field.CategoryName, "Customization", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(field.FieldName, "Override Default Outfit", StringComparison.OrdinalIgnoreCase));

        if (_rodAnchor != null)
        {
            _rodAnchor.ShowRodPreferredSetup = true;
            _rodAnchor.RodPreferredSetup =
                string.Equals(_rodAnchor.RodPreferredSetupSaved, "1", StringComparison.Ordinal);
        }
    }

    public void Build()
    {
        AddHeader("Player Profiles");

        _host.Children.Add(new TextBlock
        {
            Text = "Assign optional profiles before game launch.",
            TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
            Opacity = 0.75,
            Margin = new global::Avalonia.Thickness(0, 0, 0, 8)
        });
        _host.Children.Add(_status);

        var assignments = GoldenTeeLocalPlayerProfiles.LoadAssignments();

        for (var player = 1; player <= 4; player++)
        {
            AddPlayer(player, assignments.TryGetValue(player, out var assigned) ? assigned : null);
        }

        CaptureBaseline();
    }

    public bool HasUnsavedChanges()
    {
        foreach (var field in ProfileFields())
        {
            if (_baseline.TryGetValue(field, out var original) &&
                !string.Equals(field.FieldValue ?? string.Empty, original, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    public void SaveIntoProfile()
    {
        foreach (var editor in _players.Values)
        {
            if (editor.Player == 1 && _rodAnchor?.RodPreferredSetup == true)
                continue;

            ApplyEditorValuesToFields(editor);
        }

        if (_rodAnchor?.RodPreferredSetup == true &&
            _players.TryGetValue(1, out var playerOne))
        {
            ApplyRodPreferredSetup(playerOne);
        }
    }

    private void AddPlayer(int player, string? assignedProfileName)
    {
        var playerPanel = new StackPanel
        {
            Spacing = 4
        };

        var playerExpander = new Expander
        {
            Header = $"Player {player}",
            IsExpanded = false,
            Content = playerPanel,
            Margin = new global::Avalonia.Thickness(0, 4, 0, 4)
        };

        _host.Children.Add(playerExpander);

        var names = GoldenTeeLocalPlayerProfiles.ListProfileNames().ToList();
        var profileOptions = new List<string> { DefaultLabel };
        profileOptions.AddRange(names);
        profileOptions.Add(CreateLabel);

        var combo = new ComboBox
        {
            ItemsSource = profileOptions,
            MinWidth = 260,
            SelectedItem = assignedProfileName != null &&
                           names.Contains(assignedProfileName, StringComparer.OrdinalIgnoreCase)
                ? names.First(x => string.Equals(x, assignedProfileName, StringComparison.OrdinalIgnoreCase))
                : DefaultLabel
        };

        var profileName = new TextBox { MinWidth = 220 };
        var initials = new TextBox { MinWidth = 100, MaxLength = 3 };

        var save = new Button { Content = "Save Profile" };
        var delete = new Button { Content = "Delete Profile" };

        var values = new Dictionary<string, Control>(StringComparer.OrdinalIgnoreCase);

        var editor = new PlayerEditor
        {
            Player = player,
            ProfileCombo = combo,
            ProfileNameBox = profileName,
            InitialsBox = initials,
            SaveButton = save,
            DeleteButton = delete,
            ValueEditors = values
        };
        _players[player] = editor;

        if (player == 1)
            AddRodPreferredSetupRow(playerPanel, editor);

        playerPanel.Children.Add(Row("Profile", combo));
        playerPanel.Children.Add(Row("Profile Name", profileName));
        playerPanel.Children.Add(Row("Initials", initials));

        foreach (var field in GetEditablePlayerFields(player))
        {
            var valueName = GetValueName(field, player);
            var control = BuildFieldEditor(field);
            AttachFieldUpdater(control, field);
            values[valueName] = control;
            playerPanel.Children.Add(Row(valueName, control));
        }

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new global::Avalonia.Thickness(0, 4, 0, 4)
        };
        actions.Children.Add(save);
        actions.Children.Add(delete);
        playerPanel.Children.Add(actions);

        combo.SelectionChanged += (_, _) => LoadSelection(editor);
        initials.TextChanged += (_, _) =>
        {
            SetInitialsFields(
                editor.Player,
                GoldenTeeLocalPlayerProfiles.NormalizeInitials(initials.Text));
        };
        save.Click += (_, _) => SaveLocalProfile(editor);
        delete.Click += (_, _) => DeleteLocalProfile(editor);

        LoadSelection(editor);
    }

    private void LoadSelection(PlayerEditor editor)
    {
        var selected = editor.ProfileCombo.SelectedItem as string ?? DefaultLabel;

        if (editor.Player == 1 &&
            !_applyingRodPreferredSetup &&
            !string.Equals(selected, DefaultLabel, StringComparison.Ordinal))
        {
            SetRodState(false);
            if (_rodCheckBox != null)
                _rodCheckBox.IsChecked = false;
            SetPlayerEditorEnabled(editor, true);
        }

        if (string.Equals(selected, DefaultLabel, StringComparison.Ordinal))
        {
            editor.LoadedProfileName = null;
            editor.ProfileNameBox.Text = string.Empty;
            LoadFieldsIntoEditor(editor);
            editor.InitialsBox.Text = ReadInitialsFromFields(editor.Player);
            editor.DeleteButton.IsEnabled = false;
            return;
        }

        if (string.Equals(selected, CreateLabel, StringComparison.Ordinal))
        {
            editor.LoadedProfileName = null;
            editor.ProfileNameBox.Text = string.Empty;
            editor.InitialsBox.Text = string.Empty;
            editor.DeleteButton.IsEnabled = false;
            return;
        }

        var profile = GoldenTeeLocalPlayerProfiles.Load(selected);
        if (profile == null)
        {
            ShowStatus($"Golden Tee profile \"{selected}\" could not be loaded.");
            return;
        }

        editor.LoadedProfileName = profile.ProfileName;
        editor.ProfileNameBox.Text = profile.ProfileName;
        editor.InitialsBox.Text = profile.Initials;
        editor.DeleteButton.IsEnabled = true;

        foreach (var valueName in editor.ValueEditors.Keys)
        {
            if (profile.Values.TryGetValue(valueName, out var value) &&
                editor.ValueEditors.TryGetValue(valueName, out var control))
            {
                SetControlValue(control, FindPlayerField(editor.Player, valueName), value);
            }
        }

        ApplyProfileToFields(editor.Player, profile);
        SaveAssignment(editor.Player, profile.ProfileName);
    }

    private void SaveLocalProfile(PlayerEditor editor)
    {
        var normalizedName =
            GoldenTeeLocalPlayerProfiles.NormalizeProfileName(editor.ProfileNameBox.Text);
        if (normalizedName == null)
        {
            ShowStatus("Profile name is required.");
            return;
        }

        var normalizedInitials =
            GoldenTeeLocalPlayerProfiles.NormalizeInitials(editor.InitialsBox.Text);
        if (normalizedInitials == null)
        {
            ShowStatus("Initials must be exactly 3 letters (A-Z).");
            return;
        }

        if (GoldenTeeLocalPlayerProfiles.ProfileNameExists(
                normalizedName,
                editor.LoadedProfileName))
        {
            ShowStatus($"A Golden Tee profile named \"{normalizedName}\" already exists.");
            return;
        }

        ApplyEditorValuesToFields(editor);

        var local = new GoldenTeeLocalPlayerProfiles.LocalPlayerProfile
        {
            ProfileName = normalizedName,
            Initials = normalizedInitials,
            Values = CapturePlayerValues(editor.Player)
        };

        GoldenTeeLocalPlayerProfiles.Save(local, editor.LoadedProfileName);
        editor.LoadedProfileName = local.ProfileName;
        SaveAssignment(editor.Player, local.ProfileName);
        RefreshProfileCombos(local.ProfileName, editor.Player);
        editor.DeleteButton.IsEnabled = true;

        ShowStatus($"Saved Golden Tee profile \"{local.ProfileName}\" for Player {editor.Player}.");
    }

    private void DeleteLocalProfile(PlayerEditor editor)
    {
        var selected = editor.LoadedProfileName;
        if (string.IsNullOrWhiteSpace(selected))
            return;

        GoldenTeeLocalPlayerProfiles.Delete(selected);
        editor.LoadedProfileName = null;
        editor.ProfileNameBox.Text = string.Empty;
        editor.InitialsBox.Text = string.Empty;
        RefreshProfileCombos(DefaultLabel, editor.Player);
        ShowStatus($"Deleted Golden Tee profile \"{selected}\".");
    }

    private void RefreshProfileCombos(string selectedForPlayer, int selectedPlayer)
    {
        var names = GoldenTeeLocalPlayerProfiles.ListProfileNames().ToList();

        foreach (var editor in _players.Values)
        {
            var current = editor.Player == selectedPlayer
                ? selectedForPlayer
                : editor.ProfileCombo.SelectedItem as string ?? DefaultLabel;

            var options = new List<string> { DefaultLabel };
            options.AddRange(names);
            options.Add(CreateLabel);
            editor.ProfileCombo.ItemsSource = options;

            editor.ProfileCombo.SelectedItem =
                options.FirstOrDefault(x => string.Equals(x, current, StringComparison.OrdinalIgnoreCase))
                ?? DefaultLabel;
        }
    }

    private void ApplyEditorValuesToFields(PlayerEditor editor)
    {
        foreach (var (valueName, control) in editor.ValueEditors)
        {
            var field = FindPlayerField(editor.Player, valueName);
            if (field == null)
                continue;

            field.FieldValue = ReadControlValue(control, field);
        }

        var initials = GoldenTeeLocalPlayerProfiles.NormalizeInitials(editor.InitialsBox.Text);
        SetInitialsFields(editor.Player, initials);
    }

    private void ApplyProfileToFields(
        int player,
        GoldenTeeLocalPlayerProfiles.LocalPlayerProfile local)
    {
        foreach (var (valueName, value) in local.Values)
        {
            var field = FindPlayerField(player, valueName);
            if (field != null)
                field.FieldValue = value;
        }

        SetInitialsFields(player, local.Initials);
        SetOutfitOverride(player, true);
    }

    private Dictionary<string, string> CapturePlayerValues(int player)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in GetEditablePlayerFields(player))
        {
            var name = GetValueName(field, player);
            values[name] = field.FieldValue ?? string.Empty;
        }
        return values;
    }

    private void LoadFieldsIntoEditor(PlayerEditor editor)
    {
        foreach (var (valueName, control) in editor.ValueEditors)
        {
            var field = FindPlayerField(editor.Player, valueName);
            if (field != null)
                SetControlValue(control, field, field.FieldValue ?? string.Empty);
        }
    }

    private FieldInformation? FindPlayerField(int player, string valueName)
    {
        return GetEditablePlayerFields(player).FirstOrDefault(field =>
            string.Equals(GetValueName(field, player), valueName, StringComparison.OrdinalIgnoreCase));
    }

    private IEnumerable<FieldInformation> GetEditablePlayerFields(int player)
    {
        var category = player == 1 ? "Customization" : $"Player {player} Customization";

        return _profile.ConfigValues.Where(field =>
            string.Equals(field.CategoryName, category, StringComparison.OrdinalIgnoreCase) &&
            !field.FieldName.EndsWith("Override Default Outfit", StringComparison.OrdinalIgnoreCase) &&
            !field.FieldName.EndsWith("Default Outfit", StringComparison.OrdinalIgnoreCase) &&
            !field.FieldName.EndsWith("Override Default Initials", StringComparison.OrdinalIgnoreCase) &&
            !field.FieldName.EndsWith("Default Initials", StringComparison.OrdinalIgnoreCase));
    }

    private static string GetValueName(FieldInformation field, int player)
    {
        var name = field.FieldName ?? string.Empty;

        if (player > 1)
        {
            var prefix = $"P{player} ";
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                name = name.Substring(prefix.Length);
        }

        if (name.StartsWith("Default ", StringComparison.OrdinalIgnoreCase))
            name = name.Substring("Default ".Length);

        return name;
    }

    private string ReadInitialsFromFields(int player)
    {
        var category = player == 1 ? "General" : $"Player {player} Customization";
        var name = player == 1 ? "Default Initials" : $"P{player} Default Initials";

        return _profile.ConfigValues.FirstOrDefault(x =>
                   string.Equals(x.CategoryName, category, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(x.FieldName, name, StringComparison.OrdinalIgnoreCase))
                   ?.FieldValue
               ?? string.Empty;
    }

    private void SetInitialsFields(int player, string? initials)
    {
        var category = player == 1 ? "General" : $"Player {player} Customization";
        var overrideName = player == 1
            ? "Override Default Initials"
            : $"P{player} Override Default Initials";
        var initialsName = player == 1
            ? "Default Initials"
            : $"P{player} Default Initials";

        var overrideField = _profile.ConfigValues.FirstOrDefault(x =>
            string.Equals(x.CategoryName, category, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.FieldName, overrideName, StringComparison.OrdinalIgnoreCase));
        var initialsField = _profile.ConfigValues.FirstOrDefault(x =>
            string.Equals(x.CategoryName, category, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.FieldName, initialsName, StringComparison.OrdinalIgnoreCase));

        if (overrideField != null)
            overrideField.FieldValue = initials == null ? "0" : "1";
        if (initialsField != null)
            initialsField.FieldValue = initials ?? string.Empty;
    }

    private void SetOutfitOverride(int player, bool enabled)
    {
        var category = player == 1 ? "Customization" : $"Player {player} Customization";
        var name = player == 1 ? "Override Default Outfit" : $"P{player} Override Default Outfit";

        var field = _profile.ConfigValues.FirstOrDefault(x =>
            string.Equals(x.CategoryName, category, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.FieldName, name, StringComparison.OrdinalIgnoreCase));

        if (field != null)
            field.FieldValue = enabled ? "1" : "0";
    }

    private void AddRodPreferredSetupRow(StackPanel playerPanel, PlayerEditor editor)
    {
        if (_rodAnchor == null || _stockProfile == null)
            return;

        var check = new CheckBox
        {
            IsChecked = _rodAnchor.RodPreferredSetup,
            VerticalAlignment = VerticalAlignment.Center
        };

        ToolTip.SetTip(
            check,
            "In Memory of Rod\n\nUses the original Golden Tee defaults for Player 1 outfit, equipment and accessories. Trackball sensitivity is locked to 25/25 while enabled.");

        check.IsCheckedChanged += async (_, _) =>
        {
            if (_applyingRodPreferredSetup)
                return;

            if (check.IsChecked == true)
            {
                if (HasCustomPlayerOneSettings() &&
                    TopLevel.GetTopLevel(_host) is Window owner)
                {
                    var confirmed = await Services.Dialogs.ConfirmAsync(
                        owner,
                        "Use Rod's Preferred Setup?",
                        "Player 1 currently has custom Golden Tee settings.\n\n" +
                        "Using Rod's Preferred Setup will reset those settings back to the original defaults and use 25/25 trackball sensitivity.\n\n" +
                        "Do you want to continue?");

                    if (!confirmed)
                    {
                        SetRodState(false);
                        check.IsChecked = false;
                        return;
                    }
                }

                ApplyRodPreferredSetup(editor);
            }
            else
            {
                SetRodState(false);
                SetPlayerEditorEnabled(editor, true);
            }
        };

        _rodCheckBox = check;
        playerPanel.Children.Add(Row("Use Rod's Preferred Setup", check));

        if (_rodAnchor.RodPreferredSetup)
            ApplyRodPreferredSetup(editor);
    }

    private void ApplyRodPreferredSetup(PlayerEditor editor)
    {
        if (_rodAnchor == null || _stockProfile?.ConfigValues == null)
            return;

        try
        {
            _applyingRodPreferredSetup = true;

            foreach (var stockField in _stockProfile.ConfigValues.Where(field =>
                         string.Equals(field.CategoryName, "Customization", StringComparison.OrdinalIgnoreCase)))
            {
                var currentField = _profile.ConfigValues.FirstOrDefault(field =>
                    string.Equals(field.CategoryName, stockField.CategoryName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(field.FieldName, stockField.FieldName, StringComparison.OrdinalIgnoreCase));

                if (currentField != null)
                    currentField.FieldValue = stockField.FieldValue;
            }

            // Rod and custom Player 1 appearance are mutually exclusive.
            _rodAnchor.FieldValue = "0";
            SetFieldValue("Trackball Sensitivity X", "25");
            SetFieldValue("Trackball Sensitivity Y", "25");
            SetRodState(true);

            // A named P1 profile must not silently override Rod at launch.
            var assignments = GoldenTeeLocalPlayerProfiles.LoadAssignments();
            assignments.Remove(1);
            GoldenTeeLocalPlayerProfiles.SaveAssignments(assignments);

            editor.LoadedProfileName = null;
            editor.ProfileCombo.SelectedItem = DefaultLabel;
            editor.ProfileNameBox.Text = string.Empty;
            editor.InitialsBox.Text = ReadInitialsFromFields(1);
            LoadFieldsIntoEditor(editor);
            SetPlayerEditorEnabled(editor, false);

            if (_rodCheckBox != null)
                _rodCheckBox.IsEnabled = true;
        }
        finally
        {
            _applyingRodPreferredSetup = false;
        }
    }

    private void SetRodState(bool enabled)
    {
        if (_rodAnchor == null)
            return;

        _rodAnchor.RodPreferredSetup = enabled;
        _rodAnchor.RodPreferredSetupSaved = enabled ? "1" : "0";
    }

    private void SetPlayerEditorEnabled(PlayerEditor editor, bool enabled)
    {
        editor.ProfileCombo.IsEnabled = enabled;
        editor.ProfileNameBox.IsEnabled = enabled;
        editor.InitialsBox.IsEnabled = enabled;
        editor.SaveButton.IsEnabled = enabled;
        editor.DeleteButton.IsEnabled = enabled && !string.IsNullOrWhiteSpace(editor.LoadedProfileName);

        foreach (var control in editor.ValueEditors.Values)
            control.IsEnabled = enabled;
    }

    private bool HasCustomPlayerOneSettings()
    {
        if (_stockProfile?.ConfigValues == null)
            return false;

        foreach (var stockField in _stockProfile.ConfigValues.Where(field =>
                     string.Equals(field.CategoryName, "Customization", StringComparison.OrdinalIgnoreCase)))
        {
            var currentField = _profile.ConfigValues.FirstOrDefault(field =>
                string.Equals(field.CategoryName, stockField.CategoryName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(field.FieldName, stockField.FieldName, StringComparison.OrdinalIgnoreCase));

            if (currentField != null &&
                !string.Equals(currentField.FieldValue, stockField.FieldValue, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private void SetFieldValue(string fieldName, string value)
    {
        var field = _profile.ConfigValues.FirstOrDefault(item =>
            string.Equals(item.FieldName?.Trim(), fieldName, StringComparison.OrdinalIgnoreCase));

        if (field != null)
            field.FieldValue = value;
    }

    private static GameProfile? LoadStockProfile(GameProfile currentProfile)
    {
        try
        {
            var profileFileName = Path.GetFileName(currentProfile.FileName);
            if (string.IsNullOrWhiteSpace(profileFileName))
                return null;

            var stockPath = Path.Combine("GameProfiles", profileFileName);
            if (!File.Exists(stockPath))
                return null;

            return JoystickHelper.DeSerializeGameProfile(stockPath, false);
        }
        catch
        {
            return null;
        }
    }

    private void SaveAssignment(int player, string profileName)
    {
        var assignments = GoldenTeeLocalPlayerProfiles.LoadAssignments();
        assignments[player] = profileName;
        GoldenTeeLocalPlayerProfiles.SaveAssignments(assignments);
    }

    private IEnumerable<FieldInformation> ProfileFields()
    {
        return _profile.ConfigValues.Where(field =>
            string.Equals(field.CategoryName, "Customization", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(field.CategoryName, "Player 2 Customization", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(field.CategoryName, "Player 3 Customization", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(field.CategoryName, "Player 4 Customization", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(field.FieldName, "Override Default Initials", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(field.FieldName, "Default Initials", StringComparison.OrdinalIgnoreCase));
    }

    private void CaptureBaseline()
    {
        _baseline.Clear();
        foreach (var field in ProfileFields())
            _baseline[field] = field.FieldValue ?? string.Empty;
    }

    private static Control BuildFieldEditor(FieldInformation field)
    {
        switch (field.FieldType)
        {
            case FieldType.Bool:
                return new CheckBox { IsChecked = field.FieldValue == "1" };

            case FieldType.Dropdown:
            case FieldType.DropdownIndex:
                var combo = new ComboBox
                {
                    ItemsSource = field.FieldOptions ?? new List<string>(),
                    SelectedItem = field.FieldValue,
                    MinWidth = 220
                };
                if (combo.SelectedItem == null && combo.ItemCount > 0)
                    combo.SelectedIndex = 0;
                return combo;

            case FieldType.Numeric:
                return new NumericUpDown
                {
                    Minimum = field.FieldMin,
                    Maximum = field.FieldMax == 0 ? decimal.MaxValue : field.FieldMax,
                    Value = decimal.TryParse(field.FieldValue, out var number) ? number : 0,
                    MinWidth = 140
                };

            default:
                return new TextBox { Text = field.FieldValue ?? string.Empty, MinWidth = 220 };
        }
    }

    private static void AttachFieldUpdater(Control control, FieldInformation field)
    {
        switch (control)
        {
            case CheckBox check:
                check.IsCheckedChanged += (_, _) =>
                    field.FieldValue = check.IsChecked == true ? "1" : "0";
                break;

            case ComboBox combo:
                combo.SelectionChanged += (_, _) =>
                    field.FieldValue = combo.SelectedItem as string ?? field.FieldValue;
                break;

            case NumericUpDown numeric:
                numeric.ValueChanged += (_, _) =>
                    field.FieldValue = ((long)(numeric.Value ?? 0)).ToString();
                break;

            case TextBox text:
                text.TextChanged += (_, _) =>
                    field.FieldValue = text.Text ?? string.Empty;
                break;
        }
    }

    private static string ReadControlValue(Control control, FieldInformation field)
    {
        return control switch
        {
            CheckBox check => check.IsChecked == true ? "1" : "0",
            ComboBox combo => combo.SelectedItem as string ?? field.FieldValue ?? string.Empty,
            NumericUpDown numeric => ((long)(numeric.Value ?? 0)).ToString(),
            TextBox text => text.Text ?? string.Empty,
            _ => field.FieldValue ?? string.Empty
        };
    }

    private static void SetControlValue(Control control, FieldInformation? field, string value)
    {
        switch (control)
        {
            case CheckBox check:
                check.IsChecked = value == "1";
                break;
            case ComboBox combo:
                combo.SelectedItem = value;
                if (combo.SelectedItem == null && combo.ItemCount > 0)
                    combo.SelectedIndex = 0;
                break;
            case NumericUpDown numeric when decimal.TryParse(value, out var parsed):
                numeric.Value = parsed;
                break;
            case TextBox text:
                text.Text = value;
                break;
        }

        if (field != null)
            field.FieldValue = value;
    }

    private void ShowStatus(string message)
    {
        _status.Text = message;
    }

    private void AddHeader(string text)
    {
        _host.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 15,
            FontWeight = global::Avalonia.Media.FontWeight.Bold,
            Margin = new global::Avalonia.Thickness(0, 12, 0, 4)
        });
    }

    private static Control Row(string label, Control editor)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("240,*"),
            Margin = new global::Avalonia.Thickness(0, 2, 0, 2),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var text = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = global::Avalonia.Media.TextWrapping.Wrap
        };

        Grid.SetColumn(text, 0);
        Grid.SetColumn(editor, 1);
        grid.Children.Add(text);
        grid.Children.Add(editor);
        return grid;
    }
}
