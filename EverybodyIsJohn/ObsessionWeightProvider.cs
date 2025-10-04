using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Threading;
using System.Threading.Tasks;

using OpenAI.Chat;

using Orleans;

namespace EverybodyIsJohn;

[GenerateSerializer]
public class ObsessionWeight
{
    [Id(0)]
    public required int Weight { get; init; }
}

[GenerateSerializer]
public class WeightResult
{
    [Id(0)]
    public required Dictionary<string, ObsessionWeight> Weights { get; init; }
}

public class ObsessionWeightProvider(ChatClient client)
{
    private static readonly string SystemPrompt =
        """
        Obsession Weighting System
        
        You are the Obsession Weighting Engine for the game Everyone Is John. Your only function is to analyze a list of player-submitted Obsessions and assign a numerical Weight to each one.
        
        Your primary goal is to ensure that the sum of all assigned Weights is exactly 100.
        If there's only one player in the input, the result MUST be exactly 100 for this obsession.
        
        Obsession Weighting Scale
        
        The Weight you assign must be an integer (no decimals) from 1 to 60, and should correspond to the overall difficulty, complexity, and risk associated with achieving the Obsession.
        Weight Range|Difficulty/Complexity Description|Examples (for context)
        1-10|Simple/Trivial: Can be achieved with one or two successful rolls, often involving common items or minor public embarrassment. Low risk.|Eat a piece of candy. Yell "Pineapple!" in public. Tie a shoelace.
        11-20|Minor Task: Requires a sequence of 2-4 successful rolls, or a single difficult/risky roll. Usually involves minor preparation or interaction.|Purchase a specific, non-obvious item (e.g., a trowel). Successfully persuade a stranger to give up their hat.
        21-30|Moderate Challenge: Requires moderate planning, several successful rolls, and carries a visible risk of failure, getting caught, or drawing attention.|Successfully hotwire and drive away a specific car. Get into a restricted area (e.g., an office).
        31-40|Major Endeavor: Requires significant planning, multiple distinct steps, and involves substantial risk or complexity. These actions fundamentally change the scene or John's status.|Successfully rob a small convenience store. Start a fire without getting caught. Travel to a specific, distant location (e.g., another city).
        41-60|Extreme/Epic Feat: Extremely difficult, highly illegal, or requiring multi-session-level preparation. These are high-risk actions that will likely end the game (John's arrest or death) upon failure or success.|Successfully blow up a building. Become romantically involved with a high-profile public figure.
        
        Process & Constraints
        
        1. Analyze and Assign Weights: Review the list of player Obsessions. Using the scale above, assign a preliminary Weight to each one based on its inherent difficulty.
        2. Calculate and Adjust: Calculate the total sum of the preliminary Weights.
        3. Finalize to 100: You must adjust the Weights up or down, making the smallest necessary changes, until the final sum is exactly 100. The final Weights must still adhere to the 1−60 range and remain integers. Prioritize proportional changes (i.e., if the total is too high, reduce the highest weights slightly before touching the lowest).
        4. Output format: a JSON object where the keys are the player id (string) and the value are the weight (int).
        
        {
          "a": 45,
          "b": 55
        }
        """;

    private static readonly JsonNode Schema = JsonSerializerOptions.Default.GetJsonSchemaAsNode(typeof(Dictionary<string, int>));

    public async Task<WeightResult> GetWeights(
        Dictionary<string, string> obsessions,
        CancellationToken cancellationToken)
    {
        var result = await client.CompleteChatAsync([
                new SystemChatMessage(SystemPrompt),
                new UserChatMessage(
                    $$"""
                      Here's the list of player ids and obsessions that you must weight against each other.

                      {{JsonSerializer.Serialize(obsessions)}}
                      """)
            ],
            new ChatCompletionOptions()
            {
                ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                    jsonSchemaFormatName: "weight_dictionary",
                    jsonSchema: BinaryData.FromObjectAsJson(Schema),
                    jsonSchemaIsStrict: true),
            },
            cancellationToken);

        var responseModel =
            JsonSerializer.Deserialize<Dictionary<string, int>>(result.Value.Content[0].Text)
            ?? throw new InvalidOperationException("Unable to deserialize response from ollama.");

        // Normalize to 100, since LLM are not calculators.
        var sum = responseModel.Sum(kvp => kvp.Value);

        return new WeightResult()
        {
            Weights = responseModel.ToDictionary(
                w => w.Key,
                w => new ObsessionWeight()
                {
                    Weight = (int)(w.Value / (double)sum * 100)
                }
            ),
        };
    }
}