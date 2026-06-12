using System.Drawing;
using System.Drawing.Imaging;

namespace EmuladorGameboy;

/// <summary>
/// Roda o emulador "sem janela" por alguns quadros e salva a tela como PNG.
/// Serve de ferramenta de depuração do vídeo.
/// </summary>
static class Screenshot
{
    // Paleta clássica esverdeada do Game Boy (tom 0 = claro ... 3 = escuro).
    private static readonly Color[] Palette =
    {
        Color.FromArgb(0x9B, 0xBC, 0x0F),
        Color.FromArgb(0x8B, 0xAC, 0x0F),
        Color.FromArgb(0x30, 0x62, 0x30),
        Color.FromArgb(0x0F, 0x38, 0x0F),
    };

    public static void Capture(string romPath, int frames, string outPath)
    {
        var emu = new Emulator(romPath);

        byte[] frame = emu.Ppu.Frame;
        try
        {
            for (int i = 0; i < frames; i++)
                frame = emu.RunFrame();
        }
        catch (NotImplementedException)
        {
            // Topou num opcode ilegal: salva o que houver na tela até aqui.
        }

        using var bmp = new Bitmap(160, 144);
        for (int y = 0; y < 144; y++)
            for (int x = 0; x < 160; x++)
                bmp.SetPixel(x, y, Palette[frame[y * 160 + x] & 3]);

        bmp.Save(outPath, ImageFormat.Png);
    }
}
