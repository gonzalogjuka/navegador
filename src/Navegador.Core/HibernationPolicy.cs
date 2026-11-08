namespace Navegador.Core;

public enum TabState
{
    /// <summary>Visible o en memoria normal.</summary>
    Live,
    /// <summary>Memoria baja + scripts pausados; vuelve al instante.</summary>
    Suspended,
    /// <summary>WebView destruido; queda URL, título y scroll. Se recarga al activarla.</summary>
    Discarded,
}

public enum HibernateAction { LowerMemory, Suspend, Discard }

public readonly record struct TabSnapshot(int Id, bool IsActive, TabState State, DateTime LastActiveUtc, bool PlayingAudio);

public readonly record struct PolicyDecision(int TabId, HibernateAction Action, string Reason);

/// <summary>
/// Decide qué pestañas bajan de nivel. Es pura: el que la llama aplica las acciones.
/// Reglas, en orden:
///   1. La pestaña activa nunca se suspende ni se descarta; con el foco perdido pasa a memoria baja.
///   2. Las que reproducen audio no se tocan.
///   3. Inactiva DiscardAfter → descartar; inactiva SuspendAfter (o foco perdido FocusLost) → suspender.
///   4. Si la memoria supera el presupuesto, se descarta además la pestaña viva de fondo usada hace más tiempo.
/// </summary>
public static class HibernationPolicy
{
    public static List<PolicyDecision> Decide(
        IReadOnlyList<TabSnapshot> tabs,
        DateTime nowUtc,
        Settings settings,
        DateTime? focusLostSinceUtc,
        double? memoryMb,
        bool activeAlreadyLowered)
    {
        var decisions = new List<PolicyDecision>();
        var focusLost = focusLostSinceUtc is { } since && (nowUtc - since).TotalSeconds >= settings.FocusLostSeconds;
        var discarded = new HashSet<int>();

        foreach (var t in tabs)
        {
            if (t.IsActive)
            {
                if (focusLost && !activeAlreadyLowered && t.State == TabState.Live)
                    decisions.Add(new(t.Id, HibernateAction.LowerMemory, "focus_lost"));
                continue;
            }
            if (t.PlayingAudio || t.State == TabState.Discarded) continue;

            var idle = nowUtc - t.LastActiveUtc;
            if (idle.TotalMinutes >= settings.DiscardAfterMinutes)
            {
                decisions.Add(new(t.Id, HibernateAction.Discard, "idle"));
                discarded.Add(t.Id);
            }
            else if (t.State == TabState.Live && (focusLost || idle.TotalSeconds >= settings.SuspendAfterSeconds))
            {
                decisions.Add(new(t.Id, HibernateAction.Suspend, focusLost ? "focus_lost" : "idle"));
            }
        }

        if (memoryMb is { } mb && mb > settings.RamBudgetMb)
        {
            var victim = tabs
                .Where(t => !t.IsActive && !t.PlayingAudio && t.State != TabState.Discarded && !discarded.Contains(t.Id))
                .OrderBy(t => t.LastActiveUtc)
                .Select(t => (TabSnapshot?)t)
                .FirstOrDefault();
            if (victim is { } v)
            {
                decisions.RemoveAll(d => d.TabId == v.Id);
                decisions.Add(new(v.Id, HibernateAction.Discard, "budget"));
            }
        }

        return decisions;
    }
}
