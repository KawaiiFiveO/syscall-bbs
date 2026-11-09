using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace LastCallBBS.ModAPI;

/// <summary>A "Key = Value" settings file. Keys ignore case; lines starting with # or ; are comments.</summary>
public sealed class ModConfig
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<string> _warn;

    private ModConfig(Action<string> warn) => _warn = warn;

    /// <summary>Reads a file. A missing file gives an empty config, so every getter returns its fallback.</summary>
    public static ModConfig Load(string path, Action<string> warn = null)
    {
        var config = new ModConfig(warn);
        if (!File.Exists(path))
            return config;
        foreach (string raw in File.ReadAllLines(path))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";"))
                continue;
            int eq = line.IndexOf('=');
            if (eq > 0)
                config._values[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
        }
        return config;
    }

    public bool Contains(string key) => _values.ContainsKey(key);

    public string GetString(string key, string fallback = "") =>
        _values.TryGetValue(key, out string v) ? v : fallback;

    public bool GetBool(string key, bool fallback = false)
    {
        if (!_values.TryGetValue(key, out string v))
            return fallback;
        switch (v.ToLowerInvariant())
        {
            case "true": case "yes": case "1": case "on": return true;
            case "false": case "no": case "0": case "off": return false;
            default:
                _warn?.Invoke($"Setting {key} has unrecognised value \"{v}\"; using {fallback}.");
                return fallback;
        }
    }

    public int GetInt(string key, int fallback = 0)
    {
        if (!_values.TryGetValue(key, out string v))
            return fallback;
        if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
            return n;
        _warn?.Invoke($"Setting {key} has unrecognised value \"{v}\"; using {fallback}.");
        return fallback;
    }
}
