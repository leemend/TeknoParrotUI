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
    // Conditional row visibility (classic JoystickControl rules): rows like
    // Wheel Left/Right or the Gun Up/Down/Left/Right buttons only appear when
    // the matching game option is enabled.
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
        _capture.BindingCaptured += captured => Dispatcher.UIThread.Post(() => OnCaptured(captured));
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

        // Input is always merged: SDL2 gamepads + RawInput keyboard/mouse.
        // The saved Input API only selects the gun flavour for games that
        // offer trackball input.
        _api = InputApi.MergedInput;
        _mergedIncludesRawInput = apiField?.FieldOptions?.Contains("RawInput") == true || profile.GunGame;
        _mergedIncludesRawInputTrackball = apiField?.FieldOptions?.Contains("RawInputTrackball") == true &&
                                           (savedValue == "RawInputTrackball" || apiField?.FieldOptions?.Contains("RawInput") != true);

        // Conditional visibility flags (same config fields as the classic UI)
        _isKeyboardOrButtonAxis = profile.ConfigValues.Any(c => c.FieldName == "Use Keyboard/Button For Axis" && c.FieldValue == "1");
        _relativeAxis = profile.ConfigValues.Any(c => c.FieldName == "Use Relative Input" && c.FieldValue == "1");
        _bg4ProMode = profile.ConfigValues.Any(c => c.FieldName == "Professional Edition Enable" && c.FieldValue == "1");
        _useDPadForGun1Stick = profile.ConfigValues.FirstOrDefault(c => c.FieldName == "GUN1StickAxisInputStyle")?.FieldValue == "UseDPadForGUN1Stick" ||
                               profile.ConfigValues.FirstOrDefault(c => c.FieldName == "Left Stick Button Mode")?.FieldValue == "1";
        _useDPadForGun2Stick = profile.ConfigValues.FirstOrDefault(c => c.FieldName == "GUN2StickAxisInputStyle")?.FieldValue == "UseDPadForGUN2Stick" ||
                               profile.ConfigValues.FirstOrDefault(c => c.FieldName == "Right Stick Button Mode")?.FieldValue == "1";
        _useAnalogAxisToAimGun1 = profile.ConfigValues.FirstOrDefault(c => c.FieldName == "GUN1AimingInputStyle")?.FieldValue == "UseAnalogAxisToAim";
        _useAnalogAxisToAimGun2 = profile.ConfigValues.FirstOrDefault(c => c.FieldName == "GUN2AimingInputStyle")?.FieldValue == "UseAnalogAxisToAim";
        _isRemoteLocalPlayMode = profile.ConfigValues.Any(c => c.FieldName == "Remote Local Play" && c.FieldValue != "Off");

        ActiveCaptureSourceRow.IsVisible = _isRemoteLocalPlayMode;
        if (!_isRemoteLocalPlayMode)
            ActiveCaptureSource.AllowedPlayer = ActiveCaptureSource.Any;
        PopulateActiveCaptureSourceSelector();

        Header.Text = $"{profile.GameNameInternal ?? profile.ProfileName} - Controls";
        ApiText.Text = "Click a binding, then press a controller button/axis, keyboard key or mouse button. Escape cancels." +
                       (_mergedIncludesRawInput || _mergedIncludesRawInputTrackball
                           ? " Lightgun/trackball devices are picked from the dropdown."
                           : "");

        // Linux: keyboards are often unreadable while mice work (vendor udev
        // ACLs vs missing 'input' group membership) - tell the user here, where
        // they would otherwise just see keys not binding.
        var accessWarnings = _rawCapture.GetAccessWarnings();
        if (accessWarnings.Count > 0)
            ApiText.Text = "⚠ " + string.Join(" ", accessWarnings) + "\n" + ApiText.Text;

        RowsPanel.Children.Clear();
        _deviceCombos.Clear();
        foreach (var button in profile.JoystickButtons.Where(IsVisibleForApi))
        {
            var row = BuildRow(button);
            if (row != null)
                RowsPanel.Children.Add(row);
        }

        StopCapture();
        // Always merged: SDL2 for controllers, RawInput for keyboards and mice
        _capture.Start(InputApi.MergedInput);
        _rawCapture.Start(registerKeyboard: true);
    }

    private bool IsVisibleForApi(JoystickButtons b)
    {
        // Classic conditional-visibility chain: option-dependent rows only
        // appear when the matching game option is enabled.
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

        // Merged view: a row is hidden only if every active input method hides it
        // (classic ShouldHideForMergedInput). DirectInput visibility counts too:
        // classic keyboard rows (e.g. Wheel Axis Left/Right) are marked
        // HideWithXInput + visible-for-DirectInput, and RawInput keyboards have
        // replaced DirectInput keyboards in every game.
        if (!b.HideWithXInput) return true;
        if (!b.HideWithDirectInput) return true;
        if (_mergedIncludesRawInput && !b.HideWithRawInput) return true;
        if (_mergedIncludesRawInputTrackball && !b.HideWithRawInputTrackball) return true;
        return false;
    }

    private string CurrentBindName(JoystickButtons b) => _api switch
    {
        InputApi.SDL2 => b.BindNameXi ?? b.BindName ?? "",
        InputApi.RawInput or InputApi.RawInputTrackball => b.BindNameRi ?? b.BindName ?? "",
        _ => b.BindName ?? ""
    };

    private Control? BuildRow(JoystickButtons binding)
    {
        // Lightgun / trackball rows are a device dropdown, not a key capture (classic UI)
        if (binding.InputMapping is InputMapping.P1LightGun or InputMapping.P2LightGun
            or InputMapping.P3LightGun or InputMapping.P4LightGun
            or InputMapping.HostTrackball or InputMapping.P1Trackball or InputMapping.P2Trackball
            or InputMapping.P3Trackball or InputMapping.P4Trackball)
        {
            if (_api is InputApi.RawInput or InputApi.RawInputTrackball ||
                (_api == InputApi.MergedInput && (_mergedIncludesRawInput || _mergedIncludesRawInputTrackball)))
                return BuildDeviceRow(binding);
            return null; // not applicable to the current input API
        }

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("240,*,Auto"), Margin = new global::Avalonia.Thickness(0, 2, 0, 2) };

        var label = new TextBlock { Text = binding.ButtonName, VerticalAlignment = VerticalAlignment.Center, TextWrapping = global::Avalonia.Media.TextWrapping.Wrap };
        if (!string.IsNullOrWhiteSpace(binding.Hint))
            ToolTip.SetTip(label, binding.Hint);

        var bindButton = new Button
        {
            Content = string.IsNullOrWhiteSpace(CurrentBindName(binding)) ? Services.Loc.T("NotBound", "(not bound)") : CurrentBindName(binding),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsEnabled = true
        };
        bindButton.Click += (_, _) => Arm(bindButton, binding);

        var clearButton = new Button { Content = "✕", Margin = new global::Avalonia.Thickness(6, 0, 0, 0) };
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

    /// <summary>
    /// Device dropdown for lightgun/trackball position mappings: pick any RawInput
    /// mouse device (lightguns enumerate as mice), the Windows cursor, or none -
    /// same list and save semantics as the classic UI.
    /// </summary>
    private Control BuildDeviceRow(JoystickButtons binding)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("240,*"), Margin = new global::Avalonia.Thickness(0, 2, 0, 2) };

        var label = new TextBlock { Text = binding.ButtonName, VerticalAlignment = VerticalAlignment.Center, TextWrapping = global::Avalonia.Media.TextWrapping.Wrap };
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
            if (_refreshingDeviceLists || combo.SelectedItem is not string selectedDeviceName)
                return;

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
            else if (SunshinePlayerInput.TryParsePlayerFromDisplayName(selectedDeviceName, out int sunshinePlayer))
            {
                path = SunshinePlayerInput.DevicePathForPlayer(sunshinePlayer);
                type = RawDeviceType.Mouse;
            }
            else
            {
                var devicePath = _rawCapture.GetMouseDevicePathByName(selectedDeviceName);
                if (devicePath == null)
                {
                    ApiText.Text = $"Device \"{selectedDeviceName}\" is not currently available — plug it in and reopen this page.";
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
        var deviceList = new List<string> { "None", "Windows Mouse Cursor", "Unknown Device" };

        if (OperatingSystem.IsWindows())
        {
            foreach (var player in SunshinePlayerInput.GetConnectedPlayers())
                deviceList.Add(SunshinePlayerInput.DisplayNameForPlayer(player));
        }

        deviceList.AddRange(_rawCapture.GetMouseDeviceList());

        if (!string.IsNullOrEmpty(binding.BindNameRi) && !deviceList.Contains(binding.BindNameRi))
            deviceList.Add(binding.BindNameRi);

        _refreshingDeviceLists = true;
        combo.ItemsSource = deviceList;
        combo.SelectedItem = string.IsNullOrEmpty(binding.BindNameRi) ? "None" : binding.BindNameRi;
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
            items.Add(new CaptureSourceItem(SunshinePlayerInput.DisplayNameForPlayer(player), player));

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

    private void ActiveCaptureSourceSelector_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ActiveCaptureSourceSelector.SelectedItem is CaptureSourceItem item)
            ActiveCaptureSource.AllowedPlayer = item.Player;
    }

    private void OnSunshineInputReceived(object? sender, SunshineInputEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (e.EventType == SunshineInputEventType.Roster)
            {
                PopulateActiveCaptureSourceSelector();
                foreach (var (combo, binding) in _deviceCombos.ToList())
                    PopulateDeviceCombo(combo, binding);
                return;
            }

            if (_armedButton == null || _armedBinding == null ||
                !ActiveCaptureSource.IsAllowed(e.Player))
                return;

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
                name = $"{SunshinePlayerInput.DisplayNameForPlayer(e.Player)} Key {(Keys)e.KeyCode}";
            }
            else if (e.EventType == SunshineInputEventType.MouseButtonDown)
            {
                var mouseButton = SunshinePlayerInput.MapMouseButton(e.MouseButton);
                if (mouseButton == RawMouseButton.None)
                    return;

                button = new RawInputButton
                {
                    DevicePath = SunshinePlayerInput.DevicePathForPlayer(e.Player),
                    DeviceType = RawDeviceType.Mouse,
                    KeyboardKey = Keys.None,
                    MouseButton = mouseButton
                };
                name = $"{SunshinePlayerInput.DisplayNameForPlayer(e.Player)} {mouseButton}";
            }

            if (button != null && name != null)
                OnRawCaptured(name, button, false);
        });
    }

    private void Arm(Button button, JoystickButtons binding)
    {
        if (_armedButton != null)
            _armedButton.Content = ArmedOriginalText ?? Services.Loc.T("NotBound", "(not bound)");

        _armedButton = button;
        _armedBinding = binding;
        ArmedOriginalText = button.Content as string;
        button.Content = Services.Loc.T("PressButtonKeyAxis", "Press a button / key / axis...");
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
                // SDL2 capture produces XInput-shaped bindings (shared storage).
                // One binding per row: the controller binding replaces any
                // keyboard/mouse binding so the two listeners never fight.
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

    private void OnRawCaptured(string name, RawInputButton button, bool isEscape)
    {
        if (_armedButton == null || _armedBinding == null)
            return;

        bool isSunshine = button.DevicePath?.StartsWith("SUNSHINE#PLAYER", StringComparison.Ordinal) == true;
        if (!isSunshine && !ActiveCaptureSource.IsAllowed(ActiveCaptureSource.Host))
            return;

        if (isEscape)
        {
            _armedButton.Content = ArmedOriginalText ?? Services.Loc.T("NotBound", "(not bound)");
            _armedButton = null;
            _armedBinding = null;
            ArmedOriginalText = null;
            return;
        }

        // RawInput captures only apply for RawInput-family APIs
        if (_api is not (InputApi.RawInput or InputApi.RawInputTrackball or InputApi.MergedInput))
            return;

        // One binding per row: keyboard/mouse replaces any controller binding
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

    private void BtnBack_Click(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        StopCapture();
        BackRequested?.Invoke();
    }

    private void BtnSave_Click(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_profile == null) return;
        // Controls are authoritative in InputBindings/<profile>.json; the user
        // XML keeps game settings/paths (and must exist for the game to count
        // as installed).
        TeknoParrotUi.Common.InputListening.ProfileStorage.BindingsStore.Save(_profile);
        System.IO.Directory.CreateDirectory("UserProfiles");
        JoystickHelper.SerializeGameProfile(_profile);
        Saved?.Invoke(_profile.GameNameInternal ?? _profile.ProfileName ?? "profile");
        StopCapture();
        BackRequested?.Invoke();
    }
}
