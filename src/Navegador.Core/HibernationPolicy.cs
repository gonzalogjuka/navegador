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

/// <summary>
/// Memoria medida: total del navegador y la parte que ocupan las páginas (renderers + proceso de GPU: Google Maps o un
/// video suben la GPU cientos de MB y se los devuelve al descartarlas).
/// </summary>
public readonly record struct MemoryReading(double TotalMb, double PagesMb);

/// <summary>Último descarte por tope: cuándo y con cuánta memoria total.</summary>
public readonly record struct BudgetDiscard(DateTime AtUtc, double TotalMb);

/// <summary>
/// Decide qué pestañas bajan de nivel. Es pura: el que la llama aplica las acciones.
/// Reglas, en orden:
///   1. La pestaña activa nunca se suspende ni se descarta; con el foco perdido pasa a memoria baja.
///   2. Las que reproducen audio no se tocan.
///   3. Inactiva DiscardAfter → descartar (si es KeepAlive, sólo suspender); inactiva SuspendAfter (o foco perdido
///      FocusLost) → suspender.
///   4. Si la memoria supera el presupuesto, se descartan las pestañas de fondo (BudgetVictims) hasta quedar en
///      BudgetTarget del tope, con margen para la próxima página. Las BudgetKeepRecentTabs usadas más recientemente
///      (en los últimos BudgetKeepRecentMinutes) van al final: descartar la pestaña de la que recién saliste obliga a
///      recargarla al volver. El tope manda: suspender no alcanza (una pestaña suspendida conserva su memoria de
///      renderer y de GPU; Google Maps suspendida dejó la GPU en 450–500 MB y descartada bajó a 170). Nunca se
///      descartan la activa, la que suena ni las KeepAlive (perderían lo escrito). Después de un descarte por tope se
///      espera BudgetCooldown a que los procesos liberen la memoria, salvo que la memoria siga subiendo por encima de
///      la de ese momento (se están cargando más páginas).
///   La ventana además hace lugar antes de cargar (abrir o despertar una pestaña) con BudgetVictims y la memoria
///   que se espera que ocupe la página nueva, para no pasar el tope y descartar recién después.
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
            foreach (var id in BudgetVictims(tabs, nowUtc, settings, m, skip: discarded))
            {
                decisions.RemoveAll(d => d.TabId == id);
                decisions.Add(new(id, HibernateAction.Discard, "budget"));
            }
        }

        return decisions;
    }

    /// <summary>
    /// Al descartar por tope se apunta a este porcentaje del tope y no al tope justo: la próxima página que se abra
    /// o se despierte tiene que entrar sin volver a pasarlo.
    /// </summary>
    public const double BudgetTarget = 0.85;

    /// <summary>Lo mínimo que se reserva para una página que va a cargar, aunque las vivas ocupen menos.</summary>
    public const double MinPageMb = 100;

    /// <summary>
    /// Pestañas a descartar para que la memoria actual más <paramref name="incomingMb"/> (una página por cargar) quede
    /// en BudgetTarget del tope. Primero las de fondo usadas hace más tiempo; las recientes (BudgetKeepRecentTabs en
    /// BudgetKeepRecentMinutes) al final. Nunca la activa, la que suena, las KeepAlive ni las de <paramref name="skip"/>.
    /// Cuántas: lo que sobra ÷ lo que ocupa en promedio cada pestaña viva (memoria de renderers), al menos una.
    /// </summary>
    public static List<int> BudgetVictims(IReadOnlyList<TabSnapshot> tabs, DateTime nowUtc, Settings settings,
        MemoryReading memory, double incomingMb = 0, IReadOnlySet<int>? skip = null)
    {
        var overflow = memory.TotalMb + incomingMb - settings.RamBudgetMb * BudgetTarget;
        if (overflow <= 0) return [];
        bool Gone(TabSnapshot t) => t.State == TabState.Discarded || (skip?.Contains(t.Id) ?? false);
        var perTab = AverageLiveTabMb(tabs, memory, skip);
        var needed = perTab > 0 ? (int)Math.Ceiling(overflow / perTab) : 1;
        var recent = tabs
            .Where(t => !t.IsActive && !Gone(t) && (nowUtc - t.LastActiveUtc).TotalMinutes < settings.BudgetKeepRecentMinutes)
            .OrderByDescending(t => t.LastActiveUtc)
            .Take(settings.BudgetKeepRecentTabs)
            .Select(t => t.Id)
            .ToHashSet();
        return tabs
            .Where(t => !t.IsActive && !t.PlayingAudio && !t.KeepAlive && !Gone(t))
            .OrderBy(t => recent.Contains(t.Id))
            .ThenBy(t => t.LastActiveUtc)
            .Take(Math.Max(1, needed))
            .Select(t => t.Id)
            .ToList();
    }

    /// <summary>Lo que se espera que ocupe una página por cargar: el promedio de las vivas, como mínimo MinPageMb.</summary>
    public static double EstimatePageMb(IReadOnlyList<TabSnapshot> tabs, MemoryReading memory) =>
        Math.Max(MinPageMb, AverageLiveTabMb(tabs, memory, null));

    private static double AverageLiveTabMb(IReadOnlyList<TabSnapshot> tabs, MemoryReading memory, IReadOnlySet<int>? skip)
    {
        var alive = tabs.Count(t => t.State != TabState.Discarded && !(skip?.Contains(t.Id) ?? false));
        return alive > 0 ? memory.PagesMb / alive : 0;
    }
}
