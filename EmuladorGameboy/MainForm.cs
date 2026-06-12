using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using EmuladorGameboy.Input;

namespace EmuladorGameboy;

/// <summary>
/// Janela principal: tela do Game Boy (160x144 ampliada) + menu compacto estilo
/// Visual Studio (tema escuro). A emulação roda ~60x/s no timer.
///
/// Input por POLLING (GetAsyncKeyState) a cada quadro — no WinForms os eventos
/// KeyDown/KeyUp "perdem" setas e Enter por causa da navegação entre controles.
/// </summary>
internal sealed class MainForm : Form
{
    private const int PixelScale = 4;
    private const int Ppu_Width = 160;
    private const int Ppu_Height = 144;

    // Texto do manual (mostrado na tela sem ROM e no menu Ajuda > Controles).
    private const string ManualText =
        "GAME BOY EMULATOR\n\n" +
        "Setas . . . . . Movimento\n" +
        "Z . . . . . . . A\n" +
        "X . . . . . . . B\n" +
        "Enter . . . . . Start\n" +
        "Backspace . . . Select\n\n" +
        "Arquivo  >  Importar ROM   para começar";

    // --- Cores do tema (estilo VS dark) ---
    private static readonly Color BgDark   = Color.FromArgb(30, 30, 30);    // #1E1E1E
    private static readonly Color BtnDark  = Color.FromArgb(45, 45, 48);    // #2D2D30
    private static readonly Color BtnHover = Color.FromArgb(62, 62, 66);    // #3E3E42
    private static readonly Color FgLight  = Color.FromArgb(220, 220, 220); // #DCDCDC
    private static readonly Color Border   = Color.FromArgb(60, 60, 60);

    // --- Win32: input por polling, foco e barra de título escura ---
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private static bool IsDown(Keys key) => (GetAsyncKeyState((int)key) & 0x8000) != 0;

    private Emulator? _emulator;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly PictureBox _screen;
    private readonly Label _manual;

    private readonly Bitmap _bitmap = new(Ppu_Width, Ppu_Height, PixelFormat.Format32bppRgb);
    private readonly int[] _pixels = new int[Ppu_Width * Ppu_Height];

    private static readonly int[] Shades =
    {
        unchecked((int)0xFF9BBC0F),
        unchecked((int)0xFF8BAC0F),
        unchecked((int)0xFF306230),
        unchecked((int)0xFF0F380F),
    };

    public MainForm()
    {
        Text = "Game Boy Emulator";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        BackColor = BgDark;
        ForeColor = FgLight;
        Font = new Font("Segoe UI", 9F);
        ClientSize = new Size(Ppu_Width * PixelScale, Ppu_Height * PixelScale + 24);

        // Tela (preenche o espaço abaixo do menu).
        _screen = new PictureBox
        {
            Dock = DockStyle.Fill,
            SizeMode = PictureBoxSizeMode.StretchImage,
            BackColor = BgDark,
            Image = _bitmap,
        };
        Controls.Add(_screen);

        // Manual sobre a tela (some quando uma ROM é carregada).
        _manual = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Text = ManualText,
            ForeColor = FgLight,
            BackColor = BgDark,
            Font = new Font("Consolas", 11F),
        };
        _screen.Controls.Add(_manual);

        // Menu compacto estilo VS (colapsável).
        Controls.Add(BuildMenu());

        _timer = new System.Windows.Forms.Timer { Interval = 16 }; // ~60 FPS
        _timer.Tick += (_, _) => Tick();
    }

    private MenuStrip BuildMenu()
    {
        var menu = new MenuStrip
        {
            BackColor = BgDark,
            ForeColor = FgLight,
            Renderer = new ToolStripProfessionalRenderer(new DarkColorTable()) { RoundedEdges = false },
            Dock = DockStyle.Top,
        };

        var arquivo = new ToolStripMenuItem("Arquivo") { ForeColor = FgLight };
        arquivo.DropDownItems.Add(NewItem("Importar ROM…", (_, _) => ImportRom()));
        arquivo.DropDownItems.Add(new ToolStripSeparator());
        arquivo.DropDownItems.Add(NewItem("Sair", (_, _) => Close()));

        var ajuda = new ToolStripMenuItem("Ajuda") { ForeColor = FgLight };
        ajuda.DropDownItems.Add(NewItem("Controles…", (_, _) =>
            MessageBox.Show(this, ManualText, "Controles", MessageBoxButtons.OK, MessageBoxIcon.Information)));

        menu.Items.Add(arquivo);
        menu.Items.Add(ajuda);
        MainMenuStrip = menu;
        return menu;
    }

    private static ToolStripMenuItem NewItem(string text, EventHandler onClick)
    {
        var item = new ToolStripMenuItem(text, null, onClick)
        {
            ForeColor = FgLight,
            BackColor = BtnDark,
        };
        return item;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Barra de título escura (Windows 10 2004+/11). Atributo 20 = dark mode.
        try
        {
            int useDark = 1;
            DwmSetWindowAttribute(Handle, 20, ref useDark, sizeof(int));
        }
        catch { /* Windows antigo: ignora */ }
    }

    private void ImportRom()
    {
        using var ofd = new OpenFileDialog
        {
            Title = "Abrir ROM de Game Boy",
            Filter = "Game Boy ROMs (*.gb;*.gbc)|*.gb;*.gbc|Todos os arquivos (*.*)|*.*",
        };
        if (ofd.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            _timer.Stop();
            _emulator = new Emulator(ofd.FileName);
            _manual.Visible = false; // esconde o manual e mostra o jogo
            Text = $"Game Boy Emulator — {_emulator.Cartridge.Title}";
            _timer.Start();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Erro ao carregar ROM:\n{ex.Message}", "Erro",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void Tick()
    {
        if (_emulator is null) return;

        PollInput();

        byte[] frame;
        try { frame = _emulator.RunFrame(); }
        catch (NotImplementedException) { _timer.Stop(); return; }

        for (int i = 0; i < _pixels.Length; i++)
            _pixels[i] = Shades[frame[i] & 3];

        var data = _bitmap.LockBits(
            new Rectangle(0, 0, Ppu_Width, Ppu_Height),
            ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
        Marshal.Copy(_pixels, 0, data.Scan0, _pixels.Length);
        _bitmap.UnlockBits(data);

        _screen.Invalidate();
    }

    /// <summary>Lê o teclado uma vez por quadro e repassa pro joypad.</summary>
    private void PollInput()
    {
        if (_emulator is null) return;

        // Só captura teclas se ESTA janela for a ativa (não rouba teclas de outros apps).
        bool active = GetForegroundWindow() == Handle;
        var j = _emulator.Joypad;

        j.SetButton(GbButton.Right,  active && IsDown(Keys.Right));
        j.SetButton(GbButton.Left,   active && IsDown(Keys.Left));
        j.SetButton(GbButton.Up,     active && IsDown(Keys.Up));
        j.SetButton(GbButton.Down,   active && IsDown(Keys.Down));
        j.SetButton(GbButton.A,      active && IsDown(Keys.Z));
        j.SetButton(GbButton.B,      active && IsDown(Keys.X));
        j.SetButton(GbButton.Start,  active && IsDown(Keys.Enter));
        j.SetButton(GbButton.Select, active && IsDown(Keys.Back));
    }

    /// <summary>Paleta de cores escuras para o MenuStrip ficar no estilo VS.</summary>
    private sealed class DarkColorTable : ProfessionalColorTable
    {
        public override Color MenuStripGradientBegin => BgDark;
        public override Color MenuStripGradientEnd => BgDark;
        public override Color ToolStripDropDownBackground => BtnDark;
        public override Color ImageMarginGradientBegin => BtnDark;
        public override Color ImageMarginGradientMiddle => BtnDark;
        public override Color ImageMarginGradientEnd => BtnDark;
        public override Color MenuItemSelected => BtnHover;
        public override Color MenuItemSelectedGradientBegin => BtnHover;
        public override Color MenuItemSelectedGradientEnd => BtnHover;
        public override Color MenuItemBorder => BtnHover;
        public override Color MenuItemPressedGradientBegin => BtnDark;
        public override Color MenuItemPressedGradientEnd => BtnDark;
        public override Color MenuBorder => Border;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Border;
    }
}
