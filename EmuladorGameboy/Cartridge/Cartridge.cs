using System.Text;

namespace EmuladorGameboy.Cartridges;

/// <summary>
/// O cartucho ("Game Pak"): a ROM + (opcional) RAM com bateria + o controlador de
/// banco (MBC). Lê o header (Fase 1) e faz o BANKING (Fase 9).
///
/// A CPU só enxerga 32 KiB de ROM por vez (0x0000-0x7FFF), mas jogos têm muito
/// mais. O MBC resolve isso trocando qual "banco" de 16 KiB aparece na janela
/// 0x4000-0x7FFF. O truque: o jogo "configura" o MBC ESCREVENDO em endereços de
/// ROM — como ROM é só leitura, o chip interpreta essas escritas como comandos.
///
/// Aqui implementamos o MBC3 (que a Pokémon Red usa) e o caso "sem MBC".
/// </summary>
internal sealed class Cartridge
{
    private readonly byte[] _rom;
    private readonly byte[] _ram;
    private readonly bool _hasMbc;

    // Estado do MBC:
    private int _romBank = 1;    // banco mapeado em 0x4000-0x7FFF (nunca 0)
    private int _ramBank = 0;    // banco de RAM mapeado em 0xA000-0xBFFF
    private bool _ramEnabled;    // a RAM precisa ser "destravada" antes de usar

    private const int TitleStart         = 0x0134;
    private const int TitleEnd           = 0x0143;
    private const int CartridgeTypeAddr  = 0x0147;
    private const int RomSizeAddr        = 0x0148;
    private const int RamSizeAddr        = 0x0149;
    private const int HeaderChecksumAddr = 0x014D;
    private const int MinimumRomSize     = 0x0150;

    public Cartridge(string path)
    {
        _rom = File.ReadAllBytes(path);
        if (_rom.Length < MinimumRomSize)
            throw new InvalidDataException(
                $"Arquivo de {_rom.Length} bytes é pequeno demais para ser uma ROM de Game Boy.");

        _ram = new byte[Math.Max(RamSizeKB * 1024, 0)];
        _hasMbc = CartridgeTypeCode != 0x00; // 0x00 = ROM ONLY (sem banking)
    }

    // ===================== Header (Fase 1) =====================
    public int FileSizeBytes => _rom.Length;

    public string Title
    {
        get
        {
            var sb = new StringBuilder();
            for (int addr = TitleStart; addr <= TitleEnd; addr++)
            {
                byte b = _rom[addr];
                if (b == 0x00) break;
                sb.Append((char)b);
            }
            return sb.ToString();
        }
    }

    public byte CartridgeTypeCode => _rom[CartridgeTypeAddr];

    public string CartridgeTypeName => CartridgeTypeCode switch
    {
        0x00 => "ROM ONLY",
        0x01 => "MBC1",
        0x02 => "MBC1+RAM",
        0x03 => "MBC1+RAM+BATTERY",
        0x0F => "MBC3+TIMER+BATTERY",
        0x10 => "MBC3+TIMER+RAM+BATTERY",
        0x11 => "MBC3",
        0x12 => "MBC3+RAM",
        0x13 => "MBC3+RAM+BATTERY",
        0x19 => "MBC5",
        0x1B => "MBC5+RAM+BATTERY",
        _    => $"Desconhecido (0x{CartridgeTypeCode:X2})",
    };

    public int RomSizeKB => 32 << _rom[RomSizeAddr];
    public int RomBanks => 2 << _rom[RomSizeAddr];

    public int RamSizeKB => _rom[RamSizeAddr] switch
    {
        0x02 => 8,
        0x03 => 32,
        0x04 => 128,
        0x05 => 64,
        _    => 0,
    };

    public byte HeaderChecksum => _rom[HeaderChecksumAddr];

    public bool IsHeaderChecksumValid()
    {
        byte checksum = 0;
        for (int addr = 0x0134; addr <= 0x014C; addr++)
            checksum = (byte)(checksum - _rom[addr] - 1);
        return checksum == HeaderChecksum;
    }

    // ===================== Banking (Fase 9) =====================

    /// <summary>Lê da ROM, aplicando o banco selecionado na janela 0x4000-0x7FFF.</summary>
    public byte ReadRom(ushort address)
    {
        if (address < 0x4000)
            return _rom[address]; // banco 0: sempre fixo

        // 0x4000-0x7FFF: banco selecionado pelo MBC
        int bank = _hasMbc ? _romBank : 1;
        int offset = bank * 0x4000 + (address - 0x4000);
        return offset < _rom.Length ? _rom[offset] : (byte)0xFF;
    }

    /// <summary>Escrita na faixa de ROM = comando pro MBC (não grava bytes de verdade).</summary>
    public void WriteRom(ushort address, byte value)
    {
        if (!_hasMbc) return;

        switch (address)
        {
            case < 0x2000: // habilita/desabilita a RAM externa
                _ramEnabled = (value & 0x0F) == 0x0A;
                break;

            case < 0x4000: // seleciona o banco de ROM (7 bits no MBC3); banco 0 vira 1
                int b = value & 0x7F;
                _romBank = b == 0 ? 1 : b;
                break;

            case < 0x6000: // seleciona o banco de RAM (0-3) — ou registrador de RTC (ignorado)
                _ramBank = value & 0x0F;
                break;

            default:       // 0x6000-0x7FFF: "latch" do relógio RTC (não implementado)
                break;
        }
    }

    public byte ReadRam(ushort address)
    {
        if (!_ramEnabled || _ramBank > 0x03) return 0xFF; // RAM travada ou registrador de RTC
        int offset = _ramBank * 0x2000 + (address - 0xA000);
        return offset < _ram.Length ? _ram[offset] : (byte)0xFF;
    }

    public void WriteRam(ushort address, byte value)
    {
        if (!_ramEnabled || _ramBank > 0x03) return;
        int offset = _ramBank * 0x2000 + (address - 0xA000);
        if (offset < _ram.Length) _ram[offset] = value;
    }
}
