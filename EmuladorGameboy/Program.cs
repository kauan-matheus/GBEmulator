using System.Windows.Forms;

namespace EmuladorGameboy;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // Modo headless de depuração: "dotnet run -- --shot [rom] [frames]"
        // roda N quadros e salva um PNG da tela. Útil pra verificar o vídeo sem
        // precisar abrir a janela.
        if (args.Length >= 1 && args[0] == "--shot")
        {
            string rom = args.Length >= 2 ? args[1] : DefaultRomPath();
            int frames = args.Length >= 3 && int.TryParse(args[2], out var f) ? f : 600;
            Screenshot.Capture(rom, frames, "frame.png");
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }

    public static string DefaultRomPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        "Pokemon - Red Version (USA, Europe) (SGB Enhanced).gb");
}
