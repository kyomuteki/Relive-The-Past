using System;
using System.Collections.Generic;
using InventorySystem.Items;
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.CustomHandlers;
using LabApi.Features;
using Logger = LabApi.Features.Console.Logger;
using LabApi.Features.Wrappers;
using LabApi.Loader.Features.Plugins;
using MEC;
using PlayerRoles;
using UnityEngine;

namespace ReliveThePast;

/// <summary>
/// Grants exactly one controlled respawn to players who die in the configured
/// opening window of a round.
/// </summary>
public sealed class ReliveThePastPlugin : Plugin<ReliveThePastConfig>
{
    private readonly SecondChanceEventHandler _events;

    public ReliveThePastPlugin()
    {
        _events = new SecondChanceEventHandler(this);
    }

    public override string Name => "Relive The Past";

    public override string Description => "Grants players one second chance when they die early in a round.";

    public override string Author => "OPGman; LabAPI conversion by Manus";

    public override Version RequiredApiVersion { get; } = new Version(LabApiProperties.CompiledVersion);

    public override void Enable()
    {
        _events.ResetForCurrentRound();
        CustomHandlersManager.RegisterEventsHandler(_events);
        Logger.Info($"{Name} enabled. Early-death window: {Config.EarlyDeathWindowSeconds:0.##} seconds.");
    }

    public override void Disable()
    {
        CustomHandlersManager.UnregisterEventsHandler(_events);
        _events.Stop();
        Logger.Info($"{Name} disabled.");
    }
}

/// <summary>
/// Server configuration. LabAPI writes the default configuration when no config file exists.
/// </summary>
public sealed class ReliveThePastConfig
{
    /// <summary>Enables the early-death second-chance mechanic.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// The number of seconds after the round begins in which a death earns a second chance.
    /// Set to 30 for the requested behavior.
    /// </summary>
    public float EarlyDeathWindowSeconds { get; set; } = 30f;

    /// <summary>
    /// Seconds to wait after an eligible death before respawning that player. A short delay
    /// prevents a burst of role changes when many players die at once.
    /// </summary>
    public float RespawnDelaySeconds { get; set; } = 3f;

    /// <summary>Role used for the second chance.</summary>
    public SecondChanceRole RespawnRole { get; set; } = SecondChanceRole.RandomClassDOrScientist;

    /// <summary>
    /// Upper bound on respawns performed in one server frame. This spreads a mass-casualty
    /// respawn across frames instead of causing a single-frame spike on high-population servers.
    /// </summary>
    public int MaxRespawnsPerFrame { get; set; } = 8;

    /// <summary>
    /// Stops a pending second-chance respawn while the alpha warhead is active or has detonated.
    /// </summary>
    public bool CancelDuringWarheadSequence { get; set; } = true;

    /// <summary>
    /// Optional Class-D keycard delay. Set to 0 to disable the keycard, preserving the original
    /// plugin's behavior.
    /// </summary>
    public float KeycardDelaySeconds { get; set; } = 0f;

    /// <summary>Keycard given to an eligible second-chance Class D.</summary>
    public ItemType KeycardType { get; set; } = ItemType.KeycardJanitor;

    /// <summary>
    /// Private broadcast shown only to a player who receives their early-death second chance.
    /// </summary>
    public string SecondChanceMessage { get; set; } = "因為你頭30秒死了，所以給你多一次機會";

    /// <summary>How long the private second-chance broadcast remains visible, in seconds.</summary>
    public float SecondChanceMessageDurationSeconds { get; set; } = 6f;

    /// <summary>Writes concise eligibility and cancellation messages to the server console.</summary>
    public bool Debug { get; set; }
}

public enum SecondChanceRole
{
    RandomClassDOrScientist,
    ClassD,
    Scientist,
}

internal sealed class SecondChanceEventHandler : CustomEventsHandler
{
    private const float SchedulerPollSeconds = 0.10f;

    private readonly ReliveThePastPlugin _plugin;
    private readonly List<RespawnRequest> _pendingRespawns = new();
    private readonly HashSet<string> _playersWhoUsedTheirChance = new(StringComparer.OrdinalIgnoreCase);

    private CoroutineHandle _schedulerHandle;
    private bool _schedulerRunning;
    private float _roundStartedAt = float.NegativeInfinity;
    private int _roundSequence;

    public SecondChanceEventHandler(ReliveThePastPlugin plugin)
    {
        _plugin = plugin;
    }

    public override void OnServerRoundStarted()
    {
        BeginNewRound();
    }

    public override void OnPlayerDeath(PlayerDeathEventArgs ev)
    {
        ReliveThePastConfig config = _plugin.Config;
        if (!config.IsEnabled || ev.Player.IsHost || !Round.IsRoundInProgress)
        {
            return;
        }

        float now = Time.realtimeSinceStartup;
        if (now - _roundStartedAt > Mathf.Max(0f, config.EarlyDeathWindowSeconds))
        {
            return;
        }

        string userId = ev.Player.UserId;
        if (string.IsNullOrWhiteSpace(userId) || !_playersWhoUsedTheirChance.Add(userId))
        {
            return;
        }

        float dueAt = now + Mathf.Max(0f, config.RespawnDelaySeconds);
        _pendingRespawns.Add(new RespawnRequest(ev.Player, _roundSequence, dueAt));

        if (config.Debug)
        {
            Logger.Debug($"Queued one second-chance respawn for {ev.Player.LogName}.");
        }

        StartSchedulerIfNeeded();
    }

    public void ResetForCurrentRound()
    {
        Stop();
        _roundStartedAt = Round.IsRoundInProgress
            ? Time.realtimeSinceStartup - (float)Round.Duration.TotalSeconds
            : float.NegativeInfinity;
    }

    public void Stop()
    {
        if (_schedulerRunning)
        {
            Timing.KillCoroutines(_schedulerHandle);
            _schedulerRunning = false;
        }

        _pendingRespawns.Clear();
        _playersWhoUsedTheirChance.Clear();
    }

    private void BeginNewRound()
    {
        Stop();
        _roundSequence++;
        _roundStartedAt = Time.realtimeSinceStartup;
    }

    private void StartSchedulerIfNeeded()
    {
        if (_schedulerRunning)
        {
            return;
        }

        _schedulerRunning = true;
        _schedulerHandle = Timing.RunCoroutine(ProcessPendingRespawns());
    }

    private IEnumerator<float> ProcessPendingRespawns()
    {
        while (_pendingRespawns.Count > 0)
        {
            float now = Time.realtimeSinceStartup;
            int respawnsThisFrame = 0;
            int maxPerFrame = Mathf.Max(1, _plugin.Config.MaxRespawnsPerFrame);

            for (int index = _pendingRespawns.Count - 1; index >= 0 && respawnsThisFrame < maxPerFrame; index--)
            {
                RespawnRequest request = _pendingRespawns[index];
                if (request.DueAt > now)
                {
                    continue;
                }

                _pendingRespawns.RemoveAt(index);
                TryRespawn(request);
                respawnsThisFrame++;
            }

            // Yield every cycle. This keeps server work bounded, including when dozens of players
            // become eligible in the same frame.
            yield return respawnsThisFrame > 0 ? Timing.WaitForOneFrame : Timing.WaitForSeconds(SchedulerPollSeconds);
        }

        _schedulerRunning = false;
    }

    private void TryRespawn(RespawnRequest request)
    {
        Player player = request.Player;
        if (request.RoundSequence != _roundSequence || !Round.IsRoundInProgress || player.IsDestroyed || player.IsAlive)
        {
            return;
        }

        if (_plugin.Config.CancelDuringWarheadSequence && (Warhead.IsDetonated || Warhead.IsDetonationInProgress))
        {
            LogCancellation(player, "the alpha warhead is active or has detonated");
            return;
        }

        RoleTypeId role = SelectRespawnRole();
        player.SetRole(role, RoleChangeReason.RemoteAdmin);

        if (!string.IsNullOrWhiteSpace(_plugin.Config.SecondChanceMessage))
        {
            ushort duration = (ushort)Mathf.Clamp(
                Mathf.CeilToInt(_plugin.Config.SecondChanceMessageDurationSeconds),
                1,
                ushort.MaxValue);

            // SendBroadcast targets this one player only. Clearing previous broadcasts also affects only this player,
            // ensuring the second-chance notification is immediately visible after their role is assigned.
            player.SendBroadcast(_plugin.Config.SecondChanceMessage, duration, shouldClearPrevious: true);
        }

        if (role == RoleTypeId.ClassD && ShouldGiveKeycard())
        {
            player.AddItem(_plugin.Config.KeycardType);
        }

        if (_plugin.Config.Debug)
        {
            Logger.Debug($"Granted one second-chance respawn to {player.LogName} as {role}.");
        }
    }

    private RoleTypeId SelectRespawnRole()
    {
        return _plugin.Config.RespawnRole switch
        {
            SecondChanceRole.ClassD => RoleTypeId.ClassD,
            SecondChanceRole.Scientist => RoleTypeId.Scientist,
            _ => UnityEngine.Random.Range(0, 2) == 0 ? RoleTypeId.ClassD : RoleTypeId.Scientist,
        };
    }

    private bool ShouldGiveKeycard()
    {
        float delay = _plugin.Config.KeycardDelaySeconds;
        return delay > 0f && Round.Duration.TotalSeconds >= delay;
    }

    private void LogCancellation(Player player, string reason)
    {
        if (_plugin.Config.Debug)
        {
            Logger.Debug($"Cancelled the pending second chance for {player.LogName}: {reason}.");
        }
    }

    private readonly struct RespawnRequest
    {
        public RespawnRequest(Player player, int roundSequence, float dueAt)
        {
            Player = player;
            RoundSequence = roundSequence;
            DueAt = dueAt;
        }

        public Player Player { get; }

        public int RoundSequence { get; }

        public float DueAt { get; }
    }
}
