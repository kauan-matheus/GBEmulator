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

    // O HEADER do cartucho fica no endereço 0x0100-0x014F. Tem informações sobre a ROM e validação.
    // Todos os endereços abaixo são RELATIVOS ao início da ROM (index 0 = endereço 0x0000).

    private const int TitleStart         = 0x0134; // Início do nome do jogo (ASCII): byte 0x0134
    private const int TitleEnd           = 0x0143; // Fim do nome do jogo: byte 0x0143. Total = 16 bytes pra guardar o título
    private const int CartridgeTypeAddr  = 0x0147; // Tipo de cartucho (MBC1, MBC3, MBC5, etc). 1 byte. Diz qual "chip" o cartucho usa
    private const int RomSizeAddr        = 0x0148; // Tamanho da ROM: 1 byte. Valor é código (0=32KB, 1=64KB, 2=128KB, etc). Fórmula: 32 << valor
    private const int RamSizeAddr        = 0x0149; // Tamanho da RAM externa: 1 byte. Outro código (0=nenhuma, 2=8KB, 3=32KB, 4=128KB, 5=64KB)
    private const int HeaderChecksumAddr = 0x014D; // Checksum do header: 1 byte. Verificação pra garantir que o header não foi corrompido
    private const int MinimumRomSize     = 0x0150; // Tamanho mínimo válido: 336 bytes. Se a ROM tem menos, não é uma ROM de Game Boy válida

    public Cartridge(string path)
    {
        // Carrega o arquivo inteiro do disco em memória como um array de bytes
        _rom = File.ReadAllBytes(path);

        // Valida se o arquivo tem pelo menos 336 bytes (tamanho mínimo de uma ROM de Game Boy válida)
        if (_rom.Length < MinimumRomSize)
            throw new InvalidDataException(
                $"Arquivo de {_rom.Length} bytes é pequeno demais para ser uma ROM de Game Boy.");

        // Aloca a RAM externa do cartucho. Usa RamSizeKB (extraído do header) para determinar o tamanho em bytes
        // Math.Max garante que nunca aloque um array negativo (caso RamSizeKB seja 0 para ROMs sem RAM externa)
        _ram = new byte[Math.Max(RamSizeKB * 1024, 0)];

        // Define se o cartucho tem um controlador de banco (MBC). 0x00 = ROM ONLY (sem MBC), qualquer outro valor = tem MBC
        _hasMbc = CartridgeTypeCode != 0x00;
    }

    // ===================== Header (Fase 1) =====================
    // Retorna o tamanho total do arquivo ROM em bytes
    public int FileSizeBytes => _rom.Length;

    // Extrai o título do jogo armazenado no header da ROM (0x0134 a 0x0143 = 16 bytes)
    public string Title
    {
        get
        {
            // Cria um StringBuilder para construir a string do título sem desperdício de memória
            var sb = new StringBuilder();

            // Itera por cada byte na faixa do título (TitleStart = 0x0134, TitleEnd = 0x0143)
            for (int addr = TitleStart; addr <= TitleEnd; addr++)
            {
                // Lê o byte atual da ROM no endereço do título
                byte b = _rom[addr];

                // Se encontrar um byte nulo (0x00), significa fim da string — interrompe o loop
                if (b == 0x00) break;

                // Converte o byte para caractere ASCII e adiciona ao StringBuilder
                sb.Append((char)b);
            }

            // Retorna a string montada
            return sb.ToString();
        }
    }

    // Lê o byte de tipo de cartucho do header (0x0147). Define qual chip controlador (MBC) o cartucho usa
    public byte CartridgeTypeCode => _rom[CartridgeTypeAddr];

    // Converte o código de tipo de cartucho em um nome legível usando switch expression
    // Cada case corresponde a um tipo específico de hardware de cartucho (MBC1, MBC3, MBC5, etc)
    public string CartridgeTypeName => CartridgeTypeCode switch
    {
        // 0x00: ROM sem controlador de banco — todas as 32 KB de ROM acessíveis diretamente
        0x00 => "ROM ONLY",
        // 0x01-0x03: Memory Bank Controller 1 (MBC1) — usado em muitos jogos clássicos
        0x01 => "MBC1",
        0x02 => "MBC1+RAM",
        0x03 => "MBC1+RAM+BATTERY",
        // 0x0F-0x13: Memory Bank Controller 3 (MBC3) — usado em Pokémon Red/Blue e tem relógio de tempo real
        0x0F => "MBC3+TIMER+BATTERY",
        0x10 => "MBC3+TIMER+RAM+BATTERY",
        0x11 => "MBC3",
        0x12 => "MBC3+RAM",
        0x13 => "MBC3+RAM+BATTERY",
        // 0x19, 0x1B: Memory Bank Controller 5 (MBC5) — versão melhorada do MBC3, sem relógio RTC
        0x19 => "MBC5",
        0x1B => "MBC5+RAM+BATTERY",
        // Qualquer tipo desconhecido: retorna o código em hexadecimal para debug
        _    => $"Desconhecido (0x{CartridgeTypeCode:X2})",
    };

    // Calcula o tamanho total da ROM em kilobytes usando bit shift left (<<)
    // O header armazena um código: 32 << 0 = 32 KB, 32 << 1 = 64 KB, 32 << 2 = 128 KB, etc
    public int RomSizeKB => 32 << _rom[RomSizeAddr];

    // Calcula a quantidade de bancos de ROM (cada banco = 16 KB acessível por vez)
    // 2 << 0 = 2 bancos (32 KB total), 2 << 1 = 4 bancos (64 KB), 2 << 2 = 8 bancos (128 KB), etc
    public int RomBanks => 2 << _rom[RomSizeAddr];

    // Converte o código de tamanho de RAM do header em kilobytes
    // O Game Boy suporta diferentes configurações de RAM externa (0, 8 KB, 32 KB, 64 KB ou 128 KB)
    public int RamSizeKB => _rom[RamSizeAddr] switch
    {
        0x02 => 8,      // Código 0x02 = 8 KB de RAM
        0x03 => 32,     // Código 0x03 = 32 KB de RAM
        0x04 => 128,    // Código 0x04 = 128 KB de RAM
        0x05 => 64,     // Código 0x05 = 64 KB de RAM
        _    => 0,      // Qualquer outro código = sem RAM externa
    };

    // Lê o checksum do header armazenado no byte 0x014D
    // O checksum valida se o header não foi corrompido durante a transmissão/armazenamento
    public byte HeaderChecksum => _rom[HeaderChecksumAddr];

    // Valida a integridade do header comparando o checksum calculado com o armazenado
    // O algoritmo subtrativo garante que qualquer alteração no header será detectada
    public bool IsHeaderChecksumValid()
    {
        // Inicializa o checksum em 0. O algoritmo Game Boy usa subtração, não XOR
        byte checksum = 0;

        // Itera por cada byte do header (0x0134 a 0x014C = 25 bytes, excluindo o próprio checksum)
        for (int addr = 0x0134; addr <= 0x014C; addr++)
            // Subtrai cada byte do header do checksum (com decremento extra de 1)
            // Fórmula: checksum = (checksum - byte - 1) & 0xFF
            checksum = (byte)(checksum - _rom[addr] - 1);

        // Se o checksum calculado bater com o armazenado, o header é válido
        return checksum == HeaderChecksum;
    }

    // ===================== Banking (Fase 9) =====================

    // Lê um byte da ROM, aplicando a lógica de banking do MBC
    // O Game Boy mapeia 0x0000-0x3FFF (banco fixo 0) e 0x4000-0x7FFF (banco selecionável)
    public byte ReadRom(ushort address)
    {
        // Endereços 0x0000-0x3FFF: sempre apontam para o banco 0 (não muda)
        if (address < 0x4000)
            return _rom[address];

        // Endereços 0x4000-0x7FFF: apontam para o banco selecionado pelo MBC
        // Se não tem MBC, assume banco 1 fixo; se tem MBC, usa o banco selecionado (_romBank)
        int bank = _hasMbc ? _romBank : 1;

        // Calcula o offset real na ROM: (banco * 16 KB) + (endereço dentro da janela)
        // 0x4000 = 16 KB (tamanho de cada banco)
        int offset = bank * 0x4000 + (address - 0x4000);

        // Retorna o byte se estiver dentro dos limites da ROM, ou 0xFF se estiver fora
        return offset < _rom.Length ? _rom[offset] : (byte)0xFF;
    }

    // Escreve um byte na faixa de ROM — isso NÃO grava de verdade, é um comando pro MBC
    // O chip MBC interpreta essas "escritas" como configurações (qual banco, RAM ativada, etc)
    public void WriteRom(ushort address, byte value)
    {
        // Se o cartucho não tem MBC, ignora qualquer "escrita" de configuração
        if (!_hasMbc) return;

        // Diferentes faixas de endereço controlam diferentes registradores do MBC
        switch (address)
        {
            // 0x0000-0x1FFF: Registrador de ativação da RAM externa
            // Escrever 0x0A nessa faixa ativa a RAM; qualquer outro valor (com baixos 4 bits) desativa
            case < 0x2000:
                _ramEnabled = (value & 0x0F) == 0x0A;
                break;

            // 0x2000-0x3FFF: Registrador de seleção de banco de ROM
            // Escrever aqui muda qual banco de ROM aparece em 0x4000-0x7FFF
            // Usa 7 bits (máximo 128 bancos); banco 0 é automaticamente convertido para 1
            case < 0x4000:
                int b = value & 0x7F;  // Máscara para 7 bits
                _romBank = b == 0 ? 1 : b;  // Banco 0 é inválido, usa 1 como padrão
                break;

            // 0x4000-0x5FFF: Registrador de seleção de banco de RAM (ou registrador de RTC no MBC3)
            // Escrever aqui muda qual banco de RAM aparece em 0xA000-0xBFFF
            // No MBC3, valores 0x08-0x0C acessam registradores de tempo real (ignorados aqui)
            case < 0x6000:
                _ramBank = value & 0x0F;  // Máscara para 4 bits (0-15, mas só 0-3 são RAM válida)
                break;

            // 0x6000-0x7FFF: Registrador de latch do relógio de tempo real (RTC)
            // No MBC3, permite "congelar" o relógio para leitura. Não implementado aqui.
            default:
                break;
        }
    }

    // Lê um byte da RAM externa do cartucho (faixa 0xA000-0xBFFF)
    // A RAM é segmentada em bancos de 8 KB, selecionáveis via WriteRom
    public byte ReadRam(ushort address)
    {
        // Retorna 0xFF se a RAM não está ativada ou se estamos acessando um registrador de RTC
        // Registradores de RTC (0x08-0x0C) não são implementados, então retornam 0xFF
        if (!_ramEnabled || _ramBank > 0x03)
            return 0xFF;

        // Calcula o offset real na RAM: (banco * 8 KB) + (endereço dentro da janela)
        // 0x2000 = 8 KB (tamanho de cada banco de RAM)
        int offset = _ramBank * 0x2000 + (address - 0xA000);

        // Retorna o byte se estiver dentro dos limites da RAM alocada, ou 0xFF se estiver fora
        return offset < _ram.Length ? _ram[offset] : (byte)0xFF;
    }

    // Escreve um byte na RAM externa do cartucho (faixa 0xA000-0xBFFF)
    public void WriteRam(ushort address, byte value)
    {
        // Ignora a escrita se a RAM não está ativada ou se estamos tentando escrever em um registrador de RTC
        if (!_ramEnabled || _ramBank > 0x03)
            return;

        // Calcula o offset real na RAM (mesmo formato que ReadRam)
        int offset = _ramBank * 0x2000 + (address - 0xA000);

        // Grava o byte se estiver dentro dos limites da RAM alocada
        if (offset < _ram.Length)
            _ram[offset] = value;
    }
}
