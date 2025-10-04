using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using Orleans;
using Orleans.Concurrency;
using Orleans.Utilities;

namespace EverybodyIsJohn;

[GenerateSerializer]
public class AvailableGame
{
    [Id(0)]
    public required string Id { get; init; }

    [Id(1)]
    public required int Participants { get; init; }
}

public interface IMatchmakingGrain : IGrainWithStringKey
{
    Task Subscribe(IMatchmakingObserver observer);
    Task Unsubscribe(IMatchmakingObserver observer);
    Task AddUpdateGame(string gameId, int participants);
    Task RemoveGame(string gameId);
    Task<List<AvailableGame>> GetAvailableGames();
}

[GenerateSerializer]
public abstract class MatchmakingMessage
{
    [GenerateSerializer]
    public class UpdatedGames : MatchmakingMessage
    {
        [Id(0)]
        public required List<AvailableGame> AvailableGames { get; init; }
    }
}

public interface IMatchmakingObserver : IGrainObserver
{
    [OneWay]
    Task Message(MatchmakingMessage message);
}

public class MatchmakingGrain(ILogger<MatchmakingGrain> logger) : Grain, IMatchmakingGrain
{
    private readonly ObserverManager<IMatchmakingObserver> _observerManager = new(TimeSpan.FromMinutes(5), logger);

    private readonly Dictionary<string, AvailableGame> _availableGames = [];

    public Task Subscribe(IMatchmakingObserver observer)
    {
        _observerManager.Subscribe(observer, observer);
        return Task.CompletedTask;
    }

    public Task Unsubscribe(IMatchmakingObserver observer)
    {
        _observerManager.Unsubscribe(observer);
        return Task.CompletedTask;
    }

    public async Task AddUpdateGame(string gameId, int participants)
    {
        _availableGames[gameId] = new AvailableGame() { Id = gameId, Participants = participants, };
        await _observerManager.Notify(o => o.Message(new MatchmakingMessage.UpdatedGames()
        {
            AvailableGames = [.. _availableGames.Values]
        }));
    }

    public async Task RemoveGame(string gameId)
    {
        _availableGames.Remove(gameId);
        await _observerManager.Notify(o => o.Message(new MatchmakingMessage.UpdatedGames()
        {
            AvailableGames = [.. _availableGames.Values]
        }));
    }

    public Task<List<AvailableGame>> GetAvailableGames()
    {
        return Task.FromResult<List<AvailableGame>>([.. _availableGames.Values]);
    }
}