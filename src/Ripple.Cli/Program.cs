using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Ripple.Core.Services;

namespace Ripple.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintUsage();
            return 0;
        }

        var settings = new SettingsStore().Load();
        var port = int.TryParse(GetOpt(args, "--port"), out var p) ? p : settings.LocalApiPort;
        var token = GetOpt(args, "--token") ?? settings.LocalApiToken;

        using var client = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}"),
            Timeout = TimeSpan.FromSeconds(5)
        };
        if (!string.IsNullOrEmpty(token))
        {
            client.DefaultRequestHeaders.Add("X-Ripple-Token", token);
        }

        var cmd = args[0].ToLowerInvariant();
        try
        {
            switch (cmd)
            {
                case "push":
                {
                    var title = GetOpt(args, "--title") ?? (args.Length > 1 && !args[1].StartsWith("--") ? args[1] : null);
                    if (string.IsNullOrWhiteSpace(title))
                    {
                        Console.Error.WriteLine("Error: --title is required.");
                        return 1;
                    }
                    var subtitle = GetOpt(args, "--subtitle");
                    var icon = GetOpt(args, "--icon") ?? "✦";
                    var accent = GetOpt(args, "--accent") ?? "#6C8CFF";
                    var priority = GetOpt(args, "--priority") ?? "Normal";
                    var durationSec = ParseDurationSeconds(GetOpt(args, "--duration") ?? "4s");
                    double? progress = double.TryParse(GetOpt(args, "--progress"), out var prog) ? prog : null;

                    var res = await client.PostAsJsonAsync("/api/v1/push", new ApiPushRequest(
                        title, subtitle, icon, accent, durationSec, progress, priority, GetOpt(args, "--key")));
                    Console.WriteLine(await res.Content.ReadAsStringAsync());
                    return res.IsSuccessStatusCode ? 0 : 1;
                }

                case "timer":
                {
                    var raw = args.Length > 1 ? args[1] : "25m";
                    var mins = Math.Max(1, ParseDurationMinutes(raw));
                    var label = GetOpt(args, "--label");
                    var res = await client.PostAsJsonAsync("/api/v1/timer", new ApiTimerRequest(mins, label));
                    Console.WriteLine(await res.Content.ReadAsStringAsync());
                    return res.IsSuccessStatusCode ? 0 : 1;
                }

                case "agent":
                {
                    var agent = GetOpt(args, "--name") ?? "Claude Code";
                    var status = GetOpt(args, "--status") ?? "running";
                    var title = GetOpt(args, "--title") ?? $"{agent} {status}";
                    var detail = GetOpt(args, "--detail");
                    var actionId = GetOpt(args, "--action-id");
                    int? tokens = int.TryParse(GetOpt(args, "--tokens"), out var tok) ? tok : null;

                    var res = await client.PostAsJsonAsync("/api/v1/agent/event", new ApiAgentEventRequest(
                        agent, status, title, detail, actionId, tokens));
                    Console.WriteLine(await res.Content.ReadAsStringAsync());
                    return res.IsSuccessStatusCode ? 0 : 1;
                }

                case "profile":
                {
                    var name = args.Length > 1 ? args[1] : GetOpt(args, "--name");
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        Console.Error.WriteLine("Usage: ripple profile <Work|Gaming|Presentation|Study|Music Party>");
                        return 1;
                    }
                    var res = await client.PostAsJsonAsync("/api/v1/profile", new { name });
                    Console.WriteLine(await res.Content.ReadAsStringAsync());
                    return res.IsSuccessStatusCode ? 0 : 1;
                }

                case "status":
                {
                    var res = await client.GetAsync("/api/v1/status");
                    Console.WriteLine(await res.Content.ReadAsStringAsync());
                    return res.IsSuccessStatusCode ? 0 : 1;
                }

                default:
                    PrintUsage();
                    return 1;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not reach Ripple at 127.0.0.1:{port}: {ex.Message}");
            return 2;
        }
    }

    private static string? GetOpt(string[] args, string flag)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }
        return null;
    }

    private static int ParseDurationSeconds(string raw)
    {
        var m = Regex.Match(raw.Trim(), @"^(\d+)\s*(s|m|h)?$", RegexOptions.IgnoreCase);
        if (!m.Success) return 4;
        var v = int.Parse(m.Groups[1].Value);
        return m.Groups[2].Value.ToLowerInvariant() switch
        {
            "m" => v * 60,
            "h" => v * 3600,
            _ => v
        };
    }

    private static int ParseDurationMinutes(string raw)
    {
        var m = Regex.Match(raw.Trim(), @"^(\d+)\s*(m|h|s)?$", RegexOptions.IgnoreCase);
        if (!m.Success) return 25;
        var v = int.Parse(m.Groups[1].Value);
        return m.Groups[2].Value.ToLowerInvariant() switch
        {
            "h" => v * 60,
            "s" => Math.Max(1, v / 60),
            _ => v
        };
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            Ripple CLI — Control your Windows Dynamic Notch from scripts & AI agents

            Usage:
              ripple push --title "Build done" [--subtitle "..."] [--icon "✓"] [--duration 4s] [--accent "#34D399"] [--priority high]
              ripple timer 25m [--label "Deep Work"]
              ripple agent --name "Claude Code" --status <running|permission|completed|error|idle> --title "..." [--detail "..."] [--tokens 1200]
              ripple profile <Work|Gaming|Presentation|Study|Music Party>
              ripple status
            """);
    }
}
