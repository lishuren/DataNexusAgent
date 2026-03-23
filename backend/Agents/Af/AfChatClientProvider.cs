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

        var clientOptions = new OpenAIClientOptions
        {
            Endpoint = new Uri(cfg.Endpoint)
        };

        var openAiClient = new OpenAIClient(new ApiKeyCredential(cfg.ApiKey), clientOptions);
        _chatClient = openAiClient.GetChatClient(cfg.Model).AsIChatClient();
    }

    public IChatClient Client => _chatClient;
}
