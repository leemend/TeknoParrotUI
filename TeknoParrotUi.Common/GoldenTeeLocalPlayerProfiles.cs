using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace TeknoParrotUi.Common
{
    /// <summary>
    /// Persistent named Golden Tee LOCAL player profiles.
    ///
    /// P1-P4 remain physical/runtime seats. A local person is identified by
    /// exactly three A-Z initials and can be assigned to any unclaimed seat.
    /// Sunshine-owned seats continue to use GoldenTeeRemotePlayerProfiles.
    /// </summary>
    public static class GoldenTeeLocalPlayerProfiles
    {
        private const string StorageDirectory = "GoldenTeeLocalPlayers";
        private const string AssignmentsFileName = "_assignments.json";

        private static readonly string[] ValueNames =
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

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true
        };

        public sealed class LocalPlayerProfile
        {
            public string Initials { get; set; } = string.Empty;

            public Dictionary<string, string> Values { get; set; } =
                new(StringComparer.OrdinalIgnoreCase);
        }

        public static string? NormalizeInitials(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            var normalized = value.Trim().ToUpperInvariant();

            if (normalized.Length != 3 ||
                normalized.Any(ch => ch < 'A' || ch > 'Z'))
            {
                return null;
            }

            return normalized;
        }

        public static IReadOnlyList<string> ListProfileInitials()
        {
            var directory = GetStorageDirectoryPath();
            if (!Directory.Exists(directory))
                return Array.Empty<string>();

            return Directory.GetFiles(directory, "*.json")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(name =>
                    !string.Equals(name, Path.GetFileNameWithoutExtension(AssignmentsFileName),
                        StringComparison.OrdinalIgnoreCase))
                .Select(NormalizeInitials)
                .Where(name => name != null)
                .Select(name => name!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static LocalPlayerProfile? Load(string initials)
        {
            var normalized = NormalizeInitials(initials);
            if (normalized == null)
                return null;

            var path = GetProfilePath(normalized);
            if (!File.Exists(path))
                return null;

            try
            {
                var json = File.ReadAllText(path);
                var profile = JsonSerializer.Deserialize<LocalPlayerProfile>(json, JsonOptions);
                if (profile == null)
                    return null;

                profile.Initials = normalized;
                profile.Values ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                // System.Text.Json may deserialize with the default comparer.
                profile.Values = new Dictionary<string, string>(
                    profile.Values,
                    StringComparer.OrdinalIgnoreCase);

                return profile;
            }
            catch
            {
                return null;
            }
        }

        public static void Save(LocalPlayerProfile profile)
        {
            ArgumentNullException.ThrowIfNull(profile);

            var normalized = NormalizeInitials(profile.Initials) ??
                throw new InvalidOperationException("Golden Tee local profile initials must be exactly 3 letters A-Z.");

            profile.Initials = normalized;
            profile.Values ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            Directory.CreateDirectory(GetStorageDirectoryPath());

            File.WriteAllText(
                GetProfilePath(normalized),
                JsonSerializer.Serialize(profile, JsonOptions));
        }

        public static Dictionary<int, string> LoadAssignments()
        {
            var path = GetAssignmentsPath();
            if (!File.Exists(path))
                return new Dictionary<int, string>();

            try
            {
                var json = File.ReadAllText(path);
                var stored = JsonSerializer.Deserialize<Dictionary<int, string>>(json, JsonOptions) ??
                    new Dictionary<int, string>();

                var result = new Dictionary<int, string>();

                foreach (var (player, initials) in stored)
                {
                    var normalized = NormalizeInitials(initials);
                    if (player is >= 1 and <= 4 && normalized != null)
                        result[player] = normalized;
                }

                return result;
            }
            catch
            {
                return new Dictionary<int, string>();
            }
        }

        public static void SaveAssignments(IReadOnlyDictionary<int, string> assignments)
        {
            Directory.CreateDirectory(GetStorageDirectoryPath());

            var normalized = new Dictionary<int, string>();

            foreach (var (player, initials) in assignments)
            {
                var value = NormalizeInitials(initials);
                if (player is >= 1 and <= 4 && value != null)
                    normalized[player] = value;
            }

            File.WriteAllText(
                GetAssignmentsPath(),
                JsonSerializer.Serialize(normalized, JsonOptions));
        }

        public static bool TryGetProfileField(
            FieldInformation field,
            out int player,
            out string valueName)
        {
            player = 0;
            valueName = string.Empty;

            if (field == null || string.IsNullOrWhiteSpace(field.FieldName))
                return false;

            // Player 1 uses the original "Customization" category and unprefixed
            // "Default X" field names.
            if (string.Equals(
                    field.CategoryName,
                    "Customization",
                    StringComparison.OrdinalIgnoreCase))
            {
                const string p1Prefix = "Default ";

                if (field.FieldName.StartsWith(p1Prefix, StringComparison.OrdinalIgnoreCase))
                {
                    var suffix = field.FieldName.Substring(p1Prefix.Length);
                    var canonical = CanonicalValueName(suffix);
                    if (canonical != null)
                    {
                        player = 1;
                        valueName = canonical;
                        return true;
                    }
                }
            }

            // P2-P4 use "P# Default X".
            for (var candidate = 2; candidate <= 4; candidate++)
            {
                var prefix = $"P{candidate} Default ";
                if (!field.FieldName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                var suffix = field.FieldName.Substring(prefix.Length);
                var canonical = CanonicalValueName(suffix);
                if (canonical == null)
                    return false;

                player = candidate;
                valueName = canonical;
                return true;
            }

            return false;
        }

        private static string? CanonicalValueName(string valueName)
        {
            return ValueNames.FirstOrDefault(name =>
                string.Equals(name, valueName, StringComparison.OrdinalIgnoreCase));
        }

        private static string GetStorageDirectoryPath() =>
            Path.Combine("UserProfiles", StorageDirectory);

        private static string GetAssignmentsPath() =>
            Path.Combine(GetStorageDirectoryPath(), AssignmentsFileName);

        private static string GetProfilePath(string normalizedInitials) =>
            Path.Combine(GetStorageDirectoryPath(), normalizedInitials + ".json");
    }
}
