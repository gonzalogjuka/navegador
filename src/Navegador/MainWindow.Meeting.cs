using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Navegador.Core;

namespace Navegador;

/// <summary>
/// Modo reunión (fase 6.1): en la pestaña donde se activa, la página recibe un micrófono virtual (Meeting/virtual-mic.js)
/// que mezcla la voz original y la traducida que manda el traductor conectado al <see cref="Meeting.MeetingHub"/>.
/// Es la única pestaña con canal página→app (IsWebMessageEnabled): se habilita al activar y se apaga al desactivar,
/// y del canal sólo se aceptan los mensajes del script. Activar o desactivar recarga la página (el canal y el script
/// se aplican a partir del próximo documento): conviene activarlo antes de entrar a la reunión.
/// </summary>
public partial class MainWindow
{
    private const int MaxMicMessageChars = 64 * 1024;
    private static readonly string VirtualMicScript = LoadResource("virtual-mic.js");

    private static string LoadResource(string name)
    {
        using var s = typeof(MainWindow).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Falta el recurso {name}");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    private async Task SetMeetingAsync(BrowserTab tab, bool on)
    {
        if (tab.Core is not { } core || on == tab.InMeeting) return;
        if (on)
        {
            if (tab.InternalPage is not null) return; // nueva pestaña, historial: no hay reunión
            await EnterMeetingAsync(tab, core, auto: false);
        }
        else
        {
            LeaveMeeting(tab, core);
        }
        _log.Event(on ? "meeting_on" : "meeting_off", ("tab_id", tab.Id), ("host", BrowserTab.HostOf(tab.Url)));
        core.Reload();
        RefreshMeeting();
    }

    /// <summary>Pone la pestaña en modo reunión para el próximo documento (no recarga).</summary>
    private async Task EnterMeetingAsync(BrowserTab tab, CoreWebView2 core, bool auto)
    {
        core.Settings.IsWebMessageEnabled = true;
        tab.MeetingHandler = (_, e) => OnMeetingMessage(tab, e);
        core.WebMessageReceived += tab.MeetingHandler;
        tab.MeetingScriptId = await core.AddScriptToExecuteOnDocumentCreatedAsync(VirtualMicScript);
        tab.InMeeting = true;
        tab.MeetingAuto = auto;
        _app.Meetings.Join(this, tab);
        if (_env is not null) _ = _app.Voz.StartAsync(_env, this); // empieza a preparar la traducción ya
    }

    /// <summary>
    /// Modo reunión automático (fase 7.2): la pestaña va a un sitio de reuniones. Se cancela esa navegación, se prende
    /// el modo y se vuelve a navegar a la misma dirección: la página carga ya con el micrófono virtual, sin recargar.
    /// </summary>
    private async Task AutoMeetingThenNavigateAsync(BrowserTab tab, CoreWebView2 core, string uri)
    {
        await EnterMeetingAsync(tab, core, auto: true);
        _log.Event("meeting_auto", ("tab_id", tab.Id), ("host", BrowserTab.HostOf(uri)));
        core.Navigate(uri);
        RefreshMeeting();
    }

    /// <summary>Saca la pestaña del modo reunión sin recargar (también al cerrarla o al perder su WebView).</summary>
    private void LeaveMeeting(BrowserTab tab, CoreWebView2? core)
    {
        if (!tab.InMeeting) return;
        tab.InMeeting = false;
        tab.MeetingAuto = false;
        tab.MeetingMicLive = false;
        _app.Meetings.Leave(tab);
        if (_app.Meetings.SessionCount == 0) _app.Voz.StopSoon();
        if (core is null) return;
        try
        {
            if (tab.MeetingScriptId is { } id) core.RemoveScriptToExecuteOnDocumentCreated(id);
            if (tab.MeetingHandler is { } h) core.WebMessageReceived -= h;
            core.Settings.IsWebMessageEnabled = false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
        tab.MeetingScriptId = null;
        tab.MeetingHandler = null;
    }

    private void OnMeetingMessage(BrowserTab tab, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!tab.InMeeting) return;
        try
        {
            var json = e.WebMessageAsJson;
            if (json.Length > MaxMicMessageChars) return;
            using var doc = JsonDocument.Parse(json);
            var m = doc.RootElement;
            if (m.ValueKind != JsonValueKind.Object || !m.TryGetProperty("t", out var type)) return;
            switch (type.GetString())
            {
                case "mic":
                    if (m.GetProperty("rate").GetInt32() != 16000) return;
                    _app.Meetings.MicAudio(tab, Convert.FromBase64String(m.GetProperty("pcm").GetString() ?? ""));
                    break;
                case "ready":
                    SendMeetingMode(tab);
                    break;
                case "mic-started":
                    tab.MeetingMicLive = true;
                    _log.Event("meeting_mic", ("tab_id", tab.Id), ("live", true));
                    _app.Meetings.Notify();
                    break;
                case "mic-stopped":
                    tab.MeetingMicLive = false;
                    _log.Event("meeting_mic", ("tab_id", tab.Id), ("live", false), ("chunks", tab.MeetingMicChunks));
                    _app.Meetings.Notify();
                    break;
                case "state":
                    _app.Log.Event("meeting_state", ("tab_id", tab.Id), ("state", json), ("mic_chunks", tab.MeetingMicChunks),
                        ("play_chunks", tab.MeetingPlayChunks));
                    break;
                case "error":
                    _log.Event("meeting_error", ("tab_id", tab.Id), ("message", m.GetProperty("message").GetString()));
                    break;
            }
        }
        catch (Exception ex) when (ex is JsonException or FormatException or KeyNotFoundException or InvalidOperationException
                                       or ArgumentException)
        {
            // Mensaje mal formado (lo podría mandar la propia página): se ignora.
        }
    }

    private void SendMeetingMode(BrowserTab tab)
    {
        if (!tab.InMeeting || tab.Core is not { } core) return;
        var mix = MeetingMix.For(tab.MeetingVoice, _app.Meetings.TranslatorConnected);
        try
        {
            core.PostWebMessageAsJson(JsonSerializer.Serialize(new
            {
                t = "mode", original = mix.Original, translated = mix.Translated, capture = mix.Capture,
            }));
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
    }

    /// <summary>Voz traducida que llega del traductor: va al micrófono virtual de la pestaña.</summary>
    internal void PlayTranslated(BrowserTab tab, byte[] pcm, int sampleRate)
    {
        if (!tab.InMeeting || tab.Core is not { } core) return;
        tab.MeetingPlayChunks++;
        try
        {
            core.PostWebMessageAsJson(JsonSerializer.Serialize(new { t = "play", rate = sampleRate, pcm = Convert.ToBase64String(pcm) }));
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
    }

    private void OnMeetingsChanged()
    {
        foreach (var t in _tabs.Where(t => t.InMeeting)) SendMeetingMode(t);
        RefreshMeeting();
    }

    private void SetMeetingVoice(BrowserTab tab, MeetingVoice voice)
    {
        tab.MeetingVoice = voice;
        _log.Event("meeting_voice", ("tab_id", tab.Id), ("voice", voice.ToString()));
        _app.Meetings.Notify(); // refresca la mezcla de las pestañas y le avisa al traductor
    }

    // ---------- Interfaz ----------

    private static readonly Brush MeetingOnBrush = new SolidColorBrush(Color.FromRgb(0x8A, 0xB4, 0xF8));
    private static readonly Brush MeetingWarnBrush = Brushes.Orange;

    private void RefreshMeeting()
    {
        var tab = _active;
        var canMeet = tab is { InternalPage: null, Core: not null };
        MeetingButton.IsEnabled = canMeet || tab?.InMeeting == true;
        var inMeeting = tab?.InMeeting == true;
        // Sólo el color "activo" se pone a mano: si no, el estilo decide (incluido el gris de deshabilitado).
        if (inMeeting) MeetingButton.Foreground = MeetingOnBrush;
        else MeetingButton.ClearValue(ForegroundProperty);
        MeetingButton.ToolTip = inMeeting
            ? "Modo reunión activo: clic para elegir qué voz escuchan los demás o desactivarlo"
            : "Modo reunión: hablás en tu idioma y en la reunión se escucha la traducción. Activalo antes de entrar (recarga la página).";

        // Barra de estado: lo que pasa con la reunión de esta pestaña, o con la que haya en otra.
        var meeting = inMeeting ? tab : _tabs.FirstOrDefault(t => t.InMeeting);
        if (meeting is null)
        {
            MeetingText.Visibility = Visibility.Collapsed;
            return;
        }
        var mix = MeetingMix.For(meeting.MeetingVoice, _app.Meetings.TranslatorConnected);
        var where = meeting == tab ? "" : $" en «{meeting.Title}»";
        string text;
        if (!_app.Meetings.TranslatorConnected && _app.Voz.Status is { } preparing)
            text = $"● Reunión{where}: {preparing} · mientras, va tu voz original";
        else if (!_app.Meetings.TranslatorConnected)
            text = mix.TranslatorMissing
                ? $"● Reunión{where}: sin traductor conectado, va tu voz original"
                : $"● Reunión{where}: sin traductor, va tu voz original";
        else if (mix.Translated == 0)
            text = $"● Reunión{where}: va tu voz original ({_app.Meetings.Translator!.Name} conectado)";
        else
            text = $"● Reunión{where}: traduciendo con {_app.Meetings.Translator!.Name}";
        if (!meeting.MeetingMicLive) text += " · la página todavía no tomó el micrófono";
        MeetingText.Text = text;
        MeetingText.Foreground = mix.TranslatorMissing ? MeetingWarnBrush : MeetingOnBrush;
        MeetingText.Visibility = Visibility.Visible;
    }

    private async void Meeting_Click(object sender, RoutedEventArgs e)
    {
        if (_active is not { } tab) return;
        if (!tab.InMeeting)
        {
            await SetMeetingAsync(tab, true);
            return;
        }
        var menu = BuildMeetingMenu(tab);
        menu.PlacementTarget = MeetingButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private ContextMenu BuildMeetingMenu(BrowserTab tab)
    {
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = "Lo que escuchan los demás", IsEnabled = false });
        foreach (var (voice, label) in new[]
                 {
                     (MeetingVoice.Auto, "Automático: traducida si hay traductor"),
                     (MeetingVoice.Translated, "Sólo la traducida"),
                     (MeetingVoice.Both, "Traducida con tu voz de fondo"),
                     (MeetingVoice.Original, "Sólo tu voz original"),
                 })
        {
            var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = tab.MeetingVoice == voice };
            item.Click += (_, _) => SetMeetingVoice(tab, voice);
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var off = new MenuItem { Header = "Desactivar modo reunión (recarga la página)" };
        off.Click += async (_, _) => await SetMeetingAsync(tab, false);
        menu.Items.Add(off);
        return menu;
    }
}
