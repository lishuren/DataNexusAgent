using System.Reflection;
using System.Text.Json;
using DataNexus.Agents;
using DataNexus.Core;
using DataNexus.Identity;
using DataNexus.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DataNexus.Tests;

public sealed class ExternalProcessRunnerTests
{
    [Fact]
    public async Task RunStreamingAsync_EmitsStatusChunksAndResult()
    {
        var runner = CreateRunner();
        var agent = CreateAgent("stream-success");
        var request = CreateRequest();
        var user = CreateUser();

        var events = await CollectAsync(runner.RunStreamingAsync(agent, request, user, CancellationToken.None));

        Assert.Collection(events,
            streamEvent =>
            {
                Assert.Equal("status", streamEvent.Type);
                Assert.Equal(agent.Name, streamEvent.SourceId);
                Assert.Contains("launched using streamed NDJSON protocol", streamEvent.Message);
            },
            streamEvent =>
            {
                Assert.Equal("status", streamEvent.Type);
                Assert.Equal("accepted:Fixture Agent", streamEvent.Message);
            },
            streamEvent =>
            {
                Assert.Equal("chunk", streamEvent.Type);
                Assert.Equal("input=hello", streamEvent.Text);
            },
            streamEvent =>
            {
                Assert.Equal("chunk", streamEvent.Type);
                Assert.Equal("|user=user-123", streamEvent.Text);
            },
            streamEvent =>
            {
                var result = Assert.IsType<ProcessingResult>(streamEvent.Result);
                Assert.Equal("result", streamEvent.Type);
                Assert.True(result.Success);
                Assert.Equal("done:stdout", result.Message);
            });
    }

    [Fact]
    public async Task RunAsync_AggregatesChunksWhenFinalResultHasNoData()
    {
        var runner = CreateRunner();
        var result = await runner.RunAsync(CreateAgent("stream-success"), CreateRequest(), CreateUser(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("done:stdout", result.Message);
        Assert.Equal("input=hello|user=user-123", Assert.IsType<string>(result.Data));
    }

    [Fact]
    public async Task RunAsync_PreservesStructuredResultData()
    {
        var runner = CreateRunner();
        var result = await runner.RunAsync(CreateAgent("result-data"), CreateRequest(), CreateUser(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("with-data", result.Message);

        var data = Assert.IsType<JsonElement>(result.Data);
        Assert.Equal("hello", data.GetProperty("input").GetString());
        Assert.Equal("stdout", data.GetProperty("outputDestination").GetString());
        Assert.Equal(2, data.GetProperty("parameterCount").GetInt32());
    }

    [Fact]
    public async Task RunAsync_FailsWhenFinalResultEventIsMissing()
    {
        var runner = CreateRunner();
        var result = await runner.RunAsync(CreateAgent("missing-result"), CreateRequest(), CreateUser(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("without emitting a final result event", result.Message);
    }

    [Fact]
    public async Task RunStreamingAsync_ConvertsInvalidProtocolOutputToFailureResult()
    {
        var runner = CreateRunner();
        var events = await CollectAsync(runner.RunStreamingAsync(
            CreateAgent("invalid-json"),
            CreateRequest(),
            CreateUser(),
            CancellationToken.None));

        var resultEvent = Assert.Single(events, streamEvent => streamEvent.Type == "result");
        var result = Assert.IsType<ProcessingResult>(resultEvent.Result);

        Assert.False(result.Success);
        Assert.Contains("invalid protocol output", result.Message);
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
        Id: 7,
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

    private static ProcessingRequest CreateRequest() => new(
        AgentId: 7,
        InputSource: "hello",
        OutputDestination: "stdout",
        Parameters: new Dictionary<string, string>
        {
            ["alpha"] = "1",
            ["beta"] = "2",
        });

    private static UserContext CreateUser() => new()
    {
        UserId = "user-123",
        IsAuthenticated = true,
    };

    private static string BuildFixtureArguments(string mode)
    {
        var configuration = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyConfigurationAttribute>()?
            .Configuration ?? "Debug";

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

    private static async Task<List<ProcessingStreamEvent>> CollectAsync(IAsyncEnumerable<ProcessingStreamEvent> stream)
    {
        var events = new List<ProcessingStreamEvent>();

        await foreach (var streamEvent in stream)
            events.Add(streamEvent);

        return events;
    }
}