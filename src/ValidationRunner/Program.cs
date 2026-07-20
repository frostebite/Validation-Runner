using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

var options = RunnerOptions.Parse(args);
var runner = new ValidationRunner(options);
return await runner.RunAsync();

internal sealed class ValidationRunner(RunnerOptions options)
{
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public async Task<int> RunAsync()
    {
        var token = NormalizeHeaderToken(FirstNonEmpty(
            Environment.GetEnvironmentVariable("PLATFORM_AUTH_TOKEN"),
            Environment.GetEnvironmentVariable("GH_TOKEN"),
            Environment.GetEnvironmentVariable("GITHUB_TOKEN")));

        if (string.IsNullOrWhiteSpace(token))
        {
            Console.Error.WriteLine("Missing platform auth token.");
            return 2;
        }

        string platformBaseUrl;
        try
        {
            platformBaseUrl = await WithRetryAsync(ResolvePlatformBaseUrlAsync, "startup", "resolve platform URL");
        }
        catch (Exception ex) when (IsPlatformUnreachable(ex))
        {
            Console.WriteLine($"startup: platform_unreachable error={FormatSafeError(ex)}");
            Console.WriteLine("summary profiles=0 failures=0 platform_unreachable=1");
            return 1;
        }

        using var http = new HttpClient { BaseAddress = new Uri(platformBaseUrl.TrimEnd('/') + "/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Validation-Runner/1.0");

        var profiles = ProfileDiscovery.Discover(options.GameClientRoot, options.TargetPlatform).ToList();
        Console.WriteLine($"target={options.TargetPlatform}");
        Console.WriteLine($"profiles={profiles.Count}");

        var failures = 0;
        var platformUnreachable = 0;
        var index = 0;
        foreach (var profile in profiles)
        {
            index++;
            var label = $"{options.TargetPlatform}:{profile.Framework}/{profile.Profile}";
            try
            {
                var build = await WithRetryAsync(() => FindLatestSandboxBuildAsync(http, profile), label, "list builds");
                if (build is null)
                {
                    Console.WriteLine($"{label}: no sandbox build");
                    if (!options.AllowMissingBuild)
                        failures++;
                    continue;
                }

                var workDir = Path.Combine(options.WorkRoot, Slug(label), build.Id.ToString(CultureInfo.InvariantCulture));
                Directory.CreateDirectory(workDir);

                var artifactRoot = await WithRetryAsync(() => DownloadAndExtractAsync(http, build, workDir), label, "download build");
                var executable = FindExecutable(artifactRoot, build, options.TargetPlatform);
                if (executable is null)
                {
                    Console.WriteLine($"{label}: no executable");
                    failures++;
                    continue;
                }

                var result = await RunTestsAsync(profile, executable, workDir, platformBaseUrl, token);
                await PostAggregateAsync(http, profile, result, index);

                Console.WriteLine($"{label}: total={result.Total} passed={result.Passed} failed={result.Failed} skipped={result.Skipped}");
                if (result.Failed > 0 || result.ExitCode != 0)
                    failures++;
            }
            catch (Exception ex) when (IsPlatformUnreachable(ex))
            {
                platformUnreachable++;
                Console.WriteLine($"{label}: platform_unreachable error={FormatSafeError(ex)}");
            }
            catch (Exception ex)
            {
                failures++;
                Console.WriteLine($"{label}: error={FormatSafeError(ex)}");
            }
        }

        Console.WriteLine($"summary profiles={profiles.Count} failures={failures} platform_unreachable={platformUnreachable}");
        return failures == 0 && platformUnreachable == 0 ? 0 : 1;
    }

    private static async Task<T> WithRetryAsync<T>(Func<Task<T>> action, string label, string operation, int maxAttempts = 3)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await action();
            }
            catch (Exception ex) when (attempt < maxAttempts && IsPlatformUnreachable(ex))
            {
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                Console.WriteLine($"{label}: {operation} attempt {attempt}/{maxAttempts} failed ({FormatSafeError(ex)}), retrying in {delay.TotalSeconds:0}s");
                await Task.Delay(delay);
            }
        }
    }

    private static bool IsPlatformUnreachable(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: null } => true,
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.BadGateway
            or System.Net.HttpStatusCode.ServiceUnavailable
            or System.Net.HttpStatusCode.GatewayTimeout } => true,
        TaskCanceledException => true,
        SocketException => true,
        _ => ex.InnerException is not null && IsPlatformUnreachable(ex.InnerException)
    };

    private async Task<string> ResolvePlatformBaseUrlAsync()
    {
        var direct = Environment.GetEnvironmentVariable("PLATFORM_BASE_URL");
        if (!string.IsNullOrWhiteSpace(direct))
            return direct;

        using var http = new HttpClient();
        var configUrl = Environment.GetEnvironmentVariable("SDKHUB_PLATFORM_CONFIG_URL")
            ?? "https://battle-buddy-games.github.io/Platform/config.json";
        var doc = await http.GetFromJsonAsync<JsonObject>(configUrl, _json);
        var tunnels = doc?["cloudflareTunnels"]?.AsArray();
        var cloud = tunnels?
            .OfType<JsonObject>()
            .FirstOrDefault(t => string.Equals(t["name"]?.GetValue<string>(), "cloud", StringComparison.OrdinalIgnoreCase));
        var address = cloud?["address"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(address))
            throw new InvalidOperationException("Could not resolve platform base URL.");
        return address;
    }

    private async Task<BuildRecord?> FindLatestSandboxBuildAsync(HttpClient http, ProfileTarget profile)
    {
        var path = "api/ci-builds?"
            + $"framework={Uri.EscapeDataString(profile.Framework)}"
            + $"&profile={Uri.EscapeDataString(profile.Profile)}"
            + "&status=success&flagSandbox=true&page=1&limit=25";

        using var response = await http.GetAsync(path);
        await EnsureSuccessAsync(response, "list builds");
        var payload = await response.Content.ReadFromJsonAsync<BuildListResponse>(_json);
        return payload?.Builds?
            .Where(b => b.Id > 0)
            .Where(b => string.Equals(b.Conclusion, "success", StringComparison.OrdinalIgnoreCase)
                || string.Equals(b.Status, "success", StringComparison.OrdinalIgnoreCase)
                || string.Equals(b.Status, "completed", StringComparison.OrdinalIgnoreCase))
            .Where(b => b.FlagSandbox)
            .Where(b => b.UnityDetail?.FolderAvailable != false)
            .Where(b => MatchesTarget(b.UnityDetail?.TargetPlatform, options.TargetPlatform)
                || MatchesTarget(b.ArtifactPath, options.TargetPlatform)
                || MatchesTarget(b.BuildIdentifier, options.TargetPlatform))
            .OrderByDescending(b => b.CompletedAt ?? b.CreatedAt ?? DateTimeOffset.MinValue)
            .FirstOrDefault();
    }

    private static bool MatchesTarget(string? value, string targetPlatform)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return targetPlatform switch
        {
            "windows" => value.Contains("windows", StringComparison.OrdinalIgnoreCase)
                || value.Contains("win64", StringComparison.OrdinalIgnoreCase),
            "linux" => value.Contains("linux", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private async Task<string> DownloadAndExtractAsync(HttpClient http, BuildRecord build, string workDir)
    {
        var zipPath = Path.Combine(workDir, "build.zip");
        using var response = await http.GetAsync($"api/ci-builds/{build.Id}/download-folder", HttpCompletionOption.ResponseHeadersRead);
        await EnsureSuccessAsync(response, "download build");
        await using (var source = await response.Content.ReadAsStreamAsync())
        await using (var target = File.Create(zipPath))
        {
            await source.CopyToAsync(target);
        }

        var extractRoot = Path.Combine(workDir, "build");
        if (Directory.Exists(extractRoot))
            Directory.Delete(extractRoot, recursive: true);
        ZipFile.ExtractToDirectory(zipPath, extractRoot);
        return extractRoot;
    }

    private static string? FindExecutable(string artifactRoot, BuildRecord build, string targetPlatform)
    {
        var names = new[]
        {
            build.BuildIdentifier,
            targetPlatform == "windows" ? "StandaloneWindows64" : null,
            targetPlatform == "linux" ? "StandaloneLinux64" : null,
            "StandaloneLinux64",
            "TurnOfWar",
            "AreaOfOperations",
            "Shell"
        }.Where(v => !string.IsNullOrWhiteSpace(v))
         .Select(v => Path.GetFileNameWithoutExtension(v!))
         .Distinct(StringComparer.OrdinalIgnoreCase)
         .ToArray();

        foreach (var file in Directory.EnumerateFiles(artifactRoot, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            var baseName = Path.GetFileNameWithoutExtension(file);
            if (targetPlatform == "windows" && !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                continue;
            if (targetPlatform == "linux" && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                continue;
            if (names.Contains(baseName, StringComparer.OrdinalIgnoreCase) && Directory.Exists(Path.Combine(Path.GetDirectoryName(file)!, $"{baseName}_Data")))
                return file;
        }

        if (targetPlatform == "windows")
        {
            return Directory.EnumerateFiles(artifactRoot, "*.exe", SearchOption.AllDirectories)
                .FirstOrDefault(f => Directory.Exists(Path.Combine(Path.GetDirectoryName(f)!, $"{Path.GetFileNameWithoutExtension(f)}_Data")));
        }

        return Directory.EnumerateFiles(artifactRoot, "*", SearchOption.AllDirectories)
            .Where(f => !Path.GetFileName(f).Contains('.', StringComparison.Ordinal))
            .FirstOrDefault(f => Directory.Exists(Path.Combine(Path.GetDirectoryName(f)!, $"{Path.GetFileName(f)}_Data")));
    }

    private async Task<TestAggregate> RunTestsAsync(ProfileTarget profile, string executable, string workDir, string platformBaseUrl, string token)
    {
        var resultsDir = Path.Combine(workDir, "test-results");
        Directory.CreateDirectory(resultsDir);
        var stdout = Path.Combine(resultsDir, "dotnet-test.out");
        var stderr = Path.Combine(resultsDir, "dotnet-test.err");

        var testRoot = Path.Combine(options.GameClientRoot, "Tools", "ClientBuildTests");
        var projects = new[]
        {
            Path.Combine(testRoot, "client-build-tests.platform", "client-build-tests.platform.csproj"),
            Path.Combine(testRoot, "client-build-tests.shell", "client-build-tests.shell.csproj"),
            Path.Combine(testRoot, "client-build-tests.multiplayer", "client-build-tests.multiplayer.csproj")
        }.Where(File.Exists).ToArray();

        var aggregate = new TestAggregate();
        foreach (var project in projects)
        {
            var trxName = $"{Path.GetFileNameWithoutExtension(project)}.trx";
            var psi = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = options.GameClientRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("test");
            psi.ArgumentList.Add(project);
            psi.ArgumentList.Add("--nologo");
            psi.ArgumentList.Add("--verbosity");
            psi.ArgumentList.Add("quiet");
            psi.ArgumentList.Add("--logger");
            psi.ArgumentList.Add($"trx;LogFileName={trxName}");
            psi.ArgumentList.Add("--results-directory");
            psi.ArgumentList.Add(resultsDir);
            psi.Environment["PLATFORM_BASE_URL"] = platformBaseUrl;
            psi.Environment["PLATFORM_AUTH_TOKEN"] = token;
            psi.Environment["GITHUB_TOKEN"] = token;
            psi.Environment["GAME_CLIENT_PATH"] = executable;
            psi.Environment["CLIENT_TARGET_PLATFORM"] = options.TargetPlatform;
            psi.Environment["CLIENT_FRAMEWORK"] = profile.Framework;
            psi.Environment["CLIENT_PROFILE"] = profile.Profile;
            psi.Environment["DOTNET_ROLL_FORWARD"] = "Major";

            using var process = Process.Start(psi) ?? throw new InvalidOperationException("dotnet test did not start.");
            await File.AppendAllTextAsync(stdout, await process.StandardOutput.ReadToEndAsync());
            await File.AppendAllTextAsync(stderr, await process.StandardError.ReadToEndAsync());
            await process.WaitForExitAsync();

            var trx = Path.Combine(resultsDir, trxName);
            var one = File.Exists(trx)
                ? TestAggregate.FromTrx(trx)
                : TestAggregate.MissingResults(process.ExitCode);
            one.ExitCode = process.ExitCode;
            aggregate.Add(one);
        }

        aggregate.ResultsDirectory = resultsDir;
        return aggregate;
    }

    private async Task PostAggregateAsync(HttpClient http, ProfileTarget profile, TestAggregate result, int index)
    {
        var runId = ParseLong(Environment.GetEnvironmentVariable("GITHUB_RUN_ID"));
        if (runId > 0 && index > 1)
            runId = (runId * 1000) + index;

        var xml = result.ToAggregateNUnitXml();
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/ci-tests/ingest")
        {
            Content = new StringContent(xml, Encoding.UTF8, "application/xml")
        };
        request.Headers.Add("X-GitHub-Run-Id", runId > 0 ? runId.ToString(CultureInfo.InvariantCulture) : DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        request.Headers.Add("X-Framework", profile.Framework);
        request.Headers.Add("X-Profile", profile.Profile);
        request.Headers.Add("X-Suite", "validation-runner");
        request.Headers.Add("X-Tier", "cloud");
        request.Headers.Add("X-Platform", options.TargetPlatform);
        request.Headers.Add("X-Category", "PackagedRuntime");
        request.Headers.Add("X-Branch", Environment.GetEnvironmentVariable("GITHUB_REF_NAME") ?? "main");
        request.Headers.Add("X-Actor", Environment.GetEnvironmentVariable("GITHUB_ACTOR") ?? "validation-runner");
        request.Headers.Add("X-Commit-Sha", Environment.GetEnvironmentVariable("GITHUB_SHA") ?? "");
        request.Headers.Add("X-Event-Type", Environment.GetEnvironmentVariable("GITHUB_EVENT_NAME") ?? "schedule");
        request.Headers.Add("X-Taxonomy-Filters", "{\"pipeline\":\"validation-runner\",\"execution\":\"packaged-runtime\"}");

        using var response = await http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
            Console.WriteLine($"{profile.Framework}/{profile.Profile}: platform ingest status={(int)response.StatusCode}");
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string operation)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = await response.Content.ReadAsStringAsync();
        if (body.Length > 160)
            body = body[..160];
        throw new HttpRequestException(
            $"{operation} failed: {(int)response.StatusCode} {response.ReasonPhrase}; {body}",
            null,
            response.StatusCode);
    }

    private static string FormatSafeError(Exception ex)
    {
        if (ex is HttpRequestException httpEx)
        {
            var status = httpEx.StatusCode.HasValue ? $" status={(int)httpEx.StatusCode.Value}" : "";
            return $"{httpEx.GetType().Name}{status}: {httpEx.Message}";
        }

        return ex.GetType().Name;
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();

    private static string? NormalizeHeaderToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var cleaned = new string(value.Where(ch => ch is >= '!' and <= '~').ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
    }

    private static long ParseLong(string? value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;

    private static string Slug(string value)
    {
        var chars = value.Select(ch => char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '-').ToArray();
        return new string(chars).Trim('-');
    }
}

internal sealed record RunnerOptions(string GameClientRoot, string WorkRoot, string TargetPlatform, bool AllowMissingBuild)
{
    public static RunnerOptions Parse(string[] args)
    {
        var gameClient = Environment.GetEnvironmentVariable("GAMECLIENT_CONFIG_ROOT")
            ?? Path.Combine(Environment.CurrentDirectory, "GameClient");
        var targetPlatform = NormalizeTarget(Environment.GetEnvironmentVariable("VALIDATION_TARGET") ?? "linux");
        var allowMissingBuild = BoolFromString(Environment.GetEnvironmentVariable("VALIDATION_ALLOW_MISSING_BUILD"));

        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--gameclient" && i + 1 < args.Length)
            {
                gameClient = args[++i];
                continue;
            }

            if (args[i] == "--target" && i + 1 < args.Length)
            {
                targetPlatform = NormalizeTarget(args[++i]);
                continue;
            }

            if (args[i] == "--allow-missing-build" && i + 1 < args.Length)
            {
                allowMissingBuild = BoolFromString(args[++i]);
            }
        }

        var work = Path.Combine(Environment.CurrentDirectory, ".runner");
        Directory.CreateDirectory(work);
        return new RunnerOptions(Path.GetFullPath(gameClient), work, targetPlatform, allowMissingBuild);
    }

    private static string NormalizeTarget(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        if (normalized == "win" || normalized == "windows" || normalized == "windows64")
            return "windows";
        if (normalized == "linux" || normalized == "linux64")
            return "linux";

        throw new ArgumentException($"Unsupported validation target '{value}'. Use 'windows' or 'linux'.");
    }

    private static bool BoolFromString(string? value)
    {
        return bool.TryParse(value, out var parsed) && parsed;
    }
}

internal static class ProfileDiscovery
{
    public static IEnumerable<ProfileTarget> Discover(string gameClientRoot, string targetPlatform)
    {
        var frameworksPath = Path.Combine(gameClientRoot, "config", "frameworks.yml");
        var profileRoot = Path.Combine(gameClientRoot, "config", "submodule-profiles");
        if (!File.Exists(frameworksPath) || !Directory.Exists(profileRoot))
            yield break;

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        var frameworkConfig = deserializer.Deserialize<Dictionary<object, object?>>(File.ReadAllText(frameworksPath));
        var buildProfiles = AsMap(frameworkConfig.GetValueOrDefault("build_profiles"));

        foreach (var profileFile in Directory.EnumerateFiles(profileRoot, "profile.yml", SearchOption.AllDirectories))
        {
            var profileDoc = deserializer.Deserialize<Dictionary<object, object?>>(File.ReadAllText(profileFile));
            if (!AsBool(profileDoc.GetValueOrDefault("active_development")))
                continue;

            var profileName = new DirectoryInfo(Path.GetDirectoryName(profileFile)!).Name;
            var matching = buildProfiles.Values
                .Select(AsMap)
                .FirstOrDefault(bp => string.Equals(AsString(bp.GetValueOrDefault("profile")), profileName, StringComparison.OrdinalIgnoreCase)
                    && SupportsSandboxTarget(bp, targetPlatform));
            if (matching is null)
                continue;

            var framework = AsString(matching.GetValueOrDefault("framework"));
            if (string.IsNullOrWhiteSpace(framework))
                continue;

            yield return new ProfileTarget(framework, profileName);
        }
    }

    private static bool SupportsSandboxTarget(Dictionary<object, object?> buildProfile, string targetPlatform)
    {
        var shapes = AsStringList(buildProfile.GetValueOrDefault("enabled_shapes"));
        if (!shapes.Contains("sandbox", StringComparer.OrdinalIgnoreCase))
            return false;

        var targets = AsStringList(buildProfile.GetValueOrDefault("supported_targets"));
        var os = AsStringList(buildProfile.GetValueOrDefault("supported_os"));
        var platforms = AsMap(buildProfile.GetValueOrDefault("platforms"));
        return targetPlatform switch
        {
            "windows" => targets.Contains("standaloneWin64", StringComparer.OrdinalIgnoreCase)
                || targets.Contains("steamWin64", StringComparer.OrdinalIgnoreCase)
                || os.Contains("windows64", StringComparer.OrdinalIgnoreCase)
                || platforms.ContainsKey("windows"),
            "linux" => targets.Contains("sandboxLinux", StringComparer.OrdinalIgnoreCase)
                || targets.Contains("steamLinux", StringComparer.OrdinalIgnoreCase)
                || os.Contains("linux64", StringComparer.OrdinalIgnoreCase)
                || platforms.ContainsKey("linux"),
            _ => false
        };
    }

    private static Dictionary<object, object?> AsMap(object? value) =>
        value as Dictionary<object, object?> ?? new Dictionary<object, object?>();

    private static string? AsString(object? value) => value?.ToString();

    private static bool AsBool(object? value) =>
        value is bool b ? b : bool.TryParse(value?.ToString(), out var parsed) && parsed;

    private static List<string> AsStringList(object? value) =>
        value is IEnumerable<object> items ? items.Select(v => v.ToString()).Where(v => !string.IsNullOrWhiteSpace(v)).Cast<string>().ToList() : [];
}

internal sealed record ProfileTarget(string Framework, string Profile);

internal sealed class BuildListResponse
{
    public List<BuildRecord>? Builds { get; set; }
}

internal sealed class BuildRecord
{
    public int Id { get; set; }
    public string? BuildIdentifier { get; set; }
    public bool FlagSandbox { get; set; }
    public string? Status { get; set; }
    public string? Conclusion { get; set; }
    public string? ArtifactPath { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public UnityDetail? UnityDetail { get; set; }
}

internal sealed class UnityDetail
{
    public string? TargetPlatform { get; set; }
    public bool? FolderAvailable { get; set; }
}

internal sealed class TestAggregate
{
    public int Total { get; private set; }
    public int Passed { get; private set; }
    public int Failed { get; private set; }
    public int Skipped { get; private set; }
    public double DurationSeconds { get; private set; }
    public int ExitCode { get; set; }
    public string? ResultsDirectory { get; set; }

    public void Add(TestAggregate other)
    {
        Total += other.Total;
        Passed += other.Passed;
        Failed += other.Failed;
        Skipped += other.Skipped;
        DurationSeconds += other.DurationSeconds;
        if (other.ExitCode != 0)
            ExitCode = other.ExitCode;
    }

    public static TestAggregate FromTrx(string path)
    {
        var doc = XDocument.Load(path);
        var counters = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Counters");
        var times = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Times");
        var total = IntAttr(counters, "total");
        var passed = IntAttr(counters, "passed");
        var failed = IntAttr(counters, "failed");
        var skipped = IntAttr(counters, "notExecuted") + IntAttr(counters, "notRunnable") + IntAttr(counters, "disconnected");
        var duration = DurationFromTimes(times);
        return new TestAggregate
        {
            Total = total,
            Passed = passed,
            Failed = failed,
            Skipped = skipped,
            DurationSeconds = duration
        };
    }

    public static TestAggregate MissingResults(int exitCode) => exitCode == 0
        ? new TestAggregate()
        : new TestAggregate
        {
            Total = 1,
            Failed = 1,
            ExitCode = exitCode
        };

    public string ToAggregateNUnitXml()
    {
        var cases = new List<XElement>();
        AddCases(cases, "Passed", Passed);
        AddCases(cases, "Failed", Failed);
        AddCases(cases, "Skipped", Skipped);

        var suite = new XElement("test-suite",
            new XAttribute("type", "Assembly"),
            new XAttribute("name", "ValidationRunner.Aggregate"),
            new XAttribute("executed", "True"),
            new XAttribute("result", Failed > 0 ? "Failure" : "Success"),
            new XAttribute("success", Failed == 0 ? "True" : "False"),
            new XAttribute("time", DurationSeconds.ToString("0.###", CultureInfo.InvariantCulture)),
            new XAttribute("asserts", "0"),
            new XElement("results", cases));

        var root = new XElement("test-results",
            new XAttribute("name", "ValidationRunner.Aggregate"),
            new XAttribute("total", Total),
            new XAttribute("errors", "0"),
            new XAttribute("failures", Failed),
            new XAttribute("not-run", Skipped),
            new XAttribute("inconclusive", "0"),
            new XAttribute("ignored", Skipped),
            new XAttribute("skipped", Skipped),
            new XAttribute("invalid", "0"),
            new XAttribute("date", DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            new XAttribute("time", DateTime.UtcNow.ToString("HH:mm:ss", CultureInfo.InvariantCulture)),
            suite);

        return new XDocument(root).ToString(SaveOptions.DisableFormatting);
    }

    private static void AddCases(List<XElement> cases, string result, int count)
    {
        for (var i = 1; i <= count; i++)
        {
            var element = new XElement("test-case",
                new XAttribute("name", $"ValidationRunner.{result}.{i}"),
                new XAttribute("executed", result == "Skipped" ? "False" : "True"),
                new XAttribute("result", result),
                new XAttribute("success", result == "Passed" ? "True" : "False"),
                new XAttribute("time", "0"),
                new XAttribute("asserts", "0"));
            if (result == "Failed")
            {
                element.Add(new XElement("failure",
                    new XElement("message", SecurityElement.Escape("One packaged runtime test failed. Details are retained in runner artifacts, not Actions output."))));
            }
            cases.Add(element);
        }
    }

    private static int IntAttr(XElement? element, string name) =>
        int.TryParse(element?.Attribute(name)?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;

    private static double DurationFromTimes(XElement? times)
    {
        if (DateTimeOffset.TryParse(times?.Attribute("start")?.Value, out var start)
            && DateTimeOffset.TryParse(times?.Attribute("finish")?.Value, out var finish)
            && finish >= start)
            return (finish - start).TotalSeconds;
        return 0;
    }
}
