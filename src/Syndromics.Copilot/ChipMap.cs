using System.Reflection;
using System.Text.Json.Nodes;

namespace Syndromics.Copilot;

/// <summary>Qubit positions and couplings of an IBM processor (ibm_kingston, Heron r2, 156 qubits, bundled).</summary>
public sealed class ChipMap
{
    public string Backend { get; }
    public IReadOnlyDictionary<int, (double X, double Y)> Pos { get; }
    public IReadOnlyList<(int A, int B)> Edges { get; }
    readonly Dictionary<int, List<int>> _nbr = new();

    ChipMap(string backend, Dictionary<int, (double, double)> pos, List<(int, int)> edges)
    {
        Backend = backend; Pos = pos; Edges = edges;
        foreach (var q in pos.Keys) _nbr[q] = new();
        foreach (var (a, b) in edges) { _nbr[a].Add(b); _nbr[b].Add(a); }
    }

    public static ChipMap Kingston()
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames().First(n => n.EndsWith("kingston_map.json"));
        using var s = asm.GetManifestResourceStream(name)!;
        var j = JsonNode.Parse(s)!;
        var pos = j["qubits"]!.AsObject().ToDictionary(k => int.Parse(k.Key),
            k => (k.Value![0]!.GetValue<double>(), k.Value![1]!.GetValue<double>()));
        var edges = j["edges"]!.AsArray().Select(e => (e![0]!.GetValue<int>(), e[1]!.GetValue<int>())).ToList();
        return new ChipMap(j["backend"]!.GetValue<string>(), pos, edges);
    }

    public bool Connected(int a, int b) => _nbr.TryGetValue(a, out var n) && n.Contains(b);

    /// <summary>A path of <paramref name="length"/> connected qubits that avoids <paramref name="avoid"/>,
    /// as close as possible to <paramref name="near"/>. Only avoids what was measured as bad: unmeasured qubits
    /// are not known to be good.</summary>
    public int[]? FindLine(int length, ISet<int> avoid, IReadOnlyCollection<int> near)
    {
        (double x, double y) c = near.Count > 0
            ? (near.Average(q => Pos[q].X), near.Average(q => Pos[q].Y)) : (8, 7);
        double D(int q) => Math.Pow(Pos[q].X - c.x, 2) + Math.Pow(Pos[q].Y - c.y, 2);
        int[]? best = null; double bestScore = double.MaxValue; int budget = 200_000;
        var path = new List<int>(); var used = new HashSet<int>();

        void Dfs(int q)
        {
            if (--budget < 0) return;
            path.Add(q); used.Add(q);
            if (path.Count == length)
            {
                var score = path.Sum(D) + 25 * path.Count(near.Contains);   // prefer fresh qubits near the old line
                if (score < bestScore) { bestScore = score; best = path.ToArray(); }
            }
            else
                foreach (var n in _nbr[q].OrderBy(D))
                    if (!used.Contains(n) && !avoid.Contains(n)) Dfs(n);
            path.RemoveAt(path.Count - 1); used.Remove(q);
        }
        foreach (var start in Pos.Keys.Where(q => !avoid.Contains(q)).OrderBy(D).Take(24)) Dfs(start);
        return best;
    }
}
