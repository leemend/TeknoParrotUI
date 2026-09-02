using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using TeknoParrotUi.Common.InputListening;

namespace TeknoParrotUi.Common
{
    /// <summary>
    /// Persistent Golden Tee appearance/equipment defaults for Sunshine clients.
    /// Local cabinet P2/P3/P4 values remain in the normal UserProfiles XML.
    /// </summary>
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
            public Dictionary<string, string> Values { get; set; } =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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

        public static bool IsAppearanceField(
            FieldInformation field,
            out int player,
            out string appearanceName)
        {
            player = 0;
            appearanceName = string.Empty;

            if (field == null || string.IsNullOrWhiteSpace(field.FieldName))
                return false;

            for (var candidate = SunshinePlayerInput.MinPlayer;
                 candidate <= SunshinePlayerInput.MaxPlayer;
                 candidate++)
            {
                var prefix = $"P{candidate} Default ";
                if (!field.FieldName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                var suffix = field.FieldName.Substring(prefix.Length);
                var canonical = AppearanceNames.FirstOrDefault(name =>
                    string.Equals(name, suffix, StringComparison.OrdinalIgnoreCase));

                if (canonical == null)
                    return false;

                player = candidate;
                appearanceName = canonical;
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
            var path = GetPath(clientUuid);

            try
            {
                if (File.Exists(path))
                {
                    var existing = JsonSerializer.Deserialize<RemoteAppearance>(
                        File.ReadAllText(path));

                    if (existing != null)
                    {
                        existing.ClientUuid = clientUuid;
                        existing.Values = existing.Values == null
                            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                            : new Dictionary<string, string>(
                                existing.Values,
                                StringComparer.OrdinalIgnoreCase);

                        SeedMissingValues(existing, profile, player);
                        return existing;
                    }
                }
            }
            catch
            {
                // Malformed remote data falls back safely to this slot's local defaults.
            }

            var created = new RemoteAppearance
            {
                ClientUuid = clientUuid
            };

            SeedMissingValues(created, profile, player);
            return created;
        }

        public static void Save(RemoteAppearance appearance)
        {
            if (appearance == null || string.IsNullOrWhiteSpace(appearance.ClientUuid))
                return;

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
                    if (!IsAppearanceField(
                            field,
                            out var fieldPlayer,
                            out var appearanceName) ||
                        fieldPlayer != player)
                    {
                        continue;
                    }

                    originalValues[field] = field.FieldValue;

                    if (remote.Values.TryGetValue(appearanceName, out var remoteValue))
                        field.FieldValue = remoteValue;
                }
            }

            return new RestoreDisposable(originalValues);
        }

        private static void SeedMissingValues(
            RemoteAppearance appearance,
            GameProfile profile,
            int player)
        {
            if (profile?.ConfigValues == null)
                return;

            foreach (var field in profile.ConfigValues)
            {
                if (!IsAppearanceField(
                        field,
                        out var fieldPlayer,
                        out var appearanceName) ||
                    fieldPlayer != player)
                {
                    continue;
                }

                if (!appearance.Values.ContainsKey(appearanceName))
                    appearance.Values[appearanceName] = field.FieldValue ?? string.Empty;
            }
        }

        private static string GetStorageRoot()
        {
            return Path.Combine("UserProfiles", StorageDirectory);
        }

        private static string GetPath(string clientUuid)
        {
            return Path.Combine(
                GetStorageRoot(),
                NormalizeUuid(clientUuid) + ".json");
        }

        private static string NormalizeUuid(string clientUuid)
        {
            if (Guid.TryParse(clientUuid, out var guid))
                return guid.ToString("D").ToUpperInvariant();

            var chars = clientUuid
                .Where(ch => char.IsLetterOrDigit(ch) || ch == '-' || ch == '_')
                .ToArray();

            var normalized = new string(chars);
            return string.IsNullOrWhiteSpace(normalized)
                ? "UNKNOWN"
                : normalized.ToUpperInvariant();
        }

        private sealed class RestoreDisposable : IDisposable
        {
            private Dictionary<FieldInformation, string> _originalValues;

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
            public static readonly NoopDisposable Instance = new NoopDisposable();
            public void Dispose()
            {
            }
        }
    }
}
