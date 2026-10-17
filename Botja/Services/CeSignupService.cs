using Dalamud.Game.Chat;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using LuminaDynamicEvent = Lumina.Excel.Sheets.DynamicEvent;
using Ocelot.Config;
using Ocelot.Lifecycle;
using System;
using System.Linq;

namespace Botja.Services;

public sealed unsafe class CeSignupService(GuiInteractionService guiInteract, IDataManager data, IChatGui chatGui, IObjectTable objects, IPluginConfig pluginConfig, IConfigSaver configSaver, IPluginLog log, AutoModeState autoModeState, IPartyList party) : IOnUpdate, IOnLoad, IOnStop
{
    private static readonly TimeSpan ActionDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromMinutes(5);
    private const uint LargeScaleEventType = 4;
    private const uint SoloEngagementEnemyType = 3;

    // The game announces a nearby CE with this phrase in chat before it shows up as recruiting in
    // the DynamicEventContainer — treat it as a short-lived hint so callers can react immediately.
    private const string CeAnnouncementText = "critical engagement";
    private static readonly TimeSpan CeAnnouncementWindow = TimeSpan.FromMinutes(2);
    private DateTime? ceAnnouncedAt;

    private enum Phase
    {
        Idle,
        Opening,
        ConfirmingRegistration,
        WaitingForCommence,
        ConfirmingCommence,
    }

    private Phase phase;
    private DateTime startedAt;
    private DateTime nextActionAt;
    private bool openRequested;
    private ushort targetEventId;

    public string Status { get; private set; } = "Idle";
    public bool IsRunning => phase != Phase.Idle;
    public string DetectedTimer => GetDetectedTimer();

    // Set once a CE's Commence is confirmed; stays set (even after Stop/Idle) until a caller
    // consumes it, so CombatControlService can pick up the newly-active CE on the next tick.
    public ushort LastCommencedEventId { get; private set; }

    // True for a short window after chat announces a nearby CE — lets AutoModeService react
    // immediately instead of waiting for the DynamicEventContainer to reflect the Register state.
    public bool HasRecentCeAnnouncement => ceAnnouncedAt is { } at && DateTime.UtcNow - at < CeAnnouncementWindow;

    public void OnLoad() => chatGui.ChatMessage += OnChatMessage;

    public void OnStop() => chatGui.ChatMessage -= OnChatMessage;

    private void OnChatMessage(IHandleableChatMessage message)
    {
        if (message.Message.TextValue.Contains(CeAnnouncementText, StringComparison.OrdinalIgnoreCase))
            ceAnnouncedAt = DateTime.UtcNow;
    }

    public void Start()
    {
        phase = Phase.Opening;
        startedAt = DateTime.UtcNow;
        nextActionAt = DateTime.MinValue;
        openRequested = false;
        targetEventId = 0;
        Status = "Opening Resistance Recruitment.";
    }

    public void Stop()
    {
        phase = Phase.Idle;
        Status = "Stopped";
    }

    public void Update()
    {
        if (phase == Phase.Idle || DateTime.UtcNow < nextActionAt)
            return;

        if (objects.LocalPlayer?.IsDead == true)
        {
            StopWithStatus("Paused: player is dead.");
            return;
        }

        if (DateTime.UtcNow - startedAt > AttemptTimeout)
        {
            StopWithStatus("Stopped: CE signup timed out.");
            return;
        }

        switch (phase)
        {
            case Phase.Opening:
                if (targetEventId == 0 && !TryFindRecruitingEvent(out targetEventId, includeWarmup: true))
                {
                    Status = "Waiting for a non-solo, non-large-scale joinable CE.";
                    break;
                }

                var pendingEvent = GetRegistrationState();
                if (pendingEvent == null)
                {
                    Status = "Waiting for CE assignment.";
                    break;
                }

                if (pendingEvent.Value.State != DynamicEventState.Register && pendingEvent.Value.State != DynamicEventState.Warmup)
                {
                    StopWithStatus("Stopped: CE registration is no longer available.");
                    break;
                }

                if (pendingEvent.Value.State == DynamicEventState.Register && IsWaitingForPartyLeader())
                {
                    Status = $"Waiting for the party leader to register or CE warmup ({pendingEvent.Value.SecondsLeft}s left).";
                    break;
                }

                if (!EnsureRecruitmentWindowOpen())
                {
                    Status = "Waiting for the recruitment window.";
                    break;
                }

                if (ClickButton("Register", "Request Deployment", "Deploy"))
                {
                    Advance(Phase.ConfirmingRegistration, "Registration requested; waiting for confirmation.");
                }
                else if (pendingEvent.Value.State == DynamicEventState.Warmup)
                {
                    Advance(Phase.WaitingForCommence, "CE warmup started; waiting for the deployment timer.");
                }
                else
                {
                    Status = "Waiting for the recruitment window.";
                }
                break;

            case Phase.ConfirmingRegistration:
                if (guiInteract.ClickSelectYesnoYes())
                    Advance(Phase.WaitingForCommence, "Registered; waiting for the CE timer.");
                break;

            case Phase.WaitingForCommence:
                var registration = GetRegistrationState();
                if (registration == null)
                {
                    Status = "Registered; waiting for CE assignment.";
                    break;
                }

                targetEventId = registration.Value.EventId;
                if (registration.Value.State == DynamicEventState.Register)
                {
                    Status = $"Registered; waiting for selection ({registration.Value.SecondsLeft}s).";
                    break;
                }

                if (registration.Value.State != DynamicEventState.Warmup)
                {
                    StopWithStatus("Stopped: CE deployment is no longer available.");
                    break;
                }

                if (autoModeState.IsRunning && !ShouldCommence(registration.Value.SecondsLeft,
                    registration.Value.WarmupSeconds, autoModeState.IsEngagedInFate))
                {
                    Status = autoModeState.IsEngagedInFate
                        ? $"Registered; continuing FATE until 15s remain ({registration.Value.SecondsLeft}s left)."
                        : $"Registered; waiting for 20s of CE warmup to elapse ({registration.Value.SecondsLeft}s left).";
                    break;
                }

                if (!EnsureRecruitmentWindowOpen())
                {
                    Status = "Waiting for the recruitment window.";
                    break;
                }

                if (ClickButton("Commence", "Enter", "Join", "Deploy Now", "Proceed", "Begin", "Start"))
                {
                    Advance(Phase.ConfirmingCommence, "Commence requested; waiting for confirmation.");
                }
                else if (ClickButton("Register", "Request Deployment", "Deploy"))
                {
                    Advance(Phase.ConfirmingRegistration, "Registration requested; waiting for confirmation.");
                }
                else
                {
                    Status = "Registered; waiting for Commence.";
                }
                break;

            case Phase.ConfirmingCommence:
                if (guiInteract.ClickSelectYesnoYes())
                {
                    LastCommencedEventId = targetEventId;
                    StopWithStatus("Commence confirmed.");
                }
                break;
        }

        nextActionAt = DateTime.UtcNow + ActionDelay;
    }

    private bool IsWaitingForPartyLeader()
    {
        if (party.Length <= 1)
            return false;

        var leaderIndex = (int)party.PartyLeaderIndex;
        return leaderIndex < 0 || leaderIndex >= party.Length || objects.LocalPlayer is not { } localPlayer ||
            party[leaderIndex]?.EntityId != localPlayer.EntityId;
    }

    private bool ClickButton(params string[] labels)
    {
        if (!guiInteract.IsCeRecruitmentWindowOpen())
            return false;

        var eventName = GetEventName(targetEventId);
        if (string.IsNullOrWhiteSpace(eventName))
            return false;

        var button = guiInteract.GetCeRecruitmentButtons().FirstOrDefault(candidate =>
            labels.Any(label => candidate.Equals(label, StringComparison.OrdinalIgnoreCase)));
        return button != null && guiInteract.ClickCeRecruitmentButton(button, eventName);
    }

    private bool EnsureRecruitmentWindowOpen()
    {
        if (guiInteract.IsCeRecruitmentWindowOpen())
        {
            openRequested = false;
            return true;
        }

        if (!openRequested)
            openRequested = guiInteract.OpenCeRecruitmentWindow();

        return false;
    }

    private void Advance(Phase nextPhase, string status)
    {
        phase = nextPhase;
        openRequested = false;
        Status = status;
    }

    private void StopWithStatus(string status)
    {
        phase = Phase.Idle;
        Status = status;
    }

    // Scans for any CE currently open for registration, regardless of our own signup state —
    // used by AutoModeService to decide whether a CE should take priority over the next FATE.
    public bool TryFindRecruitingEvent(out ushort eventId, bool includeWarmup = false)
    {
        eventId = 0;

        DynamicEventContainer* container;
        try
        {
            container = DynamicEventContainer.GetInstance();
        }
        catch
        {
            return false;
        }

        if (container == null)
            return false;

        foreach (ref var dynamicEvent in container->Events)
        {
            if (dynamicEvent.DynamicEventId != 0 &&
                (dynamicEvent.State == DynamicEventState.Register || (includeWarmup && dynamicEvent.State == DynamicEventState.Warmup)) &&
                !IsExcludedEvent(dynamicEvent.DynamicEventId))
            {
                eventId = dynamicEvent.DynamicEventId;
                return true;
            }
        }

        return false;
    }

    // True if the local player is currently inside an in-progress CE (Warmup/Battle) — regardless of
    // whether Botja's own signup flow was the one that got them there. Lets CombatControlService arm
    // combat for a CE that was already underway when Botja/Auto Mode started, or one joined manually.
    public bool TryGetCurrentEventId(out ushort eventId)
    {
        eventId = 0;

        DynamicEventContainer* container;
        try
        {
            container = DynamicEventContainer.GetInstance();
        }
        catch
        {
            return false;
        }

        if (container == null || container->CurrentEventIndex < 0 || container->CurrentEventId == 0)
            return false;

        foreach (ref var dynamicEvent in container->Events)
        {
            if (dynamicEvent.DynamicEventId == container->CurrentEventId &&
                (dynamicEvent.State == DynamicEventState.Warmup || dynamicEvent.State == DynamicEventState.Battle))
            {
                eventId = container->CurrentEventId;
                return true;
            }
        }

        return false;
    }

    private (ushort EventId, DynamicEventState State, uint SecondsLeft, uint WarmupSeconds)? GetRegistrationState()
    {
        DynamicEventContainer* container;
        try
        {
            container = DynamicEventContainer.GetInstance();
        }
        catch
        {
            return null;
        }

        if (container == null)
            return null;

        if (targetEventId == 0 && container->CurrentEventIndex >= 0)
            targetEventId = container->CurrentEventId;

        if (targetEventId == 0)
            return null;

        foreach (ref var dynamicEvent in container->Events)
        {
            if (dynamicEvent.DynamicEventId == targetEventId)
            {
                var eventPointer = (byte*)System.Runtime.CompilerServices.Unsafe.AsPointer(ref dynamicEvent);
                return (targetEventId, dynamicEvent.State,
                    GetTimerSeconds(dynamicEvent.State, dynamicEvent.StartTimestamp, dynamicEvent.SecondsLeft,
                        DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
                    *(uint*)(eventPointer + 0x70));
            }
        }

        return null;
    }

    private static bool ShouldCommence(uint secondsLeft, uint warmupSeconds, bool engagedInFate)
    {
        if (secondsLeft == 0)
            return false;

        if (secondsLeft <= 15)
            return true;

        return !engagedInFate && warmupSeconds >= secondsLeft && warmupSeconds - secondsLeft >= 20;
    }

    private static uint GetTimerSeconds(DynamicEventState state, int startTimestamp, uint secondsLeft, long nowUnix)
    {
        if ((state == DynamicEventState.Register || state == DynamicEventState.Warmup || state == DynamicEventState.Battle) && startTimestamp > 0)
            return (uint)Math.Clamp((long)startTimestamp - nowUnix, 0L, uint.MaxValue);

        return secondsLeft;
    }

    private bool IsLargeScale(ushort eventId)
    {
        var dynamicEvent = data.GetExcelSheet<LuminaDynamicEvent>()?.GetRowOrDefault(eventId);
        return dynamicEvent?.EventType.RowId == LargeScaleEventType;
    }

    private bool IsExcludedEvent(ushort eventId)
    {
        var dynamicEvent = data.GetExcelSheet<LuminaDynamicEvent>()?.GetRowOrDefault(eventId);
        return dynamicEvent?.EventType.RowId == LargeScaleEventType ||
               dynamicEvent?.EnemyType.RowId == SoloEngagementEnemyType ||
               dynamicEvent?.MaxParticipants == 1 ||
               pluginConfig.Blacklist.CeEventIds.Contains(eventId);
    }

    // eventId here is the Lumina DynamicEvent sheet row ID (the CE's template/type ID, stable across
    // every occurrence of that CE) — not a per-instance runtime handle, so this blacklist persists.
    public bool IsCeBlacklisted(ushort eventId) => pluginConfig.Blacklist.CeEventIds.Contains(eventId);

    public void BlacklistCe(ushort eventId)
    {
        if (pluginConfig.Blacklist.CeEventIds.Contains(eventId))
            return;

        pluginConfig.Blacklist.CeEventIds = [.. pluginConfig.Blacklist.CeEventIds, eventId];
        log.Info("[Blacklist] Saving CE IDs: {CeEventIds}", string.Join(", ", pluginConfig.Blacklist.CeEventIds));
        configSaver.Save();
    }

    public void UnblacklistCe(ushort eventId)
    {
        if (!pluginConfig.Blacklist.CeEventIds.Remove(eventId))
            return;

        log.Info("[Blacklist] Saving CE IDs: {CeEventIds}", string.Join(", ", pluginConfig.Blacklist.CeEventIds));
        configSaver.Save();
    }

    public string GetCeName(ushort eventId) => GetEventName(eventId);

    private string GetDetectedTimer()
    {
        DynamicEventContainer* container;
        try
        {
            container = DynamicEventContainer.GetInstance();
        }
        catch
        {
            return "No CE timer detected";
        }

        if (container == null)
            return "No registered CE timer";

        var selectedTimer = "No active CE timer";
        var selectedPriority = 0;
        foreach (ref var dynamicEvent in container->Events)
        {
            if (dynamicEvent.DynamicEventId == 0 || dynamicEvent.State == DynamicEventState.Inactive)
                continue;

            var isCurrentEvent = container->CurrentEventIndex >= 0 && dynamicEvent.DynamicEventId == container->CurrentEventId;
            if (dynamicEvent.State == DynamicEventState.Battle && !isCurrentEvent)
                continue;

            var priority = isCurrentEvent
                ? 3
                : dynamicEvent.DynamicEventId == targetEventId ? 2 : 1;
            if (priority <= selectedPriority)
                continue;

            selectedPriority = priority;
            var secondsLeft = GetTimerSeconds(dynamicEvent.State, dynamicEvent.StartTimestamp,
                dynamicEvent.SecondsLeft, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            selectedTimer = $"CE #{dynamicEvent.DynamicEventId}: {secondsLeft}s ({dynamicEvent.State})";
        }

        return selectedTimer;
    }

    private string GetEventName(ushort eventId)
    {
        var dynamicEvent = data.GetExcelSheet<LuminaDynamicEvent>()?.GetRowOrDefault(eventId);
        return dynamicEvent?.Name.ToString() ?? string.Empty;
    }
}