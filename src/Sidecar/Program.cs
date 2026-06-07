using McaDiff.Repo;
using McaHub.Sidecar;

// mcahub sidecar: watch a Minecraft world directory and auto-push backups to a hub — no CLI, no cron.
// Snapshots on a fixed interval and a debounced filesystem change, committing only when the world changed.
//
//   MCASIDE_WORLD     the world directory to watch (required)
//   MCASIDE_REMOTE    the hub world URL, e.g. http://host:5080/r/myworld (required)
//   MCASIDE_TOKEN     a write-scoped personal access token (required in accounts/token mode)
//   MCASIDE_REPO      local backup repo path (default: <world>.mcagit next to the world)
//   MCASIDE_BRANCH    branch to push (default: main)
//   MCASIDE_INTERVAL  seconds between scheduled backups (default: 300)
//   MCASIDE_AUTHOR    commit author (default: mcahub-sidecar)
//   MCASIDE_DEBOUNCE  seconds of filesystem quiet before an event-driven backup (default: 15)

static string? Env(string k) => Environment.GetEnvironmentVariable(k);
static string? Arg(string[] a, string flag) { int i = Array.IndexOf(a, flag); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }
static void Log(string m) => Console.WriteLine($"{DateTimeOffset.Now:HH:mm:ss} {m}");

string? world = Env("MCASIDE_WORLD") ?? Arg(args, "--world");
string? remote = Env("MCASIDE_REMOTE") ?? Arg(args, "--remote");
if (world is null || remote is null)
{
    Console.Error.WriteLine("set MCASIDE_WORLD (world dir) and MCASIDE_REMOTE (hub URL, e.g. http://host:5080/r/myworld).");
    return 2;
}
if (!Directory.Exists(world))
{
    Console.Error.WriteLine($"world directory not found: {world}");
    return 2;
}

string? token = Env("MCASIDE_TOKEN") ?? Arg(args, "--token");
string branch = Env("MCASIDE_BRANCH") ?? "main";
string author = Env("MCASIDE_AUTHOR") ?? "mcahub-sidecar";
string repoPath = Env("MCASIDE_REPO") ?? Arg(args, "--repo") ?? world.TrimEnd('/', '\\') + ".mcagit";
int interval = int.TryParse(Env("MCASIDE_INTERVAL"), out int iv) && iv > 0 ? iv : 300;
int debounce = int.TryParse(Env("MCASIDE_DEBOUNCE"), out int db) && db > 0 ? db : 15;

Repository repo = OpenOrInit(repoPath);
Log($"watching {world} → {remote} (every {interval}s, debounce {debounce}s)");

var gate = new object();
void TryBackup(string why)
{
    lock (gate) // serialize backups; a snapshot must not overlap a push
    {
        try
        {
            string? commit = Backup.Snapshot(repo, world, branch, author, $"auto: {why}");
            if (commit is null) { Log($"no changes ({why})"); return; }
            RemoteOps.Push(repo, remote, branch, force: false, token);
            Log($"backed up {commit[..10]} → {remote} ({why})");
        }
        catch (Exception e)
        {
            Log($"backup failed ({why}): {e.Message}"); // keep running — a hub blip shouldn't stop backups
        }
    }
}

// Debounce filesystem churn (a server save touches many files) into one backup once it goes quiet.
Timer? debounceTimer = null;
using var watcher = new FileSystemWatcher(world) { IncludeSubdirectories = true, EnableRaisingEvents = true };
void OnChange(object _, FileSystemEventArgs __) =>
    debounceTimer?.Change(TimeSpan.FromSeconds(debounce), Timeout.InfiniteTimeSpan);
debounceTimer = new Timer(_ => TryBackup("change"), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
watcher.Changed += OnChange; watcher.Created += OnChange; watcher.Deleted += OnChange; watcher.Renamed += OnChange;

using var scheduled = new Timer(_ => TryBackup("interval"), null, TimeSpan.FromSeconds(interval), TimeSpan.FromSeconds(interval));
TryBackup("startup");

var done = new ManualResetEventSlim();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.Set(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => done.Set(); // SIGTERM (docker stop / systemd)
done.Wait();
TryBackup("shutdown"); // one last backup on the way out
Log("stopped");
return 0;

static Repository OpenOrInit(string path)
{
    try { return Repository.Open(path); }
    catch { return Repository.Init(path); }
}
