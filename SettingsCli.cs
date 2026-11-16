// Copyright (c) 2026 Nathan Kelly. Licensed under the MIT License; see the LICENSE file.

using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ExchangeAppPoolMonitor;

/// <summary>
/// Command line mode used by the installer to read and write the settings the installer asks for.
/// </summary>
/// <remarks>
/// <para>
/// The installer collects the email and monitoring settings on its own pages and keeps them in a
/// small INI file (key=value lines), because that is the format NSIS can read and write natively.
/// This class converts between that INI file and monitorsettings.json:
/// </para>
/// <list type="bullet">
///   <item><c>ExchangeAppPoolMonitor.exe --export-settings &lt;ini&gt;</c>: reads monitorsettings.json and
///         writes the installer's values to the INI file, so an upgrade shows the current settings.</item>
///   <item><c>ExchangeAppPoolMonitor.exe --import-settings &lt;ini&gt;</c>: writes the INI values into
///         monitorsettings.json.</item>
/// </list>
/// <para>
/// Import only changes the settings the installer asks about. Everything else in the file (pool list,
/// cooldown, log settings, anything added by hand) is kept as it is. The JSON is edited as a tree
/// rather than through the settings classes so that nothing unknown is dropped.
/// </para>
/// <para>
/// INI keys: To, From, SmtpServer, Port, EnableSsl, Username, Password, PollIntervalSeconds,
/// Event1309IntervalSeconds, QueueThreshold, LongRunningThreshold, DryRun. Booleans are 1 or 0.
/// The INI file is written as UTF-16 with a byte order mark, which is what NSIS on Windows expects,
/// and read in any encoding with a byte order mark (or UTF-8 without one).
/// </para>
/// <para>Exit codes: 0 success, 1 file problem, 2 invalid value. Errors are printed to standard output.</para>
/// </remarks>
public static class SettingsCli
{
    private const string Section = "Settings";

    /// <summary>The settings file the running exe uses: monitorsettings.json in the exe folder.</summary>
    private static string SettingsPath => Path.Combine(AppContext.BaseDirectory, "monitorsettings.json");

    /// <summary>
    /// If the command line asks for a settings operation, runs it and returns true with the exit code.
    /// Returns false for a normal start (run the service).
    /// </summary>
    public static bool TryRun(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length < 1) return false;

        var mode = args[0].ToLowerInvariant();
        if (mode != "--export-settings" && mode != "--import-settings") return false;

        if (args.Length < 2)
        {
            Console.WriteLine($"Usage: ExchangeAppPoolMonitor.exe {args[0]} <ini file> [settings json]");
            exitCode = 1;
            return true;
        }
        var ini = args[1];
        var json = args.Length >= 3 ? args[2] : SettingsPath;

        try
        {
            if (mode == "--export-settings") Export(json, ini);
            else Import(json, ini);
        }
        catch (FormatException ex)
        {
            Console.WriteLine(ex.Message);
            exitCode = 2;
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            exitCode = 1;
        }
        return true;
    }

    /// <summary>Reads the installer's values from monitorsettings.json into the INI file.</summary>
    private static void Export(string jsonPath, string iniPath)
    {
        var root = Load(jsonPath);
        var monitor = root["Monitor"] as JsonObject;
        var email = root["Email"] as JsonObject;
        var defaults = new MonitorSettings();
        var mailDefaults = new EmailSettings();

        // Missing values fall back to the same defaults the service would use.
        var to = email?["To"] is JsonArray arr
            ? string.Join("; ", arr.Select(n => n?.ToString()).Where(v => !string.IsNullOrWhiteSpace(v)))
            : "";

        var values = new Dictionary<string, string>
        {
            ["To"] = to,
            ["From"] = Str(email, "From", mailDefaults.From),
            ["SmtpServer"] = Str(email, "SmtpServer", mailDefaults.SmtpServer),
            ["Port"] = Int(email, "Port", mailDefaults.Port).ToString(),
            ["EnableSsl"] = Bool(email, "EnableSsl", mailDefaults.EnableSsl) ? "1" : "0",
            ["Username"] = Str(email, "Username", mailDefaults.Username),
            ["Password"] = Str(email, "Password", mailDefaults.Password),
            ["PollIntervalSeconds"] = Int(monitor, "PollIntervalSeconds", defaults.PollIntervalSeconds).ToString(),
            ["Event1309IntervalSeconds"] = Int(monitor?["Event1309"] as JsonObject, "CheckIntervalSeconds", defaults.Event1309.CheckIntervalSeconds).ToString(),
            ["QueueThreshold"] = Int(monitor, "QueueThreshold", defaults.QueueThreshold).ToString(),
            ["LongRunningThreshold"] = Int(monitor?["LongRunningRequests"] as JsonObject, "Threshold", defaults.LongRunningRequests.Threshold).ToString(),
            ["DryRun"] = Bool(monitor, "DryRun", defaults.DryRun) ? "1" : "0",
        };

        var sb = new StringBuilder();
        sb.AppendLine($"[{Section}]");
        foreach (var (k, v) in values) sb.AppendLine($"{k}={v}");
        File.WriteAllText(iniPath, sb.ToString(), Encoding.Unicode); // UTF-16 LE with BOM, for NSIS
    }

    /// <summary>Writes the INI values into monitorsettings.json, keeping every other setting.</summary>
    private static void Import(string jsonPath, string iniPath)
    {
        var ini = ReadIni(iniPath);
        var root = Load(jsonPath);
        var monitor = Child(root, "Monitor");
        var email = Child(root, "Email");

        // Recipients: separated by semicolons, commas or spaces. No recipients turns email off.
        if (ini.TryGetValue("To", out var toText))
        {
            var to = toText.Split(new[] { ';', ',', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            var invalid = to.FirstOrDefault(address => !address.Contains('@'));
            if (invalid is not null) throw new FormatException($"'{invalid}' is not an email address.");
            email["To"] = new JsonArray(to.Select(a => (JsonNode?)JsonValue.Create(a)).ToArray());
            email["Enabled"] = to.Length > 0;
        }
        SetString(email, ini, "From", "From");
        SetString(email, ini, "SmtpServer", "SmtpServer");
        SetInt(email, ini, "Port", "Port", 1, 65535);
        SetBool(email, ini, "EnableSsl", "EnableSsl");
        SetString(email, ini, "Username", "Username");
        SetString(email, ini, "Password", "Password");

        SetInt(monitor, ini, "PollIntervalSeconds", "PollIntervalSeconds", 2, 86400);
        SetInt(Child(monitor, "Event1309"), ini, "Event1309IntervalSeconds", "CheckIntervalSeconds", 2, 86400);
        SetInt(monitor, ini, "QueueThreshold", "QueueThreshold", 0, int.MaxValue);
        SetInt(Child(monitor, "LongRunningRequests"), ini, "LongRunningThreshold", "Threshold", 0, int.MaxValue);
        SetBool(monitor, ini, "DryRun", "DryRun");

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            // Keep characters such as + and ' readable in addresses and passwords instead of +.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        // Write to a temporary file first, then swap it in, so a failure cannot leave a half written file.
        var temp = jsonPath + ".tmp";
        File.WriteAllText(temp, root.ToJsonString(options) + Environment.NewLine, new UTF8Encoding(false));
        File.Move(temp, jsonPath, overwrite: true);
    }

    // ------------------------------------------------------------------ helpers

    private static JsonObject Load(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"Settings file not found: {path}");
        var node = JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip, // the .NET settings loader accepts comments too
            AllowTrailingCommas = true,
        });
        return node as JsonObject ?? throw new FormatException($"{path} does not contain a JSON object.");
    }

    /// <summary>Returns the child object with this name, creating it if it is missing.</summary>
    private static JsonObject Child(JsonObject parent, string name)
    {
        if (parent[name] is JsonObject existing) return existing;
        var created = new JsonObject();
        parent[name] = created;
        return created;
    }

    private static Dictionary<string, string> ReadIni(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? section = null;
        // detectEncodingFromByteOrderMarks handles the UTF-16 files NSIS writes, and UTF-8.
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        while (reader.ReadLine() is { } rawLine)
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith(';')) continue;
            if (line.StartsWith('[') && line.EndsWith(']')) { section = line[1..^1].Trim(); continue; }
            if (!string.Equals(section, Section, StringComparison.OrdinalIgnoreCase)) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            values[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        return values;
    }

    private static void SetString(JsonObject target, Dictionary<string, string> ini, string key, string property)
    {
        if (ini.TryGetValue(key, out var v)) target[property] = v;
    }

    private static void SetInt(JsonObject target, Dictionary<string, string> ini, string key, string property, int min, int max)
    {
        if (!ini.TryGetValue(key, out var v)) return;
        if (!int.TryParse(v, out var n) || n < min || n > max)
            throw new FormatException($"{key} must be a whole number from {min} to {max} (got '{v}').");
        target[property] = n;
    }

    private static void SetBool(JsonObject target, Dictionary<string, string> ini, string key, string property)
    {
        if (ini.TryGetValue(key, out var v)) target[property] = v is "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    private static string Str(JsonObject? o, string name, string fallback) =>
        o?[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : fallback;

    private static int Int(JsonObject? o, string name, int fallback) =>
        o?[name] is JsonValue v && v.TryGetValue<int>(out var n) ? n : fallback;

    private static bool Bool(JsonObject? o, string name, bool fallback) =>
        o?[name] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : fallback;
}
