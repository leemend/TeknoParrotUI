#nullable enable annotations

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TeknoParrotUi.Common;

public static class GoldenTeeLocalPlayerProfiles
{
    private const string StorageDirectory = "GoldenTeeLocalPlayers";
    private const string AssignmentsFileName = "assignments.json";
    private const string LegacyAssignmentsFileName = "_assignments.json";

    public sealed class LocalPlayerProfile
    {
        public string ProfileName { get; set; } = string.Empty;
        public string Initials { get; set; } = string.Empty;
        public Dictionary<string, string> Values { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    public static string? NormalizeInitials(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var normalized = value.Trim().ToUpperInvariant();
        return normalized.Length == 3 && normalized.All(ch => ch is >= 'A' and <= 'Z')
            ? normalized
            : null;
    }

    public static string? NormalizeProfileName(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return normalized.Length == 0 ? null : normalized;
    }

    public static IReadOnlyList<string> ListProfileNames()
    {
        return LoadAll()
            .Select(x => x.ProfileName)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // Backward-compatible API name used by the current settings view.
    public static IReadOnlyList<string> ListProfileInitials() => ListProfileNames();

    public static LocalPlayerProfile? Load(string profileName)
    {
        var normalizedName = NormalizeProfileName(profileName);
        if (normalizedName == null)
            return null;

        return LoadAll().FirstOrDefault(x =>
            string.Equals(x.ProfileName, normalizedName, StringComparison.OrdinalIgnoreCase));
    }

    public static void Save(LocalPlayerProfile profile, string? previousProfileName = null)
    {
        if (profile == null)
            throw new ArgumentNullException(nameof(profile));

        var profileName = NormalizeProfileName(profile.ProfileName);
        if (profileName == null)
            throw new InvalidOperationException("Profile name is required.");

        var initials = NormalizeInitials(profile.Initials);
        if (initials == null)
            throw new InvalidOperationException("Initials must be exactly 3 letters (A-Z).");

        var previousName = NormalizeProfileName(previousProfileName);

        if (ProfileNameExists(
                profileName,
                ignoreLocalProfileName: previousName ?? profileName))
        {
            throw new InvalidOperationException(
                $"A Golden Tee profile named \"{profileName}\" already exists.");
        }

        profile.ProfileName = profileName;
        profile.Initials = initials;
        profile.Values ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        profile.Values = new Dictionary<string, string>(
            profile.Values,
            StringComparer.OrdinalIgnoreCase);

        Directory.CreateDirectory(GetStorageRoot());

        var newPath = GetPathForName(profileName);
        File.WriteAllText(
            newPath,
            JsonSerializer.Serialize(
                profile,
                new JsonSerializerOptions { WriteIndented = true }));

        if (previousName != null &&
            !string.Equals(previousName, profileName, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var file in Directory.EnumerateFiles(GetStorageRoot(), "*.json"))
            {
                if (IsAssignmentsFile(file) ||
                    string.Equals(file, newPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var loaded = TryReadProfile(file);
                if (loaded != null &&
                    string.Equals(
                        loaded.ProfileName,
                        previousName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(file); } catch { }
                }
            }

            var assignments = LoadAssignments();
            var changed = false;

            foreach (var player in assignments.Keys.ToList())
            {
                if (!string.Equals(
                        assignments[player],
                        previousName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                assignments[player] = profileName;
                changed = true;
            }

            if (changed)
                SaveAssignments(assignments);
        }

        // Remove duplicate/legacy copies representing the same current profile.
        foreach (var file in Directory.EnumerateFiles(GetStorageRoot(), "*.json"))
        {
            if (IsAssignmentsFile(file) ||
                string.Equals(file, newPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var loaded = TryReadProfile(file);
            if (loaded != null &&
                string.Equals(
                    loaded.ProfileName,
                    profileName,
                    StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(file); } catch { }
            }
        }
    }

    public static bool Delete(string profileName)
    {
        var normalizedName = NormalizeProfileName(profileName);
        if (normalizedName == null)
            return false;

        var deleted = false;
        if (Directory.Exists(GetStorageRoot()))
        {
            foreach (var file in Directory.EnumerateFiles(GetStorageRoot(), "*.json"))
            {
                if (IsAssignmentsFile(file))
                    continue;

                var loaded = TryReadProfile(file);
                if (loaded == null ||
                    !string.Equals(loaded.ProfileName, normalizedName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    File.Delete(file);
                    deleted = true;
                }
                catch { }
            }
        }

        var assignments = LoadAssignments();
        foreach (var player in assignments
                     .Where(x => string.Equals(x.Value, normalizedName, StringComparison.OrdinalIgnoreCase))
                     .Select(x => x.Key)
                     .ToList())
        {
            assignments.Remove(player);
        }
        SaveAssignments(assignments);

        return deleted;
    }

    public static bool ProfileNameExists(
        string profileName,
        string? ignoreLocalProfileName = null)
    {
        var normalized = NormalizeProfileName(profileName);
        if (normalized == null)
            return false;

        return ListProfileNames().Any(name =>
            !string.Equals(name, ignoreLocalProfileName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(name, normalized, StringComparison.OrdinalIgnoreCase));
    }

    public static Dictionary<int, string> LoadAssignments()
    {
        var path = GetAssignmentsPath();
        if (!File.Exists(path))
            return new Dictionary<int, string>();

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<Dictionary<int, string>>(json)
                   ?? new Dictionary<int, string>();
        }
        catch
        {
            return new Dictionary<int, string>();
        }
    }

    public static void SaveAssignments(IReadOnlyDictionary<int, string> assignments)
    {
        Directory.CreateDirectory(GetStorageRoot());

        var normalized = assignments
            .Where(x => x.Key is >= 1 and <= 4 && !string.IsNullOrWhiteSpace(x.Value))
            .ToDictionary(x => x.Key, x => x.Value.Trim());

        File.WriteAllText(
            GetAssignmentsPath(),
            JsonSerializer.Serialize(normalized, new JsonSerializerOptions { WriteIndented = true }));
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

        if (string.Equals(field.CategoryName, "Customization", StringComparison.OrdinalIgnoreCase))
        {
            player = 1;
            valueName = NormalizeValueName(field.FieldName, 1);
            return IsProfileValue(valueName);
        }

        for (var candidate = 2; candidate <= 4; candidate++)
        {
            if (!string.Equals(
                    field.CategoryName,
                    $"Player {candidate} Customization",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            player = candidate;
            valueName = NormalizeValueName(field.FieldName, candidate);
            return IsProfileValue(valueName);
        }

        return false;
    }

    private static bool IsProfileValue(string valueName)
    {
        return !string.Equals(valueName, "Override Default Initials", StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(valueName, "Default Initials", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeValueName(string fieldName, int player)
    {
        var name = fieldName ?? string.Empty;
        var prefix = $"P{player} ";
        if (player > 1 && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            name = name.Substring(prefix.Length);

        return name;
    }

    private static List<LocalPlayerProfile> LoadAll()
    {
        var result = new List<LocalPlayerProfile>();
        if (!Directory.Exists(GetStorageRoot()))
            return result;

        foreach (var file in Directory.EnumerateFiles(GetStorageRoot(), "*.json"))
        {
            if (IsAssignmentsFile(file))
                continue;

            var profile = TryReadProfile(file);
            if (profile != null)
                result.Add(profile);
        }

        return result;
    }

    private static LocalPlayerProfile? TryReadProfile(string file)
    {
        try
        {
            if (IsAssignmentsFile(file))
                return null;

            var profile = JsonSerializer.Deserialize<LocalPlayerProfile>(File.ReadAllText(file));
            if (profile == null)
                return null;

            var normalizedInitials = NormalizeInitials(profile.Initials);

            // A real legacy profile always had valid initials. If this JSON has
            // neither a profile name nor valid initials, it is not a player profile
            // (for example the legacy _assignments.json file).
            if (string.IsNullOrWhiteSpace(profile.ProfileName) &&
                normalizedInitials == null)
            {
                return null;
            }

            // Legacy local profiles used initials as their identity and had no ProfileName.
            if (string.IsNullOrWhiteSpace(profile.ProfileName))
                profile.ProfileName = normalizedInitials!;

            profile.ProfileName = profile.ProfileName.Trim();
            profile.Initials = normalizedInitials ?? string.Empty;
            profile.Values ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            profile.Values = new Dictionary<string, string>(profile.Values, StringComparer.OrdinalIgnoreCase);
            return profile;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsAssignmentsFile(string file)
    {
        var fileName = Path.GetFileName(file);

        return string.Equals(fileName, AssignmentsFileName, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(fileName, LegacyAssignmentsFileName, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetStorageRoot() =>
        Path.Combine("UserProfiles", StorageDirectory);

    private static string GetAssignmentsPath() =>
        Path.Combine(GetStorageRoot(), AssignmentsFileName);

    private static string GetPathForName(string profileName)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(profileName.Trim().ToUpperInvariant()));
        var key = Convert.ToHexString(bytes).Substring(0, 24);
        return Path.Combine(GetStorageRoot(), key + ".json");
    }
}