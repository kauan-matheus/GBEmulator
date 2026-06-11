using EmuladorGameboy.Cartridges;

namespace EmuladorGameboy.Core;

/// <summary>
/// MMU (Memory Management Unit) / barramento de memória.
///
/// A CPU só sabe fazer duas coisas com o mundo externo: ler e escrever um byte
/// num endereço de 16 bits. É a MMU que decide QUEM responde por cada faixa de
/// endereços — ROM, VRAM, RAM de trabalho, OAM, registradores de I/O, HRAM...
/// É literalmente o "switch de endereços" do diagrama de blocos.
///
/// Por isso a classe Memory.cs do esqueleto fica obsoleta: o papel dela (guardar
/// os arrays crus de RAM) foi absorvido aqui dentro da MMU.
/// </summary>
internal sealed class Mmu
{
    private readonly Cartridge _cartridge;

    // Os "chips de memória" do console, cada um um array de bytes:
    private readonly byte[] _vram = new byte[0x2000]; // 0x8000-0x9FFF  vídeo
    private readonly byte[] _eram = new byte[0x2000]; // 0xA000-0xBFFF  RAM do cartucho (MBC depois)
    private readonly byte[] _wram = new byte[0x2000]; // 0xC000-0xDFFF  RAM de trabalho
    private readonly byte[] _oam  = new byte[0x00A0]; // 0xFE00-0xFE9F  atributos de sprites
    private readonly byte[] _io   = new byte[0x0080]; // 0xFF00-0xFF7F  registradores de I/O
    private readonly byte[] _hram = new byte[0x007F]; // 0xFF80-0xFFFE  RAM rápida (alta)
    private byte _ie;                                 // 0xFFFF         interrupt enable

    public Mmu(Cartridge cartridge) => _cartridge = cartridge;

    /// <summary>Lê um byte de qualquer endereço — roteando pro dono certo.</summary>
    public byte ReadByte(ushort addr) => addr switch
    {
        <= 0x7FFF => _cartridge.ReadRom(addr),     // ROM do cartucho
        <= 0x9FFF => _vram[addr - 0x8000],
        <= 0xBFFF => _eram[addr - 0xA000],
        <= 0xDFFF => _wram[addr - 0xC000],
        <= 0xFDFF => _wram[addr - 0xE000],         // Echo RAM: espelha 0xC000-0xDDFF
        <= 0xFE9F => _oam[addr - 0xFE00],
        <= 0xFEFF => 0xFF,                         // região proibida pelo hardware
        <= 0xFF7F => _io[addr - 0xFF00],
        <= 0xFFFE => _hram[addr - 0xFF80],
        _         => _ie,                          // 0xFFFF
    };

    /// <summary>Escreve um byte em qualquer endereço.</summary>
    public void WriteByte(ushort addr, byte value)
    {
        switch (addr)
        {
            // Escrever na faixa de ROM NÃO grava nada: no hardware real, isso é
            // interpretado pelo chip MBC como um comando de banking (Fase 9).
            case <= 0x7FFF: break;
            case <= 0x9FFF: _vram[addr - 0x8000] = value; break;
            case <= 0xBFFF: _eram[addr - 0xA000] = value; break;
            case <= 0xDFFF: _wram[addr - 0xC000] = value; break;
            case <= 0xFDFF: _wram[addr - 0xE000] = value; break; // Echo RAM
            case <= 0xFE9F: _oam[addr - 0xFE00] = value; break;
            case <= 0xFEFF: break;                                // proibida
            case <= 0xFF7F: _io[addr - 0xFF00] = value; break;
            case <= 0xFFFE: _hram[addr - 0xFF80] = value; break;
            default: _ie = value; break;                          // 0xFFFF
        }
    }

    // --- Acessos de 16 bits ---
    // LEMBRE: o Game Boy é LITTLE-ENDIAN. O byte BAIXO mora no endereço menor.
    public ushort ReadWord(ushort addr)
        => (ushort)(ReadByte(addr) | (ReadByte((ushort)(addr + 1)) << 8));

    public void WriteWord(ushort addr, ushort value)
    {
        WriteByte(addr, (byte)value);                 // byte baixo primeiro
        WriteByte((ushort)(addr + 1), (byte)(value >> 8)); // depois o alto
    }
}
