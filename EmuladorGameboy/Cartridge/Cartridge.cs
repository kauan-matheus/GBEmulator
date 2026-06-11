using System.Text;

namespace EmuladorGameboy.Cartridges;

/// <summary>
/// Representa o cartucho ("Game Pak") do Game Boy.
///
/// No hardware real, o cartucho é só uma placa com um chip de ROM (e, às vezes,
/// RAM + bateria + um controlador de banco chamado "MBC"). Para o console, esse
/// chip aparece como bytes no barramento de memória nas faixas 0x0000-0x7FFF
/// (ROM) e 0xA000-0xBFFF (RAM externa, quando existe).
///
/// Nesta Fase 1 a classe só faz duas coisas:
///   1. carregar o arquivo .gb inteiro para a memória;
///   2. ler o HEADER (0x0100-0x014F) — a "etiqueta" que descreve o cartucho.
/// </summary>
internal sealed class Cartridge
{
    // Conteúdo bruto do arquivo .gb. É exatamente a sequência de bytes que os
    // chips de ROM entregariam ao console, um a um, quando ele lê os endereços.
    private readonly byte[] _rom;

    // --- Offsets do header (constantes nomeadas em vez de "números mágicos") ---
    // Manter os endereços com nome deixa o código legível e evita errar 0x148/0x149.
    private const int TitleStart         = 0x0134;
    private const int TitleEnd           = 0x0143; // inclusive (16 bytes no total)
    private const int CartridgeTypeAddr  = 0x0147;
    private const int RomSizeAddr        = 0x0148;
    private const int RamSizeAddr        = 0x0149;
    private const int HeaderChecksumAddr = 0x014D;

    // O código do jogo começa em 0x0150. Abaixo disso é só header; um arquivo
    // menor que isso nem chega a ter um header completo.
    private const int MinimumRomSize = 0x0150;

    public Cartridge(string path)
    {
        // File.ReadAllBytes lê o arquivo todo de uma vez para um byte[].
        // Em C# o tipo "byte" é unsigned (0..255), igualzinho a um byte de memória
        // real — sem as dores de "byte negativo" de outras linguagens.
        _rom = File.ReadAllBytes(path);

        // Defesa básica: um arquivo pequeno demais não é uma ROM válida.
        if (_rom.Length < MinimumRomSize)
            throw new InvalidDataException(
                $"Arquivo de {_rom.Length} bytes é pequeno demais para ser uma ROM de Game Boy.");
    }

    /// <summary>Tamanho real do arquivo carregado, em bytes.</summary>
    public int FileSizeBytes => _rom.Length;

    /// <summary>
    /// Lê um byte da ROM do cartucho (faixa 0x0000-0x7FFF vista pela CPU).
    /// Por enquanto SEM banking (o MBC vem na Fase 9): devolve o byte direto do
    /// arquivo, o que já está correto para o banco 0 e para o banco 1 no reset.
    /// </summary>
    public byte ReadRom(ushort address)
        => address < _rom.Length ? _rom[address] : (byte)0xFF;

    /// <summary>
    /// Título do jogo (0x0134-0x0143), em ASCII, preenchido com 0x00 à direita.
    /// Ex.: a Pokémon Red guarda "POKEMON RED" seguido de zeros.
    /// </summary>
    public string Title
    {
        get
        {
            var sb = new StringBuilder();
            for (int addr = TitleStart; addr <= TitleEnd; addr++)
            {
                byte b = _rom[addr];
                if (b == 0x00) break;   // o campo é preenchido com zeros à direita: paramos no 1º
                sb.Append((char)b);     // ASCII: o próprio byte JÁ é o código do caractere
            }
            return sb.ToString();
        }
    }

    /// <summary>Byte cru do tipo de cartucho (0x0147). É ele que diz qual MBC usar.</summary>
    public byte CartridgeTypeCode => _rom[CartridgeTypeAddr];

    /// <summary>Nome legível do tipo de cartucho, traduzido do byte 0x0147.</summary>
    public string CartridgeTypeName => CartridgeTypeCode switch
    {
        0x00 => "ROM ONLY",
        0x01 => "MBC1",
        0x02 => "MBC1+RAM",
        0x03 => "MBC1+RAM+BATTERY",
        0x05 => "MBC2",
        0x06 => "MBC2+BATTERY",
        0x08 => "ROM+RAM",
        0x09 => "ROM+RAM+BATTERY",
        0x0F => "MBC3+TIMER+BATTERY",
        0x10 => "MBC3+TIMER+RAM+BATTERY",
        0x11 => "MBC3",
        0x12 => "MBC3+RAM",
        0x13 => "MBC3+RAM+BATTERY",
        0x19 => "MBC5",
        0x1A => "MBC5+RAM",
        0x1B => "MBC5+RAM+BATTERY",
        0x1C => "MBC5+RUMBLE",
        0x1D => "MBC5+RUMBLE+RAM",
        0x1E => "MBC5+RUMBLE+RAM+BATTERY",
        _    => $"Desconhecido (0x{CartridgeTypeCode:X2})",
    };

    /// <summary>
    /// Tamanho da ROM em KiB. O header (0x0148) guarda um EXPOENTE, não o tamanho:
    /// tamanho = 32 KiB << valor   (ex.: valor 5 -> 32 &lt;&lt; 5 = 1024 KiB = 1 MiB).
    /// </summary>
    public int RomSizeKB => 32 << _rom[RomSizeAddr];

    /// <summary>Número de bancos de 16 KiB da ROM. = 2 &lt;&lt; valor.</summary>
    public int RomBanks => 2 << _rom[RomSizeAddr];

    /// <summary>
    /// Tamanho da RAM do cartucho (onde ficam os SAVES), em KiB (header 0x0149).
    /// Aqui o byte é um código de tabela, não um expoente.
    /// </summary>
    public int RamSizeKB => _rom[RamSizeAddr] switch
    {
        0x00 => 0,    // sem RAM
        0x02 => 8,    // 1 banco de 8 KiB
        0x03 => 32,   // 4 bancos de 8 KiB
        0x04 => 128,  // 16 bancos
        0x05 => 64,   // 8 bancos
        _    => 0,    // 0x01 é "não usado" oficialmente
    };

    /// <summary>Byte do header checksum gravado no cartucho (0x014D).</summary>
    public byte HeaderChecksum => _rom[HeaderChecksumAddr];

    /// <summary>
    /// Recalcula o header checksum EXATAMENTE como a Boot ROM faz e compara com o
    /// byte gravado em 0x014D. No hardware real, se esta conta não bate, o console
    /// TRAVA na tela do logo e o jogo nunca roda.
    ///
    /// Algoritmo (somando de 0x0134 a 0x014C, inclusive):
    ///     checksum = checksum - byte - 1
    /// </summary>
    public bool IsHeaderChecksumValid()
    {
        byte checksum = 0;
        for (int addr = 0x0134; addr <= 0x014C; addr++)
            checksum = (byte)(checksum - _rom[addr] - 1); // o cast (byte) faz o "& 0xFF" de 8 bits
        return checksum == HeaderChecksum;
    }
}
