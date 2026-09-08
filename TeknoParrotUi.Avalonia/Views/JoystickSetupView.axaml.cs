using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using TeknoParrotUi.Avalonia.Services;
using TeknoParrotUi.Common;
using TeknoParrotUi.Common.InputListening;

namespace TeknoParrotUi.Avalonia.Views;

public partial class JoystickSetupView : UserControl
{
    private GameProfile? _profile;
    private InputApi _api = InputApi.MergedInput;
    private bool _mergedIncludesRawInput;
    private bool _mergedIncludesRawInputTrackball;

    // Conditional row visibility (classic JoystickControl rules).
    private bool _isKeyboardOrButtonAxis;
    private bool _relativeAxis;
    private bool _bg4ProMode;
    private bool _useDPadForGun1Stick;
    private bool _useDPadForGun2Stick;
    private bool _useAnalogAxisToAimGun1;
    private bool _useAnalogAxisToAimGun2;
    private bool _isRemoteLocalPlayMode;

    private readonly List<(ComboBox Combo, JoystickButtons Binding)> _deviceCombos = new();
    private bool _refreshingDeviceLists;

    private readonly InputCaptureService _capture = new();
    private readonly RawInputCaptureService _rawCapture = new();
    private Button? _armedButton;
    private JoystickButtons? _armedBinding;

    public event Action? BackRequested;
    public event Action<string>? Saved;

    public JoystickSetupView()
    {
        InitializeComponent();
        Localize();
        Services.Loc.LanguageChanged += Localize;

        _capture.BindingCaptured += captured =>
            Dispatcher.UIThread.Post(() => OnCaptured(captured));

        _rawCapture.BindingCaptured += (name, button, isEscape) =>
            Dispatcher.UIThread.Post(() => OnRawCaptured(name, button, isEscape));

        SunshinePlayerInput.InputReceived += OnSunshineInputReceived;
        SunshinePlayerInput.Start();

        Unloaded += (_, _) =>
        {
            SunshinePlayerInput.InputReceived -= OnSunshineInputReceived;
            SunshinePlayerInput.Stop();
            ActiveCaptureSource.AllowedPlayer = ActiveCaptureSource.Any;
            StopCapture();
        };
    }

    private void StopCapture()
    {
        _capture.Stop();
        _rawCapture.Stop();
    }

    private void Localize()
    {
        BtnBack.Content = Services.Loc.T("Back", "Back");
        BtnSave.Content = Services.Loc.T("SettingsSaveSettings", "Save Bindings");
    }

    public void LoadProfile(GameProfile profile)
    {
        _profile = profile;
        _armedButton = null;
        _armedBinding = null;

        var apiField = profile.ConfigValues.FirstOrDefault(c => c.FieldName == "Input API");
        var savedValue = apiField?.FieldValue;

        // Input capture is always merged. For games which expose RawInput and
        // RawInputTrackball, the saved value still tells us which RawInput-family
        // rows constitute the normal local control layout.
        _api = InputApi.MergedInput;
        _mergedIncludesRawInput =
            apiField?.FieldOptions?.Contains("RawInput") == true || profile.GunGame;

        _mergedIncludesRawInputTrackball =
            apiField?.FieldOptions?.Contains("RawInputTrackball") == true &&
            (savedValue == "RawInputTrackball" ||
             apiField?.FieldOptions?.Contains("RawInput") != true);

        _isKeyboardOrButtonAxis =
            profile.ConfigValues.Any(c =>
                c.FieldName == "Use Keyboard/Button For Axis" &&
                c.FieldValue == "1");

        _relativeAxis =
            profile.ConfigValues.Any(c =>
                c.FieldName == "Use Relative Input" &&
                c.FieldValue == "1");

        _bg4ProMode =
            profile.ConfigValues.Any(c =>
                c.FieldName == "Professional Edition Enable" &&
                c.FieldValue == "1");

        _useDPadForGun1Stick =
            profile.ConfigValues.FirstOrDefault(c =>
                c.FieldName == "GUN1StickAxisInputStyle")?.FieldValue == "UseDPadForGUN1Stick" ||
            profile.ConfigValues.FirstOrDefault(c =>
                c.FieldName == "Left Stick Button Mode")?.FieldValue == "1";

        _useDPadForGun2Stick =
            profile.ConfigValues.FirstOrDefault(c =>
                c.FieldName == "GUN2StickAxisInputStyle")?.FieldValue == "UseDPadForGUN2Stick" ||
            profile.ConfigValues.FirstOrDefault(c =>
                c.FieldName == "Right Stick Button Mode")?.FieldValue == "1";

        _useAnalogAxisToAimGun1 =
            profile.ConfigValues.FirstOrDefault(c =>
                c.FieldName == "GUN1AimingInputStyle")?.FieldValue == "UseAnalogAxisToAim";

        _useAnalogAxisToAimGun2 =
            profile.ConfigValues.FirstOrDefault(c =>
                c.FieldName == "GUN2AimingInputStyle")?.FieldValue == "UseAnalogAxisToAim";

        _isRemoteLocalPlayMode =
            profile.ConfigValues.Any(c =>
                c.FieldName == "Remote Local Play" &&
                c.FieldValue != "Off");

        // Golden Tee local/RawInputTrackball mode is one shared cabinet input set.
        // There is no player/source selection locally: whoever is taking the turn
        // uses the same P1/cabinet controls. The source selector exists only when
        // Remote Local Play is active and MergedInput is being used.
        ActiveCaptureSourceRow.IsVisible = _isRemoteLocalPlayMode;

        if (_isRemoteLocalPlayMode)
        {
            PopulateActiveCaptureSourceSelector();
        }
        else
        {
            ActiveCaptureSource.AllowedPlayer = ActiveCaptureSource.Any;
            ActiveCaptureSourceSelector.ItemsSource = null;
            ActiveCaptureSourceSelector.SelectedItem = null;
        }

        if (IsGoldenTeeRemoteLocalPlay() &&
            ActiveCaptureSource.AllowedPlayer == ActiveCaptureSource.Any)
        {
            ActiveCaptureSource.AllowedPlayer = ActiveCaptureSource.Host;
            PopulateActiveCaptureSourceSelector();
        }

        Header.Text = $"{profile.GameNameInternal ?? profile.ProfileName} - Controls";
        ApiText.Text =
            "Click a binding, then press a controller button/axis, keyboard key or mouse button. Escape cancels." +
            (_mergedIncludesRawInput || _mergedIncludesRawInputTrackball
                ? " Lightgun/trackball devices are picked from the dropdown."
                : "");

        var accessWarnings = _rawCapture.GetAccessWarnings();
        if (accessWarnings.Count > 0)
            ApiText.Text = "⚠ " + string.Join(" ", accessWarnings) + "\n" + ApiText.Text;

        RebuildControlRows();

        StopCapture();

        // Always merged at capture/runtime: SDL2 for controllers and RawInput
        // for keyboard/mouse, with Sunshine feeding the remote side.
        _capture.Start(InputApi.MergedInput);
        _rawCapture.Start(registerKeyboard: true);
    }

    private void RebuildControlRows()
    {
        if (_profile == null)
            return;

        RowsPanel.Children.Clear();
        _deviceCombos.Clear();

        IEnumerable<JoystickButtons> visibleButtons =
            _profile.JoystickButtons.Where(IsVisibleForApi);

        if (IsGoldenTeeRemoteLocalPlay())
        {
            // In merged mode the input-source selector is also the player view.
            // Host means the physical cabinet/P1 controls only. Selecting P2/P3/P4
            // shows only that remote player's controls. "Any" remains available
            // when someone intentionally wants to see all active player mappings.
            int selectedPlayer = ActiveCaptureSource.AllowedPlayer;
            if (selectedPlayer != ActiveCaptureSource.Any)
            {
                visibleButtons = visibleButtons.Where(button =>
                    selectedPlayer == ActiveCaptureSource.Host
                        ? GoldenTeeRemoteControlGroup(button) == 0
                        : GoldenTeeRemoteControlGroup(button) == selectedPlayer - 1);
            }

            visibleButtons = visibleButtons
                .OrderBy(GoldenTeeRemoteControlGroup)
                .ThenBy(button => GoldenTeeControlOrder(button.ButtonName));
        }

        foreach (var button in visibleButtons)
        {
            var row = BuildRow(button);
            if (row != null)
                RowsPanel.Children.Add(row);
        }
    }

    private bool IsVisibleForApi(JoystickButtons b)
    {
        // Classic conditional visibility.
        if (_bg4ProMode && b.HideWithProMode) return false;
        if (!_bg4ProMode && b.HideWithoutProMode) return false;
        if (_isKeyboardOrButtonAxis && b.HideWithKeyboardForAxis) return false;
        if (!_isKeyboardOrButtonAxis && b.HideWithoutKeyboardForAxis) return false;
        if (_relativeAxis && b.HideWithRelativeAxis) return false;
        if (!_relativeAxis && b.HideWithoutRelativeAxis) return false;
        if (_useDPadForGun1Stick && b.HideWithUseDPadForGUN1Stick) return false;
        if (!_useDPadForGun1Stick && b.HideWithoutUseDPadForGUN1Stick) return false;
        if (_useDPadForGun2Stick && b.HideWithUseDPadForGUN2Stick) return false;
        if (!_useDPadForGun2Stick && b.HideWithoutUseDPadForGUN2Stick) return false;
        if (_useAnalogAxisToAimGun1 && b.HideWithUseAnalogAxisToAimGUN1) return false;
        if (!_useAnalogAxisToAimGun1 && b.HideWithoutUseAnalogAxisToAimGUN1) return false;
        if (_useAnalogAxisToAimGun2 && b.HideWithUseAnalogAxisToAimGUN2) return false;
        if (!_useAnalogAxisToAimGun2 && b.HideWithoutUseAnalogAxisToAimGUN2) return false;
        if (_isRemoteLocalPlayMode && b.HideWithRemoteLocalPlayMode) return false;
        if (!_isRemoteLocalPlayMode && b.HideWithoutRemoteLocalPlayMode) return false;

        // Golden Tee local mode is a single-cabinet control scheme. Every golfer
        // takes turns using the same physical controls, so only the local P1/cabinet
        // mappings belong on this screen. P2-P4 mappings exist only for remote
        // ownership and should appear only when Remote Local Play forces MergedInput.
        if (IsGoldenTeeProfile() && !_isRemoteLocalPlayMode)
        {
            if (IsGoldenTeeRemotePlayerBinding(b) ||
                IsGoldenTeeLegacyHostBinding(b))
            {
                return false;
            }

            return !b.HideWithRawInputTrackball;
        }

        // Golden Tee Remote Local Play is special:
        //
        // MergedInput is a transport/runtime detail. It should NOT turn the host
        // player's controls into the union of XInput + DirectInput + RawInput +
        // RawInputTrackball rows. The host/local side should look exactly like
        // normal RawInputTrackball setup, while P2-P4 remote rows are added below.
        //
        // This keeps P1 stable when Remote Local Play is toggled on.
        if (IsGoldenTeeRemoteLocalPlay())
        {
            // Host-prefixed controls are legacy remote-P1 transport rows. The
            // current model keeps P1 local and assigns remote clients to P2-P4,
            // so showing these duplicates is both misleading and what caused
            // the P1 controls to appear scattered.
            if (IsGoldenTeeLegacyHostBinding(b))
                return false;

            // The real local/cabinet P1 rows should look exactly like the
            // RawInputTrackball layout even though runtime input is MergedInput.
            if (!IsGoldenTeeRemotePlayerBinding(b))
                return !b.HideWithRawInputTrackball;
        }

        // Normal merged-input behavior for remote P2-P4 and every other game:
        // visible if any active input family can use the row.
        if (!b.HideWithXInput) return true;
        if (!b.HideWithDirectInput) return true;
        if (_mergedIncludesRawInput && !b.HideWithRawInput) return true;
        if (_mergedIncludesRawInputTrackball && !b.HideWithRawInputTrackball) return true;
        return false;
    }

    private bool IsGoldenTeeProfile() =>
        _profile != null &&
        GoldenTeeRemotePlayerProfiles.IsGoldenTee(_profile);

    private bool IsGoldenTeeRemoteLocalPlay() =>
        IsGoldenTeeProfile() &&
        _isRemoteLocalPlayMode;

    private static bool IsGoldenTeeRemotePlayerBinding(JoystickButtons binding)
    {
        var name = binding.ButtonName ?? string.Empty;

        // In the Golden Tee profile P2-P4 are the remotely owned seats.  Everything
        // else is the host/P1/cabinet side and therefore follows RawInputTrackball
        // visibility exactly.
        return name.StartsWith("P2 ", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("P3 ", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("P4 ", StringComparison.OrdinalIgnoreCase) ||
               binding.InputMapping is
                   InputMapping.P2LightGun or InputMapping.P3LightGun or InputMapping.P4LightGun or
                   InputMapping.P2Trackball or InputMapping.P3Trackball or InputMapping.P4Trackball;
    }

    private static bool IsGoldenTeeLegacyHostBinding(JoystickButtons binding)
    {
        var name = binding.ButtonName ?? string.Empty;

        return name.StartsWith("Host ", StringComparison.OrdinalIgnoreCase) ||
               binding.InputMapping == InputMapping.HostTrackball;
    }

    private static int GoldenTeeRemoteControlGroup(JoystickButtons binding)
    {
        var name = binding.ButtonName ?? string.Empty;

        if (name.StartsWith("P2 ", StringComparison.OrdinalIgnoreCase)) return 1;
        if (name.StartsWith("P3 ", StringComparison.OrdinalIgnoreCase)) return 2;
        if (name.StartsWith("P4 ", StringComparison.OrdinalIgnoreCase)) return 3;
        return 0; // local P1/cabinet controls first
    }

    private static int GoldenTeeControlOrder(string? buttonName)
    {
        var name = buttonName ?? string.Empty;

        // Compare the logical control name, ignoring a P2/P3/P4 prefix.
        if (name.Length > 3 &&
            name[0] == 'P' &&
            name[1] is '2' or '3' or '4' &&
            name[2] == ' ')
        {
            name = name[3..];
        }

        return name.ToUpperInvariant() switch
        {
            "TEST" => 0,
            "SERVICE" => 1,
            "COIN" => 2,
            "COIN2" => 3,
            "MONEY BILL IN" => 4,
            "VOLUME UP" => 5,
            "VOLUME DOWN" => 6,
            "START" => 10,
            "LEFT" => 11,
            "RIGHT" => 12,
            "FLYBY" => 13,
            "SPIN" => 14,
            "OPTION" => 15,
            "HELP" => 16,
            "SWITCH CLUB LEFT" => 17,
            "SWITCH CLUB RIGHT" => 18,
            "SAVE CURRENT OUTFIT TO TPUI" => 19,
            "TRACKBALL" => 20,
            _ => 100
        };
    }

    private string CurrentBindName(JoystickButtons b) => _api switch
    {
        InputApi.SDL2 => b.BindNameXi ?? b.BindName ?? "",
        InputApi.RawInput or InputApi.RawInputTrackball => b.BindNameRi ?? b.BindName ?? "",
        _ => b.BindName ?? ""
    };

    private Control? BuildRow(JoystickButtons binding)
    {
        // Lightgun / trackball rows use a device dropdown.
        if (binding.InputMapping is
            InputMapping.P1LightGun or InputMapping.P2LightGun or
            InputMapping.P3LightGun or InputMapping.P4LightGun or
            InputMapping.HostTrackball or InputMapping.P1Trackball or
            InputMapping.P2Trackball or InputMapping.P3Trackball or
            InputMapping.P4Trackball)
        {
            if (_api is InputApi.RawInput or InputApi.RawInputTrackball ||
                (_api == InputApi.MergedInput &&
                 (_mergedIncludesRawInput || _mergedIncludesRawInputTrackball)))
            {
                return BuildDeviceRow(binding);
            }

            return null;
        }

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("240,*,Auto"),
            Margin = new global::Avalonia.Thickness(0, 2, 0, 2)
        };

        var label = new TextBlock
        {
            Text = binding.ButtonName,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = global::Avalonia.Media.TextWrapping.Wrap
        };

        if (!string.IsNullOrWhiteSpace(binding.Hint))
            ToolTip.SetTip(label, binding.Hint);

        var bindButton = new Button
        {
            Content = string.IsNullOrWhiteSpace(CurrentBindName(binding))
                ? Services.Loc.T("NotBound", "(not bound)")
                : CurrentBindName(binding),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsEnabled = true
        };

        bindButton.Click += (_, _) => Arm(bindButton, binding);

        var clearButton = new Button
        {
            Content = "✕",
            Margin = new global::Avalonia.Thickness(6, 0, 0, 0)
        };

        ToolTip.SetTip(clearButton, "Clear binding");
        clearButton.Click += (_, _) =>
        {
            ClearBinding(binding);
            bindButton.Content = Services.Loc.T("NotBound", "(not bound)");
        };

        Grid.SetColumn(label, 0);
        Grid.SetColumn(bindButton, 1);
        Grid.SetColumn(clearButton, 2);

        grid.Children.Add(label);
        grid.Children.Add(bindButton);
        grid.Children.Add(clearButton);

        return grid;
    }

    private Control BuildDeviceRow(JoystickButtons binding)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("240,*"),
            Margin = new global::Avalonia.Thickness(0, 2, 0, 2)
        };

        var label = new TextBlock
        {
            Text = binding.ButtonName,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = global::Avalonia.Media.TextWrapping.Wrap
        };

        if (!string.IsNullOrWhiteSpace(binding.Hint))
            ToolTip.SetTip(label, binding.Hint);

        var combo = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        _deviceCombos.Add((combo, binding));
        PopulateDeviceCombo(combo, binding);

        combo.SelectionChanged += (_, _) =>
        {
            if (_refreshingDeviceLists ||
                combo.SelectedItem is not string selectedDeviceName)
            {
                return;
            }

            string path;
            var type = RawDeviceType.None;

            if (selectedDeviceName == "Windows Mouse Cursor")
            {
                path = "Windows Mouse Cursor";
                type = RawDeviceType.Mouse;
            }
            else if (selectedDeviceName == "None")
            {
                path = "None";
            }
            else if (selectedDeviceName == "Unknown Device")
            {
                path = "null";
                type = RawDeviceType.Mouse;
            }
            else if (SunshinePlayerInput.TryParsePlayerFromDisplayName(
                         selectedDeviceName,
                         out int sunshinePlayer))
            {
                path = SunshinePlayerInput.DevicePathForPlayer(sunshinePlayer);
                type = RawDeviceType.Mouse;
            }
            else
            {
                var devicePath =
                    _rawCapture.GetMouseDevicePathByName(selectedDeviceName);

                if (devicePath == null)
                {
                    ApiText.Text =
                        $"Device \"{selectedDeviceName}\" is not currently available - plug it in and reopen this page.";
                    return;
                }

                path = devicePath;
                type = RawDeviceType.Mouse;
            }

            binding.RawInputButton = new RawInputButton
            {
                DevicePath = path,
                DeviceType = type,
                MouseButton = RawMouseButton.None,
                KeyboardKey = Keys.None
            };

            binding.BindName = selectedDeviceName;
            binding.BindNameRi = selectedDeviceName;
        };

        Grid.SetColumn(label, 0);
        Grid.SetColumn(combo, 1);

        grid.Children.Add(label);
        grid.Children.Add(combo);

        return grid;
    }

    private void PopulateDeviceCombo(ComboBox combo, JoystickButtons binding)
    {
        var deviceList =
            new List<string> { "None", "Windows Mouse Cursor", "Unknown Device" };

        if (OperatingSystem.IsWindows())
        {
            foreach (var player in SunshinePlayerInput.GetConnectedPlayers())
                deviceList.Add(SunshinePlayerInput.DisplayNameForPlayer(player));
        }

        deviceList.AddRange(_rawCapture.GetMouseDeviceList());

        if (!string.IsNullOrEmpty(binding.BindNameRi) &&
            !deviceList.Contains(binding.BindNameRi))
        {
            deviceList.Add(binding.BindNameRi);
        }

        _refreshingDeviceLists = true;
        combo.ItemsSource = deviceList;
        combo.SelectedItem =
            string.IsNullOrEmpty(binding.BindNameRi)
                ? "None"
                : binding.BindNameRi;
        _refreshingDeviceLists = false;
    }

    private void PopulateActiveCaptureSourceSelector()
    {
        int previous = ActiveCaptureSource.AllowedPlayer;

        var items = new List<CaptureSourceItem>
        {
            new("Any", ActiveCaptureSource.Any),
            new("Host", ActiveCaptureSource.Host)
        };

        foreach (var player in SunshinePlayerInput.GetConnectedPlayers())
            items.Add(
                new CaptureSourceItem(
                    SunshinePlayerInput.DisplayNameForPlayer(player),
                    player));

        ActiveCaptureSourceSelector.ItemsSource = items;
        ActiveCaptureSourceSelector.SelectedItem =
            items.FirstOrDefault(x => x.Player == previous) ?? items[0];

        if (items.All(x => x.Player != previous))
            ActiveCaptureSource.AllowedPlayer = ActiveCaptureSource.Any;
    }

    private sealed record CaptureSourceItem(string Name, int Player)
    {
        public override string ToString() => Name;
    }

    private void ActiveCaptureSourceSelector_SelectionChanged(
        object? sender,
        SelectionChangedEventArgs e)
    {
        if (ActiveCaptureSourceSelector.SelectedItem is not CaptureSourceItem item)
            return;

        ActiveCaptureSource.AllowedPlayer = item.Player;

        if (IsGoldenTeeRemoteLocalPlay())
            RebuildControlRows();
    }

    private void OnSunshineInputReceived(object? sender, SunshineInputEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (e.EventType == SunshineInputEventType.Roster)
            {
                PopulateActiveCaptureSourceSelector();

                if (IsGoldenTeeRemoteLocalPlay())
                {
                    RebuildControlRows();
                }
                else
                {
                    foreach (var (combo, binding) in _deviceCombos.ToList())
                        PopulateDeviceCombo(combo, binding);
                }

                return;
            }

            if (_armedButton == null ||
                _armedBinding == null ||
                !ActiveCaptureSource.IsAllowed(e.Player))
            {
                return;
            }

            RawInputButton? button = null;
            string? name = null;

            if (e.EventType == SunshineInputEventType.KeyDown)
            {
                button = new RawInputButton
                {
                    DevicePath = SunshinePlayerInput.DevicePathForPlayer(e.Player),
                    DeviceType = RawDeviceType.Keyboard,
                    KeyboardKey = (Keys)e.KeyCode,
                    MouseButton = RawMouseButton.None
                };

                name =
                    $"{SunshinePlayerInput.DisplayNameForPlayer(e.Player)} Key {(Keys)e.KeyCode}";
            }
            else if (e.EventType == SunshineInputEventType.MouseButtonDown)
            {
                var mouseButton =
                    SunshinePlayerInput.MapMouseButton(e.MouseButton);

                if (mouseButton == RawMouseButton.None)
                    return;

                button = new RawInputButton
                {
                    DevicePath = SunshinePlayerInput.DevicePathForPlayer(e.Player),
                    DeviceType = RawDeviceType.Mouse,
                    KeyboardKey = Keys.None,
                    MouseButton = mouseButton
                };

                name =
                    $"{SunshinePlayerInput.DisplayNameForPlayer(e.Player)} {mouseButton}";
            }

            if (button != null && name != null)
                OnRawCaptured(name, button, false);
        });
    }

    private void Arm(Button button, JoystickButtons binding)
    {
        if (_armedButton != null)
        {
            _armedButton.Content =
                ArmedOriginalText ??
                Services.Loc.T("NotBound", "(not bound)");
        }

        _armedButton = button;
        _armedBinding = binding;
        ArmedOriginalText = button.Content as string;
        button.Content =
            Services.Loc.T(
                "PressButtonKeyAxis",
                "Press a button / key / axis...");
    }

    private string? ArmedOriginalText;

    private void OnCaptured(CapturedBinding captured)
    {
        if (_armedButton == null || _armedBinding == null)
            return;

        switch (_api)
        {
            case InputApi.SDL2 when captured.XInput != null:
            case InputApi.MergedInput when captured.XInput != null:
                _armedBinding.XInputButton = captured.XInput;
                _armedBinding.BindNameXi = captured.DisplayName;
                _armedBinding.RawInputButton = null;
                _armedBinding.BindNameRi = null;
                _armedBinding.BindName = captured.DisplayName;
                break;

            default:
                return;
        }

        _armedButton.Content = captured.DisplayName;
        _armedButton = null;
        _armedBinding = null;
        ArmedOriginalText = null;
    }

    private void OnRawCaptured(
        string name,
        RawInputButton button,
        bool isEscape)
    {
        if (_armedButton == null || _armedBinding == null)
            return;

        bool isSunshine =
            button.DevicePath?.StartsWith(
                "SUNSHINE#PLAYER",
                StringComparison.Ordinal) == true;

        if (!isSunshine &&
            !ActiveCaptureSource.IsAllowed(ActiveCaptureSource.Host))
        {
            return;
        }

        if (isEscape)
        {
            _armedButton.Content =
                ArmedOriginalText ??
                Services.Loc.T("NotBound", "(not bound)");

            _armedButton = null;
            _armedBinding = null;
            ArmedOriginalText = null;
            return;
        }

        if (_api is not
            (InputApi.RawInput or
             InputApi.RawInputTrackball or
             InputApi.MergedInput))
        {
            return;
        }

        _armedBinding.RawInputButton = button;
        _armedBinding.BindNameRi = name;
        _armedBinding.XInputButton = null;
        _armedBinding.BindNameXi = null;
        _armedBinding.BindName = name;

        _armedButton.Content = name;
        _armedButton = null;
        _armedBinding = null;
        ArmedOriginalText = null;
    }

    private void ClearBinding(JoystickButtons binding)
    {
        switch (_api)
        {
            case InputApi.SDL2:
                binding.XInputButton = null;
                binding.BindNameXi = null;
                break;

            case InputApi.RawInput:
            case InputApi.RawInputTrackball:
                binding.RawInputButton = null;
                binding.BindNameRi = null;
                break;

            default:
                binding.XInputButton = null;
                binding.DirectInputButton = null;
                binding.RawInputButton = null;
                binding.BindNameXi = null;
                binding.BindNameDi = null;
                binding.BindNameRi = null;
                break;
        }

        binding.BindName = null;
    }

    private void BtnBack_Click(
        object? sender,
        global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        StopCapture();
        BackRequested?.Invoke();
    }

    private void BtnSave_Click(
        object? sender,
        global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_profile == null)
            return;

        TeknoParrotUi.Common.InputListening.ProfileStorage.BindingsStore.Save(_profile);

        System.IO.Directory.CreateDirectory("UserProfiles");
        JoystickHelper.SerializeGameProfile(_profile);

        Saved?.Invoke(
            _profile.GameNameInternal ??
            _profile.ProfileName ??
            "profile");

        StopCapture();
        BackRequested?.Invoke();
    }
}
