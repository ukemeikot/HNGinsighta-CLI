using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

var app = new InsightaCli(args);
await app.RunAsync();

internal sealed class InsightaCli
{
    private readonly string[] _args;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _configDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".insighta");
    private readonly string _credentialsPath;

    public InsightaCli(string[] args)
    {
        _args = args;
        _credentialsPath = Path.Combine(_configDir, "credentials.json");
    }

    public async Task RunAsync()
    {
        if (_args.Length == 0)
        {
            Help();
            return;
        }

        try
        {
            switch (_args[0])
            {
                case "config" when _args.Length >= 3 && _args[1] == "set-backend":
                    await SetBackendAsync(_args[2]);
                    break;
                case "login":
                    await LoginAsync();
                    break;
                case "logout":
                    await LogoutAsync();
                    break;
                case "whoami":
                    await SendAsync(HttpMethod.Get, "/api/v1/auth/me");
                    break;
                case "profiles":
                    await ProfilesAsync(_args.Skip(1).ToArray());
                    break;
                default:
                    Help();
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            Environment.ExitCode = 1;
        }
    }

    private async Task SetBackendAsync(string backendUrl)
    {
        var credentials = await LoadAsync() ?? new Credentials { BackendUrl = backendUrl.TrimEnd('/') };
        credentials.BackendUrl = backendUrl.TrimEnd('/');
        await SaveAsync(credentials);
        Console.WriteLine($"Backend URL set to {credentials.BackendUrl}");
    }

    private async Task LoginAsync()
    {
        var credentials = await LoadAsync() ?? new Credentials { BackendUrl = Environment.GetEnvironmentVariable("INSIGHTA_BACKEND_URL") ?? "http://localhost:8080" };
        using var client = new HttpClient { BaseAddress = new Uri(credentials.BackendUrl) };
        var start = await ReadJsonAsync<AuthStartResponse>(await client.PostAsync("/api/v1/auth/cli/start", null));
        Console.WriteLine("Open this URL to sign in with GitHub:");
        Console.WriteLine(start.AuthorizationUrl);
        TryOpenBrowser(start.AuthorizationUrl);

        var deadline = DateTime.UtcNow.AddMinutes(10);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            var response = await client.PostAsync("/api/v1/auth/cli/exchange", JsonBody(new { state = start.State }));
            if ((int)response.StatusCode == 202)
            {
                Console.Write(".");
                continue;
            }

            var tokens = await ReadJsonAsync<TokenResponse>(response);
            credentials.AccessToken = tokens.AccessToken;
            credentials.RefreshToken = tokens.RefreshToken;
            credentials.ExpiresAt = DateTime.UtcNow.AddSeconds(tokens.ExpiresIn - 30);
            credentials.User = tokens.User;
            await SaveAsync(credentials);
            Console.WriteLine();
            Console.WriteLine($"Logged in as {tokens.User.GitHubUsername} ({tokens.User.Role})");
            return;
        }

        throw new InvalidOperationException("Login timed out");
    }

    private async Task LogoutAsync()
    {
        var credentials = await LoadAsync();
        if (credentials is not null && !string.IsNullOrWhiteSpace(credentials.RefreshToken))
        {
            using var client = new HttpClient { BaseAddress = new Uri(credentials.BackendUrl) };
            await client.PostAsync("/api/v1/auth/logout", JsonBody(new { refresh_token = credentials.RefreshToken }));
        }

        if (File.Exists(_credentialsPath))
        {
            File.Delete(_credentialsPath);
        }

        Console.WriteLine("Logged out");
    }

    private async Task ProfilesAsync(string[] args)
    {
        if (args.Length == 0)
        {
            Help();
            return;
        }

        switch (args[0])
        {
            case "list":
                await SendAsync(HttpMethod.Get, "/api/profiles" + Query(args.Skip(1).ToArray()));
                break;
            case "search" when args.Length >= 2:
                await SendAsync(HttpMethod.Get, "/api/profiles/search?q=" + Uri.EscapeDataString(string.Join(' ', args.Skip(1))));
                break;
            case "get" when args.Length >= 2:
                await SendAsync(HttpMethod.Get, "/api/profiles/" + args[1]);
                break;
            case "create" when args.Length >= 2:
                await CreateProfileAsync(args.Skip(1).ToArray());
                break;
            case "delete" when args.Length >= 2:
                await SendAsync(HttpMethod.Delete, "/api/profiles/" + args[1]);
                break;
            case "export":
                await ExportAsync(args.Skip(1).ToArray());
                break;
            default:
                Help();
                break;
        }
    }

    private async Task SendAsync(HttpMethod method, string path, object? body = null)
    {
        var credentials = await GetFreshCredentialsAsync();
        using var client = new HttpClient { BaseAddress = new Uri(credentials.BackendUrl) };
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);
        AddApiVersionHeader(request);
        if (body is not null)
        {
            request.Content = JsonBody(body);
        }

        Console.Error.Write("Loading...");
        var response = await client.SendAsync(request);
        Console.Error.WriteLine(" done");
        var content = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(content);
        }

        PrintResponse(content);
    }

    private async Task CreateProfileAsync(string[] args)
    {
        var name = OptionValue(args, "name") ?? string.Join(' ', args.Where(arg => !arg.StartsWith("--")));
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException("Usage: insighta profiles create --name \"Harriet Tubman\"");
        }

        await SendAsync(HttpMethod.Post, "/api/profiles", new { name });
    }

    private async Task ExportAsync(string[] args)
    {
        var credentials = await GetFreshCredentialsAsync();
        var format = OptionValue(args, "format") ?? "csv";
        if (!string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Only --format csv is supported");
        }

        var queryArgs = args
            .Where((arg, index) => !(arg == "--format" || (index > 0 && args[index - 1] == "--format")))
            .ToArray();
        var resolvedOutputPath = Path.GetFullPath($"profiles_{DateTime.UtcNow:yyyyMMddHHmmss}.csv");
        using var client = new HttpClient { BaseAddress = new Uri(credentials.BackendUrl) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);
        client.DefaultRequestHeaders.Add("X-API-Version", "1");
        Console.Error.Write("Exporting...");
        var csv = await client.GetStringAsync("/api/profiles/export?format=csv" + Query(queryArgs, prefixWithAmpersand: true));
        Console.Error.WriteLine(" done");

        await File.WriteAllTextAsync(resolvedOutputPath, csv);
        Console.WriteLine($"Exported profiles to {resolvedOutputPath}");
    }

    private async Task<Credentials> GetFreshCredentialsAsync()
    {
        var credentials = await LoadAsync() ?? throw new InvalidOperationException("Run `insighta login` first");
        if (DateTime.UtcNow < credentials.ExpiresAt && !string.IsNullOrWhiteSpace(credentials.AccessToken))
        {
            return credentials;
        }

        using var client = new HttpClient { BaseAddress = new Uri(credentials.BackendUrl) };
        var tokens = await ReadJsonAsync<TokenResponse>(await client.PostAsync("/api/v1/auth/refresh", JsonBody(new { refresh_token = credentials.RefreshToken })));
        credentials.AccessToken = tokens.AccessToken;
        credentials.RefreshToken = tokens.RefreshToken;
        credentials.ExpiresAt = DateTime.UtcNow.AddSeconds(tokens.ExpiresIn - 30);
        credentials.User = tokens.User;
        await SaveAsync(credentials);
        return credentials;
    }

    private async Task<Credentials?> LoadAsync()
    {
        if (!File.Exists(_credentialsPath))
        {
            return null;
        }

        await using var stream = File.OpenRead(_credentialsPath);
        return await JsonSerializer.DeserializeAsync<Credentials>(stream, _json);
    }

    private async Task SaveAsync(Credentials credentials)
    {
        Directory.CreateDirectory(_configDir);
        await using var stream = File.Create(_credentialsPath);
        await JsonSerializer.SerializeAsync(stream, credentials, _json);
    }

    private static StringContent JsonBody(object value) => new(JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8, "application/json");

    private async Task<T> ReadJsonAsync<T>(HttpResponseMessage response)
    {
        var content = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(content);
        }

        return JsonSerializer.Deserialize<T>(content, _json) ?? throw new InvalidOperationException("Invalid response from backend");
    }

    private static string Query(string[] args)
    {
        if (args.Length == 0) return "";
        var pairs = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--") || i + 1 >= args.Length) continue;
            var key = NormalizeOptionKey(args[i][2..]);
            pairs.Add($"{Uri.EscapeDataString(key)}={Uri.EscapeDataString(args[++i])}");
        }
        return pairs.Count == 0 ? "" : "?" + string.Join('&', pairs);
    }

    private static string Query(string[] args, bool prefixWithAmpersand)
    {
        var query = Query(args);
        return prefixWithAmpersand && query.StartsWith('?') ? "&" + query[1..] : query;
    }

    private static void PrintResponse(string content)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            if (document.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                PrintProfilesTable(data);
                if (document.RootElement.TryGetProperty("page", out var page)
                    && document.RootElement.TryGetProperty("total_pages", out var totalPages)
                    && document.RootElement.TryGetProperty("total", out var total))
                {
                    Console.WriteLine($"Page {page.GetInt32()} of {totalPages.GetInt32()} | Total {total.GetInt32()}");
                }
                return;
            }

            Console.WriteLine(JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            Console.WriteLine(content);
        }
    }

    private static void PrintProfilesTable(JsonElement profiles)
    {
        Console.WriteLine($"{"Name",-24} {"Gender",-8} {"Age",4} {"Group",-10} {"Country",-24}");
        Console.WriteLine(new string('-', 74));
        foreach (var profile in profiles.EnumerateArray())
        {
            Console.WriteLine(
                $"{Truncate(GetString(profile, "name"), 24),-24} " +
                $"{Truncate(GetString(profile, "gender"), 8),-8} " +
                $"{GetInt(profile, "age"),4} " +
                $"{Truncate(GetString(profile, "age_group"), 10),-10} " +
                $"{Truncate(GetString(profile, "country_name"), 24),-24}");
        }
    }

    private static string Pretty(string content)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            return JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return content;
        }
    }

    private static void AddApiVersionHeader(HttpRequestMessage request)
    {
        if (request.RequestUri?.OriginalString.StartsWith("/api/profiles", StringComparison.OrdinalIgnoreCase) == true)
        {
            request.Headers.Add("X-API-Version", "1");
        }
    }

    private static string? OptionValue(string[] args, string name)
    {
        var token = "--" + name;
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], token, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static string NormalizeOptionKey(string key)
    {
        return key switch
        {
            "country" => "country_id",
            "age-group" => "age_group",
            "min-age" => "min_age",
            "max-age" => "max_age",
            "sort-by" => "sort_by",
            _ => key.Replace('-', '_')
        };
    }

    private static string GetString(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var value) ? value.GetString() ?? "" : "";
    }

    private static int GetInt(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var value) && value.TryGetInt32(out var parsed) ? parsed : 0;
    }

    private static string Truncate(string value, int max)
    {
        return value.Length <= max ? value : value[..Math.Max(0, max - 1)] + ".";
    }

    private static void TryOpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch
        {
            // Printing the URL is enough when a desktop browser cannot be opened.
        }
    }

    private static void Help()
    {
        Console.WriteLine("Insighta CLI");
        Console.WriteLine("Commands: login, logout, whoami, config set-backend <url>, profiles list|search|get|create|delete|export");
        Console.WriteLine("Examples: insighta profiles create --name \"Harriet Tubman\" | insighta profiles export --format csv --gender male --country NG");
    }
}

internal sealed class Credentials
{
    [JsonPropertyName("backend_url")] public string BackendUrl { get; set; } = "http://localhost:8080";
    [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
    [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
    [JsonPropertyName("expires_at")] public DateTime ExpiresAt { get; set; }
    [JsonPropertyName("user")] public AuthUser? User { get; set; }
}

internal sealed record AuthStartResponse(
    [property: JsonPropertyName("authorization_url")] string AuthorizationUrl,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("expires_at")] DateTime ExpiresAt);

internal sealed record TokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("refresh_token")] string RefreshToken,
    [property: JsonPropertyName("expires_in")] int ExpiresIn,
    [property: JsonPropertyName("token_type")] string TokenType,
    [property: JsonPropertyName("user")] AuthUser User);

internal sealed record AuthUser(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("github_username")] string GitHubUsername,
    [property: JsonPropertyName("email")] string? Email,
    [property: JsonPropertyName("avatar_url")] string? AvatarUrl,
    [property: JsonPropertyName("role")] string Role);
