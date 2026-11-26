using System.Buffers.Binary;
using System.Text.Json;

namespace Navegador.Core.Voz;

/// <summary>Tipos de mensaje entre Astro y su traductor (astro-voz.exe).</summary>
public enum VozFrame : byte
{
    // Astro → traductor
    /// <summary>Voz original: int32 pestaña + PCM 16 kHz mono de 16 bits.</summary>
    Mic = 1,
    /// <summary>La pestaña dejó la reunión o soltó el micrófono: int32 pestaña.</summary>
    TabClosed = 2,
    /// <summary>Terminar.</summary>
    Bye = 3,
    /// <summary>Lo que se diga en esta pestaña no se aprende (ventana privada): int32 pestaña.</summary>
    NoLearn = 4,

    // Traductor → Astro
    /// <summary>JSON <see cref="VozStatus"/>: qué está preparando.</summary>
    Status = 16,
    /// <summary>JSON <see cref="VozReady"/>: listo para traducir.</summary>
    Ready = 17,
    /// <summary>Voz traducida: int32 pestaña + int32 Hz + PCM mono de 16 bits.</summary>
    Play = 18,
    /// <summary>JSON <see cref="VozPhrase"/>: una frase detectada y sus tiempos (sin el texto).</summary>
    Phrase = 19,
    /// <summary>JSON <see cref="VozError"/>.</summary>
    Error = 20,
    /// <summary>JSON <see cref="VozStats"/>: cada pocos segundos mientras llega voz, para ver que todo el camino anda.</summary>
    Stats = 21,
}

/// <summary>Contadores del traductor desde que arrancó.</summary>
/// <param name="MicFrames">Pedazos de voz recibidos de Astro.</param>
/// <param name="Windows">Ventanas de 32 ms pasadas por el detector de voz.</param>
/// <param name="Voiced">Ventanas en las que el detector oyó voz.</param>
/// <param name="Phrases">Frases cortadas.</param>
/// <param name="Done">Frases terminadas (traducidas, con o sin algo para decir).</param>
public sealed record VozStats(long MicFrames, long Windows, long Voiced, int Phrases, int Done);

/// <param name="Phase">"downloading" (con Progress 0..1) o "loading".</param>
public sealed record VozStatus(string Phase, string Detail, double Progress = 0);

/// <param name="HighPriority">El proceso quedó con prioridad alta y sin ahorro de energía.</param>
/// <param name="Mmcss">Los hilos de audio quedaron anotados en el planificador multimedia de Windows.</param>
/// <param name="GlossaryTerms">Términos del glosario y aprendidos que van de pista al reconocedor.</param>
public sealed record VozReady(string Asr, string? Voice, int Threads, int LoadMs, bool HighPriority = false, bool Mmcss = false,
    int GlossaryTerms = 0);

/// <param name="Spoken">false: la frase no tenía nada para decir (ruido, silencio) y no se mandó voz.</param>
/// <param name="TtsMs">Lo que tardó en estar lista la voz de la primera oración.</param>
/// <param name="TotalMs">De tu última palabra a que sale la voz traducida (la primera oración).</param>
/// <param name="Merged">Frases atrasadas que se juntaron en una (1 = ninguna).</param>
/// <param name="Pending">Frases esperando turno cuando salió esta.</param>
/// <param name="Chars">Largo del texto en inglés (el texto no viaja ni se guarda).</param>
/// <param name="Speculated">Se empezó a traducir antes de confirmar que la frase había terminado (adelanto).</param>
public sealed record VozPhrase(int Tab, bool Spoken, string Reason, int AudioMs, int AsrMs, int TtsMs, int TotalMs, int Merged, int Pending,
    int Chars = 0, bool Speculated = false);

/// <param name="Fatal">true: el traductor no puede seguir (la reunión lleva la voz original).</param>
public sealed record VozError(string Message, bool Fatal);

/// <summary>
/// Mensajes del named pipe entre Astro y astro-voz.exe: 1 byte de tipo, 4 bytes de largo (little-endian) y el contenido.
/// El audio va en binario (PCM); los estados, en JSON corto.
/// </summary>
public static class VozProtocol
{
    public const int HeaderSize = 5;
    /// <summary>Tope de un mensaje: una frase de 30 s a 48 kHz ocupa 2,9 MB.</summary>
    public const int MaxPayload = 8 * 1024 * 1024;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static byte[] Frame(VozFrame type, ReadOnlySpan<byte> payload = default)
    {
        if (payload.Length > MaxPayload) throw new ArgumentException("mensaje demasiado grande", nameof(payload));
        var frame = new byte[HeaderSize + payload.Length];
        frame[0] = (byte)type;
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(1), payload.Length);
        payload.CopyTo(frame.AsSpan(HeaderSize));
        return frame;
    }

    public static byte[] Mic(int tab, ReadOnlySpan<byte> pcm16k)
    {
        var frame = NewFrame(VozFrame.Mic, 4 + pcm16k.Length);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(HeaderSize), tab);
        pcm16k.CopyTo(frame.AsSpan(HeaderSize + 4));
        return frame;
    }

    public static byte[] TabClosed(int tab) => TabFrame(VozFrame.TabClosed, tab);

    public static byte[] NoLearn(int tab) => TabFrame(VozFrame.NoLearn, tab);

    private static byte[] TabFrame(VozFrame type, int tab)
    {
        var frame = NewFrame(type, 4);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(HeaderSize), tab);
        return frame;
    }

    public static byte[] Play(int tab, int rate, ReadOnlySpan<byte> pcm)
    {
        var frame = NewFrame(VozFrame.Play, 8 + pcm.Length);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(HeaderSize), tab);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(HeaderSize + 4), rate);
        pcm.CopyTo(frame.AsSpan(HeaderSize + 8));
        return frame;
    }

    public static byte[] Message<T>(VozFrame type, T value) => Frame(type, JsonSerializer.SerializeToUtf8Bytes(value, Json));

    public static T Parse<T>(ReadOnlySpan<byte> payload) =>
        JsonSerializer.Deserialize<T>(payload, Json) ?? throw new InvalidDataException("mensaje vacío");

    public static int TabOf(ReadOnlySpan<byte> payload) =>
        payload.Length >= 4 ? BinaryPrimitives.ReadInt32LittleEndian(payload) : throw new InvalidDataException("falta la pestaña");

    /// <summary>Contenido de <see cref="VozFrame.Mic"/>: pestaña y PCM (largo par).</summary>
    public static (int Tab, ReadOnlyMemory<byte> Pcm) ParseMic(byte[] payload)
    {
        var tab = TabOf(payload);
        return (tab, payload.AsMemory(4, (payload.Length - 4) & ~1));
    }

    /// <summary>Contenido de <see cref="VozFrame.Play"/>: pestaña, Hz y PCM (largo par).</summary>
    public static (int Tab, int Rate, ReadOnlyMemory<byte> Pcm) ParsePlay(byte[] payload)
    {
        if (payload.Length < 8) throw new InvalidDataException("mensaje de voz incompleto");
        var rate = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(4));
        if (rate is < 8000 or > 96000) throw new InvalidDataException("frecuencia inválida");
        return (TabOf(payload), rate, payload.AsMemory(8, (payload.Length - 8) & ~1));
    }

    /// <summary>Lee un mensaje entero. null si el otro lado cerró el pipe entre dos mensajes.</summary>
    public static (VozFrame Type, byte[] Payload)? Read(Stream stream)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        var got = stream.ReadAtLeast(header, HeaderSize, throwOnEndOfStream: false);
        if (got == 0) return null;
        if (got < HeaderSize) throw new EndOfStreamException("mensaje cortado");
        var payload = new byte[PayloadLength(header)];
        stream.ReadExactly(payload);
        return ((VozFrame)header[0], payload);
    }

    /// <inheritdoc cref="Read"/>
    public static async ValueTask<(VozFrame Type, byte[] Payload)?> ReadAsync(Stream stream, CancellationToken ct = default)
    {
        var header = new byte[HeaderSize];
        var got = await stream.ReadAtLeastAsync(header, HeaderSize, throwOnEndOfStream: false, ct).ConfigureAwait(false);
        if (got == 0) return null;
        if (got < HeaderSize) throw new EndOfStreamException("mensaje cortado");
        var payload = new byte[PayloadLength(header)];
        await stream.ReadExactlyAsync(payload, ct).ConfigureAwait(false);
        return ((VozFrame)header[0], payload);
    }

    private static int PayloadLength(ReadOnlySpan<byte> header)
    {
        var length = BinaryPrimitives.ReadInt32LittleEndian(header[1..]);
        if (length is < 0 or > MaxPayload) throw new InvalidDataException($"largo de mensaje inválido: {length}");
        return length;
    }

    private static byte[] NewFrame(VozFrame type, int payloadLength)
    {
        if (payloadLength > MaxPayload) throw new ArgumentException("mensaje demasiado grande");
        var frame = new byte[HeaderSize + payloadLength];
        frame[0] = (byte)type;
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(1), payloadLength);
        return frame;
    }
}
