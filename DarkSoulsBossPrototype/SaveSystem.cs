using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DarkSoulsBossPrototype
{
    // -------------------------------------------------------------------------
    // SaveData
    // Holds all player metrics that persist across sessions.
    // -------------------------------------------------------------------------
    public class SaveData
    {
        // --- Combat behaviour metrics ----------------------------------------

        /// <summary>Total number of rolls the player has performed across all fights.</summary>
        public int PlayerRollCount { get; set; } = 0;

        /// <summary>Total number of blocks the player has performed across all fights.</summary>
        public int PlayerBlockCount { get; set; } = 0;

        /// <summary>Total number of ranged attacks the player has fired across all fights.</summary>
        public int PlayerRangedAttackCount { get; set; } = 0;

        /// <summary>Total number of times the player has died to the boss.</summary>
        public int PlayerDeathsToBoss { get; set; } = 0;

        // --- Progression -----------------------------------------------------

        /// <summary>Skill IDs the player has unlocked (e.g. "SKILL_PARRY", "SKILL_DASH_STRIKE").</summary>
        public List<string> UnlockedSkillIDs { get; set; } = new List<string>();

        // --- Convenience helpers (not serialised) ----------------------------

        /// <summary>
        /// Returns a dominant play-style tag based on the highest relative metric.
        /// Useful for feeding directly into boss AI decision logic.
        /// </summary>
        [JsonIgnore]
        public string DominantPlayStyle
        {
            get
            {
                int roll   = PlayerRollCount;
                int block  = PlayerBlockCount;
                int ranged = PlayerRangedAttackCount;

                if (roll >= block && roll >= ranged)   return "Evader";
                if (block >= roll && block >= ranged)  return "Blocker";
                return "Ranged";
            }
        }

        /// <summary>Shorthand: how many unique skills the player has unlocked.</summary>
        [JsonIgnore]
        public int SkillCount => UnlockedSkillIDs?.Count ?? 0;
    }

    // -------------------------------------------------------------------------
    // SaveSystem
    // Static helper that owns all file I/O. Call SaveSystem.Load() on startup
    // and SaveSystem.Save() whenever a metric changes.
    // -------------------------------------------------------------------------
    public static class SaveSystem
    {
        // ------------------------------------------------------------------
        // Configuration
        // ------------------------------------------------------------------

        /// <summary>
        /// Path to the save file. Defaults to the same directory as the
        /// executable so it works out-of-the-box in MonoGame / Visual Studio.
        /// Override before calling Load() if you want a different location
        /// (e.g. Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)).
        /// </summary>
        public static string SaveFilePath { get; set; } =
            Path.Combine(AppContext.BaseDirectory, "player_data.json");

        // Shared serialiser options — pretty-print for readability, safe enum handling
        private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented          = true,
            PropertyNameCaseInsensitive = true,     // tolerates minor manual edits
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        // ------------------------------------------------------------------
        // Public API
        // ------------------------------------------------------------------

        /// <summary>
        /// Loads player data from disk. If the file does not exist, or is
        /// corrupt / empty, a fresh SaveData is returned so the game can
        /// always start cleanly.
        /// </summary>
        public static SaveData Load()
        {
            try
            {
                if (!File.Exists(SaveFilePath))
                {
                    Console.WriteLine($"[SaveSystem] No save file found at '{SaveFilePath}'. Starting fresh.");
                    return new SaveData();
                }

                string json = File.ReadAllText(SaveFilePath);

                if (string.IsNullOrWhiteSpace(json))
                {
                    Console.WriteLine("[SaveSystem] Save file is empty. Starting fresh.");
                    return new SaveData();
                }

                SaveData data = JsonSerializer.Deserialize<SaveData>(json, _jsonOptions);

                if (data == null)
                {
                    Console.WriteLine("[SaveSystem] Deserialisation returned null. Starting fresh.");
                    return new SaveData();
                }

                // Guard: ensure the list is never null after a partial write
                data.UnlockedSkillIDs ??= new List<string>();

                Console.WriteLine($"[SaveSystem] Save loaded — Deaths: {data.PlayerDeathsToBoss}, " +
                                  $"Rolls: {data.PlayerRollCount}, Blocks: {data.PlayerBlockCount}, " +
                                  $"Ranged: {data.PlayerRangedAttackCount}, " +
                                  $"Skills: {data.SkillCount}, Style: {data.DominantPlayStyle}");
                return data;
            }
            catch (JsonException ex)
            {
                Console.WriteLine($"[SaveSystem] JSON parse error — {ex.Message}. Starting fresh.");
                BackupCorruptFile();
                return new SaveData();
            }
            catch (IOException ex)
            {
                Console.WriteLine($"[SaveSystem] File read error — {ex.Message}. Starting fresh.");
                return new SaveData();
            }
        }

        /// <summary>
        /// Serialises <paramref name="data"/> to disk atomically:
        /// writes to a temp file first, then replaces the real file,
        /// so a crash mid-write never corrupts the save.
        /// </summary>
        public static bool Save(SaveData data)
        {
            if (data == null)
            {
                Console.WriteLine("[SaveSystem] Save() called with null data — skipped.");
                return false;
            }

            try
            {
                // Ensure the directory exists (relevant when SaveFilePath points
                // to a subfolder such as %AppData%\SoulsBossGame\)
                string directory = Path.GetDirectoryName(SaveFilePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    Directory.CreateDirectory(directory);

                string tempPath = SaveFilePath + ".tmp";
                string json     = JsonSerializer.Serialize(data, _jsonOptions);

                File.WriteAllText(tempPath, json);           // write to temp
                File.Move(tempPath, SaveFilePath, overwrite: true); // atomic replace

                Console.WriteLine($"[SaveSystem] Save written to '{SaveFilePath}'.");
                return true;
            }
            catch (IOException ex)
            {
                Console.WriteLine($"[SaveSystem] Failed to write save — {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Permanently removes the save file. Useful for a "New Game" option.
        /// </summary>
        public static bool DeleteSave()
        {
            try
            {
                if (File.Exists(SaveFilePath))
                {
                    File.Delete(SaveFilePath);
                    Console.WriteLine("[SaveSystem] Save file deleted.");
                }
                return true;
            }
            catch (IOException ex)
            {
                Console.WriteLine($"[SaveSystem] Failed to delete save — {ex.Message}");
                return false;
            }
        }

        /// <summary>Returns true if a save file already exists on disk.</summary>
        public static bool SaveExists() => File.Exists(SaveFilePath);

        // ------------------------------------------------------------------
        // Private helpers
        // ------------------------------------------------------------------

        /// <summary>
        /// Renames a corrupt save file instead of deleting it, giving the
        /// player a chance to recover their data manually.
        /// </summary>
        private static void BackupCorruptFile()
        {
            try
            {
                if (!File.Exists(SaveFilePath)) return;

                string backupPath = SaveFilePath + $".corrupt_{DateTime.Now:yyyyMMdd_HHmmss}";
                File.Move(SaveFilePath, backupPath, overwrite: false);
                Console.WriteLine($"[SaveSystem] Corrupt save backed up to '{backupPath}'.");
            }
            catch (IOException ex)
            {
                Console.WriteLine($"[SaveSystem] Could not back up corrupt file — {ex.Message}");
            }
        }
    }
}
