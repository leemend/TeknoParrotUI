#nullable enable annotations

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using TeknoParrotUi.Common.InputListening;

namespace TeknoParrotUi.Common;

public static class GoldenTeeRemotePlayerProfiles
{
    private const string GoldenTeeProfileName = "GoldenTeeLive2019";
    private const string StorageDirectory = "GoldenTeeRemotePlayers";

    private static readonly Dictionary<string, string> CoreDefaultValues =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Override Default Outfit"] = "1",
            ["Default Gender"] = "Male",
            ["Default Face"] = "1",
            ["Default Shirt"] = "1",
            ["Default Bottoms"] = "1",
            ["Default Shoes"] = "1",
            ["Default Hat"] = "0",
            ["Default Bodysuit"] = "0",
            ["Default Outfit"] = "0",
            ["Default Clubs"] = "0",
            ["Default Balls"] = "0"
        };

    private static readonly Dictionary<string, string> LegacyValueNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Gender"] = "Default Gender",
            ["Face"] = "Default Face",
            ["Shirt"] = "Default Shirt",
            ["Bottoms"] = "Default Bottoms",
            ["Shoes"] = "Default Shoes",
            ["Hat"] = "Default Hat",
            ["Bodysuit"] = "Default Bodysuit",
            ["Clubs"] = "Default Clubs",
            ["Balls"] = "Default Balls"
        };

    public sealed class RemoteControlBinding
    {
        public XInputButton? XInputButton { get; set; }
        public RawDeviceType RawDeviceType { get; set; }
        public RawMouseButton MouseButton { get; set; }
        public Keys KeyboardKey { get; set; }
        public string BindNameXi { get; set; } = string.Empty;
    }

    public sealed class RemoteAppearance
    {
        public string ClientUuid { get; set; } = string.Empty;
        public string ProfileName { get; set; } = string.Empty;
        public string Initials { get; set; } = string.Empty;
        public Dictionary<string, string> Values { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, RemoteControlBinding> Bindings { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    public static bool IsGoldenTee(GameProfile profile)
    {
        return profile != null &&
               string.Equals(
                   profile.ProfileName,
                   GoldenTeeProfileName,
                   StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsRemoteLocalPlayOn(GameProfile profile)
    {
        return profile?.ConfigValues?.Any(field =>
            string.Equals(field.FieldName, "Remote Local Play", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(field.FieldValue, "On", StringComparison.OrdinalIgnoreCase)) == true;
    }

    public static IReadOnlyList<RemoteAppearance> ListProfiles()
    {
        var result = new List<RemoteAppearance>();
        if (!Directory.Exists(GetStorageRoot()))
            return result;

        foreach (var file in Directory.EnumerateFiles(GetStorageRoot(), "*.json"))
        {
            var loaded = TryRead(file);
            if (loaded != null)
                result.Add(loaded);
        }

        return result;
    }

    public static RemoteAppearance? Load(string clientUuid)
    {
        if (string.IsNullOrWhiteSpace(clientUuid))
            return null;

        return TryRead(GetPath(clientUuid));
    }

    public static void SyncPairedClients(IEnumerable<KeyValuePair<string, string>> pairedClients)
    {
        var clients = pairedClients?
            .Where(x => !string.IsNullOrWhiteSpace(x.Key))
            .Select(x => new KeyValuePair<string, string>(x.Key.Trim(), (x.Value ?? string.Empty).Trim()))
            .ToList()
            ?? new List<KeyValuePair<string, string>>();

        var pairedUuids = new HashSet<string>(
            clients.Select(x => NormalizeUuid(x.Key)),
            StringComparer.OrdinalIgnoreCase);

        foreach (var client in clients)
            EnsurePairedClient(client.Key, client.Value);

        if (!Directory.Exists(GetStorageRoot()))
            return;

        foreach (var file in Directory.EnumerateFiles(GetStorageRoot(), "*.json"))
        {
            var uuidFromFile = Path.GetFileNameWithoutExtension(file);
            if (pairedUuids.Contains(uuidFromFile))
                continue;

            try { File.Delete(file); } catch { }
        }
    }

    public static RemoteAppearance EnsurePairedClient(string clientUuid, string? connectionName)
    {
        var existing = Load(clientUuid);
        if (existing != null)
        {
            if (string.IsNullOrWhiteSpace(existing.ProfileName))
            {
                existing.ProfileName = MakeUniqueProfileName(
                    string.IsNullOrWhiteSpace(connectionName) ? "Moonlight Client" : connectionName.Trim(),
                    clientUuid);
                Save(existing);
            }

            return existing;
        }

        var profileName = MakeUniqueProfileName(
            string.IsNullOrWhiteSpace(connectionName) ? "Moonlight Client" : connectionName.Trim(),
            clientUuid);

        var created = new RemoteAppearance
        {
            ClientUuid = clientUuid.Trim(),
            ProfileName = profileName
        };

        Save(created);
        return created;
    }

    public static bool Rename(string clientUuid, string profileName)
    {
        if (string.IsNullOrWhiteSpace(clientUuid))
            return false;

        var normalizedName =
            GoldenTeeLocalPlayerProfiles.NormalizeProfileName(profileName);
        if (normalizedName == null)
            return false;

        var remote = Load(clientUuid);
        if (remote == null)
            return false;

        if (GoldenTeeLocalPlayerProfiles.ProfileNameExists(
                normalizedName,
                ignoreRemoteUuid: clientUuid))
        {
            return false;
        }

        remote.ProfileName = normalizedName;
        Save(remote);
        return true;
    }

    public static void Delete(string clientUuid)
    {
        if (string.IsNullOrWhiteSpace(clientUuid))
            return;

        try
        {
            var path = GetPath(clientUuid);
            if (File.Exists(path))
                File.Delete(path);
        }
        catch { }
    }

    public static bool IsAppearanceField(
        FieldInformation field,
        out int player,
        out string appearanceName)
    {
        player = 0;
        appearanceName = string.Empty;

        if (!GoldenTeeLocalPlayerProfiles.TryGetProfileField(
                field,
                out var profilePlayer,
                out var valueName))
        {
            return false;
        }

        if (profilePlayer < SunshinePlayerInput.MinPlayer ||
            profilePlayer > SunshinePlayerInput.MaxPlayer)
        {
            return false;
        }

        player = profilePlayer;
        appearanceName = valueName;
        return true;
    }

    public static bool TryGetActiveRemoteClient(
        GameProfile profile,
        int player,
        out string clientUuid)
    {
        clientUuid = string.Empty;

        if (!IsGoldenTee(profile) ||
            !IsRemoteLocalPlayOn(profile) ||
            player < SunshinePlayerInput.MinPlayer ||
            player > SunshinePlayerInput.MaxPlayer)
        {
            return false;
        }

        if (!SunshinePlayerInput.GetConnectedPlayers().Contains(player))
            return false;

        var uuid = SunshinePlayerInput.GetClientUuid(player);
        if (string.IsNullOrWhiteSpace(uuid))
            return false;

        clientUuid = uuid.Trim();
        return true;
    }

    public static RemoteAppearance LoadOrCreate(
        GameProfile profile,
        int player,
        string clientUuid)
    {
        var existing = Load(clientUuid);
        if (existing != null)
        {
            SeedMissingValues(existing, profile, player);
            return existing;
        }

        var created = new RemoteAppearance
        {
            ClientUuid = clientUuid.Trim()
        };

        SeedMissingValues(created, profile, player);
        return created;
    }

    public static void Save(RemoteAppearance appearance)
    {
        if (appearance == null || string.IsNullOrWhiteSpace(appearance.ClientUuid))
            return;

        appearance.ClientUuid = appearance.ClientUuid.Trim();
        appearance.ProfileName = (appearance.ProfileName ?? string.Empty).Trim();

        appearance.Initials =
            GoldenTeeLocalPlayerProfiles.NormalizeInitials(appearance.Initials) ?? string.Empty;

        NormalizeStoredValues(appearance);

        appearance.Bindings ??=
            new Dictionary<string, RemoteControlBinding>(StringComparer.OrdinalIgnoreCase);
        appearance.Bindings =
            new Dictionary<string, RemoteControlBinding>(
                appearance.Bindings,
                StringComparer.OrdinalIgnoreCase);

        Directory.CreateDirectory(GetStorageRoot());

        File.WriteAllText(
            GetPath(appearance.ClientUuid),
            JsonSerializer.Serialize(
                appearance,
                new JsonSerializerOptions { WriteIndented = true }));
    }

    public static IDisposable ApplyActiveControlBindingOverlay(
        GameProfile profile)
    {
        if (!IsGoldenTee(profile) ||
            !IsRemoteLocalPlayOn(profile))
        {
            return NoopDisposable.Instance;
        }

        var overlays = new List<IDisposable>();

        for (var player = SunshinePlayerInput.MinPlayer;
             player <= SunshinePlayerInput.MaxPlayer;
             player++)
        {
            if (!TryGetActiveRemoteClient(
                    profile,
                    player,
                    out var clientUuid))
            {
                continue;
            }

            overlays.Add(
                ApplyControlBindingOverlay(
                    profile,
                    player,
                    clientUuid));
        }

        return overlays.Count == 0
            ? NoopDisposable.Instance
            : new CompositeDisposable(overlays);
    }

    public static IDisposable ApplyControlBindingOverlay(
        GameProfile profile,
        int player,
        string clientUuid)
    {
        if (!IsGoldenTee(profile) ||
            string.IsNullOrWhiteSpace(clientUuid) ||
            player < SunshinePlayerInput.MinPlayer ||
            player > SunshinePlayerInput.MaxPlayer)
        {
            return NoopDisposable.Instance;
        }

        var originalValues =
            new Dictionary<JoystickButtons, RemoteControlState>();

        foreach (var binding in profile.JoystickButtons)
        {
            if (!TryGetRemoteControlName(
                    binding,
                    player,
                    out _))
            {
                continue;
            }

            originalValues[binding] =
                CaptureControlState(binding);
        }

        ApplyControlBindings(
            profile,
            player,
            clientUuid);

        return new ControlRestoreDisposable(originalValues);
    }
    public static void CaptureControlBindings(
        GameProfile profile,
        int player,
        string clientUuid)
    {
        if (!IsGoldenTee(profile) ||
            string.IsNullOrWhiteSpace(clientUuid) ||
            player < SunshinePlayerInput.MinPlayer ||
            player > SunshinePlayerInput.MaxPlayer)
        {
            return;
        }

        var remote = LoadOrCreate(profile, player, clientUuid);
        remote.Bindings.Clear();

        foreach (var binding in profile.JoystickButtons)
        {
            if (!TryGetRemoteControlName(binding, player, out var controlName))
                continue;

            var captured = CaptureControlBinding(binding);
            if (captured != null)
                remote.Bindings[controlName] = captured;
        }

        Save(remote);
    }

    public static void ApplyControlBindings(
        GameProfile profile,
        int player,
        string clientUuid)
    {
        if (!IsGoldenTee(profile) ||
            string.IsNullOrWhiteSpace(clientUuid) ||
            player < SunshinePlayerInput.MinPlayer ||
            player > SunshinePlayerInput.MaxPlayer)
        {
            return;
        }

        var remote = Load(clientUuid);
        if (remote == null)
            return;

        foreach (var binding in profile.JoystickButtons)
        {
            if (!TryGetRemoteControlName(binding, player, out var controlName))
                continue;

            ClearControlBinding(binding);

            if (!remote.Bindings.TryGetValue(controlName, out var saved))
                continue;

            if (saved.XInputButton != null)
            {
                binding.XInputButton = CloneXInputButton(saved.XInputButton);
                binding.BindNameXi = saved.BindNameXi ?? string.Empty;
            }

            if (saved.RawDeviceType != RawDeviceType.None)
            {
                binding.RawInputButton = new RawInputButton
                {
                    DevicePath = SunshinePlayerInput.DevicePathForPlayer(player),
                    DeviceType = saved.RawDeviceType,
                    MouseButton = saved.MouseButton,
                    KeyboardKey = saved.KeyboardKey
                };

                binding.BindNameRi =
                    BuildRemoteRawBindingName(player, saved);
            }

            binding.BindName =
                !string.IsNullOrWhiteSpace(binding.BindNameXi)
                    ? binding.BindNameXi
                    : binding.BindNameRi;
        }
    }
    public static IDisposable ApplyLaunchOverlay(GameProfile profile)
    {
        if (!IsGoldenTee(profile) || !IsRemoteLocalPlayOn(profile))
            return NoopDisposable.Instance;

        var originalValues = new Dictionary<FieldInformation, string>();

        for (var player = SunshinePlayerInput.MinPlayer;
             player <= SunshinePlayerInput.MaxPlayer;
             player++)
        {
            if (!TryGetActiveRemoteClient(profile, player, out var clientUuid))
                continue;

            var remote = LoadOrCreate(profile, player, clientUuid);

            foreach (var field in profile.ConfigValues)
            {
                if (IsAppearanceField(field, out var fieldPlayer, out var appearanceName) &&
                    fieldPlayer == player)
                {
                    originalValues[field] = field.FieldValue;
                    if (remote.Values.TryGetValue(appearanceName, out var remoteValue))
                        field.FieldValue = remoteValue;
                    continue;
                }

                var fieldName = field.FieldName ?? string.Empty;
                if (string.Equals(
                        fieldName,
                        $"P{player} Override Default Initials",
                        StringComparison.OrdinalIgnoreCase))
                {
                    originalValues[field] = field.FieldValue;
                    field.FieldValue = string.IsNullOrWhiteSpace(remote.Initials) ? "0" : "1";
                }
                else if (string.Equals(
                             fieldName,
                             $"P{player} Default Initials",
                             StringComparison.OrdinalIgnoreCase))
                {
                    originalValues[field] = field.FieldValue;
                    field.FieldValue = remote.Initials ?? string.Empty;
                }
            }
        }

        return new RestoreDisposable(originalValues);
    }

    private static void NormalizeStoredValues(
        RemoteAppearance appearance)
    {
        appearance.Values ??=
            new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);

        var normalized =
            new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);

        // Canonical values win if both a new key and its legacy alias exist.
        foreach (var pair in appearance.Values)
        {
            if (LegacyValueNames.ContainsKey(pair.Key))
                continue;

            normalized[pair.Key] =
                pair.Value ?? string.Empty;
        }

        foreach (var pair in appearance.Values)
        {
            if (!LegacyValueNames.TryGetValue(
                    pair.Key,
                    out var canonicalName))
            {
                continue;
            }

            if (!normalized.ContainsKey(canonicalName))
            {
                normalized[canonicalName] =
                    pair.Value ?? string.Empty;
            }
        }

        appearance.Values = normalized;
    }

    private static string GetDefaultProfileValue(
        FieldInformation field,
        string valueName)
    {
        if (CoreDefaultValues.TryGetValue(
                valueName,
                out var knownDefault))
        {
            return knownDefault;
        }

        if (field.FieldOptions != null &&
            field.FieldOptions.Count > 0)
        {
            return field.FieldOptions[0] ??
                   string.Empty;
        }

        return "0";
    }
    private static void SeedMissingValues(
        RemoteAppearance appearance,
        GameProfile profile,
        int player)
    {
        NormalizeStoredValues(appearance);

        if (profile?.ConfigValues == null)
            return;

        // Remote profiles use the same customization-field definition as local
        // profiles, but never seed a brand-new remote player from the local
        // P2/P3/P4 values currently selected on the host.
        foreach (var field in profile.ConfigValues)
        {
            if (!GoldenTeeLocalPlayerProfiles.TryGetProfileField(
                    field,
                    out var fieldPlayer,
                    out var valueName) ||
                fieldPlayer != player)
            {
                continue;
            }

            if (appearance.Values.ContainsKey(valueName))
                continue;

            appearance.Values[valueName] =
                GetDefaultProfileValue(
                    field,
                    valueName);
        }
    }

    private sealed class RemoteControlState
    {
        public XInputButton? XInputButton { get; init; }
        public RawInputButton? RawInputButton { get; init; }
        public string? BindName { get; init; }
        public string? BindNameXi { get; init; }
        public string? BindNameRi { get; init; }
    }

    private static RemoteControlState CaptureControlState(
        JoystickButtons binding)
    {
        return new RemoteControlState
        {
            XInputButton = binding.XInputButton == null
                ? null
                : CloneXInputButton(binding.XInputButton),
            RawInputButton = binding.RawInputButton == null
                ? null
                : CloneRawInputButton(binding.RawInputButton),
            BindName = binding.BindName,
            BindNameXi = binding.BindNameXi,
            BindNameRi = binding.BindNameRi
        };
    }

    private static void RestoreControlState(
        JoystickButtons binding,
        RemoteControlState state)
    {
        binding.XInputButton = state.XInputButton == null
            ? null
            : CloneXInputButton(state.XInputButton);

        binding.RawInputButton = state.RawInputButton == null
            ? null
            : CloneRawInputButton(state.RawInputButton);

        binding.BindName = state.BindName;
        binding.BindNameXi = state.BindNameXi;
        binding.BindNameRi = state.BindNameRi;
    }

    private static RawInputButton CloneRawInputButton(
        RawInputButton source)
    {
        return new RawInputButton
        {
            DevicePath = source.DevicePath,
            DeviceType = source.DeviceType,
            MouseButton = source.MouseButton,
            KeyboardKey = source.KeyboardKey
        };
    }
    private static bool TryGetRemoteControlName(
        JoystickButtons binding,
        int player,
        out string controlName)
    {
        controlName = string.Empty;

        if (binding == null ||
            string.IsNullOrWhiteSpace(binding.ButtonName))
        {
            return false;
        }

        var prefix = $"P{player} ";
        if (!binding.ButtonName.StartsWith(
                prefix,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        controlName = binding.ButtonName.Substring(prefix.Length).Trim();
        return !string.IsNullOrWhiteSpace(controlName);
    }

    private static RemoteControlBinding? CaptureControlBinding(
        JoystickButtons binding)
    {
        var hasRawInput =
            binding.RawInputButton != null &&
            binding.RawInputButton.DeviceType != RawDeviceType.None;

        var hasXInput = binding.XInputButton != null;

        if (!hasRawInput && !hasXInput)
            return null;

        return new RemoteControlBinding
        {
            XInputButton = hasXInput
                ? CloneXInputButton(binding.XInputButton!)
                : null,
            RawDeviceType = hasRawInput
                ? binding.RawInputButton!.DeviceType
                : RawDeviceType.None,
            MouseButton = hasRawInput
                ? binding.RawInputButton!.MouseButton
                : RawMouseButton.None,
            KeyboardKey = hasRawInput
                ? binding.RawInputButton!.KeyboardKey
                : Keys.None,
            BindNameXi = binding.BindNameXi ?? string.Empty
        };
    }

    private static XInputButton CloneXInputButton(XInputButton source)
    {
        return new XInputButton
        {
            IsLeftThumbX = source.IsLeftThumbX,
            IsRightThumbX = source.IsRightThumbX,
            IsLeftThumbY = source.IsLeftThumbY,
            IsRightThumbY = source.IsRightThumbY,
            IsAxisMinus = source.IsAxisMinus,
            IsLeftTrigger = source.IsLeftTrigger,
            IsRightTrigger = source.IsRightTrigger,
            ButtonCode = source.ButtonCode,
            IsButton = source.IsButton,
            ButtonIndex = source.ButtonIndex,

            // Sunshine's live GamepadSlot mapping is authoritative.
            // InputListenerXInput replaces this with the current slot at runtime.
            XInputIndex = 0
        };
    }

    private static void ClearControlBinding(JoystickButtons binding)
    {
        binding.XInputButton = null;
        binding.RawInputButton = null;
        binding.BindName = null;
        binding.BindNameXi = null;
        binding.BindNameRi = null;
    }

    private static string BuildRemoteRawBindingName(
        int player,
        RemoteControlBinding binding)
    {
        var deviceName =
            SunshinePlayerInput.DisplayNameForPlayer(player);

        if (binding.RawDeviceType == RawDeviceType.Keyboard)
            return $"{deviceName} Key {binding.KeyboardKey}";

        if (binding.RawDeviceType == RawDeviceType.Mouse &&
            binding.MouseButton != RawMouseButton.None)
        {
            return $"{deviceName} {binding.MouseButton}";
        }

        return deviceName;
    }
    private static string MakeUniqueProfileName(string seed, string clientUuid)
    {
        var baseName = string.IsNullOrWhiteSpace(seed) ? "Moonlight Client" : seed.Trim();
        var candidate = baseName;
        var suffix = 2;

        while (GoldenTeeLocalPlayerProfiles.ProfileNameExists(
                   candidate,
                   ignoreRemoteUuid: clientUuid))
        {
            candidate = $"{baseName} ({suffix++})";
        }

        return candidate;
    }

    private static RemoteAppearance? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;

            var profile =
                JsonSerializer.Deserialize<RemoteAppearance>(File.ReadAllText(path));
            if (profile == null)
                return null;

            profile.ClientUuid = string.IsNullOrWhiteSpace(profile.ClientUuid)
                ? Path.GetFileNameWithoutExtension(path)
                : profile.ClientUuid.Trim();

            // Leave legacy/missing names blank until Sunshine's paired roster
            // seeds the real Connection Name for this UUID.
            profile.ProfileName = (profile.ProfileName ?? string.Empty).Trim();

            profile.Initials =
                GoldenTeeLocalPlayerProfiles.NormalizeInitials(profile.Initials) ?? string.Empty;

            NormalizeStoredValues(profile);

            profile.Bindings ??=
                new Dictionary<string, RemoteControlBinding>(StringComparer.OrdinalIgnoreCase);
            profile.Bindings =
                new Dictionary<string, RemoteControlBinding>(
                    profile.Bindings,
                    StringComparer.OrdinalIgnoreCase);

            return profile;
        }
        catch
        {
            return null;
        }
    }

    private static string GetStorageRoot() =>
        Path.Combine("UserProfiles", StorageDirectory);

    private static string GetPath(string clientUuid) =>
        Path.Combine(GetStorageRoot(), NormalizeUuid(clientUuid) + ".json");

    private static string NormalizeUuid(string clientUuid)
    {
        if (Guid.TryParse(clientUuid, out var guid))
            return guid.ToString("D").ToUpperInvariant();

        var chars = (clientUuid ?? string.Empty)
            .Where(ch => char.IsLetterOrDigit(ch) || ch == '-' || ch == '_')
            .ToArray();

        var normalized = new string(chars);
        return string.IsNullOrWhiteSpace(normalized)
            ? "UNKNOWN"
            : normalized.ToUpperInvariant();
    }

    private sealed class CompositeDisposable : IDisposable
    {
        private List<IDisposable>? _items;

        public CompositeDisposable(
            List<IDisposable> items)
        {
            _items = items;
        }

        public void Dispose()
        {
            var items = _items;
            _items = null;

            if (items == null)
                return;

            for (var i = items.Count - 1;
                 i >= 0;
                 i--)
            {
                try
                {
                    items[i].Dispose();
                }
                catch
                {
                }
            }
        }
    }

    private sealed class ControlRestoreDisposable : IDisposable
    {
        private Dictionary<JoystickButtons, RemoteControlState>?
            _originalValues;

        public ControlRestoreDisposable(
            Dictionary<JoystickButtons, RemoteControlState>
                originalValues)
        {
            _originalValues = originalValues;
        }

        public void Dispose()
        {
            var values = _originalValues;
            _originalValues = null;

            if (values == null)
                return;

            foreach (var pair in values)
            {
                RestoreControlState(
                    pair.Key,
                    pair.Value);
            }
        }
    }
    private sealed class RestoreDisposable : IDisposable
    {
        private Dictionary<FieldInformation, string>? _originalValues;

        public RestoreDisposable(Dictionary<FieldInformation, string> originalValues)
        {
            _originalValues = originalValues;
        }

        public void Dispose()
        {
            var values = _originalValues;
            _originalValues = null;
            if (values == null)
                return;

            foreach (var pair in values)
                pair.Key.FieldValue = pair.Value;
        }
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static readonly NoopDisposable Instance = new();
        public void Dispose() { }
    }
}