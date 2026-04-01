# Microsoft Agent Framework — Technical Design & Learning Guide

> This document covers every MAF concept used in DataNexus and explains *why* each pattern exists.
> After reading it you should be able to build, chain, and extend MAF agents from scratch.

---

## Table of Contents

1. [What is MAF?](#1-what-is-maf)
2. [Package Setup](#2-package-setup)
3. [The Core Type: `AIAgent`](#3-the-core-type-aiagent)
4. [Registering `IChatClient`](#4-registering-ichatclient)
5. [Building a `ChatClientAgent`](#5-building-a-chatclientagent)
6. [Middleware: `.AsBuilder().Use().Build()`](#6-middleware-asbuilderbuild)
7. [Running a Single Agent](#7-running-a-single-agent)
8. [Structured Output: `RunAsync<T>()`](#8-structured-output-runasinct)
9. [Streaming: `RunStreamingAsync()`](#9-streaming-runstreamingasync)
10. [Dynamic Context: `AIContextProvider`](#10-dynamic-context-aicontextprovider)
11. [Workflow Patterns](#11-workflow-patterns)
    - [Sequential](#111-sequential)  
    - [Concurrent (Fan-out / Fan-in)](#112-concurrent-fan-out--fan-in)  
    - [Handoff (Triage + Specialists)](#113-handoff-triage--specialists)  
    - [Group Chat (Round-Robin)](#114-group-chat-round-robin)  
    - [DAG Graph (WorkflowBuilder)](#115-dag-graph-workflowbuilder)
12. [Executing Workflows: `InProcessExecution`](#12-executing-workflows-inprocessexecution)
13. [Streaming Workflows: `StreamingRun`](#13-streaming-workflows-streamingrun)
14. [External Agent Pattern](#14-external-agent-pattern)
15. [Self-Correction Loop](#15-self-correction-loop)
16. [Plugin Middleware Pattern](#16-plugin-middleware-pattern)
17. [Error Signalling with `PluginError`](#17-error-signalling-with-pluginerror)
18. [Key Package Constraint](#18-key-package-constraint)
19. [Cheat Sheet: MAF API Surface](#19-cheat-sheet-maf-api-surface)

---

## 1. What is MAF?

**Microsoft Agent Framework** (NuGet: `Microsoft.Agents.AI`, `Microsoft.Agents.AI.Workflows`) is a .NET library for composing, running, and orchestrating LLM-backed agents.

The central mental model:

```
IChatClient  ──────►  ChatClientAgent  ──────►  middleware pipeline  ──────►  AIAgent
(raw HTTP to LLM)   (wraps the client)           (.AsBuilder().Use())      (runnable unit)
```

The framework separates:
- **What an agent does** (`ChatClientAgent` + options/instructions)
- **How messages are processed** (`.Use()` middleware)
- **How multiple agents collaborate** (`AgentWorkflowBuilder` + `InProcessExecution`)

---

## 2. Package Setup

```xml
<!-- backend/DataNexus.csproj -->
<PackageReference Include="Microsoft.Agents.AI"           Version="1.0.0-rc4" />
<PackageReference Include="Microsoft.Agents.AI.Workflows" Version="1.0.0-rc4" />
<PackageReference Include="Microsoft.Extensions.AI"       Version="10.3.0" />   <!-- pinned: see §18 -->
<PackageReference Include="Microsoft.Extensions.AI.OpenAI" Version="10.3.0" />  <!-- pinned: see §18 -->
```

Primary namespaces imported throughout the DataNexus backend:

```csharp
using Microsoft.Agents.AI;           // AIAgent, ChatClientAgent, AgentResponse,
                                     // AgentResponseUpdate, AIContextProvider …
using Microsoft.Agents.AI.Workflows; // AgentWorkflowBuilder, InProcessExecution,
                                     // WorkflowBuilder, Run, StreamingRun …
using Microsoft.Extensions.AI;       // IChatClient, ChatMessage, ChatRole, ChatOptions …
```

---

## 3. The Core Type: `AIAgent`

Everything in MAF is an `AIAgent`. It is an abstract base with two key methods:

```csharp
// Buffered — waits for the full response
Task<AgentResponse> RunAsync(
    IEnumerable<ChatMessage> messages,
    AgentSession? session,
    AgentRunOptions? options,
    CancellationToken cancellationToken);

// Streaming — yields chunks as they arrive
IAsyncEnumerable<AgentResponseUpdate> RunStreamingAsync(
    IEnumerable<ChatMessage> messages,
    AgentSession? session,
    AgentRunOptions? options,
    CancellationToken cancellationToken);
```

There are also convenience overloads that accept a plain `string` input — MAF wraps it into a `ChatMessage`:

```csharp
var response = await agent.RunAsync("Parse this file…", cancellationToken: ct);
string text = response.Text;  // convenience property — text of all assistant messages joined

await foreach (var update in agent.RunStreamingAsync("Parse…", cancellationToken: ct))
{
    Console.Write(update.Text);    // chunk text
    Console.Write(update.AgentId); // which agent produced this chunk
}
```

**`AgentResponse`** contains:
- `IReadOnlyList<ChatMessage> Messages` — the full assistant reply
- `string? Text` — shortcut: `.Messages` assistant text joined
- Workflow-level properties (event count, etc.) when used inside a workflow

**`AgentResponseUpdate`** (streaming):
- `ChatRole Role`
- `string? Text` — the chunk
- `string? AgentId` — name of the agent that emitted the chunk

---

## 4. Registering `IChatClient`

MAF's `ChatClientAgent` is backed by `IChatClient` from `Microsoft.Extensions.AI`. In DataNexus, the production client targets GitHub Models (GPT-4o via the Azure AI Inference endpoint):

```csharp
// Program.cs
builder.Services.AddSingleton<IChatClient>(_ =>
{
    var apiKey  = builder.Configuration["GitHubModels:ApiKey"];
    var endpoint = new Uri(builder.Configuration["GitHubModels:Endpoint"]
                           ?? "https://models.inference.ai.azure.com");
    var model   = builder.Configuration["GitHubModels:Model"] ?? "gpt-4o";

    // OpenAI SDK client pointed at GitHub Models endpoint
    return new OpenAIClient(new ApiKeyCredential(apiKey), new OpenAIClientOptions { Endpoint = endpoint })
        .AsChatClient(model);
});
```

Any `IChatClient` implementation works here — Azure OpenAI, Ollama, etc. MAF does not care.

---

## 5. Building a `ChatClientAgent`

`ChatClientAgent` is the standard MAF implementation of `AIAgent` that wraps an `IChatClient`.

```csharp
var agent = new ChatClientAgent(
    chatClient,             // IChatClient
    new ChatClientAgentOptions
    {
        Name        = "Data Analyst",
        Description = "Parses and transforms tabular data",
        ChatOptions = new ChatOptions
        {
            Instructions = "You are a senior data analyst. Your output must be valid JSON.",
        },
        AIContextProviders = [myContextProvider],  // see §10
    },
    loggerFactory,
    services: null);        // optional IServiceProvider for plugin resolution
```

`ChatClientAgentOptions` key fields:

| Property | Purpose |
|---|---|
| `Name` | Human-readable identifier. Shows up in logs and `AgentResponseUpdate.AgentId`. |
| `Description` | Describes what the agent does. Used by Handoff mode to route tasks. |
| `ChatOptions.Instructions` | The system prompt sent with every request. |
| `AIContextProviders` | List of `AIContextProvider` instances that inject dynamic context (see §10). |

---

## 6. Middleware: `.AsBuilder().Use().Build()`

This is the most important MAF concept. Middleware intercepts every call to `RunAsync` / `RunStreamingAsync`, allowing you to:
- Transform input messages before they reach the LLM
- Transform or validate the LLM's response
- Add logging or telemetry
- Completely replace the LLM with a different implementation (see §14)

### Pattern

```csharp
AIAgent wrappedAgent = baseAgent
    .AsBuilder()
    .Use(
        runFunc: async (messages, session, options, innerAgent, cancellationToken) =>
        {
            // ── PRE-PROCESSING ────────────────────────────────────────────
            // Transform `messages` here before forwarding to the inner agent.

            var response = await innerAgent.RunAsync(messages, session, options, cancellationToken);

            // ── POST-PROCESSING ───────────────────────────────────────────
            // Inspect or transform `response` here.

            return response;
        },
        runStreamingFunc: (messages, session, options, innerAgent, cancellationToken) =>
        {
            // Mirror the streaming path.
            // A simple pass-through if this middleware only observes:
            return innerAgent.RunStreamingAsync(messages, session, options, cancellationToken);
        })
    .Build();
```

### Key rules

1. **`innerAgent` is the next agent in the chain** — always `await inner.RunAsync(…)` to continue the pipeline. Returning without calling `inner` short-circuits the rest of the chain.
2. **Always implement `runStreamingFunc`** — pass `null` only if streaming must collapse to buffered execution. For observing middleware (logging), return `innerAgent.RunStreamingAsync(…)` unchanged.
3. **`.Use()` can be called multiple times** — middlewares are stacked, outermost first:

```
caller
  └─ middleware A (outermost)
       └─ middleware B
            └─ ChatClientAgent (innermost — actual LLM call)
```

### DataNexus example — audit logging middleware

```csharp
// AgentFactory.cs — applied to every agent
agent.AsBuilder()
    .Use(
        runFunc: async (messages, session, options, inner, ct) =>
        {
            logger.LogInformation("[User: {UserId}] Agent '{Agent}' starting", userId, agentDef.Name);
            var response = await inner.RunAsync(messages, session, options, ct);
            logger.LogInformation("[User: {UserId}] Agent '{Agent}' completed", userId, agentDef.Name);
            return response;
        },
        runStreamingFunc: (messages, session, options, inner, ct) =>
        {
            logger.LogInformation("[User: {UserId}] Agent '{Agent}' streaming", userId, agentDef.Name);
            return inner.RunStreamingAsync(messages, session, options, ct);   // pass-through
        })
    .Build();
```

---

## 7. Running a Single Agent

Once you have an `AIAgent` (with or without middleware), calling it is straightforward:

```csharp
// ── Buffered ──────────────────────────────────────────────────────────────
AgentResponse response = await agent.RunAsync(inputText, cancellationToken: ct);
string output = response.Text ?? string.Empty;

// ── Streaming ─────────────────────────────────────────────────────────────
var sb = new StringBuilder();
await foreach (var update in agent.RunStreamingAsync(inputText, cancellationToken: ct))
{
    if (!string.IsNullOrWhiteSpace(update.Text))
    {
        sb.Append(update.Text);
        // stream `update.Text` to the HTTP client here
    }
}
string fullOutput = sb.ToString();
```

DataNexus wraps both paths in `DataNexusEngine.RunSingleAgentAsync` and `StreamRuntimeAgentAsync`.

---

## 8. Structured Output: `RunAsync<T>()`

MAF supports structured (typed) output. Instead of parsing raw text, the LLM is instructed to produce JSON that MAF deserializes into `T`:

```csharp
// PlannerService.cs
var response = await plannerAgent.RunAsync<PlannerStructuredStepsPlan>(
    userMessage,
    serializerOptions: new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase },
    cancellationToken: ct);

PlannerStructuredStepsPlan plan = response.Result;
```

The `Result` property on the returned `AgentResponse<T>` holds the deserialized object.

**How it works**: MAF combines the agent's system prompt with a JSON schema derived from the type `T` and passes it to the LLM as a structured output constraint (if supported by the model) or as a format instruction. If the model response does not parse, `RunAsync<T>` throws `InvalidOperationException`.

DataNexus uses two separate types depending on the workflow kind the user requested:
- `PlannerStructuredStepsPlan` — for Structured (linear list of steps)
- `PlannerStructuredGraphPlan` — for Graph DAG (nodes + edges)

---

## 9. Streaming: `RunStreamingAsync()`

`RunStreamingAsync` returns `IAsyncEnumerable<AgentResponseUpdate>`. Each `AgentResponseUpdate` carries:

| Property | Value |
|---|---|
| `Text` | The chunk of text for this update |
| `Role` | Usually `ChatRole.Assistant` |
| `AgentId` | The `Name` of the agent that produced this update |

When used inside a workflow, you also receive orchestration events (agent started, agent completed, workflow completed) as sentinel updates. See §13 for details.

```csharp
// Minimal NDJSON SSE server endpoint
await foreach (var update in agent.RunStreamingAsync(input, cancellationToken: ct))
{
    if (!string.IsNullOrWhiteSpace(update.Text))
        await response.WriteAsync($"data: {JsonSerializer.Serialize(update.Text)}\n\n");
}
```

---

## 10. Dynamic Context: `AIContextProvider`

`AIContextProvider` is MAF's mechanism for injecting dynamic content into an agent's context window at invocation time without hard-coding it into the system prompt. This is useful for:
- Including a live catalog of available agents (Planner)
- Injecting RAG (retrieved documents) results
- Providing per-request configuration

### Writing an `AIContextProvider`

```csharp
// PlannerContextProvider.cs
internal sealed class PlannerContextProvider(
    IReadOnlyList<AgentDefinition> candidates,
    ExecutionMode requestedExecutionMode,
    OrchestrationWorkflowKind requestedWorkflowKind) : AIContextProvider
{
    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(new AIContext
        {
            Instructions = BuildPlannerContext(candidates, requestedExecutionMode, requestedWorkflowKind),
        });
    }
    // ...
}
```

`AIContext.Instructions` is appended to the agent's system prompt at invocation time. You can also provide `Messages` (conversation turns) or `Tools` (function tools) from a provider.

### Registering a provider

```csharp
var agent = new ChatClientAgent(chatClient,
    new ChatClientAgentOptions
    {
        Name = "Planner",
        ChatOptions = new ChatOptions { Instructions = basePrompt },
        AIContextProviders = [new PlannerContextProvider(agents, mode, kind)],
    }, loggerFactory, null);
```

Multiple providers can be registered — MAF calls each and merges the results.

---

## 11. Workflow Patterns

MAF's `AgentWorkflowBuilder` creates multi-agent `AIAgent` objects. The result is itself an `AIAgent`, so you run it with the same `RunAsync` / `RunStreamingAsync` API.

### 11.1 Sequential

Each agent runs in order. The output of agent N becomes the input to agent N+1.

```csharp
// AgentWorkflowBuilder.BuildSequential(name, agents)
var workflow = AgentWorkflowBuilder.BuildSequential(
    "DataPipeline",
    new List<AIAgent> { analystAgent, validatorAgent, integratorAgent });

var run = await InProcessExecution.RunAsync(workflow, inputText, cancellationToken: ct);
```

**Use when**: you have a linear processing chain where each stage depends on the previous one.

In DataNexus:
- Default pipeline: `Data Analyst → API Integrator`
- Orchestrations with `ExecutionMode.Sequential` (the default)
- Pipelines with `ExecutionMode.Sequential`

### 11.2 Concurrent (Fan-out / Fan-in)

All agents receive the same input and run in parallel. Their outputs are merged.

```csharp
// AgentWorkflowBuilder.BuildConcurrent(name, agents, aggregator)
var aggregator = new ConcurrentAggregator(ConcurrentAggregatorMode.Concatenate);
// ConcurrentAggregatorMode options: Concatenate (default) | First | Last

var workflow = AgentWorkflowBuilder.BuildConcurrent(
    "ParallelAnalysis",
    new List<AIAgent> { sentimentAgent, summaryAgent, keywordsAgent },
    aggregator);
```

- `Concatenate` — joins all outputs in order
- `First` — returns only the first agent's result
- `Last` — returns only the last agent's result

**Use when**: agents are independent and can operate on the same input simultaneously (e.g. parallel analysis branches).

### 11.3 Handoff (Triage + Specialists)

One agent acts as a **triage router** and hands tasks off to specialist agents using LLM tool calls. Each specialist can escalate back to the triage agent.

```csharp
// AgentWorkflowBuilder.CreateHandoffBuilderWith(triageAgent)
//     .WithHandoffs(specialist1, specialist2, ...)
//     .Build()
private static AIAgent BuildHandoffWorkflow(string name, List<AIAgent> agents, int triageStepNumber = 1)
{
    var triageIndex = Math.Max(0, Math.Min(triageStepNumber - 1, agents.Count - 1));
    var triage = agents[triageIndex];
    var specialists = agents.Where((_, idx) => idx != triageIndex).ToList();

    return AgentWorkflowBuilder
        .CreateHandoffBuilderWith(triage)
        .WithHandoffs([.. specialists])
        .Build(name);
}
```

**How it works**:
1. The triage agent receives a user message
2. It decides — via LLM tool calls generated by MAF — which specialist to delegate to
3. The specialist processes the task
4. The specialist can hand back to the triage agent or return a final answer

**`TriageStepNumber`** (1-based): which step in the orchestration acts as the triage. DataNexus stores this per-orchestration in the database.

**Use when**: you have a complex user request that may need different kinds of expertise, and you want an LLM to dynamically decide routing.

### 11.4 Group Chat (Round-Robin)

All agents participate in a shared conversation, taking turns until a termination condition or the maximum iteration cap is reached.

```csharp
// AgentWorkflowBuilder.CreateGroupChatBuilderWith(manager)
//     .AddParticipants(...)
//     .Build()
private static AIAgent BuildGroupChatWorkflow(string name, List<AIAgent> agents, int maxIterations)
{
    var manager = agents[0];
    var participants = agents[1..].ToArray();

    return AgentWorkflowBuilder
        .CreateGroupChatBuilderWith(manager)
        .AddParticipants(participants)
        .WithMaxIterations(maxIterations)  // default 10, clamped 2–50 in DataNexus
        .Build(name);
}
```

**Use when**: you want agents to debate, critique, or iteratively refine an answer (e.g. one agent writes, another reviews, another edits).

### 11.5 DAG Graph (WorkflowBuilder)

For orchestrations where `WorkflowKind = Graph`, DataNexus builds an explicit DAG using `WorkflowBuilder` instead of the `AgentWorkflowBuilder` shortcuts.

```csharp
// DataNexusEngine.RunGraphOrchestrationAsync
var builder = new WorkflowBuilder(orchestrationName);

// Add every node as a registered agent
foreach (var (nodeId, agent) in nodeAgents)
    builder.AddAgent(nodeId, agent);

// Add edges — ordinary flow edge
builder.AddEdge(sourceNodeId, targetNodeId);

// Fan-in barrier edge — target node waits for ALL incoming sources
// Used when multiple branches need to join before continuing
builder.AddFanInBarrierEdge(lastBranchSourceId, targetNodeId);

AIAgent graphWorkflow = builder.Build(rootNodeId);
```

**Rules enforced**:
- Exactly one root node (in-degree 0)
- Exactly one terminal node (out-degree 0)
- Acyclic — `validateGraph()` on the frontend and `OrchestrationGraphRules.NormalizeGraph` on the backend both verify this before a plan is approved
- All nodes reachable from the root

**Fan-in barrier**: when multiple branches converge on a single join node, MAF needs to know it must wait for all incoming edges. Use `AddFanInBarrierEdge` for these merges; `AddEdge` for simple sequential connections.

---

## 12. Executing Workflows: `InProcessExecution`

All workflow types built by `AgentWorkflowBuilder` or `WorkflowBuilder` are run via `InProcessExecution`:

```csharp
// Buffered — waits for the full workflow to finish
Run run = await InProcessExecution.RunAsync(
    workflow,
    inputText,
    cancellationToken: ct);

// Extract output from the run
string? output = run.NewEvents
    .OfType<AgentResponseEvent>()
    .LastOrDefault()?.Response.Text;

// Or use the convenience helper DataNexus defines:
private static string? ResolveWorkflowOutput(IEnumerable<WorkflowEvent> events)
{
    return events
        .OfType<AgentResponseEvent>()
        .LastOrDefault()?.Response.Text;
}
```

**`Run` properties** you care about:

| Property | Description |
|---|---|
| `Run.NewEvents` | `IReadOnlyList<WorkflowEvent>` — everything that happened |
| `Run.NewEventCount` | Shortcut for `NewEvents.Count` |

**Notable `WorkflowEvent` subtypes**:
- `WorkflowStartedEvent` — workflow began
- `ExecutorInvokedEvent` — a specific agent was invoked
- `AgentResponseEvent` — an agent produced a response (contains `Response.Text`)
- `AgentResponseUpdateEvent` — a streaming chunk (only in streaming mode)
- `ExecutorCompletedEvent` — a specific agent finished
- `WorkflowCompletedEvent` — the entire workflow finished

---

## 13. Streaming Workflows: `StreamingRun`

For streaming, `InProcessExecution` returns a `StreamingRun`:

```csharp
StreamingRun streamingRun = await InProcessExecution.RunStreamingAsync(
    workflow,
    inputText,
    cancellationToken: ct);

await foreach (var update in streamingRun.WatchStreamAsync(ct))
{
    // update is AgentResponseUpdate — same as single-agent streaming
    if (!string.IsNullOrWhiteSpace(update.Text))
        yield return ProcessingStreamEvent.Chunk(update.Text, update.AgentId ?? "agent");
}

// The Run summary becomes available AFTER the stream completes
Run completedRun = await streamingRun.GetRunAsync(ct);
```

**Important ordering**: you must fully consume `WatchStreamAsync` before calling `GetRunAsync`. The `Run` is only populated once the stream is exhausted.

DataNexus wraps this in `StreamWorkflowExecutionAsync` (a method in `DataNexusEngine`) which detects `[PLUGIN_ERROR]` in streamed output, implements self-correction retries, and translates updates to `ProcessingStreamEvent` for the NDJSON HTTP response.

---

## 14. External Agent Pattern

MAF agents don't have to call an LLM. DataNexus demonstrates wrapping a **CLI/script process** as an `AIAgent`, so it can participate in any AF workflow transparently.

The trick is: create a `ChatClientAgent` as a structural placeholder (MAF requires it for the builder), then completely override execution via `.Use()` middleware:

```csharp
// ExternalAgentAdapter.cs
var inner = new ChatClientAgent(dummyChatClient, name: agentDef.Name, instructions: "");

return inner.AsBuilder()
    .Use(
        runFunc: async (messages, session, options, _, cancellationToken) =>
        {
            // Extract the last user message as the process's stdin input
            var inputText = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? "";

            // Run the actual CLI/script process
            var result = await runner.RunAsync(agentDef, request, user, cancellationToken);

            // Return result as an assistant ChatMessage — MAF doesn't care it came from a process
            var outputText = result.Success ? result.Data?.ToString() : PluginError.Format("...");
            return new AgentResponse([new ChatMessage(ChatRole.Assistant, outputText)]);
        },
        runStreamingFunc: (messages, session, options, _, ct) =>
            StreamExternalAsync(runner, agentDef, /*...*/ ct))
    .Build();
```

Note: the fourth parameter `_` in `runFunc` is `innerAgent` — it is intentionally discarded because we don't call the LLM at all. This is safe because the middleware is the outermost (and only) layer for external agents.

---

## 15. Self-Correction Loop

DataNexus implements an application-level self-correction loop around `InProcessExecution.RunAsync`. If an agent's output contains `[PLUGIN_ERROR]` (injected by the middleware when a plugin fails), the engine retries the entire workflow from scratch up to `MaxCorrectionAttempts` times:

```csharp
// DataNexusEngine.cs
for (var attempt = 1; attempt <= maxAttempts; attempt++)
{
    var run = await InProcessExecution.RunAsync(workflow, inputText, cancellationToken: ct);
    var output = ExtractWorkflowOutput(run);

    if (output is not null && !PluginError.IsPluginError(output))
        break;  // success — exit retry loop

    if (!enableSelfCorrection) break;
    // else: log and retry with the same input
}
```

Self-correction only applies to `Sequential` and `Concurrent` modes. `Handoff` and `GroupChat` have their own internal routing/iteration logic, so whole-workflow retry would be counterproductive.

---

## 16. Plugin Middleware Pattern

DataNexus defines two executable plugins applied as middleware on every LLM agent:

```
InputProcessor → LLM (ChatClientAgent) → OutputIntegrator
```

Both are implemented via a single `.Use()` call in `AgentFactory.AttachPluginMiddleware`:

```csharp
return baseAgent.AsBuilder()
    .Use(
        runFunc: async (messages, session, options, inner, ct) =>
        {
            // ── InputProcessor ─────────────────────────────────────────
            if (hasInputPlugin)
            {
                var ctx = new PluginContext(userId, lastUserMessage, parameters);
                var result = await inputPlugin.ExecuteAsync(ctx, ct);
                if (!result.Success)
                    return CreatePluginErrorResponse(PluginError.Format(result.ErrorMessage));

                // Replace the last user message with parsed/decoded content
                messages = ReplaceLastUserMessage(messages, result.Output);
            }

            // ── LLM call ───────────────────────────────────────────────
            var response = await inner.RunAsync(messages, session, options, ct);
            trace.RawLlmResponse = response.Text;

            // ── OutputIntegrator ───────────────────────────────────────
            if (hasOutputPlugin)
            {
                var outCtx = new PluginContext(userId, response.Text, schema, parameters);
                var outResult = await outputPlugin.ExecuteAsync(outCtx, ct);
                if (!outResult.Success)
                    return CreatePluginErrorResponse(PluginError.Format(outResult.ErrorMessage));
            }

            return response;
        },
        runStreamingFunc: (messages, session, options, inner, ct) =>
            RunStreamingWithPluginsAsync(/*...*/))
    .Build();
```

`AgentExecutionTrace` captures the full trace of what happened for returning debug info to the frontend.

---

## 17. Error Signalling with `PluginError`

Plugin failures need to propagate up through the AF message chain so the self-correction loop can detect them. MAF doesn't have a dedicated error type for this, so DataNexus uses a sentinel prefix in the response text:

```csharp
// PluginConstants.cs
internal static class PluginError
{
    internal const string Prefix = "[PLUGIN_ERROR]";

    // Build a plugin error response text
    internal static string Format(string message)       => $"[PLUGIN_ERROR] {message}";
    internal static string Format(string code, string msg) => $"[PLUGIN_ERROR] {code}: {msg}";

    // Check if a string is a plugin error
    internal static bool IsPluginError(string? text) =>
        text is not null && text.StartsWith("[PLUGIN_ERROR]", StringComparison.Ordinal);
}

// When a plugin fails, the middleware returns a fake AgentResponse:
private static AgentResponse CreatePluginErrorResponse(string errorText) =>
    new([new ChatMessage(ChatRole.Assistant, errorText)]);
```

The engine checks `PluginError.IsPluginError(output)` after each workflow run to decide whether to retry.

---

## 18. Key Package Constraint

> ⚠️ **Do NOT upgrade `Microsoft.Extensions.AI.OpenAI` above `10.3.0`** until MAF releases a version that targets `Microsoft.Extensions.AI.Abstractions >= 10.4.0`.

MAF 1.0.0-rc4 was compiled against `Microsoft.Extensions.AI.Abstractions` 10.3.x. The type `FunctionApprovalRequestContent` was removed in 10.4.0, causing a runtime `TypeLoadException` on startup. The constraint is enforced by a comment in `DataNexus.csproj`.

---

## 19. Cheat Sheet: MAF API Surface

```csharp
// ── Construct an agent ─────────────────────────────────────────────────────
var agent = new ChatClientAgent(chatClient, new ChatClientAgentOptions
{
    Name = "MyAgent",
    ChatOptions = new ChatOptions { Instructions = "system prompt" },
    AIContextProviders = [new MyContextProvider()],
}, loggerFactory, services: null);

// ── Add middleware ──────────────────────────────────────────────────────────
AIAgent wrapped = agent.AsBuilder()
    .Use(
        runFunc:         async (msgs, session, opts, inner, ct) => await inner.RunAsync(msgs, session, opts, ct),
        runStreamingFunc: (msgs, session, opts, inner, ct) => inner.RunStreamingAsync(msgs, session, opts, ct))
    .Build();

// ── Run a single agent ─────────────────────────────────────────────────────
AgentResponse  resp    = await wrapped.RunAsync("input text", cancellationToken: ct);
string?        text    = resp.Text;

await foreach (AgentResponseUpdate chunk in wrapped.RunStreamingAsync("input", cancellationToken: ct))
    Console.Write(chunk.Text);

// ── Structured output ──────────────────────────────────────────────────────
var typed = await wrapped.RunAsync<MyResponseType>("input", serializerOptions: opts, cancellationToken: ct);
MyResponseType result = typed.Result;

// ── Build multi-agent workflows ────────────────────────────────────────────
AIAgent seq        = AgentWorkflowBuilder.BuildSequential("WF", agents);
AIAgent concurrent = AgentWorkflowBuilder.BuildConcurrent("WF", agents, new ConcurrentAggregator(ConcurrentAggregatorMode.Concatenate));
AIAgent handoff    = AgentWorkflowBuilder.CreateHandoffBuilderWith(triage).WithHandoffs(specialists).Build("WF");
AIAgent groupChat  = AgentWorkflowBuilder.CreateGroupChatBuilderWith(manager).AddParticipants(rest).WithMaxIterations(10).Build("WF");

// ── DAG graph workflow ─────────────────────────────────────────────────────
var wb = new WorkflowBuilder("WF");
wb.AddAgent("node1", agent1);
wb.AddAgent("node2", agent2);
wb.AddEdge("node1", "node2");
wb.AddFanInBarrierEdge("branchN", "joinNode");  // for merge nodes
AIAgent dag = wb.Build("node1");  // pass root node id

// ── Execute workflows ──────────────────────────────────────────────────────
Run run = await InProcessExecution.RunAsync(dag, "input", cancellationToken: ct);
string? output = run.NewEvents.OfType<AgentResponseEvent>().LastOrDefault()?.Response.Text;

StreamingRun sr = await InProcessExecution.RunStreamingAsync(dag, "input", cancellationToken: ct);
await foreach (AgentResponseUpdate upd in sr.WatchStreamAsync(ct))
    Console.Write(upd.Text);
Run completedRun = await sr.GetRunAsync(ct);    // available after stream ends

// ── Context provider ───────────────────────────────────────────────────────
internal sealed class MyProvider(string dynamicData) : AIContextProvider
{
    protected override ValueTask<AIContext> ProvideAIContextAsync(InvokingContext ctx, CancellationToken ct = default)
        => ValueTask.FromResult(new AIContext { Instructions = $"Today's context: {dynamicData}" });
}
```

---

## Reading Guide

If you want to learn by tracing the code in DataNexus:

| Goal | File to read |
|---|---|
| How is an LLM agent constructed? | [backend/Agents/AgentFactory.cs](../backend/Agents/AgentFactory.cs) — `CreateAgentAsync` |
| How is plugin middleware attached? | [backend/Agents/AgentFactory.cs](../backend/Agents/AgentFactory.cs) — `AttachPluginMiddleware` |
| How does a sequential workflow run? | [backend/Agents/DataNexusEngine.cs](../backend/Agents/DataNexusEngine.cs) — `RunDefaultPipelineAsync`, `RunPipelineAsync` |
| How does an orchestration run all 4 modes? | [backend/Agents/DataNexusEngine.cs](../backend/Agents/DataNexusEngine.cs) — `RunOrchestrationAsync` |
| How does the planner use structured output? | [backend/Agents/PlannerService.cs](../backend/Agents/PlannerService.cs) — `RunPlannerAsync<T>` |
| How does `AIContextProvider` work in practice? | [backend/Agents/PlannerContextProvider.cs](../backend/Agents/PlannerContextProvider.cs) |
| How can a non-LLM process be an AF agent? | [backend/Agents/ExternalAgentAdapter.cs](../backend/Agents/ExternalAgentAdapter.cs) |
| How does streaming work end-to-end? | [backend/Agents/DataNexusEngine.cs](../backend/Agents/DataNexusEngine.cs) — `StreamWorkflowExecutionAsync` |
| What is `IChatClient` and how is it wired? | [backend/Program.cs](../backend/Program.cs) — `AddSingleton<IChatClient>` |
| Plugin error constants | [backend/Agents/PluginConstants.cs](../backend/Agents/PluginConstants.cs) |
