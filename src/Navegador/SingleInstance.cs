using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Navegador;

/// <summary>
/// Una sola instancia por carpeta de datos. Dos procesos escribirían la misma sesión y los mismos favoritos (gana el
/// último) y uno podría borrar el perfil privado del otro. El segundo arranque le pasa su pedido (dirección, --private)
/// al primero por un pipe con nombre que sólo puede abrir el mismo usuario, y termina sin crear ventanas ni log.
/// Cada --data-dir es otra instancia (las pruebas con carpetas temporales no chocan con el navegador abierto).
/// </summary>
public sealed class SingleInstance : IDisposable
{
    public sealed record Request(string? Url, bool Private);

    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stop = new();

    private SingleInstance(Mutex mutex, string pipeName)
    {
        _mutex = mutex;
        _pipeName = pipeName;
    }

    private static string KeyFor(string root) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(root).TrimEnd('\\').ToLowerInvariant())))[..16];

    /// <summary>Devuelve la instancia si este proceso es el primero; null si ya había otro (y ya le pasó el pedido).</summary>
    /// <exception cref="TimeoutException">La otra instancia existe pero no atiende el pipe.</exception>
    public static SingleInstance? TryAcquire(string root, Request request)
    {
        var key = KeyFor(root);
        var mutex = new Mutex(initiallyOwned: true, $@"Local\Navegador-{key}", out var created);
        if (created) return new SingleInstance(mutex, $"Navegador-{key}");
        mutex.Dispose();
        Forward($"Navegador-{key}", request);
        return null;
    }

    private static void Forward(string pipeName, Request request)
    {
        // Este proceso lo abrió el usuario, así que puede ceder el permiso de pasar una ventana al frente.
        NativeMethods.AllowSetForegroundWindow(NativeMethods.AsfwAny);
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
        client.Connect(5000);
        using var writer = new StreamWriter(client, new UTF8Encoding(false));
        writer.WriteLine(JsonSerializer.Serialize(request));
    }

    /// <summary>Atiende pedidos de otros arranques en segundo plano; <paramref name="onRequest"/> corre en ese hilo.</summary>
    public void Listen(Action<Request> onRequest)
    {
        _ = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(_stop.Token);
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var line = await reader.ReadLineAsync(_stop.Token);
                    if (line is not null && JsonSerializer.Deserialize<Request>(line) is { } request) onRequest(request);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
            }
        });
    }

    /// <summary>Llamar desde el hilo que creó la instancia (el mutex es del hilo que lo tomó).</summary>
    public void Dispose()
    {
        _stop.Cancel();
        try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
        _mutex.Dispose();
    }
}
