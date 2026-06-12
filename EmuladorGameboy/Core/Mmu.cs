using EmuladorGameboy.Cartridges;
using EmuladorGameboy.Graphics;
using EmuladorGameboy.Input;

namespace EmuladorGameboy.Core;

/// <summary>
/// MMU / barramento de memória. A CPU só lê/escreve bytes; a MMU decide QUEM
/// responde por cada endereço e encaminha pro componente certo:
///   ROM/RAM externa -> Cartridge (com banking pelo MBC)
///   VRAM/OAM/regs de vídeo -> PPU
///   timer -> GbTimer        joypad -> Joypad
/// e guarda diretamente a WRAM, a HRAM e os registradores de I/O restantes.
/// </summary>
internal sealed class Mmu
{
    private readonly Cartridge _cartridge;
    private Ppu _ppu = null!;
    private GbTimer _timer = null!;
    private Joypad _joypad = null!;

    private readonly byte[] _wram = new byte[0x2000]; // 0xC000-0xDFFF
    private readonly byte[] _hram = new byte[0x007F]; // 0xFF80-0xFFFE
    private readonly byte[] _io = new byte[0x0080];   // 0xFF00-0xFF7F (som, serial, IF...)
    private byte _ie;                                 // 0xFFFF

    public Mmu(Cartridge cartridge) => _cartridge = cartridge;

    /// <summary>Liga os componentes que a MMU precisa rotear (evita dependência circular no construtor).</summary>
    public void Attach(Ppu ppu, GbTimer timer, Joypad joypad)
    {
        _ppu = ppu;
        _timer = timer;
        _joypad = joypad;
    }

    /// <summary>Liga um bit em IF (0xFF0F): é assim que PPU/Timer/Joypad "avisam" a CPU.</summary>
    public void RequestInterrupt(int bit) => _io[0x0F] |= (byte)(1 << bit);

    public byte ReadByte(ushort addr)
    {
        switch (addr)
        {
            case <= 0x7FFF: return _cartridge.ReadRom(addr);
            case <= 0x9FFF: return _ppu.ReadVram(addr);
            case <= 0xBFFF: return _cartridge.ReadRam(addr);
            case <= 0xDFFF: return _wram[addr - 0xC000];
            case <= 0xFDFF: return _wram[addr - 0xE000];          // Echo RAM
            case <= 0xFE9F: return _ppu.ReadOam(addr);
            case <= 0xFEFF: return 0xFF;                          // proibida
            case 0xFF00: return _joypad.Read();
            case >= 0xFF04 and <= 0xFF07: return _timer.ReadRegister(addr);
            case >= 0xFF40 and <= 0xFF4B: return _ppu.ReadRegister(addr);
            case <= 0xFF7F: return _io[addr - 0xFF00];
            case <= 0xFFFE: return _hram[addr - 0xFF80];
            default: return _ie;                                  // 0xFFFF
        }
    }

    public void WriteByte(ushort addr, byte value)
    {
        switch (addr)
        {
            case <= 0x7FFF: _cartridge.WriteRom(addr, value); break; // comandos do MBC
            case <= 0x9FFF: _ppu.WriteVram(addr, value); break;
            case <= 0xBFFF: _cartridge.WriteRam(addr, value); break;
            case <= 0xDFFF: _wram[addr - 0xC000] = value; break;
            case <= 0xFDFF: _wram[addr - 0xE000] = value; break;
            case <= 0xFE9F: _ppu.WriteOam(addr, value); break;
            case <= 0xFEFF: break;
            case 0xFF00: _joypad.Write(value); break;
            case >= 0xFF04 and <= 0xFF07: _timer.WriteRegister(addr, value); break;
            case 0xFF46: DoOamDma(value); break;                     // OAM DMA
            case >= 0xFF40 and <= 0xFF4B: _ppu.WriteRegister(addr, value); break;
            case <= 0xFF7F: _io[addr - 0xFF00] = value; break;
            case <= 0xFFFE: _hram[addr - 0xFF80] = value; break;
            default: _ie = value; break;
        }
    }

    /// <summary>
    /// OAM DMA: escrever em 0xFF46 copia 160 bytes de (value*0x100) para a OAM.
    /// Os jogos usam isso TODO quadro pra atualizar os sprites de uma vez.
    /// </summary>
    private void DoOamDma(byte value)
    {
        ushort src = (ushort)(value << 8);
        for (int i = 0; i < 0xA0; i++)
            _ppu.WriteOam((ushort)(0xFE00 + i), ReadByte((ushort)(src + i)));
    }

    // 16 bits, sempre little-endian (byte baixo primeiro).
    public ushort ReadWord(ushort addr)
        => (ushort)(ReadByte(addr) | (ReadByte((ushort)(addr + 1)) << 8));

    public void WriteWord(ushort addr, ushort value)
    {
        WriteByte(addr, (byte)value);
        WriteByte((ushort)(addr + 1), (byte)(value >> 8));
    }
}
