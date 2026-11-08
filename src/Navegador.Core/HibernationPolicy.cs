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

/// <param name="KeepAlive">No se puede descartar (p. ej. tiene un formulario escrito sin enviar); sí suspender.</param>
public readonly record struct TabSnapshot(int Id, bool IsActive, TabState State, DateTime LastActiveUtc, bool PlayingAudio, bool KeepAlive = false);

public readonly record struct PolicyDecision(int TabId, HibernateAction Action, string Reason);

/// <summary>Memoria medida: total del navegador y la parte de los procesos que dibujan páginas (renderers).</summary>
public readonly record struct MemoryReading(double TotalMb, double RendererMb);

/// <summary>Último descarte por tope: cuándo y con cuánta memoria total.</summary>
public readonly record struct BudgetDiscard(DateTime AtUtc, double TotalMb);

/// <summary>
/// Decide qué pestañas bajan de nivel. Es pura: el que la llama aplica las acciones.
/// Reglas, en orden:
///   1. La pestaña activa nunca se suspende ni se descarta; con el foco perdido pasa a memoria baja.
///   2. Las que reproducen audio no se tocan.
///   3. Inactiva DiscardAfter → descartar (si es KeepAlive, sólo suspender); inactiva SuspendAfter (o foco perdido
///      FocusLost) → suspender.
///   4. Si la memoria supera el presupuesto, se descartan las pestañas de fondo usadas hace más tiempo, tantas como
///      haga falta para volver debajo del tope según lo que ocupa en promedio cada pestaña viva. Se salvan las
///      BudgetKeepRecentTabs pestañas de fondo usadas más recientemente si se usaron en los últimos
///      BudgetKeepRecentMinutes (descartar la pestaña de la que recién saliste obliga a recargarla al volver, y eso
///      sube la memoria otra vez: el bucle de descartar y recargar) y las KeepAlive. El tope es una meta, no un corte.
///      Después de un descarte por tope se espera BudgetCooldown a que los procesos liberen la memoria, salvo que la
///      memoria siga subiendo por encima de la de ese momento (se están cargando más páginas).
/// </summary>
public static class HibernationPolicy
{
    public static readonly TimeSpan BudgetCooldown = TimeSpan.FromSeconds(15);

    public static List<PolicyDecision> Decide(
        IReadOnlyList<TabSnapshot> tabs,
        DateTime nowUtc,
        Settings settings,
        DateTime? focusLostSinceUtc,
        MemoryReading? memory,
        bool activeAlreadyLowered,
        BudgetDiscard? lastBudgetDiscard = null)
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
            if (idle.TotalMinutes >= settings.DiscardAfterMinutes && !t.KeepAlive)
            {
                decisions.Add(new(t.Id, HibernateAction.Discard, "idle"));
                discarded.Add(t.Id);
            }
            else if (t.State == TabState.Live && (focusLost || idle.TotalSeconds >= settings.SuspendAfterSeconds))
            {
                decisions.Add(new(t.Id, HibernateAction.Suspend, focusLost ? "focus_lost" : "idle"));
            }
        }

        var coolingDown = lastBudgetDiscard is { } last && nowUtc - last.AtUtc < BudgetCooldown
                          && memory is { } now && now.TotalMb <= last.TotalMb;
        if (memory is { } m && m.TotalMb > settings.RamBudgetMb && !coolingDown)
        {
            var alive = tabs.Count(t => t.State != TabState.Discarded && !discarded.Contains(t.Id));
            var perTab = alive > 0 ? m.RendererMb / alive : 0;
            var needed = perTab > 0 ? (int)Math.Ceiling((m.TotalMb - settings.RamBudgetMb) / perTab) : 1;
            var recent = tabs
                .Where(t => !t.IsActive && t.State != TabState.Discarded && (nowUtc - t.LastActiveUtc).TotalMinutes < settings.BudgetKeepRecentMinutes)
                .OrderByDescending(t => t.LastActiveUtc)
                .Take(settings.BudgetKeepRecentTabs)
                .Select(t => t.Id)
                .ToHashSet();
            var victims = tabs
                .Where(t => !t.IsActive && !t.PlayingAudio && !t.KeepAlive && t.State != TabState.Discarded && !discarded.Contains(t.Id)
                            && !recent.Contains(t.Id))
                .OrderBy(t => t.LastActiveUtc)
                .Take(Math.Max(1, needed));
            foreach (var v in victims)
            {
                decisions.RemoveAll(d => d.TabId == v.Id);
                decisions.Add(new(v.Id, HibernateAction.Discard, "budget"));
            }
        }

        return decisions;
    }
}
