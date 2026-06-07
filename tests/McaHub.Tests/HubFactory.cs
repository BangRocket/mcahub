using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;

namespace McaHub.Tests;

public enum HubMode { Open, Token, Accounts }

/// <summary>
/// Boots the hub in-memory for tests against fresh temp directories, in a chosen auth mode.
/// Every instance owns isolated DataDir/CacheDir/MapDir/DbPath under the system temp dir
/// (removed on Dispose), and forces OAuth off so ambient MCAHUB_* env or a dev .env can't
/// leak a real provider into a test. Mode is driven purely through IConfiguration (UseSetting),
/// never process-wide env, so parallel tests don't race.
/// </summary>
public sealed class HubFactory : WebApplicationFactory<Program>
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mcahub-test-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly HubMode _mode;
    private readonly IEnumerable<KeyValuePair<string, string>>? _settings;
    private readonly List<string>? _logSink;

    public HubFactory(HubMode mode = HubMode.Open, string masterToken = "test-master-token",
        IEnumerable<KeyValuePair<string, string>>? settings = null, List<string>? logSink = null)
    {
        _mode = mode;
        MasterToken = masterToken;
        _settings = settings;
        _logSink = logSink;
    }

    /// <summary>The master token configured in <see cref="HubMode.Token"/> mode.</summary>
    public string MasterToken { get; }

    /// <summary>The repo data directory, so a test can seed an on-disk repo (e.g. a pre-accounts world).</summary>
    public string DataDir => Path.Combine(_root, "repos");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(_root);
        builder.UseSetting("urls", "http://localhost:5080"); // loopback so the #9 startup guard never trips under test
        builder.UseSetting("DataDir", Path.Combine(_root, "repos"));
        builder.UseSetting("CacheDir", Path.Combine(_root, "cache"));
        builder.UseSetting("MapDir", Path.Combine(_root, "maps"));
        builder.UseSetting("DbPath", Path.Combine(_root, "hub.json"));

        // Force OAuth off in every mode (we never exercise a real provider); empty id ⇒ oauth=false.
        builder.UseSetting("OAuthClientId", "");
        builder.UseSetting("OAuthClientSecret", "");
        // Explicit so ambient env can't flip the mode.
        builder.UseSetting("DevLogin", _mode == HubMode.Accounts ? "1" : "0");
        builder.UseSetting("PushToken", _mode == HubMode.Token ? MasterToken : "");

        // Per-test overrides (size caps, rate limits, …) applied last so they win.
        if (_settings is not null)
            foreach (KeyValuePair<string, string> kv in _settings)
                builder.UseSetting(kv.Key, kv.Value);

        if (_logSink is not null)
            builder.ConfigureLogging(lb =>
            {
                lb.SetMinimumLevel(LogLevel.Trace); // capture everything so a secret can't hide at Debug/Trace
                lb.AddProvider(new ListLoggerProvider(_logSink));
            });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }
}

/// <summary>Captures every formatted log message into a list, so a test can assert what is (not) logged.</summary>
internal sealed class ListLoggerProvider(List<string> sink) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new ListLogger(sink);
    public void Dispose() { }

    private sealed class ListLogger(List<string> sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            string line = formatter(state, exception) + (exception is null ? "" : " " + exception);
            lock (sink) sink.Add(line);
        }
    }
}
