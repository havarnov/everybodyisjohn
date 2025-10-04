using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;

using Orleans;

namespace EverybodyIsJohn.Pages;

public partial class Index(
    IHttpContextAccessor contextAccessor,
    NavigationManager navigationManager,
    IClusterClient client)
{
    private readonly CancellationTokenSource _cts = new();

    // MUST be set in OnInitializedAsync.
    private MatchmakingObserver _observer = null!;
    private IMatchmakingGrain _grain = null!;
    private Task _keepAliveTask = null!;

    [Parameter]
    public List<AvailableGame> AvailableGames { get; set; } = [];

    protected override async Task OnInitializedAsync()
    {
        _grain = client.GetGrain<IMatchmakingGrain>("JOHN");

        AvailableGames = await _grain.GetAvailableGames();

        _observer = new MatchmakingObserver(this);
        var obj = client.CreateObjectReference<IMatchmakingObserver>(_observer);
        await _grain.Subscribe(obj);

        _keepAliveTask = Task.Run(
            async () =>
            {
                try
                {

                    while (!_cts.IsCancellationRequested)
                    {
                        await _grain.Subscribe(obj);
                        await Task.Delay(TimeSpan.FromSeconds(10), _cts.Token);
                    }

                    await _grain.Unsubscribe(obj);
                }
                catch (OperationCanceledException)
                {
                    await _grain.Unsubscribe(obj);
                }
            },
            CancellationToken.None);

        await base.OnInitializedAsync();
    }

    private async Task CreateGame()
    {
        if (contextAccessor.HttpContext?.Request.Cookies.TryGetValue("john", out var playerId) != true
            || string.IsNullOrEmpty(playerId))
        {
            navigationManager.NavigateTo("/");
            return;
        }

        var gameId = Guid.NewGuid().ToString();
        var grain = client.GetGrain<IGameGrain>(gameId);
        await grain.Create(playerId);
        await _cts.CancelAsync();
        await _keepAliveTask;
        navigationManager.NavigateTo($"/GameLobby/{gameId}");
    }

    private async Task SetAvailableGames(List<AvailableGame> availableGames)
    {
        AvailableGames = availableGames;
        await InvokeAsync(StateHasChanged);
    }

    private class MatchmakingObserver(Index index) : IMatchmakingObserver
    {
        public async Task Message(MatchmakingMessage message)
        {
            switch (message)
            {
                case MatchmakingMessage.UpdatedGames { AvailableGames: var availableGames, }:
                    await index.SetAvailableGames(availableGames);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(message));
            }
        }
    }
}