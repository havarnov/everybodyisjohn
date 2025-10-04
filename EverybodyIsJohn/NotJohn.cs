using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Threading;
using System.Threading.Tasks;

using OpenAI.Chat;

namespace EverybodyIsJohn;

public class NotJohn(ChatClient client)
{
    private static readonly JsonNode DoubleSchema = JsonSerializerOptions.Default.GetJsonSchemaAsNode(typeof(double));
    private static readonly JsonNode IntSchema = JsonSerializerOptions.Default.GetJsonSchemaAsNode(typeof(int));
    private static readonly string SystemPrompt =
        """
        You are the **Narrator and Story Engine** for a tabletop roleplaying game based on "Everybody Is John."
        
        Your role is to maintain the **cohesive and continuous narrative** of John and his life. You must embody the following principles:
        
        1.  **Impartiality:** You are a neutral, omniscient entity. You do not favor any player's "wants" (voices) and must judge all actions based on the established story, John's personality, environment, and plausible reality.
        2.  **Continuity:** You must ensure the story flows logically from one action to the next, maintaining consistency in John's character, his environment, and any ongoing events.
        3.  **Realism & Plausibility:** All probabilities and story progression must reflect a realistic, plausible outcome given the current narrative context. Bizarre or impossible events should have a low probability unless the story context specifically supports them.
        4.  **Probability Task:** When asked for a probability, your output **MUST** be a single floating-point number between $0.0$ and $1.0$. This represents the likelihood of the specified activity being the *next* logical event in the story.
        5.  **Story Progression Task:** When asked to incorporate activities, you must weave them naturally into the existing narrative of John's life, resolving the actions and then expanding the story slightly to set the scene for the next events.
        
        **Always remember the core conflict:** John is an ordinary, somewhat passive person whose mind is controlled by multiple, competing, external "voices" (the players), each with their own selfish "want." John's actions are often the result of this internal conflict.
        """;

    private readonly List<ChatMessage> _messages = [new SystemChatMessage(SystemPrompt)];

    public async Task<double> GetProbability(
        string activity,
        CancellationToken cancellationToken)
    {
        var prompt =
            $"""
             Given the current story of John what is the probability of the next activity in the story being the following:

             {activity}
             """;

        var response = await client.CompleteChatAsync(
            [.. _messages, new UserChatMessage(prompt)],
            options: new ChatCompletionOptions()
            {
                ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                    jsonSchemaFormatName: "double_schema",
                    jsonSchema: BinaryData.FromObjectAsJson(DoubleSchema),
                    jsonSchemaIsStrict: true),
            },
            cancellationToken: cancellationToken);

        var probability =
            JsonSerializer.Deserialize<double?>(response.Value.Content[0].Text)
            ?? 0.5;

        return probability;
    }

    public async Task<string> ProgressStory(
        List<string> activities,
        CancellationToken cancellationToken)
    {
        var prompt =
            $"""
             Progress the story of John by including the following activities (approx. 150 words):
             {JsonSerializer.Serialize(activities)}
             """;
        _messages.Add(new UserChatMessage(prompt));
        var response = await client.CompleteChatAsync(
            _messages,
            cancellationToken: cancellationToken);
        _messages.Add(new AssistantChatMessage(response.Value.Content[0].Text));

        return response.Value.Content[0].Text;
    }

    public async Task<string> StartStory(CancellationToken cancellationToken)
    {
        _messages.Add(new UserChatMessage(
            """
            Start the story of John .
            Make the setting fun and absurd.
            Don't make it to long (approx. 150 words).
            """));
        var response = await client.CompleteChatAsync(
            _messages,
            cancellationToken: cancellationToken);

        _messages.Add(new AssistantChatMessage(response.Value.Content[0].Text));

        return response.Value.Content[0].Text;
    }

    public async Task<int> CountOccurrencesOfObsession(string obsession, CancellationToken cancellationToken)
    {
        var prompt =
            $"""
             Count the number of times this obsession has occured during the story.
             DO NOT INCLUDE THE SUBMISSIONS TEXT, ONLY THE ACTUAL STORY.

             obsession: {obsession}
             """;
        var response = await client.CompleteChatAsync(
                [.. _messages, new UserChatMessage(prompt)],
                options: new ChatCompletionOptions()
                {
                    ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                        jsonSchemaFormatName: "int_schema",
                        jsonSchema: BinaryData.FromObjectAsJson(IntSchema),
                        jsonSchemaIsStrict: true),
                },
                cancellationToken: cancellationToken);

        var count =
            JsonSerializer.Deserialize<int?>(response.Value.Content[0].Text)
            ?? 0;

        return count;
    }
}