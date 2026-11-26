using Navegador.Core.Voz;

namespace Navegador.Tests;

public class VozProtocolTests
{
    [Fact]
    public void Mic_frame_round_trips_through_a_stream()
    {
        var pcm = new byte[] { 1, 2, 3, 4, 5, 6 };
        using var stream = new MemoryStream(VozProtocol.Mic(42, pcm));
        var (type, payload) = VozProtocol.Read(stream)!.Value;
        Assert.Equal(VozFrame.Mic, type);
        var (tab, audio) = VozProtocol.ParseMic(payload);
        Assert.Equal(42, tab);
        Assert.Equal(pcm, audio.ToArray());
        Assert.Null(VozProtocol.Read(stream)); // fin limpio entre dos mensajes
    }

    [Fact]
    public async Task Play_frame_round_trips_async_and_keeps_the_rate()
    {
        var pcm = new byte[] { 10, 20, 30, 40 };
        using var stream = new MemoryStream(VozProtocol.Play(7, 24000, pcm));
        var (type, payload) = (await VozProtocol.ReadAsync(stream))!.Value;
        Assert.Equal(VozFrame.Play, type);
        var (tab, rate, audio) = VozProtocol.ParsePlay(payload);
        Assert.Equal((7, 24000), (tab, rate));
        Assert.Equal(pcm, audio.ToArray());
    }

    [Fact]
    public void Several_frames_are_read_in_order()
    {
        using var stream = new MemoryStream();
        stream.Write(VozProtocol.Message(VozFrame.Status, new VozStatus("downloading", "reconocimiento de voz", 0.5)));
        stream.Write(VozProtocol.TabClosed(3));
        stream.Write(VozProtocol.Frame(VozFrame.Bye));
        stream.Position = 0;

        var first = VozProtocol.Read(stream)!.Value;
        Assert.Equal(VozFrame.Status, first.Type);
        var status = VozProtocol.Parse<VozStatus>(first.Payload);
        Assert.Equal(("downloading", "reconocimiento de voz", 0.5), (status.Phase, status.Detail, status.Progress));
        var second = VozProtocol.Read(stream)!.Value;
        Assert.Equal((VozFrame.TabClosed, 3), (second.Type, VozProtocol.TabOf(second.Payload)));
        Assert.Equal(VozFrame.Bye, VozProtocol.Read(stream)!.Value.Type);
        Assert.Null(VozProtocol.Read(stream));
    }

    [Fact]
    public void Odd_pcm_length_drops_the_last_byte()
    {
        var (_, payload) = VozProtocol.Read(new MemoryStream(VozProtocol.Mic(1, [1, 2, 3])))!.Value;
        Assert.Equal(2, VozProtocol.ParseMic(payload).Pcm.Length);
    }

    [Fact]
    public void A_cut_frame_is_an_error_not_a_clean_end()
    {
        var frame = VozProtocol.Mic(1, new byte[100]);
        Assert.Throws<EndOfStreamException>(() => VozProtocol.Read(new MemoryStream(frame, 0, 3)));
        Assert.Throws<EndOfStreamException>(() => VozProtocol.Read(new MemoryStream(frame, 0, 50)));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(VozProtocol.MaxPayload + 1)]
    public void A_frame_with_an_absurd_length_is_rejected(int length)
    {
        var header = new byte[VozProtocol.HeaderSize];
        header[0] = (byte)VozFrame.Play;
        BitConverter.TryWriteBytes(header.AsSpan(1), length);
        Assert.Throws<InvalidDataException>(() => VozProtocol.Read(new MemoryStream(header)));
    }

    [Fact]
    public void Play_with_an_invalid_rate_or_too_short_is_rejected()
    {
        var bad = VozProtocol.Read(new MemoryStream(VozProtocol.Play(1, 100, new byte[4])))!.Value.Payload;
        Assert.Throws<InvalidDataException>(() => VozProtocol.ParsePlay(bad));
        Assert.Throws<InvalidDataException>(() => VozProtocol.ParsePlay(new byte[6]));
        Assert.Throws<InvalidDataException>(() => VozProtocol.TabOf(new byte[2]));
    }
}

public class PhraseSegmenterTests
{
    private const int WindowMs = 32;

    /// <summary>Un segmentador con un detector de mentira: hay "voz" si la ventana no es silencio.</summary>
    private static PhraseSegmenter New(SegmenterOptions? options = null) => new(w => Math.Abs(w[0]) > 0.1f, options);

    private static byte[] Audio(int ms, bool voice)
    {
        var pcm = new byte[ms * 16 * 2];
        if (voice) for (var i = 0; i < pcm.Length; i += 2) { pcm[i] = 0x00; pcm[i + 1] = 0x40; } // 0,5 de amplitud
        return pcm;
    }

    private static List<Phrase> Run(PhraseSegmenter s, params (int Ms, bool Voice)[] parts) => Run(s, null, parts);

    /// <param name="tentatives">Si no es null, recibe los adelantos (null = adelanto anulado).</param>
    private static List<Phrase> Run(PhraseSegmenter s, List<Phrase?>? tentatives, params (int Ms, bool Voice)[] parts)
    {
        var phrases = new List<Phrase>();
        double now = 0;
        foreach (var (ms, voice) in parts)
        {
            // En pedazos de 21 ms, como los manda la pestaña en reunión.
            var audio = Audio(ms, voice);
            for (var at = 0; at < audio.Length; at += 672)
            {
                now += 21;
                s.Push(audio.AsSpan(at, Math.Min(672, audio.Length - at)), now, phrases.Add, tentatives is null ? null : tentatives.Add);
            }
        }
        return phrases;
    }

    [Fact]
    public void Speech_followed_by_a_pause_is_one_phrase()
    {
        var phrases = Run(New(), (320, false), (2000, true), (800, false));
        var p = Assert.Single(phrases);
        Assert.Equal("pausa", p.Reason);
        Assert.InRange(p.SpeechMs, 2000 - WindowMs, 2000 + WindowMs);
        // Lleva un poco del audio anterior (320 ms) y sólo 160 ms del silencio final, no los 650 de la pausa.
        var ms = p.Samples.Length / 16;
        Assert.InRange(ms, 2000 + 320 + 160 - 2 * WindowMs, 2000 + 320 + 160 + 2 * WindowMs);
    }

    [Fact]
    public void Short_pauses_do_not_cut_the_phrase()
    {
        var phrases = Run(New(), (1000, true), (300, false), (1000, true), (300, false), (1000, true), (800, false));
        Assert.Single(phrases);
    }

    [Fact]
    public void Two_phrases_separated_by_a_long_pause()
    {
        var phrases = Run(New(), (1500, true), (700, false), (1500, true), (700, false));
        Assert.Equal(2, phrases.Count);
        Assert.All(phrases, p => Assert.Equal("pausa", p.Reason));
        Assert.True(phrases[1].LastVoiceMs > phrases[0].LastVoiceMs);
    }

    [Fact]
    public void A_click_or_cough_is_not_a_phrase()
    {
        Assert.Empty(Run(New(), (500, false), (150, true), (1000, false)));
    }

    [Fact]
    public void Talking_without_pauses_is_cut_at_the_maximum()
    {
        var phrases = Run(New(), (25_000, true), (700, false));
        Assert.Equal(3, phrases.Count); // 10 s + 10 s + el resto
        Assert.Equal(["larga", "larga", "pausa"], phrases.Select(p => p.Reason));
        Assert.All(phrases.Take(2), p => Assert.InRange(p.Samples.Length / 16, 10_000 - WindowMs, 10_000 + WindowMs));
        // Nada se pierde ni se repite: entre las tres suman lo hablado (más el silencio final que se conserva).
        Assert.InRange(phrases.Sum(p => p.Samples.Length) / 16, 25_000, 25_000 + 160 + 2 * WindowMs);
    }

    [Fact]
    public void Flush_emits_the_phrase_in_progress()
    {
        var s = New();
        var phrases = Run(s, (1200, true));
        Assert.Empty(phrases);
        Assert.True(s.Active);
        s.Flush(phrases.Add);
        Assert.Equal("fin", Assert.Single(phrases).Reason);
        Assert.False(s.Active);
    }

    [Fact]
    public void Last_voice_time_is_when_the_last_voiced_chunk_arrived()
    {
        // 320 ms de silencio y 1 s de voz: en el reloj de la prueba (21 ms por pedazo, 16 + 48 pedazos) la voz termina
        // en 1344 ms. Después, silencio. El aviso llega con la ventana del detector: hasta una ventana y un pedazo más.
        var p = Assert.Single(Run(New(), (320, false), (1000, true), (900, false)));
        Assert.InRange(p.LastVoiceMs, 1344 - WindowMs, 1344 + WindowMs + 21);
    }

    [Fact]
    public void Options_change_the_pause_that_cuts()
    {
        var phrases = Run(New(new SegmenterOptions { PauseMs = 200 }), (1000, true), (300, false), (1000, true), (300, false));
        Assert.Equal(2, phrases.Count);
    }

    [Fact]
    public void A_short_phrase_waits_and_joins_what_comes_next()
    {
        // "Hola" (0,4 s), una pausa que ya cortaría una frase normal, y sigue hablando: sale una sola frase.
        var phrases = Run(New(), (400, true), (800, false), (1500, true), (800, false));
        var p = Assert.Single(phrases);
        Assert.InRange(p.SpeechMs, 1900 - 2 * WindowMs, 1900 + 2 * WindowMs);
    }

    [Fact]
    public void A_short_phrase_alone_still_goes_out_after_the_hold()
    {
        Assert.Empty(Run(New(), (400, true), (800, false))); // todavía esperando
        var alone = Assert.Single(Run(New(), (400, true), (1200, false)));
        Assert.Equal("pausa", alone.Reason);
    }

    [Fact]
    public void A_long_phrase_is_cut_at_a_short_pause()
    {
        // Una pausa de 400 ms no corta una frase normal, pero sí una que ya lleva más de 5 s.
        Assert.Single(Run(New(), (2000, true), (400, false), (1000, true), (800, false)));
        Assert.Equal(2, Run(New(), (5500, true), (400, false), (1000, true), (800, false)).Count);
    }

    [Fact]
    public void The_tentative_phrase_is_exactly_the_phrase_that_comes_out()
    {
        var tentatives = new List<Phrase?>();
        var p = Assert.Single(Run(New(), tentatives, (320, false), (2000, true), (800, false)));
        var t = Assert.Single(tentatives);
        Assert.NotNull(t);
        // Mismo audio muestra por muestra: lo traducido por adelantado vale para la frase definitiva.
        Assert.Equal(t.Samples, p.Samples);
        Assert.Equal(t.LastVoiceMs, p.LastVoiceMs);
    }

    [Fact]
    public void The_tentative_comes_long_before_the_pause_that_confirms()
    {
        var tentatives = new List<Phrase?>();
        // 250 ms de silencio: todavía no hay frase, pero el adelanto ya salió.
        Assert.Empty(Run(New(), tentatives, (2000, true), (250, false)));
        Assert.NotNull(Assert.Single(tentatives));
    }

    [Fact]
    public void Talking_again_withdraws_the_tentative()
    {
        var tentatives = new List<Phrase?>();
        var p = Assert.Single(Run(New(), tentatives, (1500, true), (300, false), (1500, true), (800, false)));
        Assert.Equal(3, tentatives.Count);
        Assert.NotNull(tentatives[0]);
        Assert.Null(tentatives[1]);             // siguió hablando: lo adelantado no vale
        Assert.Equal(tentatives[2]!.Samples, p.Samples);
        Assert.True(tentatives[2]!.Samples.Length > tentatives[0]!.Samples.Length);
    }

    [Fact]
    public void Noise_gets_no_tentative()
    {
        var tentatives = new List<Phrase?>();
        Run(New(), tentatives, (500, false), (150, true), (1000, false));
        Assert.Empty(tentatives);
    }
}

public class PhraseTextTests
{
    [Theory]
    [InlineData(" Hello, how are you? ", "Hello, how are you?")]
    [InlineData("Thank you.", "Thank you.")]                       // "gracias" se dice en serio en una reunión
    [InlineData("[BLANK_AUDIO]", "")]
    [InlineData("(music)", "")]
    [InlineData(" [Music] Good morning. ", "Good morning.")]
    [InlineData("*coughs* Sorry.", "Sorry.")]
    [InlineData("Thanks for watching!", "")]
    [InlineData("Subscribe!", "")]
    [InlineData("Please like and subscribe to the channel.", "")]
    [InlineData("¡Suscríbete!", "")]
    [InlineData("you", "")]
    [InlineData("...", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Clean_keeps_speech_and_drops_noise(string? input, string expected) =>
        Assert.Equal(expected, PhraseText.Clean(input));

    [Fact]
    public void A_loop_is_said_once()
    {
        Assert.Equal("I'm going to say this.",
            PhraseText.Clean("I'm going to say I'm going to say I'm going to say I'm going to say this."));
        Assert.Equal("no", PhraseText.Clean("no no no no no"));
    }

    [Fact]
    public void A_long_phrase_said_twice_and_cut_is_said_once()
    {
        // Lo que devolvió Whisper en la prueba de punta a punta (cortado por el tope de largo en la tercera vuelta).
        Assert.Equal("Atmolysis, del griego, atmis, vapor, and lean.", PhraseText.Clean(
            "Atmolysis, del griego, atmis, vapor, and lean. Atmolysis, del griego, atmis, vapor, and lean. Atmolysis, del griego, atmis, vapor,"));
        Assert.Equal("of the gases in the air", PhraseText.Clean("of the gases in the air of the gases in the air"));
        // Lo que sigue a la repetición y no es otra vuelta se conserva.
        Assert.Equal("I think so and we continue", PhraseText.Clean("I think so I think so and we continue"));
    }

    [Fact]
    public void Normal_repetition_is_kept()
    {
        Assert.Equal("very very good", PhraseText.Clean("very very good"));
        Assert.Equal("We go to the park and we go to the beach.", PhraseText.Clean("We go to the park and we go to the beach."));
    }

    [Fact]
    public void A_whole_sentence_said_twice_is_said_once()
    {
        const string once = "I would like to know what you think of the budget, because I think we are getting short.";
        Assert.Equal(once, PhraseText.Clean(once + " " + once));
    }

    [Fact]
    public void Sentences_split_long_text_and_keep_short_pieces_together()
    {
        Assert.Equal(["Hello everyone, thank you for joining the meeting.", "Today I want to show you the progress of the project."],
            PhraseText.Sentences("Hello everyone, thank you for joining the meeting. Today I want to show you the progress of the project."));
        Assert.Equal(["Ok. Let's go."], PhraseText.Sentences("Ok. Let's go."));
        Assert.Equal(["Dr. Smith said the release is ready for production. Great!"],
            PhraseText.Sentences("Dr. Smith said the release is ready for production. Great!"));
        Assert.Equal(["What worries me the most is the cost of the virtual machines,", "which last month got out of hand, so I set up a board."],
            PhraseText.Sentences("What worries me the most is the cost of the virtual machines, which last month got out of hand, so I set up a board."));
        Assert.Empty(PhraseText.Sentences(""));
    }
}
