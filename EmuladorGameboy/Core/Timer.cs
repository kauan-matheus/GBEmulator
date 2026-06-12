namespace EmuladorGameboy.Core;

/// <summary>
/// O timer programável do Game Boy (registradores 0xFF04-0xFF07).
///
///   DIV  (FF04) — incrementa SEMPRE a 16384 Hz (a cada 256 T-cycles). Escrever zera.
///   TIMA (FF05) — contador que sobe na frequência escolhida pelo TAC. Ao ESTOURAR,
///                 recarrega de TMA e pede a interrupção de Timer (bit 2 de IF).
///   TMA  (FF06) — valor de recarga do TIMA.
///   TAC  (FF07) — bit 2 liga/desliga; bits 0-1 escolhem a frequência.
///
/// Chamamos o nome 'GbTimer' (e não 'Timer') só pra não colidir com os vários
/// tipos 'Timer' do .NET (System.Threading.Timer, etc.).
/// </summary>
internal sealed class GbTimer
{
    private readonly Mmu _mmu;

    private int _divCounter;  // acumula T-cycles rumo ao próximo incremento de DIV
    private int _timaCounter; // idem para o TIMA
    private byte _div, _tima, _tma, _tac;

    public GbTimer(Mmu mmu) => _mmu = mmu;

    public byte ReadRegister(ushort addr) => addr switch
    {
        0xFF04 => _div,
        0xFF05 => _tima,
        0xFF06 => _tma,
        0xFF07 => (byte)(_tac | 0xF8), // bits não usados leem como 1
        _ => 0xFF,
    };

    public void WriteRegister(ushort addr, byte v)
    {
        switch (addr)
        {
            case 0xFF04: _div = 0; _divCounter = 0; break; // QUALQUER escrita zera o DIV
            case 0xFF05: _tima = v; break;
            case 0xFF06: _tma = v; break;
            case 0xFF07: _tac = (byte)(v & 0x07); break;
        }
    }

    public void Step(int cycles)
    {
        // DIV: sempre contando.
        _divCounter += cycles;
        while (_divCounter >= 256)
        {
            _divCounter -= 256;
            _div++;
        }

        // TIMA: só se habilitado (bit 2 do TAC).
        if ((_tac & 0x04) == 0) return;

        int period = (_tac & 0x03) switch
        {
            0 => 1024, // 4096 Hz
            1 => 16,   // 262144 Hz
            2 => 64,   // 65536 Hz
            _ => 256,  // 16384 Hz
        };

        _timaCounter += cycles;
        while (_timaCounter >= period)
        {
            _timaCounter -= period;
            if (_tima == 0xFF)
            {
                _tima = _tma;             // estourou: recarrega de TMA
                _mmu.RequestInterrupt(2); // e pede a interrupção de Timer
            }
            else
            {
                _tima++;
            }
        }
    }
}
