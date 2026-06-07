# Test Harness (#19) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stand up an xUnit + `WebApplicationFactory<Program>` test project with a `HubFactory` that boots the hub hermetically in any auth mode, plus a CI job — so every later security fix lands with a regression test.

**Architecture:** A new `tests/McaHub.Tests` project references the hub and boots it in-memory via `WebApplicationFactory<Program>`. A `HubFactory` drives the hub's mode (open / token / accounts) **purely through `IConfiguration` (`UseSetting`)** against fresh temp directories, so tests are hermetic and parallel-safe. A small config-first refactor in `Auth.Read`/`Program` makes dev-login, the proxy switch, and the master token controllable via config (matching how the dir paths already read). CI checks out the sibling `mcadiff` core pinned to a known commit.

**Tech Stack:** .NET 10, xUnit, `Microsoft.AspNetCore.Mvc.Testing`, GitHub Actions.

---

### Task 1: Expose the entry point + make auth/proxy/token config-first

**Files:**
- Modify: `src/McaHub/Auth.cs:28-49` (`Auth.Read`)
- Modify: `src/McaHub/Program.cs:39` (proxy switch) and end-of-file (entry-point exposure)

- [ ] **Step 1: Make `Auth.Read` config-first for dev-login and treat an empty master token as unset**

In `src/McaHub/Auth.cs`, replace the body of `Read` (lines 28-49) with:

```csharp
    public static Config Read(IConfiguration c)
    {
        string? V(string key, string env) => c[key] ?? Environment.GetEnvironmentVariable(env);
        // config-first then env (matches V), so tests can drive the mode via IConfiguration
        bool Flag(string key, string env) => V(key, env) is "1" or "true" or "TRUE";

        string? clientId = V("OAuthClientId", "MCAHUB_OAUTH_CLIENT_ID");
        string? clientSecret = V("OAuthClientSecret", "MCAHUB_OAUTH_CLIENT_SECRET");
        bool oauth = clientId is { Length: > 0 } && clientSecret is { Length: > 0 };
        bool dev = Flag("DevLogin", "MCAHUB_DEV_LOGIN");
        string? master = V("PushToken", "MCAHUB_TOKEN");
        return new Config(
            Accounts: oauth || dev,
            Oauth: oauth,
            DevLogin: dev,
            Provider: V("OAuthProvider", "MCAHUB_OAUTH_PROVIDER") ?? "github",
            ClientId: clientId,
            ClientSecret: clientSecret,
            AuthUrl: V("OAuthAuthUrl", "MCAHUB_OAUTH_AUTH_URL") ?? "https://github.com/login/oauth/authorize",
            TokenUrl: V("OAuthTokenUrl", "MCAHUB_OAUTH_TOKEN_URL") ?? "https://github.com/login/oauth/access_token",
            UserUrl: V("OAuthUserUrl", "MCAHUB_OAUTH_USER_URL") ?? "https://api.github.com/user",
            Scope: V("OAuthScope", "MCAHUB_OAUTH_SCOPE") ?? "read:user",
            MasterToken: master is { Length: > 0 } ? master : null);
    }
```

(Changes vs. original: `Flag` takes a config key + env name and reads config first; `dev` uses `"DevLogin"`; an empty `PushToken`/`MCAHUB_TOKEN` now resolves to `null` instead of enabling a broken empty-token mode.)

- [ ] **Step 2: Make the proxy switch config-first in `Program.cs`**

In `src/McaHub/Program.cs`, change line 39 from:

```csharp
    if (Environment.GetEnvironmentVariable("MCAHUB_BEHIND_PROXY") is "1" or "true")
```

to:

```csharp
    if ((app.Configuration["BehindProxy"] ?? Environment.GetEnvironmentVariable("MCAHUB_BEHIND_PROXY")) is "1" or "true")
```

- [ ] **Step 3: Expose `Program` as a partial class for `WebApplicationFactory<Program>`**

At the very end of `src/McaHub/Program.cs` (after the `LoadDotEnv` local function), add:

```csharp

// Exposed so WebApplicationFactory<Program> can boot the app in integration tests.
public partial class Program;
```

- [ ] **Step 4: Build to verify the refactor compiles**

Run: `dotnet build src/McaHub/McaHub.csproj -v q -clp:NoSummary`
Expected: `Build succeeded.` / `0 Error(s)`

- [ ] **Step 5: Commit**

```bash
git add src/McaHub/Auth.cs src/McaHub/Program.cs
git commit -m "Make auth mode/proxy/token config-first; expose Program for tests"
```

---

### Task 2: Scaffold the xUnit test project

**Files:**
- Create: `tests/McaHub.Tests/McaHub.Tests.csproj` (via template)
- Delete: `tests/McaHub.Tests/UnitTest1.cs` (template placeholder)
- Modify: `McaHub.slnx`

- [ ] **Step 1: Create the project from the xUnit template**

Run:
```bash
dotnet new xunit -o tests/McaHub.Tests -n McaHub.Tests
rm tests/McaHub.Tests/UnitTest1.cs
```
Expected: project + `xunit`/`Microsoft.NET.Test.Sdk` package refs created with SDK-appropriate versions.

- [ ] **Step 2: Add the project reference and the testing package**

Run:
```bash
dotnet add tests/McaHub.Tests/McaHub.Tests.csproj reference src/McaHub/McaHub.csproj
dotnet add tests/McaHub.Tests/McaHub.Tests.csproj package Microsoft.AspNetCore.Mvc.Testing
```
Expected: both succeed; `Microsoft.AspNetCore.Mvc.Testing` resolves a `10.0.x` version.

- [ ] **Step 3: Add the test project to the solution**

Run: `dotnet sln McaHub.slnx add tests/McaHub.Tests/McaHub.Tests.csproj --solution-folder tests`
Expected: `Project ... added to the solution.`
Fallback if the CLI rejects slnx: hand-edit `McaHub.slnx` to add, before the `McaDiff` line:
```xml
  <Folder Name="/tests/">
    <Project Path="tests/McaHub.Tests/McaHub.Tests.csproj" />
  </Folder>
```

- [ ] **Step 4: Verify the empty project builds and the runner works**

Run: `dotnet test tests/McaHub.Tests/McaHub.Tests.csproj`
Expected: build succeeds, `Passed! - Failed: 0, Passed: 0` (no tests yet) or the template test passed if not deleted. If a build error mentions `Microsoft.AspNetCore.App`, add `<FrameworkReference Include="Microsoft.AspNetCore.App" />` in a new `<ItemGroup>` in the csproj and re-run.

- [ ] **Step 5: Commit**

```bash
git add tests/McaHub.Tests/McaHub.Tests.csproj McaHub.slnx
git commit -m "Scaffold McaHub.Tests xUnit project"
```

---

### Task 3: HubFactory test helper

**Files:**
- Create: `tests/McaHub.Tests/HubFactory.cs`

- [ ] **Step 1: Write `HubFactory`**

Create `tests/McaHub.Tests/HubFactory.cs`:

```csharp
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

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

    public HubFactory(HubMode mode = HubMode.Open, string masterToken = "test-master-token")
    {
        _mode = mode;
        MasterToken = masterToken;
    }

    /// <summary>The master token configured in <see cref="HubMode.Token"/> mode.</summary>
    public string MasterToken { get; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(_root);
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
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }
}
```

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build tests/McaHub.Tests/McaHub.Tests.csproj -v q -clp:NoSummary`
Expected: `Build succeeded.`

- [ ] **Step 3: Commit**

```bash
git add tests/McaHub.Tests/HubFactory.cs
git commit -m "Add HubFactory: hermetic in-memory hub for tests"
```

---

### Task 4: Tests proving the harness across modes

**Files:**
- Create: `tests/McaHub.Tests/AuthReadTests.cs`
- Create: `tests/McaHub.Tests/HarnessSmokeTests.cs`

- [ ] **Step 1: Write the `Auth.Read` config-first unit tests**

Create `tests/McaHub.Tests/AuthReadTests.cs`:

```csharp
using Microsoft.Extensions.Configuration;
using Xunit;

namespace McaHub.Tests;

public class AuthReadTests
{
    private static Auth.Config Read(params (string Key, string Value)[] settings)
    {
        // Baseline forces OAuth off so ambient env can't make Oauth true under test.
        (string, string)[] baseline = [("OAuthClientId", ""), ("OAuthClientSecret", "")];
        IConfiguration c = new ConfigurationBuilder()
            .AddInMemoryCollection(baseline.Concat(settings.Select(s => (s.Key, s.Value)))
                .Select(s => new KeyValuePair<string, string?>(s.Item1, s.Item2)))
            .Build();
        return Auth.Read(c);
    }

    [Fact]
    public void DevLogin_can_be_enabled_through_configuration()
    {
        Auth.Config cfg = Read(("DevLogin", "1"));
        Assert.True(cfg.Accounts);
        Assert.True(cfg.DevLogin);
        Assert.False(cfg.Oauth);
    }

    [Fact]
    public void Empty_push_token_is_treated_as_unset()
    {
        Assert.Null(Read(("PushToken", "")).MasterToken);
    }

    [Fact]
    public void Push_token_is_read_from_configuration()
    {
        Assert.Equal("secret", Read(("PushToken", "secret")).MasterToken);
    }
}
```

- [ ] **Step 2: Run them — expect PASS (Task 1 already implemented the behavior)**

Run: `dotnet test tests/McaHub.Tests/McaHub.Tests.csproj --filter AuthReadTests`
Expected: `Passed!` with 3 passed. If `DevLogin_can_be_enabled...` fails on `Oauth`, the dev shell exports real `MCAHUB_OAUTH_*`; the baseline empty values should still win — confirm `Auth.Read` reads `c[key]` before env (it does).

- [ ] **Step 3: Write the integration smoke tests**

Create `tests/McaHub.Tests/HarnessSmokeTests.cs`:

```csharp
using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace McaHub.Tests;

public class HarnessSmokeTests
{
    [Fact]
    public async Task Open_mode_home_page_returns_200()
    {
        using var hub = new HubFactory(HubMode.Open);
        using HttpClient client = hub.CreateClient();
        HttpResponseMessage resp = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Open_mode_has_no_account_page()
    {
        using var hub = new HubFactory(HubMode.Open);
        using HttpClient client = hub.CreateClient();
        HttpResponseMessage resp = await client.GetAsync("/account");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Accounts_mode_account_page_redirects_to_login()
    {
        using var hub = new HubFactory(HubMode.Accounts);
        using HttpClient client = hub.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        HttpResponseMessage resp = await client.GetAsync("/account");
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.StartsWith("/auth/login", resp.Headers.Location?.OriginalString);
    }
}
```

These prove: the app boots and renders (open `GET /` → 200), and **mode selection via `IConfiguration` actually takes effect** — `/account` is absent in open mode (404) but present and auth-gated in accounts mode (302 → `/auth/login`). If accounts mode didn't engage, the 302 assertion fails, catching a `UseSetting`-propagation regression.

- [ ] **Step 4: Run the full test project — expect all green**

Run: `dotnet test tests/McaHub.Tests/McaHub.Tests.csproj`
Expected: `Passed!` with 6 passed, 0 failed.
If `Accounts_mode_...` returns 404 instead of 302, `UseSetting("DevLogin","1")` did not reach the pre-`Build()` `Auth.Read`; switch `HubFactory.ConfigureWebHost` to also set the values via `builder.ConfigureAppConfiguration((_, cb) => cb.AddInMemoryCollection(...))` AND keep `UseSetting`, then re-run. (Expected to pass with `UseSetting` alone.)

- [ ] **Step 5: Commit**

```bash
git add tests/McaHub.Tests/AuthReadTests.cs tests/McaHub.Tests/HarnessSmokeTests.cs
git commit -m "Add harness smoke + Auth.Read config tests"
```

---

### Task 5: CI workflow

**Files:**
- Create: `.github/workflows/ci.yml`

- [ ] **Step 1: Write the workflow (sibling core pinned to a known commit)**

Create `.github/workflows/ci.yml`:

```yaml
name: CI

on:
  push:
    branches: [main]
  pull_request:

jobs:
  test:
    runs-on: ubuntu-latest
    steps:
      - name: Check out hub
        uses: actions/checkout@v4
        with:
          path: mcahub

      - name: Check out mcadiff core (pinned)
        uses: actions/checkout@v4
        with:
          repository: BangRocket/mcadiff
          ref: 41f6f2fd2b4d7f2bef935646ae8a7763526e4d6d
          path: mca-git

      - name: Set up .NET 10
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'

      - name: Restore
        working-directory: mcahub
        run: dotnet restore McaHub.slnx

      - name: Build
        working-directory: mcahub
        run: dotnet build McaHub.slnx --configuration Release --no-restore

      - name: Test
        working-directory: mcahub
        run: dotnet test McaHub.slnx --configuration Release --no-build
```

The checkout paths put the hub at `<workspace>/mcahub` and the core at `<workspace>/mca-git`, so the build's `..\..\..\mca-git\src\McaDiff\McaDiff.csproj` reference resolves. The core is pinned to today's `main` (`41f6f2f`) for reproducibility and to seed the supply-chain work (#12).

- [ ] **Step 2: Validate the workflow YAML locally**

Run: `python3 -c "import yaml,sys; yaml.safe_load(open('.github/workflows/ci.yml')); print('yaml ok')"`
Expected: `yaml ok`

- [ ] **Step 3: Commit**

```bash
git add .github/workflows/ci.yml
git commit -m "Add CI: dotnet test with pinned mcadiff core"
```

---

### Task 6: Verify, push, and open the PR

- [ ] **Step 1: Full green run from the solution**

Run: `dotnet test McaHub.slnx`
Expected: build succeeds; `Passed!` with 6 passed, 0 failed.

- [ ] **Step 2: Push the branch**

Run: `git push -u origin harden/19-test-harness`

- [ ] **Step 3: Open the PR**

Run:
```bash
gh pr create --base main --title "Stand up the test project (WebApplicationFactory + HubFactory + CI)" --body "$(cat <<'EOF'
Stands up the test foundation for the security-hardening campaign (roadmap: docs/superpowers/specs/2026-06-05-security-hardening-roadmap-design.md).

- xUnit `tests/McaHub.Tests` with `WebApplicationFactory<Program>`
- `HubFactory`: boots the hub hermetically (temp dirs, OAuth forced off) in open/token/accounts mode, driven purely via IConfiguration
- Small config-first refactor: dev-login, the proxy switch, and the master token now read config-then-env (matching the dir paths); empty master token is treated as unset
- Smoke + `Auth.Read` tests proving mode selection works
- CI workflow: `dotnet test` against the sibling mcadiff core pinned to 41f6f2f

Closes #19.

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```
Expected: PR URL printed.

---

## Notes / risks

- **`UseSetting` → pre-`Build()` config reads:** the harness depends on `UseSetting` values being visible to `builder.Configuration[...]` reads that run before `builder.Build()`. This is the documented WAF pattern and is verified by the accounts-mode smoke test. Fallback noted in Task 4 Step 4.
- **CI core build:** if the pinned `mcadiff` core needs more than `McaDiff.csproj` to restore on a clean runner, the Build step will surface it; fix forward (the local build references only `McaDiff.csproj` and is clean).
- **slnx tooling:** `dotnet sln/restore/build/test` against `.slnx` requires the .NET 10 SDK (present: 10.0.300). Hand-edit fallback for `dotnet sln` noted in Task 2 Step 3.
