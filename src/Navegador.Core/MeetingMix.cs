namespace Navegador.Core;

/// <summary>Qué voz escuchan los demás en una reunión con modo reunión.</summary>
public enum MeetingVoice
{
    /// <summary>Traducida si hay un traductor conectado; si no, la original.</summary>
    Auto,
    Original,
    Translated,
    /// <summary>La traducida con la original bajita de fondo, como un doblaje.</summary>
    Both,
}

/// <summary>
/// Mezcla que arma el micrófono virtual: ganancia de la voz original, de la traducida y si la voz original se manda al
/// traductor. Regla: la reunión nunca queda muda. Sin traductor conectado va la voz original, aunque se haya pedido la
/// traducida (TranslatorMissing avisa en la interfaz).
/// </summary>
public readonly record struct MeetingMix(double Original, double Translated, bool Capture, bool TranslatorMissing)
{
    public const double BackgroundOriginal = 0.35;

    public static MeetingMix For(MeetingVoice voice, bool translatorConnected) => (voice, translatorConnected) switch
    {
        (MeetingVoice.Original, _) => new(1, 0, false, false),
        (MeetingVoice.Auto, false) => new(1, 0, false, false),
        (_, false) => new(1, 0, false, true),
        (MeetingVoice.Both, true) => new(BackgroundOriginal, 1, true, false),
        _ => new(0, 1, true, false), // Auto o Translated con traductor
    };
}
