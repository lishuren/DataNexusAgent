using System.Text.Json;
using DataNexus.Agents;
using DataNexus.Core;
using DataNexus.Identity;
using DataNexus.Models;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DataNexus.Tests;

public sealed class ExternalAgentAdapterTests
{
    [Fact]
    public async Task RunAsync_PreservesRequestContextThroughMafAdapter()
    {
        var trace = new ExternalAgentExecutionTrace();
        var agent = ExternalAgentAdapter.Create(
            new NoOpChatClient(),
            CreateRunner(),
            CreateAgent("result-data"),
            CreateUser(),
            NullLogger.Instance,
            outputDestination: "database",
            parameters: CreateParameters(),
            trace);

        var response = await agent.RunAsync("hello", cancellationToken: CancellationToken.None);

        var result = Assert.IsType<ProcessingResult>(trace.LastResult);
        Assert.True(result.Success);

        var data = Assert.IsType<JsonElement>(result.Data);
        Assert.Equal("database", data.GetProperty("outputDestination").GetString());
        Assert.Equal(2, data.GetProperty("parameterCount").GetInt32());
        Assert.Contains("database", response.Text ?? string.Empty);
    }

    [Fact]
    public async Task RunStreamingAsync_PreservesOutputDestinationThroughMafAdapter()
    {
        var trace = new ExternalAgentExecutionTrace();
        var agent = ExternalAgentAdapter.Create(
            new NoOpChatClient(),
            CreateRunner(),
            CreateAgent("stream-success"),
            CreateUser(),
            NullLogger.Instance,
            outputDestination: "database",
            parameters: CreateParameters(),
            trace);

        var updates = new List<AgentResponseUpdate>();
        await foreach (var update in agent.RunStreamingAsync("hello", cancellationToken: CancellationToken.None))
            updates.Add(update);

        Assert.Collection(updates,
            update => Assert.Equal("input=hello", update.Text),
            update => Assert.Equal("|user=user-123", update.Text));

        var result = Assert.IsType<ProcessingResult>(trace.LastResult);
        Assert.True(result.Success);
        Assert.Equal("done:database", result.Message);
    }

    private static ExternalProcessRunner CreateRunner()
    {
        var options = Options.Create(new ExternalAgentOptions
        {
            Enabled = true,
            AllowedCommands = ["dotnet"],
            MaxTimeoutSeconds = 30,
        });

        return new ExternalProcessRunner(options, NullLogger<ExternalProcessRunner>.Instance);
    }

    private static AgentDefinition CreateAgent(string mode) => new(
        Id: 11,
        Name: "Fixture Agent",
        Icon: "🤖",
        Description: "Fixture-backed external agent",
        ExecutionType: AgentExecutionType.External,
        SystemPrompt: string.Empty,
        Command: "dotnet",
        Arguments: BuildFixtureArguments(mode),
        WorkingDirectory: null,
        TimeoutSeconds: 15,
        UiSchema: null,
        Plugins: string.Empty,
        Skills: string.Empty,
        Scope: SkillScope.Private,
        OwnerId: null,
        PublishedByUserId: null,
        IsBuiltIn: false);

    private static UserContext CreateUser() => new()
    {
        UserId = "user-123",
        IsAuthenticated = true,
    };

    private static IReadOnlyDictionary<string, string> CreateParameters() => new Dictionary<string, string>
    {
        ["alpha"] = "1",
        ["beta"] = "2",
    };

    private static string BuildFixtureArguments(string mode)
    {
        var configuration = typeof(ExternalAgentAdapterTests).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyConfigurationAttribute), false)
            .OfType<System.Reflection.AssemblyConfigurationAttribute>()
            .FirstOrDefault()?.Configuration ?? "Debug";

        return $"run --no-build -c {configuration} -f net10.0 --project \"{GetFixtureProjectPath()}\" -- {mode}";
    }

    private static string GetFixtureProjectPath()
    {
        var root = FindRepoRoot();
        return Path.Combine(root, "tests", "ExternalAgentFixture", "ExternalAgentFixture.csproj");
    }

    private static string FindRepoRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "DataNexus.sln")))
                return current.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate the DataNexus repository root.");
    }

    private sealed class NoOpChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromException<ChatResponse>(new NotSupportedException("The dummy chat client should not be invoked."));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            ThrowStreaming();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }

        private static async IAsyncEnumerable<ChatResponseUpdate> ThrowStreaming()
        {
            throw new NotSupportedException("The dummy chat client should not be invoked.");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }
}