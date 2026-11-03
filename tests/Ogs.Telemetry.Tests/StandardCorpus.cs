/// <summary>The standard's conformance directory: beside this repo (local
/// development) or inside it (CI checks the standard out into the workspace).</summary>
static class StandardCorpus
{
    public static string Dir(string sub = "")
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Ogs.Telemetry.slnx")))
            dir = dir.Parent;
        if (dir is null) throw new InvalidOperationException("repo root (Ogs.Telemetry.slnx) not found");
        foreach (var root in new[] { dir.Parent!.FullName, dir.FullName })
        {
            var candidate = Path.Combine(root, "OGS-Telemetry-Standard", "conformance");
            if (Directory.Exists(candidate)) return Path.Combine(candidate, sub);
        }
        throw new InvalidOperationException("OGS-Telemetry-Standard checkout not found beside or inside the repo");
    }
}
