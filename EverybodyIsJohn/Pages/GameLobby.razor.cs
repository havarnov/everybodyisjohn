using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;

using Orleans;

namespace EverybodyIsJohn.Pages;

public class LobbyMessage
{
    public required string PlayerId { get; init; }
    public required string Nickname { get; init; }
    public required string Message { get; init; }
}

public partial class GameLobby(
    IHttpContextAccessor contextAccessor,
    IClusterClient client,
    NavigationManager navigationManager) :
    ComponentBase, IDisposable
{
    private readonly CancellationTokenSource _cts = new();

    // MUST be set in OnInitializedAsync.
    private IGameGrain _grain = null!;
    private IGameGrainObserver _obj = null!;
    private Task _keepAliveTask = null!;
    private GameObserver _observer = null!;
    private string _playerId = null!;
    private Dictionary<string, string> _players = null!;

    [Parameter]
    public required string GameId { get; init; }

    [Parameter]
    public bool IsHost { get; set; }

    [Parameter]
    public bool HasJoined { get; set; }

    [Parameter]
    public bool IsStarting { get; set; }

    [Parameter]
    public string? Nickname { get; set; }

    [Parameter]
    public string? Obsession { get; set; }

    [Parameter]
    public string? JoinError { get; set; }

    [Parameter]
    public List<LobbyMessage> LobbyMessages { get; set; } = [];

    [Parameter]
    public string? NewLobbyMessage { get; set; }

    protected override async Task OnInitializedAsync()
    {
        if (contextAccessor.HttpContext?.Request.Cookies.TryGetValue("john", out _playerId!) != true
            || string.IsNullOrEmpty(_playerId))
        {
            navigationManager.NavigateTo("/");
            return;
        }

        _grain = client.GetGrain<IGameGrain>(GameId);

        var state = await _grain.GetState(_playerId);

        if (state is GameState.NonExisting)
        {
            navigationManager.NavigateTo("/");
            return;
        }

        if (state is GameState.Started)
        {
            navigationManager.NavigateTo($"/Game/{GameId}");
            return;
        }

        if (state is not GameState.NotStarted lobby)
        {
            throw new ArgumentException("GameLobby is unknown state.");
        }

        IsHost = lobby.IsHost;
        _players = lobby.Players;
        Nickname = lobby.Nickname;
        Obsession = lobby.Obsession;
        HasJoined = lobby.Nickname is not null;

        _observer = new GameObserver(this);
        _obj = client.CreateObjectReference<IGameGrainObserver>(_observer);
        await _grain.Subscribe(_obj);
        _keepAliveTask = StartKeepAliveTask();

        await base.OnInitializedAsync();
    }

    private async Task StartKeepAliveTask()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await _grain.Subscribe(_obj);
                await Task.Delay(TimeSpan.FromSeconds(10), _cts.Token);
            }

            await _grain.Unsubscribe(_obj);
        }
        catch (OperationCanceledException)
        {
            await _grain.Unsubscribe(_obj);
        }
    }

    private async Task JoinGame()
    {
        if (string.IsNullOrWhiteSpace(Nickname)
            || string.IsNullOrWhiteSpace(Obsession))
        {
            JoinError = $"Nick & {nameof(Obsession)} is required.";
            await InvokeAsync(StateHasChanged);
            return;
        }

        await _grain.Join(_playerId, Nickname, Obsession);
        HasJoined = true;
        await InvokeAsync(StateHasChanged);
    }

    private async Task StartGame()
    {
        IsStarting = true;
        await InvokeAsync(StateHasChanged);
        await _grain.Start(_playerId);
    }

    private async Task AddLobbyMessage()
    {
        if (string.IsNullOrWhiteSpace(Nickname) || string.IsNullOrWhiteSpace(NewLobbyMessage))
        {
            return;
        }

        await _grain.NewLobbyMessage(_playerId, NewLobbyMessage);
        NewLobbyMessage = null;
        await InvokeAsync(StateHasChanged);
    }

    private async Task UpdatedPlayerList(Dictionary<string, string> players)
    {
        _players = players;
        LobbyMessages = [.. LobbyMessages
            .Select(l => new LobbyMessage
            {
                PlayerId = l.PlayerId,
                Nickname = _players.GetValueOrDefault(l.PlayerId, "[JOHN]"),
                Message = l.Message,
            })];
        await InvokeAsync(StateHasChanged);
    }

    private async Task IncomingChatMessage(GameLobbyMessage.ChatMessage message)
    {
        LobbyMessages.Add(new LobbyMessage()
        {
            PlayerId = message.FromPlayerId,
            Nickname = _players.GetValueOrDefault(message.FromPlayerId, "[JOHN]"),
            Message = message.Message
        });
        await InvokeAsync(StateHasChanged);
    }

    private async Task GameStarted()
    {
        await _cts.CancelAsync();
        await _keepAliveTask;

        navigationManager.NavigateTo($"/Game/{GameId}");
    }

    private class GameObserver(GameLobby game) : IGameGrainObserver
    {
        public Task NewGameMessage(GameMessage message)
        {
            return Task.CompletedTask;
        }

        public async Task NewGameLobbyMessage(GameLobbyMessage message)
        {
            switch (message)
            {
                case GameLobbyMessage.ChatMessage msg:
                    await game.IncomingChatMessage(msg);
                    break;
                case GameLobbyMessage.UpdatedPlayerList { Players: var players, }:
                    await game.UpdatedPlayerList(players);
                    break;
                case GameLobbyMessage.GameStarted:
                    await game.GameStarted();
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(message));
            }
        }
    }

    public void Dispose()
    {
        if (!_cts.IsCancellationRequested)
        {
            _cts.Cancel();
            _cts.Dispose();
        }

        _keepAliveTask?.Wait();
    }
}