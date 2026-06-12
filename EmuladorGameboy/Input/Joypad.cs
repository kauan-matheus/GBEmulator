using EmuladorGameboy.Core;

namespace EmuladorGameboy.Input;

/// <summary>Os 8 botões do Game Boy.</summary>
internal enum GbButton { Right, Left, Up, Down, A, B, Select, Start }

/// <summary>
/// O joypad (registrador 0xFF00).
///
/// O hardware é uma matriz 2x4. O jogo ESCOLHE qual grupo quer ler — direções OU
/// ações — zerando o bit 4 ou 5; e LÊ o estado nos bits 0-3. Detalhe que confunde
/// todo mundo: a lógica é INVERTIDA — 0 = pressionado, 1 = solto.
///
///   bit 5 (0=ações)      bit 3   bit 2   bit 1   bit 0
///   bit 4 (0=direções)   Down/St Up/Sel Left/B  Right/A
/// </summary>
internal sealed class Joypad
{
    private readonly Mmu _mmu;

    private byte _select = 0x30;        // bits 4 e 5 (nenhum grupo selecionado no reset)
    private readonly bool[] _pressed = new bool[8]; // indexado por GbButton

    public Joypad(Mmu mmu) => _mmu = mmu;

    /// <summary>Valor lido em 0xFF00, montado a partir do grupo selecionado.</summary>
    public byte Read()
    {
        // Começa com tudo "solto" (bits 0-3 = 1) e os bits altos fixos.
        int result = 0xC0 | _select | 0x0F;

        if ((_select & 0x10) == 0) // direções selecionadas
        {
            if (_pressed[(int)GbButton.Right]) result &= ~0x01;
            if (_pressed[(int)GbButton.Left])  result &= ~0x02;
            if (_pressed[(int)GbButton.Up])    result &= ~0x04;
            if (_pressed[(int)GbButton.Down])  result &= ~0x08;
        }
        if ((_select & 0x20) == 0) // ações selecionadas
        {
            if (_pressed[(int)GbButton.A])      result &= ~0x01;
            if (_pressed[(int)GbButton.B])      result &= ~0x02;
            if (_pressed[(int)GbButton.Select]) result &= ~0x04;
            if (_pressed[(int)GbButton.Start])  result &= ~0x08;
        }

        return (byte)result;
    }

    /// <summary>A CPU escreve aqui só pra escolher o grupo (bits 4 e 5).</summary>
    public void Write(byte value) => _select = (byte)(value & 0x30);

    /// <summary>A UI chama isto quando uma tecla é pressionada/solta.</summary>
    public void SetButton(GbButton button, bool pressed)
    {
        bool was = _pressed[(int)button];
        _pressed[(int)button] = pressed;

        // Apertar (transição solto->pressionado) dispara a interrupção de Joypad.
        if (!was && pressed) _mmu.RequestInterrupt(4);
    }
}
