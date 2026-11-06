using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Brisk.Services;

public sealed record PowerPlan(Guid Id, string Name, bool Active);

/// <summary>Thin wrapper over powercfg.exe (ships with every Windows install).</summary>
public static partial class PowerPlans
{
    // Greedy (.+) so names that contain parentheses, e.g. "Ultimate Performance (ExitLag)", stay whole.
    [GeneratedRegex(@"([0-9a-fA-F\-]{36})\s+\((.+)\)\s*(\*)?\s*$", RegexOptions.Multiline)]
    private static partial Regex PlanLine();

    public static List<PowerPlan> List()
    {
        var output = Run("/list");
        var plans = new List<PowerPlan>();
        foreach (Match m in PlanLine().Matches(output))
            plans.Add(new PowerPlan(Guid.Parse(m.Groups[1].Value), m.Groups[2].Value.Trim(), m.Groups[3].Success));
        return plans;
    }

    public static bool SetActive(Guid id)
    {
        Run($"/setactive {id}");
        return List().Any(p => p.Id == id && p.Active);
    }

    private static string Run(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("powercfg.exe", args)
            {
                RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            var text = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return text;
        }
        catch { return ""; }
    }
}
