using Astro.Voz;

// astro-voz.exe: el traductor de voz de Astro (español → inglés), todo local.
//   astro-voz.exe --pipe NOMBRE --models CARPETA [--parent PID] [--threads N]
//       Lo lanza Astro al empezar una reunión: recibe tu voz por el named pipe y devuelve la voz en inglés.
//   astro-voz.exe --file AUDIO.wav --models CARPETA [--out RESULTADO.json] [--threads N]
//       Prueba sin navegador: traduce un WAV entero y deja las frases y los tiempos en JSON.
var options = Options.Parse(args);
if (options is null) return 2;
try
{
    return options.File is not null ? FileTest.Run(options) : Service.Run(options);
}
catch (Exception ex)
{
    // Sin consola ni pipe no hay a quién avisarle: el código de salida es lo que ve Astro (o la prueba).
    if (options.Out is not null) File.WriteAllText(options.Out, "{\"error\":" + System.Text.Json.JsonSerializer.Serialize(ex.ToString()) + "}");
    return 1;
}

namespace Astro.Voz
{
    internal sealed record Options(string? Pipe, string? File, string? Out, string Models, int Parent, int Threads)
    {
        public static Options? Parse(string[] args)
        {
            string? pipe = null, file = null, output = null, models = null;
            var parent = 0;
            // La mitad de los hilos de la PC, entre 2 y 6: medido en un i5 de 12 hilos, 6 da ~250 ms por frase y más
            // hilos no mejoran (los núcleos de bajo consumo frenan al resto).
            var threads = Math.Clamp(Environment.ProcessorCount / 2, 2, 6);
            for (var i = 0; i + 1 < args.Length; i += 2)
            {
                var value = args[i + 1];
                switch (args[i])
                {
                    case "--pipe": pipe = value; break;
                    case "--file": file = value; break;
                    case "--out": output = value; break;
                    case "--models": models = value; break;
                    case "--parent": int.TryParse(value, out parent); break;
                    case "--threads": if (int.TryParse(value, out var t) && t is >= 1 and <= 32) threads = t; break;
                    default: return null;
                }
            }
            if (models is null || (pipe is null) == (file is null)) return null;
            return new Options(pipe, file, output, models, parent, threads);
        }
    }
}
