using System;
using System.IO;
using System.Linq;

namespace TeknoParrotUi.Common.InputListening
{
    /// <summary>
    /// Golden Tee Remote Local Play Options-menu arbitration.
    ///
    /// Option itself is broadcast to all four Golden Tee player control words so the
    /// game sees it regardless of whose turn is currently active. The player who
    /// physically pressed Option owns menu navigation until Option is pressed again,
    /// another player presses Option, that remote player disconnects, or input resets.
    ///
    /// Normal P1/P2/P3/P4 Start and trackball paths remain isolated. Only the current
    /// Options owner's Start + trackball are additionally mirrored through the dedicated
    /// Host override path. No normal player buffer is copied into another player buffer.
    /// </summary>
    public static class GoldenTeeOptionsControl
    {
        private static readonly object Sync = new object();

        private static readonly bool[] RemoteOptionDown = new bool[5];
        private static readonly bool[] RemoteStartDown = new bool[5];

        private static int _ownerPlayer;
        private static bool _localOptionDown;
        private static bool _localStartDown;
        private static GameProfile _activeProfile;

        public static void Reset(GameProfile profile = null)
        {
            lock (Sync)
            {
                _activeProfile = profile;
                _ownerPlayer = 0;
                _localOptionDown = false;
                _localStartDown = false;
                Array.Clear(RemoteOptionDown, 0, RemoteOptionDown.Length);
                Array.Clear(RemoteStartDown, 0, RemoteStartDown.Length);
            }
        }

        public static void HandleDigitalInput(
            GameProfile profile,
            int player,
            InputMapping mapping,
            bool pressed)
        {
            if (!IsEnabled(profile) || player < SunshinePlayerInput.MinPlayer || player > SunshinePlayerInput.MaxPlayer)
                return;

            lock (Sync)
            {
                PruneDisconnectedPlayers();

                if (IsOptionMapping(player, mapping))
                {
                    bool wasPressed = RemoteOptionDown[player];
                    RemoteOptionDown[player] = pressed;

                    if (pressed && !wasPressed)
                        ToggleOrAssignOwner(player);

                    return;
                }

                if (IsStartMapping(player, mapping))
                    RemoteStartDown[player] = pressed;
            }
        }

        /// <summary>
        /// Observe the already-routed remote player state from the IT pipe. This is the
        /// authoritative path for remote gamepad buttons because Moonlight gamepads arrive
        /// through SDL2, not as keyboard/mouse events on the Sunshine identity pipe.
        /// </summary>
        public static void ObserveRemoteInput(
            int player,
            bool optionPressed,
            bool startPressed)
        {
            if (!IsEnabled(_activeProfile) || player < SunshinePlayerInput.MinPlayer || player > SunshinePlayerInput.MaxPlayer)
                return;

            lock (Sync)
            {
                PruneDisconnectedPlayers();

                bool wasPressed = RemoteOptionDown[player];
                RemoteOptionDown[player] = optionPressed;
                RemoteStartDown[player] = startPressed;

                if (optionPressed && !wasPressed)
                    ToggleOrAssignOwner(player);
            }
        }

        /// <summary>
        /// Observe local P1 directly from the IT control sender. This makes P1 behave
        /// exactly like remote P2-P4 for Options ownership without changing P1's normal
        /// cabinet controls.
        /// </summary>
        public static void ObserveLocalInput(bool optionPressed, bool startPressed)
        {
            if (!IsEnabled(_activeProfile))
            {
                lock (Sync)
                {
                    _localOptionDown = false;
                    _localStartDown = false;
                    if (_ownerPlayer == 1)
                        _ownerPlayer = 0;
                }
                return;
            }

            lock (Sync)
            {
                bool wasPressed = _localOptionDown;
                _localOptionDown = optionPressed;
                _localStartDown = startPressed;

                if (optionPressed && !wasPressed)
                    ToggleOrAssignOwner(1);
            }
        }

        public static void HandlePlayerDisconnected(int player)
        {
            if (player < SunshinePlayerInput.MinPlayer || player > SunshinePlayerInput.MaxPlayer)
                return;

            lock (Sync)
            {
                RemoteOptionDown[player] = false;
                RemoteStartDown[player] = false;

                if (_ownerPlayer == player)
                    _ownerPlayer = 0;
            }
        }

        /// <summary>
        /// While any physical player's Option is held, broadcast the Option bit to all
        /// four Golden Tee control words. This is intentionally only the single Option
        /// bit; normal control state and trackball buffers stay siloed.
        /// </summary>
        public static bool ShouldBroadcastOption()
        {
            if (!IsEnabled(_activeProfile))
                return false;

            lock (Sync)
            {
                PruneDisconnectedPlayers();

                if (_localOptionDown)
                    return true;

                for (int player = SunshinePlayerInput.MinPlayer; player <= SunshinePlayerInput.MaxPlayer; player++)
                {
                    if (RemoteOptionDown[player])
                        return true;
                }

                return false;
            }
        }

        /// <summary>
        /// Mirror only the current owner's physical Start into ControlHost.
        /// Their normal Start path remains active and unchanged in parallel.
        /// </summary>
        public static int GetOwnerPlayer()
        {
            lock (Sync)
            {
                PruneDisconnectedPlayers();
                return _ownerPlayer;
            }
        }

        public static bool ShouldMirrorStart()
        {
            if (!IsEnabled(_activeProfile))
                return false;

            lock (Sync)
            {
                PruneDisconnectedPlayers();

                if (_ownerPlayer == 1)
                {
                    bool legacyHostStartDown =
                        InputCode.StreamingPlayerDigitalButtons[6].Start.HasValue &&
                        InputCode.StreamingPlayerDigitalButtons[6].Start.Value;

                    return _localStartDown || legacyHostStartDown;
                }

                return _ownerPlayer >= SunshinePlayerInput.MinPlayer &&
                       _ownerPlayer <= SunshinePlayerInput.MaxPlayer &&
                       RemoteStartDown[_ownerPlayer];
            }
        }

        /// <summary>
        /// True only for the currently owning player. Local RawInputTrackball uses this
        /// for P1; SunshineInputListener uses it for P2-P4. Both mirror the event into
        /// HostTrackball without touching any other player's normal trackball buffer.
        /// </summary>
        public static bool ShouldMirrorTrackball(GameProfile profile, int player)
        {
            if (!IsEnabled(profile) || player < 1 || player > 4)
                return false;

            lock (Sync)
            {
                PruneDisconnectedPlayers();
                return _ownerPlayer == player;
            }
        }

        private static void ToggleOrAssignOwner(int player)
        {
            if (_ownerPlayer == player)
                _ownerPlayer = 0;
            else
                _ownerPlayer = player;

            Array.Clear(RemoteStartDown, 0, RemoteStartDown.Length);
        }

        private static void PruneDisconnectedPlayers()
        {
            var connected = SunshinePlayerInput.GetConnectedPlayers();

            for (int player = SunshinePlayerInput.MinPlayer; player <= SunshinePlayerInput.MaxPlayer; player++)
            {
                if (connected.Contains(player))
                    continue;

                RemoteOptionDown[player] = false;
                RemoteStartDown[player] = false;

                if (_ownerPlayer == player)
                    _ownerPlayer = 0;
            }
        }

        private static bool IsEnabled(GameProfile profile)
        {
            if (profile?.ConfigValues == null ||
                profile.EmulationProfile != EmulationProfile.IncredibleTechnologies)
            {
                return false;
            }

            string fileName = Path.GetFileName(profile.FileName ?? string.Empty);
            if (!fileName.StartsWith("GoldenTeeLive20", StringComparison.OrdinalIgnoreCase) ||
                !fileName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return profile.ConfigValues.Any(x =>
                string.Equals(x.FieldName, "Remote Local Play", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(x.FieldValue, "Off", StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsOptionMapping(int player, InputMapping mapping)
        {
            return player switch
            {
                2 => mapping == InputMapping.Stream2P1Button3,
                3 => mapping == InputMapping.Stream3P1Button3,
                4 => mapping == InputMapping.Stream4P1Button3,
                _ => false
            };
        }

        private static bool IsStartMapping(int player, InputMapping mapping)
        {
            return player switch
            {
                2 => mapping == InputMapping.Stream2P1ButtonStart,
                3 => mapping == InputMapping.Stream3P1ButtonStart,
                4 => mapping == InputMapping.Stream4P1ButtonStart,
                _ => false
            };
        }
    }
}