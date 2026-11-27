using Astro.Voz;

// astro-voz.exe: el traductor de voz de Astro (español → inglés), todo local.
//   astro-voz.exe --pipe NOMBRE --models CARPETA [--data CARPETA] [--parent PID] [opciones]
//       Lo lanza Astro al empezar una reunión: recibe tu voz por el named pipe y devuelve la voz en inglés.
//   astro-voz.exe --file AUDIO.wav --models CARPETA [--out RESULTADO.json] [--data CARPETA] [--remote OTROS.wav] [opciones]
//       Prueba sin navegador: pasa un WAV a ritmo de reunión y deja las frases y los tiempos en JSON.
//       --remote: otro WAV, en inglés, que suena a la vez y hace de "los demás".
//   --data         carpeta con el glosario (voz-glosario.txt), lo aprendido (voz-aprendido.json), el libro de frases
//                  (voz-expresiones.txt) y los apuntes (voz-reuniones\). Sin ella no hay nada de eso.
//   --threads N    hilos del reconocedor y del intérprete.
//   --voice        natural (voz neuronal, por defecto; si no se puede, la de Windows) o windows.
//   --learn        off: no anota los términos que se usan (el glosario escrito a mano vale igual).
//   --interpreter  off: sin modelo de lenguaje; Whisper traduce directo (más rápido, ~1,1 GB menos, más literal).
//   --notes        off: no guarda las expresiones de la reunión.
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
    internal sealed record Options(string? Pipe, string? File, string? Out, string Models, string? Data, int Parent, int Threads,
        bool NaturalVoice, bool Learn, bool Interpreter, bool Notes, string? Remote)
    {
        public static Options? Parse(string[] args)
        {
            string? pipe = null, file = null, output = null, models = null, data = null, remote = null;
            var parent = 0;
            bool natural = true, learn = true, interpreter = true, notes = true;
            // Todos los hilos de la PC menos uno (que queda para el video de la reunión), entre 2 y 6. Medido: en un i5
            // de 12 hilos (2 núcleos rápidos + 8 lentos) 6 da lo mejor y más no mejora; en uno de 6 núcleos sin hilos
            // dobles, 3 hilos (la regla anterior: la mitad) tardaba casi el doble que 5 (729 ms por frase con small).
            var threads = Math.Clamp(Environment.ProcessorCount - 1, 2, 6);
            for (var i = 0; i + 1 < args.Length; i += 2)
            {
                var value = args[i + 1];
                switch (args[i])
                {
                    case "--pipe": pipe = value; break;
                    case "--file": file = value; break;
                    case "--out": output = value; break;
                    case "--models": models = value; break;
                    case "--data": data = value; break;
                    case "--remote": remote = value; break;
                    case "--parent": int.TryParse(value, out parent); break;
                    case "--threads": if (int.TryParse(value, out var t) && t is >= 1 and <= 32) threads = t; break;
                    case "--voice": natural = value != "windows"; break;
                    case "--learn": learn = value != "off"; break;
                    case "--interpreter": interpreter = value != "off"; break;
                    case "--notes": notes = value != "off"; break;
                    default: return null;
                }
            }
            if (models is null || (pipe is null) == (file is null)) return null;
            return new Options(pipe, file, output, models, data, parent, threads, natural, learn, interpreter, notes, remote);
        }
    }
}
