using System;
using System.Collections.Generic;
using System.IO.MemoryMappedFiles;
using System.Linq;

namespace TeknoParrotUi.Common.InputListening
{
    /// <summary>
    /// .NET 8 input-pipeline adapter for the TeknoParrot Sunshine identity bridge.
    ///
    /// Sunshine still injects keyboard/mouse/gamepad input normally, but its custom
    /// TeknoParrot pipe supplies the per-Moonlight-client identity that Windows
    /// RawInput cannot preserve for synthetic input. This listener consumes those
    /// tagged events and dispatches bindings that use SUNSHINE#PLAYERn as their
    /// synthetic device path.
    /// </summary>
    internal sealed class SunshineInputListener : IInputListener
    {
        private readonly object _trackballLock = new object();

        private GameProfile _gameProfile;
        private List<JoystickButtons> _bindings = new List<JoystickButtons>();

        private MemoryMappedFile _trackball1;
        private MemoryMappedFile _trackball2;
        private MemoryMappedFile _trackball3;
        private MemoryMappedFile _trackball4;
        private MemoryMappedFile _trackballHost;

        private MemoryMappedViewAccessor _trackball1View;
        private MemoryMappedViewAccessor _trackball2View;
        private MemoryMappedViewAccessor _trackball3View;
        private MemoryMappedViewAccessor _trackball4View;
        private MemoryMappedViewAccessor _trackballHostView;

        private short _dx1, _dy1;
        private short _dx2, _dy2;
        private short _dx3, _dy3;
        private short _dx4, _dy4;
        private short _dxHost, _dyHost;

        public string Name => "SunshineTeknoParrotInput";
        public bool IsSupported => OperatingSystem.IsWindows();

        public void Start(GameProfile gameProfile, List<JoystickButtons> joystickButtons)
        {
            _gameProfile = gameProfile;
            _bindings = joystickButtons?
                .Where(x => x?.RawInputButton != null)
                .ToList() ?? new List<JoystickButtons>();

            EnsureTrackballBuffers();
            GoldenTeeOptionsControl.Reset(_gameProfile);

            SunshinePlayerInput.InputReceived -= OnSunshineInputReceived;
            SunshinePlayerInput.InputReceived += OnSunshineInputReceived;
            SunshinePlayerInput.Start();
        }

        public void WndProcReceived(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            // Sunshine events arrive through its named pipe, not WM_INPUT.
        }

        public void Stop()
        {
            SunshinePlayerInput.InputReceived -= OnSunshineInputReceived;
            SunshinePlayerInput.Stop();
            GoldenTeeOptionsControl.Reset();

            DisposeTrackballBuffers();
            _bindings.Clear();
            _gameProfile = null;
        }

        private void OnSunshineInputReceived(object sender, SunshineInputEventArgs e)
        {
            if (e.Player < SunshinePlayerInput.MinPlayer || e.Player > SunshinePlayerInput.MaxPlayer)
                return;

            string path = SunshinePlayerInput.DevicePathForPlayer(e.Player);

            try
            {
                switch (e.EventType)
                {
                    case SunshineInputEventType.KeyDown:
                        ProcessKeyboard(e.Player, path, (Keys)e.KeyCode, true);
                        break;
                    case SunshineInputEventType.KeyUp:
                        ProcessKeyboard(e.Player, path, (Keys)e.KeyCode, false);
                        break;
                    case SunshineInputEventType.MouseButtonDown:
                        ProcessMouseButton(e.Player, path, SunshinePlayerInput.MapMouseButton(e.MouseButton), true);
                        break;
                    case SunshineInputEventType.MouseButtonUp:
                        ProcessMouseButton(e.Player, path, SunshinePlayerInput.MapMouseButton(e.MouseButton), false);
                        break;
                    case SunshineInputEventType.MouseMove:
                        ProcessTrackball(e.Player, path, e.DeltaX, e.DeltaY);
                        break;
                    case SunshineInputEventType.Roster:
                        if (!e.Connected)
                            GoldenTeeOptionsControl.HandlePlayerDisconnected(e.Player);
                        break;
                    case SunshineInputEventType.MouseWheel:
                    case SunshineInputEventType.AbsPosition:
                    case SunshineInputEventType.GamepadSlot:
                    default:
                        // Roster/gamepad ownership are retained by SunshinePlayerInput for
                        // UI/capture use. The normal merged input pipeline still receives
                        // actual virtual gamepad state through SDL2.
                        break;
                }
            }
            catch
            {
                // Input must never tear down the game session.
            }
        }

        private void ProcessKeyboard(int player, string path, Keys key, bool pressed)
        {
            foreach (var binding in _bindings.Where(x =>
                         x.RawInputButton.DevicePath == path &&
                         x.RawInputButton.DeviceType == RawDeviceType.Keyboard &&
                         x.RawInputButton.KeyboardKey == key))
            {
                Dispatch(binding, pressed, player);
            }
        }

        private void ProcessMouseButton(int player, string path, RawMouseButton button, bool pressed)
        {
            foreach (var binding in _bindings.Where(x =>
                         x.RawInputButton.DevicePath == path &&
                         x.RawInputButton.DeviceType == RawDeviceType.Mouse &&
                         x.RawInputButton.MouseButton == button))
            {
                Dispatch(binding, pressed, player);
            }
        }

        private void Dispatch(JoystickButtons binding, bool pressed, int player)
        {
            if (!IsBindingActive(binding))
                return;

            GoldenTeeOptionsControl.HandleDigitalInput(_gameProfile, player, binding.InputMapping, pressed);
            MappingDispatch.Apply(binding.InputMapping, pressed, _gameProfile);
        }

        private bool IsBindingActive(JoystickButtons binding)
        {
            bool remoteMode = _gameProfile?.ConfigValues != null &&
                              _gameProfile.ConfigValues.Any(x =>
                                  x.FieldName == "Remote Local Play" &&
                                  x.FieldValue != "Off");

            if (remoteMode && binding.HideWithRemoteLocalPlayMode)
                return false;
            if (!remoteMode && binding.HideWithoutRemoteLocalPlayMode)
                return false;

            return true;
        }

        private void ProcessTrackball(int player, string path, int deltaX, int deltaY)
        {
            foreach (var binding in _bindings.Where(x =>
                         x.RawInputButton.DevicePath == path &&
                         x.RawInputButton.DeviceType == RawDeviceType.Mouse &&
                         IsTrackballMapping(x.InputMapping)))
            {
                if (!IsBindingActive(binding))
                    continue;

                WriteTrackball(binding.InputMapping, deltaX, deltaY);

                if (GoldenTeeOptionsControl.ShouldMirrorTrackball(_gameProfile, player))
                {
                    GoldenTeeOptionsTrackballBroadcast.BroadcastToOtherPlayers(
                        player,
                        deltaX,
                        deltaY);
                }

                // Preserve the player's normal isolated trackball path, then mirror the same
                // already-tagged delta into the dedicated Host MMF only while that player owns
                // Golden Tee's Options menu. No P1/P2/P3/P4 buffer is ever read or rewritten.
                if (binding.InputMapping != InputMapping.HostTrackball &&
                    GoldenTeeOptionsControl.ShouldMirrorTrackball(_gameProfile, player))
                {
                    lock (_trackballLock)
                    {
                        Accumulate(_trackballHostView, ref _dxHost, ref _dyHost, deltaX, deltaY);
                    }
                }
            }
        }

        private static bool IsTrackballMapping(InputMapping mapping) =>
            mapping == InputMapping.HostTrackball ||
            mapping == InputMapping.P1Trackball ||
            mapping == InputMapping.P2Trackball ||
            mapping == InputMapping.P3Trackball ||
            mapping == InputMapping.P4Trackball;

        private void WriteTrackball(InputMapping mapping, int deltaX, int deltaY)
        {
            lock (_trackballLock)
            {
                bool remoteMode = _gameProfile?.ConfigValues != null &&
                                  _gameProfile.ConfigValues.Any(x =>
                                      x.FieldName == "Remote Local Play" &&
                                      x.FieldValue != "Off");

                if (remoteMode)
                {
                    switch (mapping)
                    {
                        case InputMapping.HostTrackball:
                            Accumulate(_trackballHostView, ref _dxHost, ref _dyHost, deltaX, deltaY);
                            return;
                        case InputMapping.P2Trackball:
                            Accumulate(_trackball2View, ref _dx2, ref _dy2, deltaX, deltaY);
                            return;
                        case InputMapping.P3Trackball:
                            Accumulate(_trackball3View, ref _dx3, ref _dy3, deltaX, deltaY);
                            return;
                        case InputMapping.P4Trackball:
                            Accumulate(_trackball4View, ref _dx4, ref _dy4, deltaX, deltaY);
                            return;
                    }
                }

                // Original/non-remote behavior and P1 both use the original buffer.
                Accumulate(_trackball1View, ref _dx1, ref _dy1, deltaX, deltaY);
            }
        }

        private static void Accumulate(
            MemoryMappedViewAccessor accessor,
            ref short currentX,
            ref short currentY,
            int deltaX,
            int deltaY)
        {
            if (accessor == null)
                return;

            if (accessor.ReadInt32(8) == 1)
            {
                currentX = 0;
                currentY = 0;
                accessor.Write(8, 0);
            }

            int nextX = Math.Clamp(currentX + deltaX, short.MinValue, short.MaxValue);
            int nextY = Math.Clamp(currentY + deltaY, short.MinValue, short.MaxValue);

            currentX = (short)nextX;
            currentY = (short)nextY;

            accessor.Write(0, (int)currentX);
            accessor.Write(4, (int)currentY);
        }

        private void EnsureTrackballBuffers()
        {
            // Same names/layout used by the side-BETA RawInputTrackball path:
            // [0]=deltaX int32, [4]=deltaY int32, [8]=reset flag int32.
            _trackball1 = MemoryMappedFile.CreateOrOpen("RawInputTrackballSharedMemory", 12);
            _trackball2 = MemoryMappedFile.CreateOrOpen("RawInputTrackballSharedMemory2", 12);
            _trackball3 = MemoryMappedFile.CreateOrOpen("RawInputTrackballSharedMemory3", 12);
            _trackball4 = MemoryMappedFile.CreateOrOpen("RawInputTrackballSharedMemory4", 12);
            _trackballHost = MemoryMappedFile.CreateOrOpen("RawInputTrackballSharedMemoryHost", 12);

            _trackball1View = _trackball1.CreateViewAccessor();
            _trackball2View = _trackball2.CreateViewAccessor();
            _trackball3View = _trackball3.CreateViewAccessor();
            _trackball4View = _trackball4.CreateViewAccessor();
            _trackballHostView = _trackballHost.CreateViewAccessor();

            ResetBuffer(_trackball1View);
            ResetBuffer(_trackball2View);
            ResetBuffer(_trackball3View);
            ResetBuffer(_trackball4View);
            ResetBuffer(_trackballHostView);

            _dx1 = _dy1 = _dx2 = _dy2 = _dx3 = _dy3 =
                _dx4 = _dy4 = _dxHost = _dyHost = 0;
        }

        private static void ResetBuffer(MemoryMappedViewAccessor accessor)
        {
            accessor.Write(0, 0);
            accessor.Write(4, 0);
            accessor.Write(8, 0);
        }

        private void DisposeTrackballBuffers()
        {
            _trackball1View?.Dispose();
            _trackball2View?.Dispose();
            _trackball3View?.Dispose();
            _trackball4View?.Dispose();
            _trackballHostView?.Dispose();

            _trackball1?.Dispose();
            _trackball2?.Dispose();
            _trackball3?.Dispose();
            _trackball4?.Dispose();
            _trackballHost?.Dispose();

            _trackball1View = _trackball2View = _trackball3View =
                _trackball4View = _trackballHostView = null;
            _trackball1 = _trackball2 = _trackball3 =
                _trackball4 = _trackballHost = null;
        }
    }
}