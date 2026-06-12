using EmuladorGameboy.Core;

namespace EmuladorGameboy.Graphics;

/// <summary>
/// PPU (Picture Processing Unit) — o "chip de vídeo" do Game Boy.
///
/// Desenha a tela LINHA POR LINHA (como uma TV CRT). Cada quadro tem 154
/// scanlines (0-143 visíveis, 144-153 = VBlank) e cada scanline dura 456 "dots"
/// (= T-cycles). A PPU passa por 4 modos dentro de cada linha:
///   modo 2 (OAM scan, 80) -> modo 3 (desenho, ~172) -> modo 0 (HBlank, ~204)
/// e, nas linhas 144-153, fica no modo 1 (VBlank), quando dispara a interrupção
/// mais importante: o jogo aproveita esse intervalo pra atualizar a VRAM.
///
/// A PPU é dona da VRAM (tiles + mapas) e da OAM (sprites). A CPU acessa esses
/// dados através da MMU, que encaminha pra cá.
/// </summary>
internal sealed class Ppu
{
    public const int Width = 160;
    public const int Height = 144;

    private readonly Mmu _mmu;

    private readonly byte[] _vram = new byte[0x2000];          // 0x8000-0x9FFF
    private readonly byte[] _oam = new byte[0xA0];             // 0xFE00-0xFE9F (40 sprites x 4 bytes)
    private readonly byte[] _frame = new byte[Width * Height]; // tela final: tom 0-3 por pixel
    private readonly byte[] _bgLine = new byte[Width];         // cor de fundo (0-3) da linha atual (p/ prioridade de sprite)

    /// <summary>A tela pronta (160x144), 1 byte por pixel = tom de cinza 0..3.</summary>
    public byte[] Frame => _frame;

    // --- Registradores da PPU (0xFF40-0xFF4B) ---
    private byte _lcdc;  // FF40 controle: liga LCD, camadas, endereços de tiles/mapas
    private byte _stat;  // FF41 status + habilita interrupções de STAT
    private byte _scy, _scx; // FF42/FF43 scroll do background
    private byte _ly;    // FF44 linha atual (0-153)
    private byte _lyc;   // FF45 valor de comparação com LY
    private byte _bgp;   // FF47 paleta do background
    private byte _obp0, _obp1; // FF48/FF49 paletas dos sprites
    private byte _wy, _wx; // FF4A/FF4B posição da janela

    private int _mode = 2;   // modo atual (0,1,2,3)
    private int _dots;       // dots acumulados na scanline atual
    private int _windowLine; // contador interno de linha da janela

    public Ppu(Mmu mmu) => _mmu = mmu;

    // ----- Acesso da MMU à VRAM e OAM -----
    public byte ReadVram(ushort addr) => _vram[addr - 0x8000];
    public void WriteVram(ushort addr, byte v) => _vram[addr - 0x8000] = v;
    public byte ReadOam(ushort addr) => _oam[addr - 0xFE00];
    public void WriteOam(ushort addr, byte v) => _oam[addr - 0xFE00] = v;

    // ----- Acesso da MMU aos registradores -----
    public byte ReadRegister(ushort addr) => addr switch
    {
        0xFF40 => _lcdc,
        // STAT: bit 7 sempre 1, bits 0-1 = modo, bit 2 = coincidência LY==LYC
        0xFF41 => (byte)(0x80 | _stat | (byte)_mode | (_ly == _lyc ? 0x04 : 0x00)),
        0xFF42 => _scy,
        0xFF43 => _scx,
        0xFF44 => _ly,
        0xFF45 => _lyc,
        0xFF47 => _bgp,
        0xFF48 => _obp0,
        0xFF49 => _obp1,
        0xFF4A => _wy,
        0xFF4B => _wx,
        _ => 0xFF,
    };

    public void WriteRegister(ushort addr, byte v)
    {
        switch (addr)
        {
            case 0xFF40:
                bool wasOn = (_lcdc & 0x80) != 0;
                _lcdc = v;
                // Desligar o LCD zera a contagem (a tela "apaga").
                if (wasOn && (_lcdc & 0x80) == 0)
                {
                    _ly = 0; _dots = 0; _mode = 0; _windowLine = 0;
                }
                break;
            case 0xFF41: _stat = (byte)(v & 0x78); break; // só os bits 3-6 (habilitações) são graváveis
            case 0xFF42: _scy = v; break;
            case 0xFF43: _scx = v; break;
            case 0xFF44: break;                           // LY é somente leitura
            case 0xFF45: _lyc = v; break;
            case 0xFF47: _bgp = v; break;
            case 0xFF48: _obp0 = v; break;
            case 0xFF49: _obp1 = v; break;
            case 0xFF4A: _wy = v; break;
            case 0xFF4B: _wx = v; break;
        }
    }

    /// <summary>Avança a PPU por 'cycles' T-cycles, movendo a máquina de estados.</summary>
    public void Step(int cycles)
    {
        if ((_lcdc & 0x80) == 0) return; // LCD desligado: PPU parada

        _dots += cycles;
        switch (_mode)
        {
            case 2: // OAM scan
                if (_dots >= 80) { _dots -= 80; _mode = 3; }
                break;

            case 3: // desenho da scanline
                if (_dots >= 172)
                {
                    _dots -= 172;
                    _mode = 0;
                    RenderScanline();
                    if ((_stat & 0x08) != 0) _mmu.RequestInterrupt(1); // STAT (modo 0/HBlank)
                }
                break;

            case 0: // HBlank
                if (_dots >= 204)
                {
                    _dots -= 204;
                    _ly++;
                    if (_ly == 144)
                    {
                        _mode = 1;
                        _mmu.RequestInterrupt(0);                       // VBlank!
                        if ((_stat & 0x10) != 0) _mmu.RequestInterrupt(1); // STAT (modo 1)
                    }
                    else
                    {
                        _mode = 2;
                        if ((_stat & 0x20) != 0) _mmu.RequestInterrupt(1); // STAT (modo 2)
                    }
                    CheckLyc();
                }
                break;

            case 1: // VBlank (linhas 144-153)
                if (_dots >= 456)
                {
                    _dots -= 456;
                    _ly++;
                    if (_ly > 153)
                    {
                        _ly = 0;
                        _windowLine = 0;
                        _mode = 2;
                        if ((_stat & 0x20) != 0) _mmu.RequestInterrupt(1);
                    }
                    CheckLyc();
                }
                break;
        }
    }

    private void CheckLyc()
    {
        // Interrupção STAT por coincidência LY == LYC (se habilitada no bit 6).
        if (_ly == _lyc && (_stat & 0x40) != 0) _mmu.RequestInterrupt(1);
    }

    // ===================== Renderização de uma scanline =====================
    private void RenderScanline()
    {
        int ly = _ly;

        bool bgEnable = (_lcdc & 0x01) != 0;
        bool winEnable = (_lcdc & 0x20) != 0 && _wy <= ly;
        bool signed = (_lcdc & 0x10) == 0;                 // área de tiles 0x8800 (índice com sinal)?
        int bgMap = (_lcdc & 0x08) != 0 ? 0x1C00 : 0x1800; // offset na VRAM (0x9C00/0x9800 - 0x8000)
        int winMap = (_lcdc & 0x40) != 0 ? 0x1C00 : 0x1800;
        bool windowDrawn = false;

        for (int x = 0; x < Width; x++)
        {
            int colorId;

            if (winEnable && x >= _wx - 7)
            {
                int winX = x - (_wx - 7);
                int tile = _vram[winMap + (_windowLine / 8) * 32 + (winX / 8)];
                colorId = TilePixel(TileVramIndex(tile, signed), _windowLine % 8, winX % 8);
                windowDrawn = true;
            }
            else if (bgEnable)
            {
                int bgX = (x + _scx) & 0xFF;
                int bgY = (ly + _scy) & 0xFF;
                int tile = _vram[bgMap + (bgY / 8) * 32 + (bgX / 8)];
                colorId = TilePixel(TileVramIndex(tile, signed), bgY % 8, bgX % 8);
            }
            else
            {
                colorId = 0; // BG desligado: cor 0
            }

            _bgLine[x] = (byte)colorId;
            _frame[ly * Width + x] = Shade(_bgp, colorId);
        }

        if (windowDrawn) _windowLine++;

        if ((_lcdc & 0x02) != 0)
            RenderSprites(ly);
    }

    // Converte um índice de tile no offset (dentro da VRAM) onde o tile começa.
    // Modo "unsigned": base 0x8000 -> offset 0. Modo "signed": base 0x9000 -> offset 0x1000.
    private static int TileVramIndex(int tile, bool signed)
        => signed ? 0x1000 + (sbyte)tile * 16 : tile * 16;

    // Lê o color id (0-3) de um pixel de tile. Os 2 bytes da linha são "bit planes".
    private int TilePixel(int tileVramIndex, int line, int xInTile)
    {
        byte lo = _vram[tileVramIndex + line * 2];
        byte hi = _vram[tileVramIndex + line * 2 + 1];
        int bit = 7 - xInTile; // o pixel da esquerda é o bit mais significativo
        return (((hi >> bit) & 1) << 1) | ((lo >> bit) & 1);
    }

    // Aplica uma paleta (BGP/OBP) a um color id, devolvendo o tom final (0-3).
    private static byte Shade(byte palette, int colorId) => (byte)((palette >> (colorId * 2)) & 3);

    private void RenderSprites(int ly)
    {
        int height = (_lcdc & 0x04) != 0 ? 16 : 8;

        // Coleta até 10 sprites que cruzam esta scanline (limite real do hardware).
        Span<int> visible = stackalloc int[10];
        int count = 0;
        for (int i = 0; i < 40 && count < 10; i++)
        {
            int spriteY = _oam[i * 4] - 16;
            if (ly >= spriteY && ly < spriteY + height)
                visible[count++] = i;
        }

        // Prioridade no DMG: menor X por cima; empate, menor índice de OAM por cima.
        // Ordenamos por X (e índice) e desenhamos do MENOR prioridade pro maior.
        for (int a = 0; a < count; a++)
            for (int b = a + 1; b < count; b++)
            {
                int xa = _oam[visible[a] * 4 + 1], xb = _oam[visible[b] * 4 + 1];
                if (xb < xa || (xb == xa && visible[b] < visible[a]))
                    (visible[a], visible[b]) = (visible[b], visible[a]);
            }

        for (int s = count - 1; s >= 0; s--)
        {
            int i = visible[s];
            int spriteY = _oam[i * 4] - 16;
            int spriteX = _oam[i * 4 + 1] - 8;
            int tile = _oam[i * 4 + 2];
            int attr = _oam[i * 4 + 3];

            bool flipY = (attr & 0x40) != 0;
            bool flipX = (attr & 0x20) != 0;
            bool behindBg = (attr & 0x80) != 0;
            byte palette = (attr & 0x10) != 0 ? _obp1 : _obp0;

            int row = ly - spriteY;
            if (flipY) row = height - 1 - row;
            if (height == 16) tile &= 0xFE; // sprite 8x16 usa par de tiles

            int tileVramIndex = tile * 16; // sprites sempre na área 0x8000 (unsigned)
            byte lo = _vram[tileVramIndex + row * 2];
            byte hi = _vram[tileVramIndex + row * 2 + 1];

            for (int px = 0; px < 8; px++)
            {
                int screenX = spriteX + px;
                if (screenX < 0 || screenX >= Width) continue;

                int bit = flipX ? px : 7 - px;
                int colorId = (((hi >> bit) & 1) << 1) | ((lo >> bit) & 1);
                if (colorId == 0) continue;                       // cor 0 = transparente
                if (behindBg && _bgLine[screenX] != 0) continue;  // atrás do fundo (exceto cor 0)

                _frame[ly * Width + screenX] = Shade(palette, colorId);
            }
        }
    }
}
