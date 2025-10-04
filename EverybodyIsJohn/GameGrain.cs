using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using OpenAI.Chat;

using Orleans;
using Orleans.Concurrency;
using Orleans.Runtime;
using Orleans.Utilities;

namespace EverybodyIsJohn;

[GenerateSerializer]
public abstract class GameState
{
    private GameState()
    {
    }

    [GenerateSerializer]
    public class NonExisting : GameState;

    [GenerateSerializer]
    public class NotStarted : GameState
    {
        [Id(0)]
        public required bool IsHost { get; init; }

        [Id(1)]
        public required string? Nickname { get; init; }

        [Id(2)]
        public required string? Obsession { get; init; }

        [Id(3)]
        public required Dictionary<string, string> Players { get; init; }
    }

    [GenerateSerializer]
    public class Started : GameState
    {
        [Id(0)]
        public required bool IsHost { get; init; }

        [Id(1)]
        public required string Nickname { get; init; }

        [Id(2)]
        public required string Obsession { get; init; }

        [Id(3)]
        public required Dictionary<string, string> Players { get; init; }

        [Id(4)]
        public required GameRound CurrentRound { get; set; }

        [Id(5)]
        public required List<string> Messages { get; init; }

        [Id(6)]
        public required bool IsFinished { get; init; }
    }
}

public interface IGameGrain : IGrainWithStringKey
{
    Task Subscribe(IGameGrainObserver observer);
    Task Unsubscribe(IGameGrainObserver observer);
    Task<GameState> GetState(string playerId);
    Task Create(string playerId);
    Task Join(string playerId, string nickname, string obsession);
    Task NewLobbyMessage(string playerId, string message);
    Task Start(string playerId);
    Task AddInput(string playerId, string newInput);
}

[GenerateSerializer]
public abstract class GameLobbyMessage
{
    [GenerateSerializer]
    public class ChatMessage : GameLobbyMessage
    {
        [Id(0)]
        public required string FromPlayerId { get; init; }

        [Id(1)]
        public required string Message { get; init; }
    }

    [GenerateSerializer]
    public class UpdatedPlayerList : GameLobbyMessage
    {
        [Id(0)]
        public required Dictionary<string, string> Players { get; init; }
    }

    [GenerateSerializer]
    public class GameStarting : GameLobbyMessage;

    [GenerateSerializer]
    public class GameStarted : GameLobbyMessage;
}

[GenerateSerializer]
public abstract class GameMessage
{
    [GenerateSerializer]
    public class RoundUpdated : GameMessage
    {
        [Id(0)]
        public required GameRound GameRound { get; init; }
    }

    [GenerateSerializer]
    public class NewMessage : GameMessage
    {
        [Id(0)]
        public required string Message { get; init; }
    }

    [GenerateSerializer]
    public class GameEnded : GameMessage;
}

public interface IGameGrainObserver : IGrainObserver
{
    [OneWay]
    Task NewGameMessage(GameMessage message);

    [OneWay]
    Task NewGameLobbyMessage(GameLobbyMessage message);
}

public class Player
{
    public required string Id { get; init; }
    public required string Nickname { get; init; }
    public required string Obsession { get; init; }
}

[GenerateSerializer]
public class GameRound
{
    [Id(0)]
    public required DateTimeOffset EndsAt { get; init; }

    [Id(1)]
    public Dictionary<string, string> Inputs { get; } = [];

    [Id(2)]
    public required bool IsProcessing { get; set; }

    [Id(4)]
    public required int Number { get; set; }

    [Id(5)]
    public required int Total { get; init; }
}

public class PersistantGameState
{
    public required bool IsStarted { get; set; }
    public required string HostPlayerId { get; init; }
    public Dictionary<string, int> ObsessionWeights { get; set; } = [];
    public Dictionary<string, Player> Players { get; } = [];

    public List<GameRound> PreviousRounds { get; } = [];
    public GameRound? CurrentRound { get; set; }

    public List<string> Messages { get; } = [];
    public bool IsFinished { get; set; }
}

public class GameGrain(
    ObsessionWeightProvider obsessionWeightProvider,
    ILogger<GameGrain> logger,
    ChatClient chatClient,
    [PersistentState("gameState")]
    IPersistentState<PersistantGameState> state) :
    Grain, IGameGrain
{
    private readonly ObserverManager<IGameGrainObserver> _observerManager = new(TimeSpan.FromMinutes(5), logger);
    private static readonly TimeSpan RoundDuration = TimeSpan.FromMinutes(1);
    private static readonly int TotalRounds = 3;
    private readonly NotJohn _notJohn = new(chatClient);

    public Task Subscribe(IGameGrainObserver observer)
    {
        _observerManager.Subscribe(observer, observer);
        return Task.CompletedTask;
    }

    public Task Unsubscribe(IGameGrainObserver observer)
    {
        _observerManager.Unsubscribe(observer);
        return Task.CompletedTask;
    }

    public async Task Create(string playerId)
    {
        if (state.RecordExists)
        {
            throw new InvalidOperationException("Already created");
        }

        state.State = new PersistantGameState
        {
            IsStarted = false,
            HostPlayerId = playerId,
        };
        await state.WriteStateAsync();
        var matchmaking = this.GrainFactory.GetGrain<IMatchmakingGrain>("JOHN");
        await matchmaking.AddUpdateGame(this.GetPrimaryKeyString(), 0);
    }

    public async Task Join(string playerId, string nickname, string obsession)
    {
        if (state.State.IsStarted)
        {
            throw new InvalidOperationException("Can't join already that's stareted.");
        }

        state.State.Players[playerId] = new Player() { Id = playerId, Nickname = nickname, Obsession = obsession, };
        await state.WriteStateAsync();

        await _observerManager.Notify(o => o.NewGameLobbyMessage(new GameLobbyMessage.UpdatedPlayerList()
        {
            Players = state.State.Players.ToDictionary(p => p.Key, p => p.Value.Nickname),
        }));

        var matchmaking = this.GrainFactory.GetGrain<IMatchmakingGrain>("JOHN");
        await matchmaking.AddUpdateGame(this.GetPrimaryKeyString(), state.State.Players.Count);
    }

    public async Task NewLobbyMessage(string playerId, string message)
    {
        await _observerManager.Notify(o => o.NewGameLobbyMessage(new GameLobbyMessage.ChatMessage()
        {
            FromPlayerId = playerId,
            Message = message,
        }));
    }

    public async Task Start(string playerId)
    {
        if (state.State.HostPlayerId != playerId)
        {
            return;
        }

        var matchmaking = this.GrainFactory.GetGrain<IMatchmakingGrain>("JOHN");
        await matchmaking.RemoveGame(this.GetPrimaryKeyString());

        var weights = await obsessionWeightProvider.GetWeights(
            state.State.Players.ToDictionary(p => p.Key, p => p.Value.Obsession),
            CancellationToken.None);

        var start = await _notJohn.StartStory(CancellationToken.None);
        state.State.Messages.Add(start);
        var endsAt = DateTimeOffset.UtcNow.Add(RoundDuration);

        state.State.ObsessionWeights = weights.Weights.ToDictionary(w => w.Key, w => w.Value.Weight);
        state.State.IsStarted = true;
        state.State.CurrentRound = new GameRound
        {
            EndsAt = endsAt,
            IsProcessing = false,
            Number = 1,
            Total = TotalRounds,
        };

        await state.WriteStateAsync();

        await _observerManager.Notify(o => o.NewGameLobbyMessage(new GameLobbyMessage.GameStarted()));

        this.RegisterGrainTimer(
            ProcessRound,
            new GrainTimerCreationOptions(
                endsAt - DateTimeOffset.UtcNow,
                Timeout.InfiniteTimeSpan));
    }

    private async Task ProcessRound(CancellationToken cancellationToken)
    {
        if (state.State.CurrentRound is null)
        {
            throw new InvalidOperationException("Round not started");
        }

        state.State.CurrentRound.IsProcessing = true;
        await state.WriteStateAsync(cancellationToken);
        await _observerManager.Notify(o => o.NewGameMessage(new GameMessage.RoundUpdated
        {
            GameRound = state.State.CurrentRound,
        }));

        var newActivities = new List<string>();

        foreach (var player in state.State.CurrentRound.Inputs)
        {
            var probability = await _notJohn.GetProbability(player.Value, CancellationToken.None);
            var included = Random.Shared.NextDouble() <= probability;
            if (included)
            {
                newActivities.Add(player.Value);
            }

            var playerDetails = state.State.Players[player.Key];

            var i = included
                ? $"submission was included (probability {probability:F1}):"
                : $"submission was _NOT_ included (probability {probability:F1}):";
            var playerSubmissionMsg = $"{playerDetails.Nickname} {i}\r\n {player.Value}\r\n\r\n";
            state.State.Messages.Add(playerSubmissionMsg);
            await _observerManager.Notify(o => o.NewGameMessage(new GameMessage.NewMessage()
            {
                Message = playerSubmissionMsg,
            }));
        }

        if (newActivities.Count > 0)
        {
            var next = await _notJohn.ProgressStory(newActivities, CancellationToken.None);
            state.State.Messages.Add(next);

            await _observerManager.Notify(
                o => o.NewGameMessage(new GameMessage.NewMessage()
                {
                    Message = next,
                }));
        }

        if (state.State.CurrentRound.Number == 3)
        {
            await EndGame();
            return;
        }

        var endsAt = DateTimeOffset.UtcNow.Add(RoundDuration);
        state.State.PreviousRounds.Add(state.State.CurrentRound);
        state.State.CurrentRound = new GameRound
        {
            EndsAt = endsAt,
            IsProcessing = false,
            Number = state.State.CurrentRound.Number + 1,
            Total = TotalRounds,
        };

        var newRoundMessage = $"================ ROUND {state.State.CurrentRound.Number} ====================\r\n";
        state.State.Messages.Add(newRoundMessage);
        await _observerManager.Notify(
            o => o.NewGameMessage(new GameMessage.NewMessage()
            {
                Message = newRoundMessage,
            }));

        await state.WriteStateAsync(cancellationToken);
        await _observerManager.Notify(o => o.NewGameMessage(new GameMessage.RoundUpdated
        {
            GameRound = state.State.CurrentRound,
        }));
        this.RegisterGrainTimer(
            ProcessRound,
            new GrainTimerCreationOptions(
                endsAt - DateTimeOffset.UtcNow,
                Timeout.InfiniteTimeSpan));
    }

    public async Task EndGame()
    {
        state.State.IsFinished = true;
        await state.WriteStateAsync();
        await _observerManager.Notify(o => o.NewGameMessage(new GameMessage.GameEnded()));

        var resultMsg = $"================ RESULTS ====================\r\n";
        state.State.Messages.Add(resultMsg);
        await _observerManager.Notify(
            o => o.NewGameMessage(new GameMessage.NewMessage()
            {
                Message = resultMsg,
            }));

        string? winnerPlayerId = null;
        Dictionary<string, (int, double)> results = [];

        foreach (var player in state.State.Players)
        {
            var count = await _notJohn.CountOccurrencesOfObsession(player.Value.Obsession, CancellationToken.None);
            var weighted = count * state.State.ObsessionWeights[player.Key] / (double)100;

            if (winnerPlayerId is null || results[winnerPlayerId].Item2 < weighted)
            {
                winnerPlayerId = player.Key;
            }

            results[player.Key] = (count, weighted);

        }

        foreach (var playerResult in results.OrderByDescending(kvp => kvp.Value.Item2))
        {
            var player = state.State.Players[playerResult.Key];
            var playerResultMessage =
                $"""
                 {player.Nickname} - obsession (weight: {state.State.ObsessionWeights[playerResult.Key]}): {player.Obsession}
                 count: {playerResult.Value.Item1}, weighted: {playerResult.Value.Item2}

                 """;

            state.State.Messages.Add(playerResultMessage);
            await _observerManager.Notify(
                o => o.NewGameMessage(new GameMessage.NewMessage()
                {
                    Message = playerResultMessage,
                }));
        }

        await state.WriteStateAsync();
    }

    public async Task AddInput(string playerId, string newInput)
    {
        if (state.State.CurrentRound is null)
        {
            throw new InvalidOperationException("Can't add input to the game state.");
        }

        if (state.State.CurrentRound.IsProcessing)
        {
            return;
        }

        state.State.CurrentRound.Inputs[playerId] = newInput;
        await state.WriteStateAsync();
    }

    public async Task<GameState> GetState(string playerId)
    {
        await Task.CompletedTask;
        if (!state.RecordExists)
        {
            return new GameState.NonExisting();
        }

        var currentPlayer = state.State.Players.GetValueOrDefault(playerId);

        if (!state.State.IsStarted)
        {
            return new GameState.NotStarted()
            {
                IsHost = state.State.HostPlayerId == playerId,
                Nickname = currentPlayer?.Nickname,
                Obsession = currentPlayer?.Obsession,
                Players = state.State.Players.ToDictionary(p => p.Key, p => p.Value.Nickname),
            };
        }

        if (currentPlayer is null)
        {
            throw new InvalidOperationException("Player not found");
        }

        if (state.State.CurrentRound is null)
        {
            throw new InvalidOperationException("Round not found");
        }

        return new GameState.Started
        {
            IsHost = state.State.HostPlayerId == playerId,
            Nickname = currentPlayer.Nickname,
            Obsession = currentPlayer.Obsession,
            CurrentRound = state.State.CurrentRound,
            Players = state.State.Players.ToDictionary(p => p.Key, p => p.Value.Nickname),
            Messages = state.State.Messages,
            IsFinished = state.State.IsFinished,
        };
    }
}