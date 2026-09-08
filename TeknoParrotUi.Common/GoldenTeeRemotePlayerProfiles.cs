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

    private static readonly string[] AppearanceNames =
    {
        "Gender",
        "Face",
        "Shirt",
        "Bottoms",
        "Shoes",
        "Hat",
        "Bodysuit",
        "Clubs",
        "Balls"
    };

    public sealed class RemoteAppearance
    {
        public string ClientUuid { get; set; } = string.Empty;
        public string ProfileName { get; set; } = string.Empty;
        public string Initials { get; set; } = string.Empty;
        public Dictionary<string, string> Values { get; set; } =
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

        if (field == null || string.IsNullOrWhiteSpace(field.FieldName))
            return false;

        for (var candidate = 2; candidate <= 4; candidate++)
        {
            var prefix = $"P{candidate} Default ";
            if (!field.FieldName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var suffix = field.FieldName.Substring(prefix.Length);
            if (!AppearanceNames.Contains(suffix, StringComparer.OrdinalIgnoreCase))
                return false;

            player = candidate;
            appearanceName = AppearanceNames.First(x =>
                string.Equals(x, suffix, StringComparison.OrdinalIgnoreCase));
            return true;
        }

        return false;
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

        appearance.Values ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        appearance.Values =
            new Dictionary<string, string>(appearance.Values, StringComparer.OrdinalIgnoreCase);

        Directory.CreateDirectory(GetStorageRoot());

        File.WriteAllText(
            GetPath(appearance.ClientUuid),
            JsonSerializer.Serialize(
                appearance,
                new JsonSerializerOptions { WriteIndented = true }));
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

    private static void SeedMissingValues(
        RemoteAppearance appearance,
        GameProfile profile,
        int player)
    {
        appearance.Values ??=
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // A brand-new remote player must not inherit whichever local profile
        // happens to be assigned to P2/P3/P4 on the host. Seed the normal
        // Golden Tee defaults instead. Existing remote values are preserved.
        var defaults = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["Gender"] = "Male",
            ["Face"] = "1",
            ["Shirt"] = "1",
            ["Bottoms"] = "1",
            ["Shoes"] = "1",
            ["Hat"] = "0",
            ["Bodysuit"] = "0",
            ["Clubs"] = "0",
            ["Balls"] = "0"
        };

        foreach (var appearanceName in AppearanceNames)
        {
            if (!appearance.Values.ContainsKey(appearanceName) &&
                defaults.TryGetValue(appearanceName, out var defaultValue))
            {
                appearance.Values[appearanceName] = defaultValue;
            }
        }
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

            profile.Values ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            profile.Values =
                new Dictionary<string, string>(profile.Values, StringComparer.OrdinalIgnoreCase);
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