using System.Text.Json;
using System.Text.Json.Nodes;
using Cronos;
using SeekClaw.Runtime.Configuration;
using SeekClaw.Runtime.Coordination;
using SeekClaw.Runtime.Mcp;
using SeekClaw.Runtime.Providers;
using SeekClaw.Runtime.Scheduling;
using SeekClaw.Runtime.Workspaces;

namespace SeekClaw.Runtime.Daemon;

internal sealed class DaemonRequestException(string message) : Exception(message);

/// <summary>Structured administrative operations shared by desktop and editor clients.</summary>
internal sealed class DaemonAdminApi(
    SeekClawRuntime runtime,
    WorkspaceInfo globalWorkspace,
    IFileLockCoordinator fileLocks,
    IScheduleService? scheduler = null)
{

    public string ListLocks()
    {
        var locks = new JsonArray();
        foreach (var entry in fileLocks.Snapshot())
        {
            locks.Add((JsonNode)new JsonObject
            {
                ["workspace"] = entry.WorkspaceRoot,
                ["file"] = entry.FilePath,
                ["owner"] = entry.Owner,
                ["acquiredAt"] = entry.AcquiredAt.ToString("O"),
            });
        }
        return locks.ToJsonString();
    }

    public string InitializeWorkspace()
    {
        var created = runtime.Workspaces.Bootstrap(runtime.Workspace);
        return new JsonObject
        {
            ["path"] = runtime.Workspace.Root,
            ["created"] = Strings(created),
        }.ToJsonString();
    }

    public string GetRoutingConfig() => new JsonObject
    {
        ["failoverEnabled"] = runtime.ConfigStore.Config.Routing.FailoverEnabled,
        ["deepSeekOptimizationEnabled"] = runtime.ConfigStore.Config.Routing.DeepSeekOptimizationEnabled,
    }.ToJsonString();

    public string SetRoutingConfig(JsonObject parameters)
    {
        if (parameters["failoverEnabled"] is not JsonValue value
            || !value.TryGetValue<bool>(out var failoverEnabled))
            throw new DaemonRequestException("params.failoverEnabled (boolean) is required");

        var deepSeekOptimizationEnabled = parameters["deepSeekOptimizationEnabled"] is JsonValue deepSeekValue
            && deepSeekValue.TryGetValue<bool>(out var enabled)
                ? enabled
                : runtime.ConfigStore.Config.Routing.DeepSeekOptimizationEnabled;

        runtime.ConfigStore.Config.Routing.FailoverEnabled = failoverEnabled;
        runtime.ConfigStore.Config.Routing.DeepSeekOptimizationEnabled = deepSeekOptimizationEnabled;
        runtime.ConfigStore.Save();
        return new JsonObject
        {
            ["failoverEnabled"] = failoverEnabled,
            ["deepSeekOptimizationEnabled"] = deepSeekOptimizationEnabled,
        }.ToJsonString();
    }

    public string ListSchedules() =>
        JsonSerializer.Serialize(runtime.Schedules.List(), SeekClawJsonContext.Compact.ListScheduledTask);

    public string UpsertSchedule(JsonObject parameters)
    {
        var id = OptionalString(parameters, "id");
        var name = RequiredString(parameters, "name");
        var prompt = RequiredString(parameters, "prompt");
        var cron = RequiredString(parameters, "cron");
        var workspace = OptionalString(parameters, "workspace");
        var enabled = parameters["enabled"] is JsonValue enabledValue && enabledValue.TryGetValue<bool>(out var on)
            ? on
            : true;
        try
        {
            var task = runtime.Schedules.Upsert(id, name, workspace, prompt, cron, enabled);
            return JsonSerializer.Serialize(task, SeekClawJsonContext.Compact.ScheduledTask);
        }
        catch (CronFormatException ex)
        {
            throw new DaemonRequestException($"Invalid cron expression: {ex.Message}");
        }
    }

    public string ToggleSchedule(JsonObject parameters)
    {
        var id = RequiredString(parameters, "id");
        var enabled = parameters["enabled"] is JsonValue enabledValue && enabledValue.TryGetValue<bool>(out var on)
            ? on
            : throw new DaemonRequestException("params.enabled (boolean) is required");
        try
        {
            var task = runtime.Schedules.SetEnabled(id, enabled);
            return JsonSerializer.Serialize(task, SeekClawJsonContext.Compact.ScheduledTask);
        }
        catch (InvalidOperationException ex)
        {
            throw new DaemonRequestException(ex.Message);
        }
    }

    public string DeleteSchedule(JsonObject parameters)
    {
        var id = RequiredString(parameters, "id");
        runtime.Schedules.Remove(id);
        return "ok";
    }

    public async Task<string> RunScheduleAsync(JsonObject parameters, CancellationToken ct)
    {
        var id = RequiredString(parameters, "id");
        if (scheduler is null)
            throw new DaemonRequestException("Scheduler is not available in this host.");
        try
        {
            await scheduler.RunNowAsync(id, ct).ConfigureAwait(false);
            return "started";
        }
        catch (InvalidOperationException ex)
        {
            throw new DaemonRequestException(ex.Message);
        }
    }

    public string ListProfiles()
    {
        var profiles = new JsonArray();
        foreach (var (name, profile) in runtime.ConfigStore.Config.Profiles.OrderBy(item => item.Key))
        {
            profiles.Add((JsonNode)new JsonObject
            {
                ["name"] = name,
                ["active"] = name.Equals(runtime.ConfigStore.Config.ActiveProfile, StringComparison.OrdinalIgnoreCase),
                ["provider"] = profile.Provider,
                ["model"] = profile.Model,
                ["strategy"] = profile.Strategy,
                ["temperature"] = profile.Temperature,
                ["mode"] = profile.Mode,
            });
        }
        return profiles.ToJsonString();
    }

    public string UpsertProfile(JsonObject parameters)
    {
        var name = RequiredString(parameters, "name");
        var profiles = runtime.ConfigStore.Config.Profiles;
        if (!profiles.TryGetValue(name, out var profile))
        {
            profile = new ProfileConfig();
            profiles[name] = profile;
        }

        profile.Provider = OptionalString(parameters, "provider");
        profile.Model = OptionalString(parameters, "model");
        profile.Strategy = OptionalString(parameters, "strategy");
        profile.Mode = OptionalString(parameters, "mode");
        profile.Temperature = parameters["temperature"]?.GetValue<double?>();
        runtime.ConfigStore.Save();
        return name;
    }

    public string UseProfile(JsonObject parameters)
    {
        var name = RequiredString(parameters, "name");
        if (!runtime.ConfigStore.Config.Profiles.ContainsKey(name))
            throw new DaemonRequestException($"Profile not found: {name}");
        runtime.ConfigStore.Config.ActiveProfile = name;
        runtime.ConfigStore.Save();
        return name;
    }

    public string RemoveProfile(JsonObject parameters)
    {
        var name = RequiredString(parameters, "name");
        var config = runtime.ConfigStore.Config;
        if (name.Equals(config.ActiveProfile, StringComparison.OrdinalIgnoreCase))
            throw new DaemonRequestException("Cannot remove the active profile");
        if (!config.Profiles.Remove(name))
            throw new DaemonRequestException($"Profile not found: {name}");
        config.GetActiveProfile();
        runtime.ConfigStore.Save();
        return name;
    }

    public string ListProviders()
    {
        var config = runtime.ConfigStore.Config;
        var active = config.GetActiveProfile().Provider;
        var providers = new JsonArray();
        foreach (var provider in config.Providers.OrderBy(item => item.Priority).ThenBy(item => item.Id))
            providers.Add((JsonNode)ProviderJson(provider, provider.Id.Equals(active, StringComparison.OrdinalIgnoreCase)));
        return providers.ToJsonString();
    }

    public string UpsertProvider(JsonObject parameters)
    {
        var id = RequiredString(parameters, "id");
        var config = runtime.ConfigStore.Config;
        var provider = config.FindProvider(id);
        var isNew = provider is null;
        if (provider is null)
        {
            provider = new ProviderConfig { Id = id };
            config.Providers.Add(provider);
        }

        var kind = OptionalString(parameters, "kind") ?? provider.Kind;
        if (kind is not ("openai" or "anthropic"))
            throw new DaemonRequestException("Provider kind must be openai or anthropic");
        provider.Kind = kind;
        provider.Name = OptionalString(parameters, "name") ?? provider.Name;
        provider.BaseUrl = OptionalString(parameters, "baseUrl") ?? provider.BaseUrl;
        provider.Proxy = OptionalString(parameters, "proxy") ?? provider.Proxy;
        provider.Enabled = parameters["enabled"]?.GetValue<bool?>() ?? provider.Enabled;
        provider.Priority = parameters["priority"]?.GetValue<int?>() ?? provider.Priority;
        provider.TimeoutSeconds = parameters["timeoutSeconds"]?.GetValue<int?>() ?? provider.TimeoutSeconds;
        if (parameters.ContainsKey("modelListUrl"))
            provider.ModelListUrl = OptionalString(parameters, "modelListUrl");
        provider.PromptCaching = parameters["promptCaching"]?.GetValue<bool?>() ?? provider.PromptCaching;

        if (parameters["clearApiKey"]?.GetValue<bool>() == true)
            provider.ApiKey = null;
        else if (OptionalString(parameters, "apiKey") is { } apiKey)
            provider.ApiKey = apiKey;

        if (parameters["models"] is JsonArray models)
        {
            var existing = provider.Models.ToDictionary(model => model.Id, StringComparer.OrdinalIgnoreCase);
            provider.Models = models
                .Select(node => node?.GetValue<string>()?.Trim())
                .Where(modelId => !string.IsNullOrWhiteSpace(modelId))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(modelId => existing.TryGetValue(modelId!, out var model)
                    ? model
                    : new ModelConfig { Id = modelId! })
                .ToList();
        }

        if (string.IsNullOrWhiteSpace(provider.BaseUrl))
        {
            if (isNew) config.Providers.Remove(provider);
            throw new DaemonRequestException("Provider baseUrl is required");
        }
        if (provider.Models.Count == 0)
        {
            if (isNew) config.Providers.Remove(provider);
            throw new DaemonRequestException("At least one model is required");
        }

        runtime.ConfigStore.Save();
        return ProviderJson(provider, provider.Id.Equals(config.GetActiveProfile().Provider, StringComparison.OrdinalIgnoreCase)).ToJsonString();
    }

    public string UseProvider(JsonObject parameters)
    {
        var id = RequiredString(parameters, "id");
        var provider = runtime.ConfigStore.Config.FindProvider(id)
                       ?? throw new DaemonRequestException($"Provider not found: {id}");
        var profile = runtime.ConfigStore.Config.GetActiveProfile();
        profile.Provider = provider.Id;
        if (profile.Model is null || !provider.Models.Any(model => model.Id.Equals(profile.Model, StringComparison.OrdinalIgnoreCase)))
            profile.Model = provider.Models.FirstOrDefault()?.Id;
        runtime.ConfigStore.Save();
        return provider.Id;
    }

    public string RemoveProvider(JsonObject parameters)
    {
        var id = RequiredString(parameters, "id");
        var config = runtime.ConfigStore.Config;
        var provider = config.FindProvider(id)
                       ?? throw new DaemonRequestException($"Provider not found: {id}");
        config.Providers.Remove(provider);
        foreach (var profile in config.Profiles.Values.Where(profile =>
                     id.Equals(profile.Provider, StringComparison.OrdinalIgnoreCase)))
        {
            profile.Provider = null;
            profile.Model = null;
        }
        runtime.ConfigStore.Save();
        return id;
    }

    public async Task<string> TestProvidersAsync(JsonObject parameters, CancellationToken ct)
    {
        var id = OptionalString(parameters, "id");
        var providers = runtime.ConfigStore.Config.Providers
            .Where(provider => id is null || provider.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (providers.Count == 0) throw new DaemonRequestException("No matching providers");

        var reports = await Task.WhenAll(providers.Select(provider => runtime.Health.CheckAsync(provider, ct)))
            .ConfigureAwait(false);
        var data = new JsonArray();
        foreach (var report in reports)
        {
            data.Add((JsonNode)new JsonObject
            {
                ["id"] = report.ProviderId,
                ["online"] = report.Online,
                ["latencyMs"] = report.LatencyMs,
                ["detail"] = report.Detail,
            });
        }
        return data.ToJsonString();
    }

    public string ModelCatalog()
    {
        string? activeRef = null;
        try { activeRef = runtime.Providers.ResolveActive(runtime.Workspace.Config).Ref; }
        catch (Exception) { }

        var models = new JsonArray();
        foreach (var model in runtime.Models.All(includeDisabledProviders: true))
        {
            models.Add((JsonNode)new JsonObject
            {
                ["ref"] = model.Ref,
                ["active"] = model.Ref.Equals(activeRef, StringComparison.OrdinalIgnoreCase),
                ["provider"] = model.Provider.Id,
                ["providerEnabled"] = model.Provider.Enabled,
                ["id"] = model.Model.Id,
                ["alias"] = model.Model.Alias,
                ["contextWindow"] = model.Model.ContextWindow,
                ["maxOutput"] = model.Model.MaxOutput,
                ["tags"] = Strings(model.Model.Tags ?? []),
                ["capabilities"] = new JsonObject
                {
                    ["streaming"] = model.Capabilities.Streaming,
                    ["tools"] = model.Capabilities.ToolCalling,
                    ["thinking"] = model.Capabilities.Thinking,
                    ["vision"] = model.Capabilities.Vision,
                    ["reasoning"] = model.Capabilities.Reasoning,
                    ["maxReasoningLevel"] = model.Capabilities.MaxReasoningLevel.ToWireValue(),
                    ["mcp"] = model.Capabilities.Mcp,
                },
            });
        }
        return models.ToJsonString();
    }

    public async Task<string> TestModelAsync(JsonObject parameters, CancellationToken ct)
    {
        var reference = RequiredString(parameters, "model");
        var model = runtime.Models.Resolve(reference)
                    ?? throw new DaemonRequestException($"Model not found: {reference}");
        var result = await runtime.Providers.TestModelAsync(model, ct).ConfigureAwait(false);
        return new JsonObject
        {
            ["model"] = model.Ref,
            ["success"] = result.Success,
            ["detail"] = result.Detail,
            ["latencyMs"] = result.LatencyMs,
        }.ToJsonString();
    }

    public async Task<string> FetchProviderModelsAsync(JsonObject parameters, CancellationToken ct)
    {
        var id = RequiredString(parameters, "id");
        var provider = runtime.ConfigStore.Config.FindProvider(id)
                       ?? throw new DaemonRequestException($"Provider not found: {id}");
        var url = OptionalString(parameters, "url") ?? provider.ModelListUrl;
        var ids = await runtime.Providers.FetchModelsAsync(provider, url, ct).ConfigureAwait(false);
        var existing = provider.Models.ToDictionary(model => model.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var modelId in ids)
        {
            if (existing.ContainsKey(modelId)) continue;
            var model = new ModelConfig { Id = modelId };
            provider.Models.Add(model);
            existing[modelId] = model;
        }
        runtime.ConfigStore.Save();
        return Strings(ids).ToJsonString();
    }

    public string UpdateModel(JsonObject parameters)
    {
        var providerId = RequiredString(parameters, "provider");
        var modelId = RequiredString(parameters, "id");
        var provider = runtime.ConfigStore.Config.FindProvider(providerId)
                       ?? throw new DaemonRequestException($"Provider not found: {providerId}");
        var model = provider.Models.FirstOrDefault(item =>
            item.Id.Equals(modelId, StringComparison.OrdinalIgnoreCase))
                    ?? throw new DaemonRequestException($"Model not found: {providerId}/{modelId}");

        if (parameters.ContainsKey("alias"))
            model.Alias = parameters["alias"]?.GetValue<string>()?.Trim() is { Length: > 0 } alias ? alias : null;
        if (parameters["contextWindow"]?.GetValue<int?>() is { } contextWindow)
        {
            if (contextWindow < 1_024 || contextWindow > 10_000_000)
                throw new DaemonRequestException("params.contextWindow must be between 1024 and 10000000");
            model.ContextWindow = contextWindow;
        }
        if (parameters["maxOutput"]?.GetValue<int?>() is { } maxOutput)
        {
            if (maxOutput < 128 || maxOutput > 1_000_000)
                throw new DaemonRequestException("params.maxOutput must be between 128 and 1000000");
            model.MaxOutput = maxOutput;
        }
        if (parameters["vision"]?.GetValue<bool?>() is { } vision)
            model.Capabilities.Vision = vision;
        runtime.ConfigStore.Save();
        return new JsonObject
        {
            ["provider"] = provider.Id,
            ["id"] = model.Id,
            ["alias"] = model.Alias,
            ["contextWindow"] = model.ContextWindow,
            ["maxOutput"] = model.MaxOutput,
            ["vision"] = model.Capabilities.Vision,
        }.ToJsonString();
    }

    public string ListMcpServers()
    {
        var merged = runtime.Mcp.LoadServerConfigs(runtime.Workspace);
        var workspaceFile = LoadWorkspaceMcpConfig();
        var workspaceInline = runtime.Workspace.Config?.Mcp?.Servers;
        var statuses = runtime.Mcp.Status.ToDictionary(status => status.Name, StringComparer.OrdinalIgnoreCase);
        var servers = new JsonArray();

        foreach (var (name, server) in merged.OrderBy(item => item.Key))
        {
            statuses.TryGetValue(name, out var status);
            var scope = workspaceInline?.ContainsKey(name) == true || workspaceFile.Servers.ContainsKey(name)
                ? "workspace"
                : "global";
            servers.Add((JsonNode)McpServerJson(name, scope, server, status));
        }
        return servers.ToJsonString();
    }

    public async Task<string> UpsertMcpServerAsync(JsonObject parameters, CancellationToken ct)
    {
        var name = RequiredString(parameters, "name");
        var scope = OptionalString(parameters, "scope") ?? "workspace";
        if (scope is not ("workspace" or "global"))
            throw new DaemonRequestException("MCP scope must be workspace or global");
        var input = parameters["server"] as JsonObject
                    ?? throw new DaemonRequestException("params.server is required");

        var useInlineWorkspaceConfig = scope == "workspace"
                                       && runtime.Workspace.Config?.Mcp?.Servers.ContainsKey(name) == true;
        var target = scope == "global"
            ? runtime.ConfigStore.Config.Mcp
            : useInlineWorkspaceConfig ? runtime.Workspace.Config!.Mcp! : LoadWorkspaceMcpConfig();
        target.Servers.TryGetValue(name, out var existing);
        var server = existing ?? new McpServerConfig();
        server.Transport = OptionalString(input, "transport") ?? server.Transport;
        server.Command = OptionalString(input, "command") ?? server.Command;
        server.Url = OptionalString(input, "url") ?? server.Url;
        server.Enabled = input["enabled"]?.GetValue<bool?>() ?? server.Enabled;
        if (input["args"] is JsonArray args)
            server.Args = args.Select(node => node?.GetValue<string>() ?? "").Where(value => value.Length > 0).ToList();
        if (input["env"] is JsonObject env)
            server.Env = env.ToDictionary(item => item.Key, item => item.Value?.GetValue<string>() ?? "");

        ValidateMcpServer(name, server);
        target.Servers[name] = server;
        SaveMcpConfig(scope, target, useInlineWorkspaceConfig);
        await runtime.ConnectMcpAsync(ct).ConfigureAwait(false);
        return ListMcpServers();
    }

    public async Task<string> RemoveMcpServerAsync(JsonObject parameters, CancellationToken ct)
    {
        var name = RequiredString(parameters, "name");
        var scope = OptionalString(parameters, "scope") ?? "workspace";
        var useInlineWorkspaceConfig = scope == "workspace"
                                       && runtime.Workspace.Config?.Mcp?.Servers.ContainsKey(name) == true;
        var target = scope == "global"
            ? runtime.ConfigStore.Config.Mcp
            : useInlineWorkspaceConfig ? runtime.Workspace.Config!.Mcp! : LoadWorkspaceMcpConfig();
        if (!target.Servers.Remove(name))
            throw new DaemonRequestException($"MCP server not found in {scope} scope: {name}");
        SaveMcpConfig(scope, target, useInlineWorkspaceConfig);
        await runtime.ConnectMcpAsync(ct).ConfigureAwait(false);
        return ListMcpServers();
    }

    public async Task<string> ReloadMcpAsync(CancellationToken ct)
    {
        await runtime.ConnectMcpAsync(ct).ConfigureAwait(false);
        return ListMcpServers();
    }

    public string ListSkills()
    {
        var skills = new JsonArray();
        foreach (var skill in runtime.Skills.Discover(runtime.Workspace))
        {
            skills.Add((JsonNode)new JsonObject
            {
                ["name"] = skill.Name,
                ["description"] = skill.Manifest.Description,
                ["version"] = skill.Manifest.Version,
                ["enabled"] = skill.Enabled,
                ["directory"] = skill.Directory,
                ["scope"] = IsUnder(skill.Directory, runtime.Workspace.SkillsDir) ? "workspace" : "global",
            });
        }
        return skills.ToJsonString();
    }

    public string ToggleSkill(JsonObject parameters)
    {
        var name = RequiredString(parameters, "name");
        var enabled = parameters["enabled"]?.GetValue<bool?>()
                      ?? throw new DaemonRequestException("params.enabled is required");
        if (!runtime.Skills.Discover(runtime.Workspace).Any(skill =>
                skill.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new DaemonRequestException($"Skill not found: {name}");
        runtime.Skills.SetEnabled(name, enabled);
        return ListSkills();
    }

    public string Usage(JsonObject parameters)
    {
        var days = parameters["days"]?.GetValue<int?>();
        var since = days is { } count ? DateTimeOffset.UtcNow.AddDays(-count) : (DateTimeOffset?)null;
        var data = new JsonArray();
        foreach (var aggregate in runtime.Usage.Aggregate(since))
        {
            data.Add((JsonNode)new JsonObject
            {
                ["provider"] = aggregate.Provider,
                ["model"] = aggregate.Model,
                ["calls"] = aggregate.Calls,
                ["failures"] = aggregate.Failures,
                ["inputTokens"] = aggregate.InputTokens,
                ["totalInputTokens"] = aggregate.TotalInputTokens,
                ["cachedInputTokens"] = aggregate.CachedInputTokens,
                ["cacheCreationInputTokens"] = aggregate.CacheCreationInputTokens,
                ["outputTokens"] = aggregate.OutputTokens,
                ["cost"] = aggregate.Cost,
                ["avgLatencyMs"] = aggregate.AvgLatencyMs,
                ["successRate"] = aggregate.SuccessRate,
            });
        }
        return data.ToJsonString();
    }

    public string ListProjects()
    {
        var projects = new JsonArray();
        foreach (var project in runtime.Projects.List())
        {
            projects.Add((JsonNode)new JsonObject
            {
                ["id"] = project.Id,
                ["path"] = project.Path,
                ["name"] = project.Name,
                ["createdAt"] = project.CreatedAt,
                ["updatedAt"] = project.UpdatedAt,
            });
        }
        return projects.ToJsonString();
    }

    public string UpsertProject(JsonObject parameters)
    {
        var path = RequiredString(parameters, "path");
        if (SeekClawPaths.IsForbiddenProjectPath(path))
            throw new DaemonRequestException(
                "The user profile or the SeekClaw state directory cannot be registered as a project.");
        var project = runtime.Projects.Upsert(
            OptionalString(parameters, "id"), path, OptionalString(parameters, "name"));
        return new JsonObject
        {
            ["id"] = project.Id,
            ["path"] = project.Path,
            ["name"] = project.Name,
            ["createdAt"] = project.CreatedAt,
            ["updatedAt"] = project.UpdatedAt,
        }.ToJsonString();
    }

    public string RemoveProject(JsonObject parameters)
    {
        var id = RequiredString(parameters, "id");
        // The desktop cleanup of invalid project rows (e.g. a project whose path is the
        // user profile) keeps sessions so history is preserved instead of being deleted
        // together with the row; a user-initiated removal still deletes the sessions.
        var keepSessions = parameters["keepSessions"]?.GetValue<bool?>() ?? false;
        var project = runtime.Projects.Get(id)
                      ?? throw new DaemonRequestException($"Project not found: {id}");
        if (!keepSessions)
        {
            var workspace = Directory.Exists(project.Path)
                ? runtime.Workspaces.Detect(project.Path)
                : new WorkspaceInfo { Root = Path.GetFullPath(project.Path), ProjectKinds = [] };
            runtime.Sessions.DeleteAll(workspace);
        }
        runtime.Projects.Remove(id);
        return id;
    }

    public string ListSessions(JsonObject parameters)
    {
        var workspace = SessionWorkspace(parameters);
        var includeArchived = parameters["includeArchived"]?.GetValue<bool?>() ?? false;
        return JsonSerializer.Serialize(
            runtime.Sessions.List(workspace, includeArchived).ToList(),
            SeekClawJsonContext.Default.ListSessionHeader);
    }

    public string GetSession(JsonObject parameters)
    {
        var id = RequiredString(parameters, "id");
        var workspace = SessionWorkspace(parameters);
        var session = runtime.Sessions.Load(workspace, id)
                      ?? throw new DaemonRequestException($"Session not found: {id}");
        var messages = new JsonArray();
        foreach (var message in session.Messages)
        {
            var images = new JsonArray();
            foreach (var image in message.Images ?? [])
                images.Add((JsonNode)new JsonObject
                {
                    ["id"] = image.Id,
                    ["name"] = image.Name,
                    ["mediaType"] = image.MediaType,
                    ["data"] = image.Data,
                    ["sizeBytes"] = image.SizeBytes,
                });
            var viewedImages = new JsonArray();
            foreach (var image in message.ViewedImages ?? [])
                viewedImages.Add((JsonNode)new JsonObject
                {
                    ["id"] = image.Id,
                    ["name"] = image.Name,
                });
            var toolCalls = new JsonArray();
            foreach (var call in message.ToolCalls ?? [])
            {
                toolCalls.Add((JsonNode)new JsonObject
                {
                    ["id"] = call.Id,
                    ["name"] = call.Name,
                });
            }
            messages.Add((JsonNode)new JsonObject
            {
                ["role"] = message.Role.ToString().ToLowerInvariant(),
                ["text"] = message.Text,
                ["images"] = images,
                ["thinking"] = message.Thinking,
                ["viewedImages"] = viewedImages,
                ["toolCalls"] = toolCalls,
                ["toolCallId"] = message.ToolCallId,
                ["toolName"] = message.ToolName,
                ["toolSuccess"] = message.ToolSuccess,
                ["toolDiff"] = message.ToolDiff,
                ["toolFilePath"] = message.ToolFilePath,
            });
        }
        return new JsonObject
        {
            ["id"] = session.Header.Id,
            ["title"] = session.Header.Title,
            ["workspace"] = workspace.IsGlobal ? null : session.Header.Workspace ?? workspace.Root,
            ["archived"] = session.Header.Archived,
            ["reasoningLevel"] = session.Header.ReasoningLevel.ToWireValue(),
            ["networkEnabled"] = session.Header.NetworkEnabled,
            ["createdAt"] = session.Header.CreatedAt,
            ["updatedAt"] = session.Header.UpdatedAt,
            ["messages"] = messages,
        }.ToJsonString();
    }

    public string UpdateSession(JsonObject parameters)
    {
        var workspace = SessionWorkspace(parameters);
        var id = RequiredString(parameters, "id");
        var title = parameters.ContainsKey("title")
            ? parameters["title"]?.GetValue<string>() ?? ""
            : null;
        var reasoningLevel = parameters.ContainsKey("reasoningLevel")
            ? ParseReasoningLevel(parameters["reasoningLevel"], "params.reasoningLevel")
            : (ReasoningLevel?)null;
        var networkEnabled = parameters.ContainsKey("networkEnabled")
            ? parameters["networkEnabled"]?.GetValue<bool?>() ?? true
            : (bool?)null;
        try
        {
            var header = runtime.Sessions.UpdateMetadata(
                workspace, id, title: title, reasoningLevel: reasoningLevel,
                networkEnabled: networkEnabled);
            return JsonSerializer.Serialize(header, SeekClawJsonContext.Default.SessionHeader);
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException or ArgumentException)
        {
            throw new DaemonRequestException(ex.Message);
        }
    }

    public string TruncateSession(JsonObject parameters)
    {
        var workspace = SessionWorkspace(parameters);
        var id = RequiredString(parameters, "id");
        var keepCount = parameters["keepCount"]?.GetValue<int?>();
        if (keepCount is null or < 0)
            throw new DaemonRequestException("params.keepCount is required (>= 0)");
        runtime.Sessions.Truncate(workspace, id, keepCount.Value);
        var remaining = runtime.Sessions.Load(workspace, id)?.Messages.Count ?? 0;
        return remaining.ToString();
    }

    public string ArchiveSession(JsonObject parameters)
    {
        var workspace = SessionWorkspace(parameters);
        var id = RequiredString(parameters, "id");
        var archived = parameters["archived"]?.GetValue<bool?>() ?? true;
        try
        {
            var header = runtime.Sessions.UpdateMetadata(workspace, id, archived: archived);
            return JsonSerializer.Serialize(header, SeekClawJsonContext.Default.SessionHeader);
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException or ArgumentException)
        {
            throw new DaemonRequestException(ex.Message);
        }
    }

    public string DeleteSession(JsonObject parameters)
    {
        var workspace = SessionWorkspace(parameters);
        var id = RequiredString(parameters, "id");
        try
        {
            runtime.Sessions.Delete(workspace, id);
            return id;
        }
        catch (Exception ex) when (ex is FileNotFoundException or ArgumentException)
        {
            throw new DaemonRequestException(ex.Message);
        }
    }

    public async Task<string> DoctorAsync(CancellationToken ct)
    {
        var checks = new JsonArray();
        foreach (var check in runtime.Health.RunChecks(runtime.Workspace))
        {
            checks.Add((JsonNode)new JsonObject
            {
                ["name"] = check.Name,
                ["ok"] = check.Ok,
                ["detail"] = check.Detail,
                ["kind"] = "runtime",
            });
        }

        var providers = runtime.ConfigStore.Config.Providers.Where(provider => provider.Enabled).ToList();
        var reports = await Task.WhenAll(providers.Select(provider => runtime.Health.CheckAsync(provider, ct)))
            .ConfigureAwait(false);
        foreach (var report in reports)
        {
            checks.Add((JsonNode)new JsonObject
            {
                ["name"] = $"Provider {report.ProviderId}",
                ["ok"] = report.Online,
                ["detail"] = $"{report.Detail} ({report.LatencyMs:0} ms)",
                ["kind"] = "provider",
            });
        }
        return checks.ToJsonString();
    }

    private static JsonObject ProviderJson(ProviderConfig provider, bool active) => new()
    {
        ["id"] = provider.Id,
        ["name"] = provider.DisplayName,
        ["kind"] = provider.Kind,
        ["baseUrl"] = provider.BaseUrl,
        ["apiKey"] = provider.ApiKey,
        ["apiKeyConfigured"] = !string.IsNullOrWhiteSpace(provider.ResolveApiKey()),
        ["models"] = Strings(provider.Models.Select(model => model.Id)),
        ["enabled"] = provider.Enabled,
        ["priority"] = provider.Priority,
        ["timeoutSeconds"] = provider.TimeoutSeconds,
        ["modelListUrl"] = provider.ModelListUrl,
        ["promptCaching"] = provider.PromptCaching,
        ["proxy"] = provider.Proxy,
        ["active"] = active,
    };

    private static JsonObject McpServerJson(
        string name,
        string scope,
        McpServerConfig server,
        McpServerStatus? status) => new()
    {
        ["name"] = name,
        ["scope"] = scope,
        ["transport"] = server.Transport,
        ["command"] = server.Command,
        ["args"] = Strings(server.Args ?? []),
        ["url"] = server.Url,
        ["envKeys"] = Strings(server.Env is null ? Enumerable.Empty<string>() : server.Env.Keys),
        ["enabled"] = server.Enabled,
        ["connected"] = status?.Connected ?? false,
        ["toolCount"] = status?.ToolCount ?? 0,
        ["error"] = status?.Error,
    };

    private McpConfig LoadWorkspaceMcpConfig()
    {
        var file = Path.Combine(runtime.Workspace.McpDir, "servers.json");
        if (!File.Exists(file)) return new McpConfig();
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(file), SeekClawJsonContext.Default.McpConfig)
                   ?? new McpConfig();
        }
        catch (JsonException)
        {
            throw new DaemonRequestException($"Invalid MCP config: {file}");
        }
    }

    private void SaveMcpConfig(string scope, McpConfig config, bool inlineWorkspaceConfig)
    {
        if (scope == "global")
        {
            runtime.ConfigStore.Save();
            return;
        }

        if (inlineWorkspaceConfig)
        {
            Directory.CreateDirectory(runtime.Workspace.SeekClawDir);
            File.WriteAllText(
                Path.Combine(runtime.Workspace.SeekClawDir, "config.json"),
                JsonSerializer.Serialize(runtime.Workspace.Config!, SeekClawJsonContext.Default.WorkspaceConfig));
            return;
        }

        Directory.CreateDirectory(runtime.Workspace.McpDir);
        File.WriteAllText(
            Path.Combine(runtime.Workspace.McpDir, "servers.json"),
            JsonSerializer.Serialize(config, SeekClawJsonContext.Default.McpConfig));
    }

    private static void ValidateMcpServer(string name, McpServerConfig server)
    {
        var valid = server.Transport.ToLowerInvariant() switch
        {
            "stdio" => !string.IsNullOrWhiteSpace(server.Command),
            "sse" => Uri.TryCreate(server.Url, UriKind.Absolute, out _),
            _ => false,
        };
        if (!valid)
            throw new DaemonRequestException($"MCP server '{name}' has an invalid transport/command/url combination");
    }

    private static string RequiredString(JsonObject parameters, string name) =>
        OptionalString(parameters, name)
        ?? throw new DaemonRequestException($"params.{name} is required");

    private static ReasoningLevel ParseReasoningLevel(JsonNode? node, string parameterName)
    {
        var value = node is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text)
            ? text
            : null;
        if (ReasoningLevelExtensions.TryParse(value, out var level)) return level;
        throw new DaemonRequestException(
            $"{parameterName} must be one of: none, low, medium, high, max, xhigh, ultra");
    }

    private static string? OptionalString(JsonObject parameters, string name)
    {
        var value = parameters[name]?.GetValue<string>()?.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private SeekClaw.Runtime.Workspaces.WorkspaceInfo SessionWorkspace(JsonObject parameters)
    {
        if (parameters["global"]?.GetValue<bool?>() == true) return globalWorkspace;
        var requested = OptionalString(parameters, "workspace");
        if (requested is null) return runtime.Workspace;

        string fullPath;
        try { fullPath = Path.GetFullPath(requested); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new DaemonRequestException($"Invalid workspace path: {ex.Message}");
        }
        if (!Directory.Exists(fullPath))
            throw new DaemonRequestException($"Workspace directory not found: {fullPath}");
        return runtime.Workspaces.Detect(fullPath);
    }

    private static JsonArray Strings(IEnumerable<string> values) =>
        new(values.Select(value => JsonValue.Create(value)).ToArray());

    private static bool IsUnder(string path, string root)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return relative != ".."
               && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
               && !Path.IsPathRooted(relative);
    }
}
