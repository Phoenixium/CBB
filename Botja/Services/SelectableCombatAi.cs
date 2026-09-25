using Ocelot.Rotation.Services;

namespace Botja.Services;

public enum CombatAiSelection
{
    MiscAi = 1,
    Wrath = 2,
    RotationSolverReborn = 3,
    BossMod = 4,
    BossModReborn = 5,
}

public interface ISelectableCombatAi
{
    CombatAiSelection Kind { get; }

    string DisplayName { get; }

    void EnsurePresets();

    void Enable(CombatActivity activity);

    void Disable();
}

public sealed class CombatAiBackendAdapter(
    ICombatAiBackend backend,
    CombatAiSelection kind,
    string displayName
) : ISelectableCombatAi
{
    public CombatAiSelection Kind => kind;

    public string DisplayName => displayName;

    public void EnsurePresets() => backend.EnsurePresets();

    public void Enable(CombatActivity activity) => backend.Enable(activity);

    public void Disable() => backend.Disable();
}

public sealed class JobRotationCombatAiAdapter(
    IJobRotationBackend backend,
    CombatAiSelection kind,
    string displayName
) : ISelectableCombatAi
{
    public CombatAiSelection Kind => kind;

    public string DisplayName => displayName;

    public void EnsurePresets() => backend.Prepare(new JobRotationSessionOptions());

    public void Enable(CombatActivity activity) => backend.Enable(activity);

    public void Disable() => backend.Disable();
}