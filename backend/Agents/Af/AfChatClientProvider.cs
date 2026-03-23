using DataNexus.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenAI;
using System.ClientModel;

namespace DataNexus.Agents.Af;

/// <summary>
/// Singleton provider that builds a reusable IChatClient from GitHub Models config
/// using the OpenAI SDK v2 with a custom endpoint.
/// GitHub Models exposes an OpenAI-compatible inference endpoint, so the standard
/// OpenAI SDK targeting https://models.inference.ai.azure.com works directly.
/// </summary>
public sealed class AfChatClientProvider
{
    private readonly IChatClient _chatClient;

    public AfChatClientProvider(IOptions<GitHubModelsConfig> config)
    {
        var cfg = config.Value;

        if (string.IsNullOrWhiteSpace(cfg.ApiKey))
            throw new InvalidOperationException(
                "GitHubModels:ApiKey is required for the Agent Framework runtime. " +
                "Set it in appsettings or user secrets.");

        var clientOptions = new OpenAIClientOptions
        {
            Endpoint = new Uri(cfg.Endpoint)
        };

        var openAiClient = new OpenAIClient(new ApiKeyCredential(cfg.ApiKey), clientOptions);
        _chatClient = openAiClient.GetChatClient(cfg.Model).AsIChatClient();
    }

    /// <summary>Gets the shared <see cref="IChatClient"/> instance backed by GitHub Models.</summary>
    public IChatClient Client => _chatClient;
}
