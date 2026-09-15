using Dalamud.Game.Chat;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using LuminaDynamicEvent = Lumina.Excel.Sheets.DynamicEvent;
using Ocelot.Lifecycle;
using System;
using System.Linq;

namespace Botja.Services;

public sealed unsafe class CeSignupService(GuiInteractionService guiInteract, IDataManager data, IChatGui chatGui, IObjectTable objects, BlacklistConfig blacklist) : IOnUpdate, IOnLoad, IOnStop
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
        CheckingImmediateCommence,
        WaitingForSelection,
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
                RequestRecruitmentWindow();
                if (!TryFindRecruitingEvent(out targetEventId))
                {
                    Status = "Waiting for a non-solo, non-large-scale recruiting CE.";
                }
                else if (ClickButton("Register", "Request Deployment", "Deploy"))
                {
                    Advance(Phase.ConfirmingRegistration, "Registration requested; waiting for confirmation.");
                }
                else
                {
                    Status = "Waiting for the recruitment window.";
                }
                break;

            case Phase.ConfirmingRegistration:
                if (guiInteract.ClickSelectYesnoYes())
                    Advance(Phase.CheckingImmediateCommence, "Registration confirmed; reopening recruitment to check deployment.");
                break;

            case Phase.CheckingImmediateCommence:
                RequestRecruitmentWindow();
                if (!guiInteract.IsCeRecruitmentWindowOpen())
                {
                    Status = "Registration confirmed; opening recruitment to check deployment.";
                    break;
                }

                if (ClickButton("Commence", "Enter", "Join", "Deploy Now", "Proceed", "Begin", "Start"))
                    Advance(Phase.ConfirmingCommence, "Commence requested; waiting for confirmation.");
                else
                    Advance(Phase.WaitingForSelection, "Registered; waiting for the recruitment timer.");
                break;

            case Phase.WaitingForSelection:
                var registration = GetRegistrationState();
                if (registration == null)
                {
                    Status = "Registered; waiting for CE assignment.";
                    break;
                }

                targetEventId = registration.Value.EventId;
                if (registration.Value.State == DynamicEventState.Register && registration.Value.SecondsLeft > 0)
                {
                    Status = $"Registered; waiting for selection ({registration.Value.SecondsLeft}s).";
                    break;
                }

                Advance(Phase.WaitingForCommence, "Recruitment ended; opening the deployment window.");
                break;

            case Phase.WaitingForCommence:
                RequestRecruitmentWindow();
                if (ClickButton("Commence", "Enter", "Join", "Deploy Now", "Proceed", "Begin", "Start"))
                {
                    Advance(Phase.ConfirmingCommence, "Commence requested; waiting for confirmation.");
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

    private bool ClickButton(params string[] labels)
    {
        var eventName = GetEventName(targetEventId);
        if (string.IsNullOrWhiteSpace(eventName))
            return false;

        var button = guiInteract.GetCeRecruitmentButtons().FirstOrDefault(candidate =>
            labels.Any(label => candidate.Equals(label, StringComparison.OrdinalIgnoreCase)));
        return button != null && guiInteract.ClickCeRecruitmentButton(button, eventName);
    }

    private void RequestRecruitmentWindow()
    {
        if (openRequested)
            return;

        openRequested = guiInteract.OpenCeRecruitmentWindow();
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
    public bool TryFindRecruitingEvent(out ushort eventId)
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
                dynamicEvent.State == DynamicEventState.Register &&
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

    private (ushort EventId, DynamicEventState State, uint SecondsLeft)? GetRegistrationState()
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
                return (targetEventId, dynamicEvent.State, dynamicEvent.SecondsLeft);
        }

        return null;
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
               blacklist.CeEventIds.Contains(eventId);
    }

    // eventId here is the Lumina DynamicEvent sheet row ID (the CE's template/type ID, stable across
    // every occurrence of that CE) — not a per-instance runtime handle, so this blacklist persists.
    public bool IsCeBlacklisted(ushort eventId) => blacklist.CeEventIds.Contains(eventId);

    public void BlacklistCe(ushort eventId) => blacklist.CeEventIds.Add(eventId);

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

        ushort eventId = targetEventId;
        if (eventId == 0 && container->CurrentEventIndex >= 0)
            eventId = container->CurrentEventId;
        if (eventId == 0)
            return "No registered CE timer";

        foreach (ref var dynamicEvent in container->Events)
        {
            if (dynamicEvent.DynamicEventId != eventId)
                continue;

            return $"CE #{dynamicEvent.DynamicEventId}: {dynamicEvent.SecondsLeft}s ({dynamicEvent.State})";
        }

        return "Registered CE timer unavailable";
    }

    private string GetEventName(ushort eventId)
    {
        var dynamicEvent = data.GetExcelSheet<LuminaDynamicEvent>()?.GetRowOrDefault(eventId);
        return dynamicEvent?.Name.ToString() ?? string.Empty;
    }
}