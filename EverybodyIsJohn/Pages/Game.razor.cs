using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;

using Orleans;

namespace EverybodyIsJohn.Pages;

public partial class Game(
    IHttpContextAccessor contextAccessor,
    IClusterClient client,
    NavigationManager navigationManager) :
    ComponentBase, IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private Task? _task = null;

    // MUST be set in OnInitializedAsync.
    private IGameGrain _grain = null!;
    private IGameGrainObserver _obj = null!;
    private GameObserver _observer = null!;
    private string _playerId = null!;
    private DateTimeOffset? _next = null;
    private int _round = 1;
    private int _totalRounds = 0;

    [Parameter]
    public required string GameId { get; init; }

    [Parameter]
    public bool IsFinished { get; set; }

    [Parameter]
    public bool IsHost { get; set; }

    [Parameter]
    public required string Obsession { get; set; }

    [Parameter]
    public required string HeaderInfo { get; set; }

    [Parameter]
    public required bool InputEnabled { get; set; }

    [Parameter]
    public List<string> Messages { get; set; } = [];

    [Parameter]
    public string? NewInput { get; set; }

    [Parameter]
    public string? CurrentSubmission { get; set; }

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

        if (state is GameState.NotStarted)
        {
            navigationManager.NavigateTo($"/GameLobby/{GameId}");
            return;
        }

        if (state is not GameState.Started game)
        {
            throw new ArgumentException("GameLobby is unknown state.");
        }

        _next = game.CurrentRound.EndsAt;
        _round = game.CurrentRound.Number;
        _totalRounds = game.CurrentRound.Total;
        InputEnabled = !game.CurrentRound.IsProcessing;
        IsHost = game.IsHost;
        Obsession = game.Obsession;
        Messages.AddRange(game.Messages);
        IsFinished = game.IsFinished;

        _task = HeaderTask(_cts.Token);

        _observer = new GameObserver(this);
        _obj = client.CreateObjectReference<IGameGrainObserver>(_observer);
        await _grain.Subscribe(_obj);

        _ = Task.Run(
            async () =>
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
            },
            CancellationToken.None);

        await base.OnInitializedAsync();
    }

    private async Task HeaderTask(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (IsFinished)
            {
                HeaderInfo = "Game finished";
                await InvokeAsync(StateHasChanged);
            }
            else if (!InputEnabled)
            {
                HeaderInfo = "processing..";
                await InvokeAsync(StateHasChanged);
            }
            else if (_next is null)
            {
                HeaderInfo = "waiting...";
                await InvokeAsync(StateHasChanged);
            }
            else
            {
                var until = _next.Value - DateTimeOffset.UtcNow;
                HeaderInfo = $"Next round ({_round}/{_totalRounds}): {until.TotalSeconds:F0} seconds";
                await InvokeAsync(StateHasChanged);
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
    }

    private async Task AddInput()
    {
        if (string.IsNullOrWhiteSpace(NewInput))
        {
            return;
        }

        await _grain.AddInput(_playerId, NewInput);
        CurrentSubmission = NewInput;
        NewInput = null;
        await InvokeAsync(StateHasChanged);
    }

    private async Task NewMessage(string msg)
    {
        Messages.Add(msg);
        await InvokeAsync(StateHasChanged);
    }

    private async Task GameEnded()
    {
        IsFinished = true;
        await InvokeAsync(StateHasChanged);
    }

    private async Task GameRoundUpdated(GameRound gameRound)
    {
        if (_next != gameRound.EndsAt)
        {
            CurrentSubmission = null;
        }

        _next = gameRound.EndsAt;
        InputEnabled = !gameRound.IsProcessing;
        await InvokeAsync(StateHasChanged);
    }

    private class GameObserver(Game game) : IGameGrainObserver
    {
        public async Task NewGameMessage(GameMessage message)
        {
            switch (message)
            {
                case GameMessage.RoundUpdated { GameRound: var gameRound, }:
                    await game.GameRoundUpdated(gameRound);
                    break;
                case GameMessage.NewMessage { Message: var msg, }:
                    await game.NewMessage(msg);
                    break;
                case GameMessage.GameEnded:
                    await game.GameEnded();
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(message));
            }
        }

        public Task NewGameLobbyMessage(GameLobbyMessage message)
        {
            return Task.CompletedTask;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();

        _task?.Wait();
    }
}