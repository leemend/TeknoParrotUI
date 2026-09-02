using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using TeknoParrotUi.Avalonia.Controls;
using TeknoParrotUi.Common;
using TeknoParrotUi.Common.Android;
using TeknoParrotUi.Common.Proton;

namespace TeknoParrotUi.Avalonia.Views;

public partial class GameSettingsView : UserControl
{
    private GameProfile? _profile;
    private readonly Dictionary<FieldInformation, Func<string>> _valueReaders = new();
    private readonly Dictionary<FieldInformation, string> _baseline = new();
    private string _baselinePath = "";
    private string _baselinePath2 = "";
    private TextBox? _gamePathBox;
    private TextBox? _gamePath2Box;
    private ComboBox? _wineRunnerCombo;
    private TextBox? _wineRunnerPathBox;
    private string _baselineWineRunner = "";
    private string _baselineWineRunnerPath = "";
    private const string WineRunnerNotInstalledSuffix = " (not installed)";
    private ComboBox? _prefixModeCombo;
    private TextBlock? _prefixInfoBlock;
    private TextBlock? _prefixExplainBlock;
    private string _baselinePrefixMode = "";
    private ComboBox? _fullscreenScalingCombo;
    private TextBlock? _fullscreenScalingInfoBlock;
    private string _baselineFullscreenScaling = "";
    private CheckBox? _androidDebugLoggingCheck;
    private bool _baselineAndroidDebugLogging;
    private ComboBox? _androidDisplayModeCombo;
    private TextBlock? _androidDisplayModeInfoBlock;
    private string _baselineAndroidDisplayMode = "";

    // Side-BETA Golden Tee / conditional-settings state.
    private GameProfile? _stockGoldenTeeProfile;
    private FieldInformation? _rodPreferredSetupAnchor;
    private bool _applyingRodPreferredSetup;
    private CheckBox? _rodPreferredSetupCheckBox;
    private Control? _rodPreferredSetupRow;
    private readonly Dictionary<FieldInformation, Control> _fieldEditors = new();
    private readonly Dictionary<FieldInformation, Control> _fieldRows = new();
    private bool _syncingRemoteLocalPlayInputApi;

    public event Action? BackRequested;
    public event Action<string>? Saved;

    public GameSettingsView()
    {
        InitializeComponent();
        if (OperatingSystem.IsAndroid())
        {
            FieldsPanel.MaxWidth = double.PositiveInfinity;
            FieldsPanel.HorizontalAlignment = HorizontalAlignment.Stretch;
            ActionsPanel.MaxWidth = double.PositiveInfinity;
            ActionsPanel.HorizontalAlignment = HorizontalAlignment.Stretch;
            foreach (var button in new[] { BtnBack, BtnSave })
            {
                button.Width = double.NaN;
                button.MinHeight = 48;
                button.HorizontalAlignment = HorizontalAlignment.Stretch;
            }
        }
        Localize();
        Services.Loc.LanguageChanged += Localize;
    }

    private void Localize()
    {
        BtnBack.Content = Services.Loc.T("Back", "Back");
        BtnSave.Content = Services.Loc.T("SettingsSaveSettings", "Save Settings");
    }

    public void LoadProfile(GameProfile profile)
    {
        _profile = profile;
        Header.Text = $"{profile.GameNameInternal ?? profile.ProfileName} - Settings";
        _valueReaders.Clear();
        _fieldEditors.Clear();
        _fieldRows.Clear();
        _rodPreferredSetupCheckBox = null;
        _rodPreferredSetupRow = null;
        FieldsPanel.Children.Clear();

        ConfigureRodPreferredSetup(profile);
        UpdateConditionalVisibilityModel();

        _gamePathBox = null;
        _gamePath2Box = null;
        if (OperatingSystem.IsAndroid() &&
            profile.EmulatorType is EmulatorType.pcsx2x6 or EmulatorType.Dolphin)
        {
            var isDolphin = profile.EmulatorType == EmulatorType.Dolphin;
            AddCategoryHeader(isDolphin ? "TeknoDolphin Arcade Image" :
                "PCSX2X6 Arcade Manifest");
            FieldsPanel.Children.Add(Row(
                isDolphin ? "Selected-storage game image" :
                    "App-owned game descriptor",
                new TextBox
                {
                    Text = isDolphin
                        ? profile.ExecutableName
                        : "/storage/emulated/0/Android/data/com.teknogods.tekno2x6/files/" +
                          "TeknoParrot/games/" + profile.ExecutableName,
                    IsReadOnly = true,
                    HorizontalAlignment = HorizontalAlignment.Stretch
                }));
            if (isDolphin)
            {
                var selectDolphinImage = new Button
                {
                    Content = "Select or Change Game Image (Internal / SD)",
                    MinHeight = OperatingSystem.IsAndroid() ? 48 : 0,
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };
                selectDolphinImage.Click += async (_, _) =>
                {
                    var top = TopLevel.GetTopLevel(this);
                    if (top is not Window owner)
                        return;
                    if (!Services.PlatformDolphinGameImport.IsAvailable)
                    {
                        await Services.Dialogs.InfoAsync(
                            owner,
                            "TeknoDolphin unavailable",
                            "Update TeknoParrotUI and TeknoDolphin before selecting a game image.");
                        return;
                    }

                    selectDolphinImage.IsEnabled = false;
                    try
                    {
                        var selected = await Services.PlatformDolphinGameImport.ImportAsync(
                            profile.ExecutableName ?? string.Empty);
                        if (selected)
                            await Services.PlatformGameCatalogSync.RefreshNowAsync();
                        var ready = selected &&
                            Services.PlatformGameCatalogSync.ReadyExecutables.Contains(
                                profile.ExecutableName ?? string.Empty,
                                StringComparer.OrdinalIgnoreCase);
                        await Services.Dialogs.InfoAsync(
                            owner,
                            ready ? "TeknoDolphin image ready" : "TeknoDolphin image unavailable",
                            ready
                                ? "TeknoDolphin retained read access and will play the image directly from the selected storage."
                                : "Selection was cancelled, rejected, or the storage provider did not grant persistent read access.");
                    }
                    catch (Exception error)
                    {
                        await Services.Dialogs.InfoAsync(
                            owner,
                            "TeknoDolphin image unavailable",
                            "TeknoDolphin could not select the game image: " + error.Message);
                    }
                    finally
                    {
                        selectDolphinImage.IsEnabled = true;
                    }
                };
                FieldsPanel.Children.Add(selectDolphinImage);
            }
        }
        else
        {
            AddCategoryHeader(Services.Loc.T(
                "GameSettingsGameHeader", "Game"));
            _gamePathBox = AddPathRow(
                BuildExecutableLabel(
                    "GameSettingsExecutableLabelShort",
                    "Executable",
                    profile.ExecutableName),
                profile.GamePath,
                profile.ExecutableName);
            if (profile.HasTwoExecutables)
                _gamePath2Box = AddPathRow(
                    BuildExecutableLabel(
                        profile.EmulatorType is EmulatorType.TeknoVegas or EmulatorType.TeknoViper
                            ? "GameSettingsTeknoVegasChdLabel"
                            : "GameSettingsSecondGameExecutableLabel",
                        profile.EmulatorType is EmulatorType.TeknoVegas or EmulatorType.TeknoViper
                            ? "Game CHD"
                            : "Second Game Executable",
                        profile.ExecutableName2),
                    profile.GamePath2,
                    profile.ExecutableName2);
        }

        _wineRunnerCombo = null;
        _wineRunnerPathBox = null;
        _prefixModeCombo = null;
        _fullscreenScalingCombo = null;
        _androidDebugLoggingCheck = null;
        _androidDisplayModeCombo = null;
        _androidDisplayModeInfoBlock = null;
        if (OperatingSystem.IsLinux())
        {
            AddWineRunnerSection(profile);
            AddWinePrefixModeSection(profile);
            AddFullscreenScalingSection(profile);
        }
        else if (OperatingSystem.IsAndroid())
        {
            AddAndroidDiagnosticsSection(profile);
        }

        foreach (var category in profile.ConfigValues.Select(c => c.CategoryName).Distinct())
        {
            var categoryFields = profile.ConfigValues.Where(c => c.CategoryName == category).ToList();

            if (string.Equals(category, "Customization", StringComparison.OrdinalIgnoreCase) &&
                categoryFields.Any(f => string.Equals(
                    f.FieldName, "Override Default Outfit", StringComparison.OrdinalIgnoreCase)))
            {
                AddCategoryHeader("Appearance & Equipment");

                AddExpandablePlayerAppearanceCategory(
                    "Player 1 Customization",
                    categoryFields,
                    includeRodPreferredSetup: true);
                continue;
            }

            if (TryGetAdditionalGoldenTeePlayerNumber(category, out var playerNumber))
            {
                AddExpandablePlayerAppearanceCategory(
                    $"Player {playerNumber} Customization",
                    categoryFields);
                continue;
            }

            AddCategoryHeader(category);
            foreach (var field in categoryFields)
                AddFieldEditor(field);
        }

        SyncRemoteLocalPlayInputApi();

        // Baseline for unsaved-change detection (editor values normalize e.g. "" -> "0",
        // so compare against the editors' initial output rather than raw FieldValues)
        _baseline.Clear();
        foreach (var (field, read) in _valueReaders)
            _baseline[field] = read() ?? "";
        _baselinePath = _gamePathBox?.Text ?? "";
        _baselinePath2 = _gamePath2Box?.Text ?? "";
        _baselineWineRunner = _wineRunnerCombo?.SelectedItem as string ?? "";
        _baselineWineRunnerPath = _wineRunnerPathBox?.Text ?? "";
        _baselinePrefixMode = _prefixModeCombo?.SelectedItem as string ?? "";
        _baselineFullscreenScaling = _fullscreenScalingCombo?.SelectedItem as string ?? "";
        _baselineAndroidDebugLogging = _androidDebugLoggingCheck?.IsChecked == true;
        _baselineAndroidDisplayMode = _androidDisplayModeCombo?.SelectedItem as string ?? "";
    }

    private void AddAndroidDiagnosticsSection(GameProfile profile)
    {
        if (!AndroidLaunchRecipeCatalog.TryGetValidated(
                profile.ProfileName ?? string.Empty, out var recipe, out _))
            return;

        AddCategoryHeader(Services.Loc.T(
            "GameSettingsAndroidDiagnosticsHeader", "Android Performance & Diagnostics"));
        var displayOptions = new List<string>
        {
            $"Use game default ({DisplayModeName(recipe.DisplayMode)})",
            "Fit screen (experimental)",
            "Windowed (centered native size)",
            "Exclusive fullscreen"
        };
        _androidDisplayModeCombo = new ComboBox
        {
            ItemsSource = displayOptions,
            SelectedIndex = profile.AndroidDisplayMode switch
            {
                AndroidDisplayMode.AspectFit => 1,
                AndroidDisplayMode.Centered => 2,
                AndroidDisplayMode.Fullscreen => 3,
                _ => 0
            },
            MinWidth = 280
        };
        FieldsPanel.Children.Add(Row(
            Services.Loc.T("GameSettingsAndroidDisplayModeLabel", "Game display"),
            _androidDisplayModeCombo));
        _androidDisplayModeInfoBlock = new TextBlock
        {
            TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
            FontSize = 11,
            Opacity = 0.78
        };
        FieldsPanel.Children.Add(_androidDisplayModeInfoBlock);
        _androidDisplayModeCombo.SelectionChanged += (_, _) =>
            UpdateAndroidDisplayModePreview(recipe.DisplayMode);
        UpdateAndroidDisplayModePreview(recipe.DisplayMode);

        _androidDebugLoggingCheck = new CheckBox
        {
            IsChecked = profile.AndroidDebugLogging ?? !recipe.PerformanceModeDefault,
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(
            _androidDebugLoggingCheck,
            Services.Loc.T(
                "GameSettingsAndroidDebugLoggingHint",
                "Enable only while troubleshooting. This records Wine, graphics, Box64, " +
                "Winlator guest-process, bridge, and arcade-protocol diagnostics."));
        FieldsPanel.Children.Add(Row(
            Services.Loc.T("GameSettingsAndroidDebugLoggingLabel", "Debug logging"),
            _androidDebugLoggingCheck));
        FieldsPanel.Children.Add(new TextBlock
        {
            Text = Services.Loc.T(
                "GameSettingsAndroidDebugLoggingExplain",
                "Off is performance mode. Enable this temporarily when collecting logs for support."),
            TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
            FontSize = 11,
            Opacity = 0.78
        });
    }

    private void UpdateAndroidDisplayModePreview(string recipeMode)
    {
        if (_androidDisplayModeCombo == null || _androidDisplayModeInfoBlock == null)
            return;
        var effective = _androidDisplayModeCombo.SelectedIndex switch
        {
            1 => AndroidLaunchRecipe.DisplayModeAspectFit,
            2 => AndroidLaunchRecipe.DisplayModeCentered,
            3 => AndroidLaunchRecipe.DisplayModeFullscreen,
            _ => recipeMode
        };
        _androidDisplayModeInfoBlock.Text = effective switch
        {
            AndroidLaunchRecipe.DisplayModeCentered =>
                "Recommended: keeps the game windowed at native size and avoids Winlator fullscreen transformations.",
            AndroidLaunchRecipe.DisplayModeFullscreen =>
                "Requests the game's own fullscreen mode. Some Wine games only work in windowed mode.",
            _ => "Experimental: Winlator scales the window to the display. Some Wine games terminate on this renderer path."
        };
    }

    private static string DisplayModeName(string value) => value switch
    {
        AndroidLaunchRecipe.DisplayModeCentered => "windowed / centered",
        AndroidLaunchRecipe.DisplayModeFullscreen => "exclusive fullscreen",
        _ => "experimental fit screen"
    };

    /// <summary>
    /// Per-game Gamescope fullscreen-scaling override - a compatibility
    /// fallback switch only (no game resolution fields anywhere). Backed by
    /// GameProfile.FullscreenScalingMode; shows the same policy/availability/
    /// display information GamescopeLauncher itself uses so a user can see
    /// exactly what would happen without launching the game.
    /// </summary>
    private void AddFullscreenScalingSection(GameProfile profile)
    {
        AddCategoryHeader(Services.Loc.T("GameSettingsFullscreenScalingHeader", "Fullscreen game scaling (Linux)"));

        var options = new List<string> { "Use global default", "Automatic fullscreen fit", "Disabled" };
        _fullscreenScalingCombo = new ComboBox
        {
            ItemsSource = options,
            SelectedIndex = profile.FullscreenScalingMode switch
            {
                LinuxFullscreenScalingMode.AutomaticFit => 1,
                LinuxFullscreenScalingMode.Disabled => 2,
                _ => 0
            },
            MinWidth = 220
        };
        FieldsPanel.Children.Add(Row(Services.Loc.T("GameSettingsFullscreenScalingLabel", "Fullscreen game scaling"), _fullscreenScalingCombo));

        _fullscreenScalingInfoBlock = new TextBlock { TextWrapping = global::Avalonia.Media.TextWrapping.Wrap, FontFamily = "monospace", FontSize = 11 };
        FieldsPanel.Children.Add(_fullscreenScalingInfoBlock);

        _fullscreenScalingCombo.SelectionChanged += (_, _) => UpdateFullscreenScalingPreview(profile);
        UpdateFullscreenScalingPreview(profile);
    }

    /// <summary>
    /// Resolves what the combo's CURRENT (possibly unsaved) selection would
    /// mean via the real GamescopeLaunchPolicy - never mutates
    /// <paramref name="profile"/>, so Cancel/Back still discards it.
    /// </summary>
    private void UpdateFullscreenScalingPreview(GameProfile profile)
    {
        if (_fullscreenScalingCombo == null || _fullscreenScalingInfoBlock == null)
            return;

        var gameMode = _fullscreenScalingCombo.SelectedIndex switch
        {
            1 => LinuxFullscreenScalingMode.AutomaticFit,
            2 => LinuxFullscreenScalingMode.Disabled,
            _ => LinuxFullscreenScalingMode.Default
        };
        var globalMode = Lazydata.ParrotData.FullscreenScalingMode ?? LinuxFullscreenScalingMode.Disabled;
        var isExternalEmulator = TeknoParrotUi.Common.GameLaunch.ExternalEmulatorLauncher.IsExternalEmulator(profile);
        var forced = GamescopeEnvironment.ForceGamescopeRequested;
        var noGamescope = GamescopeEnvironment.NoGamescopeRequested;

        var decision = GamescopeLaunchPolicy.Resolve(globalMode, gameMode, noGamescope, forced,
            isExternalEmulator, GamescopeEnvironment.IsAlreadyInsideGamescope(), GamescopeEnvironment.AllowNestedOverrideRequested);

        var display = LinuxDisplayResolver.Resolve();

        _fullscreenScalingInfoBlock.Text =
            $"Configured: {gameMode}    Global default: {globalMode}    Effective: {decision.EffectiveMode}\n" +
            $"External emulator profile: {isExternalEmulator}    Forced by environment: {decision.ForcedByEnvironment || noGamescope}\n" +
            $"Monitor resolution: {(display.IsValid ? $"{display.Width}x{display.Height} ({display.Source})" : "unresolved")}";
    }

    /// <summary>
    /// Per-game wine/Proton runner override (Linux only - ignored on Windows,
    /// where GameProfile.ProtonVersion/WineRunnerPath are simply never read).
    /// Backed directly by those two fields: the combo selects ProtonVersion
    /// (a packaged version name, "system" for plain system wine, or empty for
    /// the global default from the Linux Setup page); the path box, when
    /// non-empty, sets WineRunnerPath and takes priority over the combo.
    /// </summary>
    private void AddWineRunnerSection(GameProfile profile)
    {
        AddCategoryHeader(Services.Loc.T("GameSettingsWineRunnerHeader", "Wine Runner (Linux)"));

        var options = new List<string> { "Auto (default)", "System Wine" };
        options.AddRange(ProtonPackageManager.ListInstalledVersions());

        var current = profile.ProtonVersion;
        var isSystem = string.Equals(current, "system", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(current) && !isSystem && !options.Contains(current))
            options.Add(current + WineRunnerNotInstalledSuffix);

        _wineRunnerCombo = new ComboBox { ItemsSource = options, MinWidth = 260 };
        var selectedIndex = 0;
        if (isSystem)
            selectedIndex = 1;
        else if (!string.IsNullOrEmpty(current))
        {
            var idx = options.FindIndex(o => o == current || o == current + WineRunnerNotInstalledSuffix);
            if (idx >= 0)
                selectedIndex = idx;
        }
        _wineRunnerCombo.SelectedIndex = selectedIndex;
        FieldsPanel.Children.Add(Row(Services.Loc.T("GameSettingsWineRunnerLabel", "Proton/Wine version"), _wineRunnerCombo));

        _wineRunnerPathBox = new TextBox
        {
            Text = profile.WineRunnerPath ?? "",
            PlaceholderText = "Leave empty unless this game needs a specific wine binary",
            MinWidth = 400
        };
        var browseWine = new Button { Content = "Browse..." };
        browseWine.Click += async (_, _) =>
        {
            var top = TopLevel.GetTopLevel(this);
            if (top == null) return;
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select wine/Proton binary",
                AllowMultiple = false
            });
            if (files.Count > 0)
                _wineRunnerPathBox.Text = files[0].TryGetLocalPath() ?? _wineRunnerPathBox.Text;
        };
        FieldsPanel.Children.Add(Row(Services.Loc.T("GameSettingsWineRunnerPathLabel", "Custom binary (overrides version above)"), new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children = { _wineRunnerPathBox, browseWine }
        }));
    }

    /// <summary>
    /// Per-game Wine/Proton PREFIX (environment) override - shared vs isolated,
    /// independent of the runner BINARY chosen above. Backed by
    /// GameProfile.WinePrefixMode (nullable - see its docs for the legacy
    /// migration distinction). Shows a live preview of what the current combo
    /// selection would resolve to via WinePrefixManager, without saving.
    /// </summary>
    private void AddWinePrefixModeSection(GameProfile profile)
    {
        AddCategoryHeader(Services.Loc.T("GameSettingsWinePrefixModeHeader", "Wine Prefix (Environment)"));

        var options = new List<string> { "Use global default", "Shared prefix", "Isolated prefix" };
        _prefixModeCombo = new ComboBox
        {
            ItemsSource = options,
            SelectedIndex = profile.WinePrefixMode switch
            {
                WinePrefixMode.Shared => 1,
                WinePrefixMode.Isolated => 2,
                _ => 0
            },
            MinWidth = 220
        };
        FieldsPanel.Children.Add(Row(Services.Loc.T("GameSettingsWinePrefixModeLabel", "Wine prefix mode"), _prefixModeCombo));

        _prefixInfoBlock = new TextBlock { TextWrapping = global::Avalonia.Media.TextWrapping.Wrap, FontFamily = "monospace", FontSize = 11 };
        FieldsPanel.Children.Add(_prefixInfoBlock);

        _prefixExplainBlock = new TextBlock
        {
            TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
            Foreground = global::Avalonia.Media.Brushes.Gray,
            Margin = new global::Avalonia.Thickness(0, 2, 0, 8)
        };
        FieldsPanel.Children.Add(_prefixExplainBlock);

        var resetButton = new Button { Content = Services.Loc.T("GameSettingsResetIsolatedPrefix", "Reset This Game's Isolated Prefix") };
        resetButton.Click += async (_, _) => await ResetIsolatedPrefixAsync(profile);
        FieldsPanel.Children.Add(resetButton);

        _prefixModeCombo.SelectionChanged += (_, _) => UpdateWinePrefixPreview(profile);
        UpdateWinePrefixPreview(profile);
    }

    /// <summary>
    /// Resolves what the combo's CURRENT (possibly unsaved) selection would
    /// mean, via the profile-agnostic WinePrefixManager.Resolve overload - does
    /// NOT mutate <paramref name="profile"/>, so Cancel/Back still discards it.
    /// </summary>
    private void UpdateWinePrefixPreview(GameProfile profile)
    {
        if (_prefixModeCombo == null || _prefixInfoBlock == null || _prefixExplainBlock == null)
            return;

        WinePrefixMode? previewMode = _prefixModeCombo.SelectedIndex switch
        {
            1 => WinePrefixMode.Shared,
            2 => WinePrefixMode.Isolated,
            _ => WinePrefixMode.Default
        };

        var wine = ProtonLauncher.ResolveWineBinary(profile);
        var runnerKind = wine != null && ProtonLauncher.FindProtonScript(wine) != null
            ? WineRunnerKind.Proton
            : WineRunnerKind.PlainWine;
        var group = TeknoParrotUi.Common.GameLaunch.GameLaunchArguments.RequiresJapaneseLocale(profile)
            ? WinePrefixCompatibilityGroup.Japanese
            : WinePrefixCompatibilityGroup.Standard;

        var env = WinePrefixManager.Resolve(WinePrefixManager.ProfileIdentifier(profile), previewMode, group, runnerKind);

        var pathLines = runnerKind == WineRunnerKind.PlainWine
            ? $"WINEPREFIX: {env.WinePrefixPath}"
            : $"Compat-data path: {env.SteamCompatDataPath}\nActual Wine prefix: {env.ActualPrefixPath}";

        _prefixInfoBlock.Text =
            $"Configured: {env.ConfiguredMode}    Effective: {env.EffectiveMode}{(env.MigratedFromLegacyIsolated ? " (existing isolated prefix kept)" : "")}\n" +
            $"Runner: {runnerKind}    Compatibility group: {env.CompatibilityGroup}\n" +
            pathLines;

        _prefixExplainBlock.Text = env.EffectiveMode == WinePrefixMode.Isolated
            ? Services.Loc.T("GameSettingsPrefixIsolatedExplain",
                "A separate Wine environment will be created for this game. This may use approximately 1-2 GB of additional disk space.")
            : Services.Loc.T("GameSettingsPrefixSharedExplain",
                "This game will use the common TeknoParrot environment. Any existing isolated prefix will be preserved.");
    }

    private async System.Threading.Tasks.Task ResetIsolatedPrefixAsync(GameProfile profile)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
            return;

        var wine = ProtonLauncher.ResolveWineBinary(profile);
        var runnerKind = wine != null && ProtonLauncher.FindProtonScript(wine) != null
            ? WineRunnerKind.Proton
            : WineRunnerKind.PlainWine;

        var confirmed = await Services.Dialogs.ConfirmAsync(owner,
            Services.Loc.T("GameSettingsResetIsolatedPrefixTitle", "Reset Isolated Prefix"),
            Services.Loc.T("GameSettingsResetIsolatedPrefixConfirm",
                "This deletes this game's dedicated Wine environment (if one exists) and recreates it fresh. The shared prefix and other games are never affected. Continue?"));
        if (!confirmed)
            return;

        var result = await System.Threading.Tasks.Task.Run(() => WinePrefixManager.ResetIsolated(profile, runnerKind));
        await Services.Dialogs.InfoAsync(owner,
            Services.Loc.T("GameSettingsResetIsolatedPrefixTitle", "Reset Isolated Prefix"), result.Message);
    }

    private void AddCategoryHeader(string text)
    {
        FieldsPanel.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 15,
            FontWeight = global::Avalonia.Media.FontWeight.Bold,
            Margin = new global::Avalonia.Thickness(0, 12, 0, 4)
        });
    }

    private static bool TryGetAdditionalGoldenTeePlayerNumber(string? category, out int playerNumber)
    {
        playerNumber = 0;
        if (string.IsNullOrWhiteSpace(category))
            return false;

        return category switch
        {
            "Player 2 Customization" => (playerNumber = 2) == 2,
            "Player 3 Customization" => (playerNumber = 3) == 3,
            "Player 4 Customization" => (playerNumber = 4) == 4,
            _ => false
        };
    }

    private void AddExpandablePlayerAppearanceCategory(
        string header,
        IReadOnlyList<FieldInformation> fields,
        bool includeRodPreferredSetup = false)
    {
        var panel = new StackPanel
        {
            Spacing = 4,
            Margin = new global::Avalonia.Thickness(12, 4, 0, 4)
        };

        if (includeRodPreferredSetup)
        {
            var rodAnchor = fields.FirstOrDefault(field => field.ShowRodPreferredSetup);
            if (rodAnchor != null)
                AddRodPreferredSetupEditor(rodAnchor, panel);
        }

        foreach (var field in fields)
            AddFieldEditor(field, panel, includeRodPreferredSetupRow: false);

        FieldsPanel.Children.Add(new Expander
        {
            Header = header,
            IsExpanded = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new global::Avalonia.Thickness(0, 10, 0, 0),
            Content = panel
        });
    }

    private static string GetFieldDisplayName(FieldInformation field)
    {
        var name = field.FieldName;

        if (TryGetAdditionalGoldenTeePlayerNumber(field.CategoryName, out var playerNumber))
        {
            var prefix = $"P{playerNumber} ";
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                name = name.Substring(prefix.Length);
        }

        var isPlayerAppearanceField =
            string.Equals(field.CategoryName, "Customization", StringComparison.OrdinalIgnoreCase) ||
            TryGetAdditionalGoldenTeePlayerNumber(field.CategoryName, out _);

        if (!isPlayerAppearanceField)
            return name;

        if (string.Equals(name, "Override Default Outfit", StringComparison.OrdinalIgnoreCase))
            return "Edit Player Defaults";

        return name.StartsWith("Default ", StringComparison.OrdinalIgnoreCase)
            ? name.Substring("Default ".Length)
            : name;
    }

    /// <summary>
    /// "Game Executable (GameProject-Win64-Shipping.exe)" - shows the expected file
    /// name(s) from the profile, matching the classic UI (';'/'|' = alternatives).
    /// </summary>
    private static string BuildExecutableLabel(string key, string fallback, string? executableName)
    {
        var label = Services.Loc.T(key, fallback);
        if (string.IsNullOrEmpty(executableName))
            return label;
        var pretty = executableName.Replace("|", " or ").Replace(";", " or ");
        return $"{label} ({pretty})";
    }

    private TextBox AddPathRow(string label, string? value, string? executableName = null)
    {
        var box = new TextBox
        {
            Text = value ?? "",
            MinWidth = OperatingSystem.IsAndroid() ? 0 : 400,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var browse = new Button { Content = "Browse..." };
        browse.Click += async (_, _) =>
        {
            var top = TopLevel.GetTopLevel(this);
            if (top == null) return;

            var pickerTitle =
                $"{Services.Loc.T("GameSettingsSelectGameExecutable", "Select Game Executable")} - {label}";
            var pickerOptions = new FilePickerOpenOptions
            {
                Title = pickerTitle,
                AllowMultiple = false
            };

            if (!OperatingSystem.IsAndroid())
            {
                // Filter to the exact executable(s) the profile expects (classic behaviour);
                // profiles separate alternatives with '|' or ';'. Android deliberately has
                // no filter: SAF providers expose game files under inconsistent MIME types,
                // and omitting FileTypeFilter makes the platform request */*.
                var filters = new List<FilePickerFileType>();
                if (!string.IsNullOrEmpty(executableName))
                {
                    var names = executableName.Split('|', ';')
                        .Select(n => n.Trim())
                        .Where(n => n.Length > 0)
                        .ToArray();
                    if (names.Length > 0)
                        filters.Add(new FilePickerFileType($"{Services.Loc.T("GameSettingsGameExecutableFilter", "Game executable")} ({string.Join(", ", names)})")
                        {
                            Patterns = names
                        });
                }
                filters.Add(new FilePickerFileType(Services.Loc.T("GameSettingsAllFiles", "All Files"))
                {
                    Patterns = new[] { "*.*" }
                });
                pickerOptions.FileTypeFilter = filters;
            }

            string? selectedDocument = null;
            string? localPath = null;
            if (OperatingSystem.IsAndroid() &&
                Services.PlatformGameExecutablePicker.IsAvailable)
            {
                selectedDocument = await Services.PlatformGameExecutablePicker
                    .PickAsync(pickerTitle);
                if (selectedDocument == null)
                    return;
            }
            else
            {
                var files = await top.StorageProvider.OpenFilePickerAsync(pickerOptions);
                if (files.Count == 0)
                    return;
                selectedDocument = files[0].Path.ToString();
                localPath = files[0].TryGetLocalPath();
            }

            string? selectedPath;
            if (OperatingSystem.IsAndroid())
            {
                // Android storage providers may expose a transient cache path
                // through TryGetLocalPath(). Resolve the original SAF URI first
                // so selecting a shared game cannot be replaced by an unusable
                // private path and clear the field.
                if (!Services.PlatformDocumentPathResolver.TryResolve(
                        selectedDocument,
                        out selectedPath))
                {
                    selectedPath =
                        AndroidDocumentPathResolver.TryNormalizeSharedPath(
                            localPath ?? string.Empty,
                            out var normalizedLocalPath)
                            ? normalizedLocalPath
                            : null;
                }
            }
            else
                selectedPath = localPath;
            if (!string.IsNullOrWhiteSpace(selectedPath))
            {
                if (OperatingSystem.IsAndroid() &&
                    (_profile?.EmulatorType is EmulatorType.OpenParrot or EmulatorType.TeknoParrot) &&
                    !AndroidWinlatorGamePath.IsAllowedSharedPath(
                        selectedPath,
                        "/storage/emulated/0/Download"))
                {
                    if (top is Window owner)
                    {
                        await Services.Dialogs.InfoAsync(
                            owner,
                            "Choose a local game folder",
                            "Android did not expose this as a readable shared-storage path. " +
                            "Choose the executable inside a normal folder in Internal storage " +
                            "or on a readable SD card, not from cloud storage or protected " +
                            "Android/data. TeknoParrot exposes only the selected executable's " +
                            "containing folder to Winlator.");
                    }
                }
                else if (OperatingSystem.IsAndroid() &&
                         _profile?.EmulatorType == EmulatorType.RPCS3 &&
                         !AndroidRpcs3x6GamePath.IsConfigured(selectedPath))
                {
                    if (top is Window owner)
                    {
                        await Services.Dialogs.InfoAsync(
                            owner,
                            "Choose this game's EBOOT.BIN",
                            "Select the EBOOT.BIN inside this exact game layout: " +
                            "dev_hdd0/game/SCEEXE000/USRDIR/EBOOT.BIN.");
                    }
                }
                else
                {
                    box.Text = selectedPath;
                }
            }
            else if (OperatingSystem.IsAndroid() && top is Window owner)
            {
                await Services.Dialogs.InfoAsync(
                    owner,
                    Services.Loc.T(
                        "GameSettingsExecutablePathUnavailableTitle",
                        "Game executable path unavailable"),
                    Services.Loc.T(
                        "GameSettingsExecutablePathUnavailable",
                        "Android did not expose a usable shared-storage path for this file. " +
                        "Choose the executable from Internal storage or Downloads, not from " +
                        "a cloud, recent-files, or protected application provider."));
            }
        };
        var pathEditor = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 6,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        Grid.SetColumn(box, 0);
        Grid.SetColumn(browse, 1);
        pathEditor.Children.Add(box);
        pathEditor.Children.Add(browse);
        FieldsPanel.Children.Add(Row(label, pathEditor));
        return box;
    }

    private void AddFieldEditor(FieldInformation field, Panel? targetPanel = null, bool includeRodPreferredSetupRow = true)
    {
        targetPanel ??= FieldsPanel;
        Control editor;

        switch (field.FieldType)
        {
            case FieldType.Bool:
                {
                    var cb = new CheckBox { IsChecked = field.FieldValue == "1" };
                    _valueReaders[field] = () => cb.IsChecked == true ? "1" : "0";
                    cb.IsCheckedChanged += (_, _) =>
                    {
                        field.FieldValue = cb.IsChecked == true ? "1" : "0";
                        HandleConfigFieldValueChanged(field);
                    };
                    editor = cb;
                    break;
                }

            case FieldType.Dropdown:
            case FieldType.DropdownIndex:
                {
                    var options = field.FieldOptions ?? new List<string>();
                    var selected = field.FieldValue;
                    if (field.FieldName == "Input API")
                    {
                        var remoteLocalPlayOn = _profile?.ConfigValues?.Any(c =>
                            string.Equals(c.FieldName, "Remote Local Play", StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(c.FieldValue, "On", StringComparison.OrdinalIgnoreCase)) == true;

                        if (remoteLocalPlayOn)
                        {
                            options = new List<string> { "MergedInput" };
                            selected = "MergedInput";
                            field.FieldValue = "MergedInput";
                        }
                        else
                        {
                            options = options.FindAll(o => o is "RawInput" or "RawInputTrackball");

                            if (options.Count == 0)
                                return;

                            if (!options.Contains(selected ?? ""))
                                selected = options.Contains("RawInputTrackball")
                                    ? "RawInputTrackball"
                                    : options[0];

                            field.FieldValue = selected;
                        }
                    }

                    var combo = new ComboBox
                    {
                        ItemsSource = options,
                        SelectedItem = selected,
                        MinWidth = 220
                    };
                    if (combo.SelectedItem == null && options.Count > 0)
                        combo.SelectedIndex = 0;

                    _valueReaders[field] = () => combo.SelectedItem as string ?? field.FieldValue ?? string.Empty;
                    combo.SelectionChanged += (_, _) =>
                    {
                        field.FieldValue = combo.SelectedItem as string ?? field.FieldValue ?? string.Empty;
                        HandleConfigFieldValueChanged(field);
                    };
                    editor = combo;
                    break;
                }

            case FieldType.Slider:
                {
                    var slider = new Slider
                    {
                        Minimum = field.FieldMin,
                        Maximum = field.FieldMax,
                        Width = 220,
                        Value = double.TryParse(field.FieldValue, out var v) ? v : field.FieldMin
                    };
                    if (field.FieldStep > 0)
                    {
                        slider.TickFrequency = field.FieldStep;
                        slider.IsSnapToTickEnabled = true;
                    }

                    var valueLabel = new TextBlock
                    {
                        VerticalAlignment = VerticalAlignment.Center,
                        Text = field.FieldValue
                    };

                    slider.PropertyChanged += (_, e) =>
                    {
                        if (e.Property != Slider.ValueProperty)
                            return;

                        var value = ((int)slider.Value).ToString();
                        valueLabel.Text = value;
                        field.FieldValue = value;
                        HandleConfigFieldValueChanged(field);
                    };

                    _valueReaders[field] = () => ((int)slider.Value).ToString();
                    editor = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        Children = { slider, valueLabel }
                    };
                    break;
                }

            case FieldType.Numeric:
                {
                    var num = new NumericUpDown
                    {
                        Minimum = field.FieldMin,
                        Maximum = field.FieldMax == 0 ? decimal.MaxValue : field.FieldMax,
                        Value = decimal.TryParse(field.FieldValue, out var nv) ? nv : 0,
                        MinWidth = 140
                    };
                    _valueReaders[field] = () => ((long)(num.Value ?? 0)).ToString();
                    num.ValueChanged += (_, _) =>
                    {
                        field.FieldValue = ((long)(num.Value ?? 0)).ToString();
                        HandleConfigFieldValueChanged(field);
                    };
                    editor = num;
                    break;
                }

            case FieldType.KeyCapture:
                {
                    var keyBox = new KeyCaptureBox
                    {
                        MinWidth = 220,
                        HorizontalAlignment = HorizontalAlignment.Left
                    };
                    keyBox.HexValue = field.FieldValue ?? "0x0";
                    _valueReaders[field] = () => keyBox.HexValue;
                    editor = keyBox;
                    break;
                }

            case FieldType.MonitorSelection:
                {
                    var monitorCombo = new ComboBox { MinWidth = 220 };
                    var screens = (TopLevel.GetTopLevel(this) as Window)?.Screens.All;
                    var items = new List<string>();
                    if (screens != null)
                    {
                        for (int i = 0; i < screens.Count; i++)
                            items.Add($"Monitor {i + 1} ({screens[i].Bounds.Width}x{screens[i].Bounds.Height}{(screens[i].IsPrimary ? ", primary" : "")})");
                    }

                    if (items.Count == 0)
                        items.Add("Monitor 1");

                    monitorCombo.ItemsSource = items;
                    monitorCombo.SelectedIndex =
                        int.TryParse(field.FieldValue, out var mi) && mi >= 0 && mi < items.Count ? mi : 0;
                    _valueReaders[field] = () => monitorCombo.SelectedIndex.ToString();
                    monitorCombo.SelectionChanged += (_, _) =>
                    {
                        field.FieldValue = monitorCombo.SelectedIndex.ToString();
                        HandleConfigFieldValueChanged(field);
                    };
                    editor = monitorCombo;
                    break;
                }

            case FieldType.Password:
                {
                    // Avalonia has no separate WPF-style PasswordBox binding helper here.
                    // Keep the value editable while masking it visually.
                    var password = new TextBox
                    {
                        Text = field.FieldValue ?? "",
                        MinWidth = 220,
                        PasswordChar = '?'
                    };
                    _valueReaders[field] = () => password.Text ?? "";
                    password.TextChanged += (_, _) =>
                    {
                        field.FieldValue = password.Text ?? "";
                        HandleConfigFieldValueChanged(field);
                    };
                    editor = password;
                    break;
                }

            default:
                {
                    var tb = new TextBox { Text = field.FieldValue ?? "", MinWidth = 220 };
                    _valueReaders[field] = () => tb.Text ?? "";
                    tb.TextChanged += (_, _) =>
                    {
                        field.FieldValue = tb.Text ?? "";
                        HandleConfigFieldValueChanged(field);
                    };
                    editor = tb;
                    break;
                }
        }

        if (!string.IsNullOrWhiteSpace(field.Hint))
            ToolTip.SetTip(editor, field.Hint);

        _fieldEditors[field] = editor;

        if (field.ShowRodPreferredSetup && includeRodPreferredSetupRow)
            AddRodPreferredSetupEditor(field, targetPanel);

        var row = Row(GetFieldDisplayName(field), editor);
        _fieldRows[field] = row;
        targetPanel.Children.Add(row);

        ApplyConditionalVisibilityToControl(field);
    }

    private void AddRodPreferredSetupEditor(FieldInformation field, Panel targetPanel)
    {
        var rodCheck = new CheckBox
        {
            IsChecked = field.RodPreferredSetup,
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(
            rodCheck,
            "In Memory of Rod\n\nUses the original Golden Tee defaults for outfit, clubs, putter, balls, " +
            "and accessories. Trackball sensitivity is locked to 25/25 while enabled.");

        rodCheck.IsCheckedChanged += async (_, _) =>
        {
            if (_applyingRodPreferredSetup)
                return;

            if (rodCheck.IsChecked == true)
            {
                if (HasCustomGoldenTeeSettings() &&
                    TopLevel.GetTopLevel(this) is Window owner)
                {
                    var confirmed = await Services.Dialogs.ConfirmAsync(
                        owner,
                        "Use Rod's Preferred Setup?",
                        "You currently have custom Golden Tee settings.\n\n" +
                        "Using Rod's Preferred Setup will reset those custom settings back to their default values.\n\n" +
                        "Do you want to continue?");

                    if (!confirmed)
                    {
                        SetRodToggleState(false, "0");
                        rodCheck.IsChecked = false;
                        return;
                    }
                }

                ApplyRodPreferredSetup();
                SyncEditorsFromFields();
            }
            else
            {
                SetRodToggleState(false, "0");
                UpdateConditionalVisibilityModel();
                ApplyConditionalVisibilityToControls();
            }
        };

        _rodPreferredSetupCheckBox = rodCheck;
        _rodPreferredSetupRow = Row("Use Rod's Preferred Setup", rodCheck);
        targetPanel.Children.Add(_rodPreferredSetupRow);
    }

    private void HandleConfigFieldValueChanged(FieldInformation field)
    {
        if (_applyingRodPreferredSetup || _syncingRemoteLocalPlayInputApi)
            return;

        // Custom Golden Tee outfit and Rod mode are mutually exclusive.
        if (ReferenceEquals(field, _rodPreferredSetupAnchor) &&
            string.Equals(field.FieldValue, "1", StringComparison.OrdinalIgnoreCase))
        {
            SetRodToggleState(false, "0");
            if (_rodPreferredSetupCheckBox != null)
                _rodPreferredSetupCheckBox.IsChecked = false;
        }

        if (string.Equals(field.FieldName, "Remote Local Play", StringComparison.OrdinalIgnoreCase))
            SyncRemoteLocalPlayInputApi();

        UpdateConditionalVisibilityModel();
        ApplyConditionalVisibilityToControls();
    }

    private void SyncRemoteLocalPlayInputApi()
    {
        if (_syncingRemoteLocalPlayInputApi || _profile?.ConfigValues == null)
            return;

        var remoteField = _profile.ConfigValues.FirstOrDefault(c =>
            string.Equals(c.FieldName, "Remote Local Play", StringComparison.OrdinalIgnoreCase));
        var inputApiField = _profile.ConfigValues.FirstOrDefault(c =>
            string.Equals(c.FieldName, "Input API", StringComparison.OrdinalIgnoreCase));

        if (remoteField == null || inputApiField == null ||
            !_fieldEditors.TryGetValue(inputApiField, out var editor) ||
            editor is not ComboBox combo)
            return;

        var remoteOn = string.Equals(remoteField.FieldValue, "On", StringComparison.OrdinalIgnoreCase);

        _syncingRemoteLocalPlayInputApi = true;
        try
        {
            if (remoteOn)
            {
                inputApiField.FieldValue = "MergedInput";
                combo.ItemsSource = new List<string> { "MergedInput" };
                combo.SelectedItem = "MergedInput";
                inputApiField.IsEditorEnabled = false;
                combo.IsEnabled = false;
            }
            else
            {
                var localOptions = (inputApiField.FieldOptions ?? new List<string>())
                    .Where(o => o is "RawInput" or "RawInputTrackball")
                    .ToList();

                if (localOptions.Count == 0)
                    localOptions.AddRange(new[] { "RawInput", "RawInputTrackball" });

                combo.ItemsSource = localOptions;

                if (string.Equals(inputApiField.FieldValue, "MergedInput", StringComparison.OrdinalIgnoreCase) ||
                    !localOptions.Contains(inputApiField.FieldValue ?? ""))
                {
                    inputApiField.FieldValue = localOptions.Contains("RawInputTrackball")
                        ? "RawInputTrackball"
                        : localOptions[0];
                }

                combo.SelectedItem = inputApiField.FieldValue;
                inputApiField.IsEditorEnabled = true;
                combo.IsEnabled = true;
            }
        }
        finally
        {
            _syncingRemoteLocalPlayInputApi = false;
        }
    }

    private void ConfigureRodPreferredSetup(GameProfile profile)
    {
        _stockGoldenTeeProfile = null;
        _rodPreferredSetupAnchor = null;

        if (profile.ConfigValues == null || !IsGoldenTeeProfile(profile))
            return;

        _rodPreferredSetupAnchor = profile.ConfigValues.FirstOrDefault(field =>
            string.Equals(field.CategoryName, "Customization", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(field.FieldName, "Override Default Outfit", StringComparison.OrdinalIgnoreCase));

        if (_rodPreferredSetupAnchor == null)
            return;

        _stockGoldenTeeProfile = LoadStockGoldenTeeProfile(profile);
        if (_stockGoldenTeeProfile == null)
            return;

        foreach (var field in profile.ConfigValues)
        {
            field.ShowRodPreferredSetup = false;
            field.IsEditorVisible = true;
            field.IsEditorEnabled = true;
        }

        _rodPreferredSetupAnchor.ShowRodPreferredSetup = true;

        if (string.IsNullOrWhiteSpace(_rodPreferredSetupAnchor.RodPreferredSetupSaved))
        {
            _rodPreferredSetupAnchor.RodPreferredSetupSaved = "0";
            _rodPreferredSetupAnchor.RodPreferredSetup = false;
            return;
        }

        _rodPreferredSetupAnchor.RodPreferredSetup =
            string.Equals(_rodPreferredSetupAnchor.RodPreferredSetupSaved, "1", StringComparison.Ordinal);

        if (_rodPreferredSetupAnchor.RodPreferredSetup)
            ApplyRodPreferredSetup();
    }

    private static bool IsGoldenTeeProfile(GameProfile profile)
    {
        var fileName = Path.GetFileName(profile.FileName ?? string.Empty);
        return fileName.StartsWith("GoldenTeeLive20", StringComparison.OrdinalIgnoreCase) &&
               fileName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase);
    }

    private static GameProfile? LoadStockGoldenTeeProfile(GameProfile currentProfile)
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
        catch (Exception ex)
        {
            Debug.WriteLine($"Could not load stock Golden Tee profile for Rod preferred setup: {ex.Message}");
            return null;
        }
    }

    private void ApplyRodPreferredSetup()
    {
        if (_profile?.ConfigValues == null || _rodPreferredSetupAnchor == null)
            return;

        try
        {
            _applyingRodPreferredSetup = true;

            if (_stockGoldenTeeProfile?.ConfigValues != null)
            {
                foreach (var stockField in _stockGoldenTeeProfile.ConfigValues.Where(field =>
                             string.Equals(field.CategoryName, "Customization", StringComparison.OrdinalIgnoreCase)))
                {
                    var currentField = _profile.ConfigValues.FirstOrDefault(field =>
                        string.Equals(field.CategoryName, stockField.CategoryName, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(field.FieldName, stockField.FieldName, StringComparison.OrdinalIgnoreCase));

                    if (currentField != null)
                        currentField.FieldValue = stockField.FieldValue;
                }
            }

            // Rod and normal outfit customization are mutually exclusive.
            _rodPreferredSetupAnchor.FieldValue = "0";

            SetFieldValue("Trackball Sensitivity X", "25");
            SetFieldValue("Trackball Sensitivity Y", "25");

            _rodPreferredSetupAnchor.RodPreferredSetupSaved = "1";
            _rodPreferredSetupAnchor.RodPreferredSetup = true;

            if (_rodPreferredSetupCheckBox != null)
                _rodPreferredSetupCheckBox.IsChecked = true;
        }
        finally
        {
            _applyingRodPreferredSetup = false;
        }

        UpdateConditionalVisibilityModel();
        ApplyConditionalVisibilityToControls();
    }

    private void SetRodToggleState(bool enabled, string savedValue)
    {
        if (_rodPreferredSetupAnchor == null)
            return;

        try
        {
            _applyingRodPreferredSetup = true;
            _rodPreferredSetupAnchor.RodPreferredSetupSaved = savedValue;
            _rodPreferredSetupAnchor.RodPreferredSetup = enabled;
        }
        finally
        {
            _applyingRodPreferredSetup = false;
        }
    }

    private void SetFieldValue(string fieldName, string value)
    {
        var field = _profile?.ConfigValues?.FirstOrDefault(item =>
            string.Equals(item.FieldName?.Trim(), fieldName, StringComparison.OrdinalIgnoreCase));
        if (field != null)
            field.FieldValue = value;
    }

    private bool HasCustomGoldenTeeSettings()
    {
        if (_profile?.ConfigValues == null || _stockGoldenTeeProfile?.ConfigValues == null)
            return false;

        foreach (var stockField in _stockGoldenTeeProfile.ConfigValues.Where(field =>
                     string.Equals(field.CategoryName, "Customization", StringComparison.OrdinalIgnoreCase)))
        {
            var currentField = _profile.ConfigValues.FirstOrDefault(field =>
                string.Equals(field.CategoryName, stockField.CategoryName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(field.FieldName, stockField.FieldName, StringComparison.OrdinalIgnoreCase));

            if (currentField != null &&
                !string.Equals(currentField.FieldValue, stockField.FieldValue, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool IsRodTrackballSensitivityField(FieldInformation field) =>
        string.Equals(field.FieldName, "Trackball Sensitivity X", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(field.FieldName, "Trackball Sensitivity Y", StringComparison.OrdinalIgnoreCase);

    private void UpdateConditionalVisibilityModel()
    {
        if (_profile?.ConfigValues == null)
            return;

        var useRodPreferredSetup = _rodPreferredSetupAnchor?.RodPreferredSetup == true;

        foreach (var field in _profile.ConfigValues)
        {
            field.IsEditorVisible = true;
            field.IsEditorEnabled = true;

            if (string.IsNullOrEmpty(field.VisibleWhenField))
            {
                field.IsVisible = true;
            }
            else
            {
                var controller = _profile.ConfigValues.FirstOrDefault(f =>
                    string.Equals(f.FieldName, field.VisibleWhenField, StringComparison.OrdinalIgnoreCase));

                if (controller == null)
                {
                    field.IsVisible = true;
                }
                else
                {
                    var acceptedValues = (field.VisibleWhenValue ?? string.Empty)
                        .Split(',')
                        .Select(v => v.Trim());

                    field.IsVisible = acceptedValues.Any(v =>
                        string.Equals(controller.FieldValue, v, StringComparison.OrdinalIgnoreCase));
                }
            }

            if (useRodPreferredSetup && IsRodTrackballSensitivityField(field))
            {
                field.FieldValue = "25";
                field.IsEditorEnabled = false;
            }

            // Player 1 mirrors the additional-player UI: only the Rod toggle and
            // Edit Player Defaults are shown until customization is enabled.
            if (!useRodPreferredSetup &&
                _rodPreferredSetupAnchor != null &&
                string.Equals(field.CategoryName, "Customization", StringComparison.OrdinalIgnoreCase) &&
                !ReferenceEquals(field, _rodPreferredSetupAnchor) &&
                !string.Equals(_rodPreferredSetupAnchor.FieldValue, "1", StringComparison.OrdinalIgnoreCase))
            {
                field.IsVisible = false;
            }

            if (!useRodPreferredSetup ||
                !string.Equals(field.CategoryName, "Customization", StringComparison.OrdinalIgnoreCase))
                continue;

            if (ReferenceEquals(field, _rodPreferredSetupAnchor))
            {
                field.IsVisible = true;
                field.IsEditorVisible = false;
            }
            else
            {
                field.IsVisible = false;
            }
        }
    }

    private void ApplyConditionalVisibilityToControls()
    {
        if (_profile?.ConfigValues == null)
            return;

        foreach (var field in _profile.ConfigValues)
            ApplyConditionalVisibilityToControl(field);

        if (_rodPreferredSetupRow != null)
            _rodPreferredSetupRow.IsVisible = _rodPreferredSetupAnchor?.ShowRodPreferredSetup == true;
    }

    private void ApplyConditionalVisibilityToControl(FieldInformation field)
    {
        if (_fieldRows.TryGetValue(field, out var row))
            row.IsVisible = field.IsVisible && field.IsEditorVisible;

        if (_fieldEditors.TryGetValue(field, out var editor))
            editor.IsEnabled = field.IsEditorEnabled;
    }

    private void SyncEditorsFromFields()
    {
        // Rebuilding the page is the safest way to synchronize heterogeneous dynamic
        // editors after Rod mode resets several fields at once.
        if (_profile != null)
            LoadProfile(_profile);
    }

    private static Control Row(string label, Control editor)
    {
        var compact = OperatingSystem.IsAndroid();
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions(compact ? "*" : "240,*"),
            RowDefinitions = new RowDefinitions(compact ? "Auto,Auto" : "Auto"),
            Margin = new global::Avalonia.Thickness(0, 2, 0, 2)
        };
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = global::Avalonia.Media.TextWrapping.Wrap };
        Grid.SetColumn(text, 0);
        if (compact)
        {
            Grid.SetRow(editor, 1);
            editor.Margin = new global::Avalonia.Thickness(0, 3, 0, 0);
        }
        else
        {
            Grid.SetColumn(editor, 1);
        }
        grid.Children.Add(text);
        grid.Children.Add(editor);
        return grid;
    }

    private void BtnBack_Click(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e) => HandleBack();

    private async void HandleBack()
    {
        // Don't silently discard changes (e.g. a switched Input API) - losing an
        // unsaved API change makes freshly-bound controls dead in-game.
        if (HasUnsavedChanges() && TopLevel.GetTopLevel(this) is Window owner)
        {
            var result = await Services.Dialogs.ConfirmCancelAsync(owner,
                Services.Loc.T("UnsavedChanges", "Unsaved Changes"),
                Services.Loc.T("GameSettingsUnsavedPrompt", "You have unsaved settings changes. Save them before leaving?"));
            if (result == null)
                return; // cancel: stay on the settings page
            if (result == true)
            {
                SaveProfile();
                return; // SaveProfile already navigates back
            }
        }
        BackRequested?.Invoke();
    }

    private bool HasUnsavedChanges()
    {
        if (_profile == null)
            return false;
        if (_gamePathBox != null && (_gamePathBox.Text ?? "") != _baselinePath)
            return true;
        if (_gamePath2Box != null && (_gamePath2Box.Text ?? "") != _baselinePath2)
            return true;
        if (_wineRunnerCombo != null && (_wineRunnerCombo.SelectedItem as string ?? "") != _baselineWineRunner)
            return true;
        if (_wineRunnerPathBox != null && (_wineRunnerPathBox.Text ?? "") != _baselineWineRunnerPath)
            return true;
        if (_prefixModeCombo != null && (_prefixModeCombo.SelectedItem as string ?? "") != _baselinePrefixMode)
            return true;
        if (_fullscreenScalingCombo != null && (_fullscreenScalingCombo.SelectedItem as string ?? "") != _baselineFullscreenScaling)
            return true;
        if (_androidDebugLoggingCheck != null &&
            (_androidDebugLoggingCheck.IsChecked == true) != _baselineAndroidDebugLogging)
            return true;
        if (_androidDisplayModeCombo != null &&
            (_androidDisplayModeCombo.SelectedItem as string ?? "") != _baselineAndroidDisplayMode)
            return true;
        foreach (var (field, read) in _valueReaders)
        {
            if (_baseline.TryGetValue(field, out var original) && (read() ?? "") != original)
                return true;
        }
        return false;
    }

    private void BtnSave_Click(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e) => SaveProfile();

    private void SaveProfile()
    {
        if (_profile == null) return;

        // Rod mode is an invariant: save stock customization and 25/25 sensitivity.
        if (_rodPreferredSetupAnchor?.RodPreferredSetup == true)
            ApplyRodPreferredSetup();

        _profile.GamePath = _gamePathBox?.Text ?? _profile.GamePath;
        if (_gamePath2Box != null)
            _profile.GamePath2 = _gamePath2Box.Text ?? _profile.GamePath2;

        if (_wineRunnerCombo != null)
        {
            var selected = _wineRunnerCombo.SelectedItem as string;
            _profile.ProtonVersion = selected switch
            {
                null or "Auto (default)" => null,
                "System Wine" => "system",
                _ when selected.EndsWith(WineRunnerNotInstalledSuffix, StringComparison.Ordinal)
                    => selected[..^WineRunnerNotInstalledSuffix.Length],
                _ => selected
            };
        }
        if (_wineRunnerPathBox != null)
            _profile.WineRunnerPath = string.IsNullOrWhiteSpace(_wineRunnerPathBox.Text) ? null : _wineRunnerPathBox.Text.Trim();

        if (_prefixModeCombo != null)
        {
            _profile.WinePrefixMode = _prefixModeCombo.SelectedIndex switch
            {
                1 => WinePrefixMode.Shared,
                2 => WinePrefixMode.Isolated,
                _ => WinePrefixMode.Default
            };
        }

        if (_fullscreenScalingCombo != null)
        {
            _profile.FullscreenScalingMode = _fullscreenScalingCombo.SelectedIndex switch
            {
                1 => LinuxFullscreenScalingMode.AutomaticFit,
                2 => LinuxFullscreenScalingMode.Disabled,
                _ => LinuxFullscreenScalingMode.Default
            };
        }

        if (_androidDebugLoggingCheck != null)
        {
            var selectedDebugLogging = _androidDebugLoggingCheck.IsChecked == true;
            // Saving an unrelated setting must not turn an inherited recipe
            // default into a permanent per-game override. Preserve null until
            // the player actually changes this switch; retain an existing
            // explicit override even when its current value is unchanged.
            if (_profile.AndroidDebugLogging.HasValue ||
                selectedDebugLogging != _baselineAndroidDebugLogging)
                _profile.AndroidDebugLogging = selectedDebugLogging;
        }
        if (_androidDisplayModeCombo != null)
        {
            _profile.AndroidDisplayMode = _androidDisplayModeCombo.SelectedIndex switch
            {
                1 => AndroidDisplayMode.AspectFit,
                2 => AndroidDisplayMode.Centered,
                3 => AndroidDisplayMode.Fullscreen,
                _ => null
            };
        }

        foreach (var (field, read) in _valueReaders)
            field.FieldValue = read();

        Directory.CreateDirectory("UserProfiles");
        JoystickHelper.SerializeGameProfile(_profile);
        Saved?.Invoke(_profile.GameNameInternal ?? _profile.ProfileName ?? "profile");
        BackRequested?.Invoke();
    }
}


