using EmuladorGameboy.Cartridges;
using EmuladorGameboy.Core;
using EmuladorGameboy.Graphics;
using EmuladorGameboy.Input;

namespace EmuladorGameboy;

/// <summary>
/// O "core": cria e conecta todos os componentes e roda a emulação por quadros.
/// </summary>
internal sealed class Emulator
{
    // Um quadro do Game Boy dura 70224 T-cycles (154 linhas x 456 dots).
    private const int CyclesPerFrame = 70224;

    public Cartridge Cartridge { get; }
    public Mmu Mmu { get; }
    public Ppu Ppu { get; }
    public GbTimer Timer { get; }
    public Joypad Joypad { get; }
    public Cpu Cpu { get; }

    public Emulator(string romPath)
    {
        Cartridge = new Cartridge(romPath);
        Mmu = new Mmu(Cartridge);
        Ppu = new Ppu(Mmu);
        Timer = new GbTimer(Mmu);
        Joypad = new Joypad(Mmu);
        Mmu.Attach(Ppu, Timer, Joypad);  // liga as referências cruzadas
        Cpu = new Cpu(Mmu);
    }

    /// <summary>
    /// Roda a emulação por um quadro inteiro e devolve a tela pronta (160x144).
    /// O segredo da sincronização: a cada instrução, a CPU diz quantos ciclos
    /// gastou, e nós avançamos PPU e timer pelo MESMO tanto.
    /// O callback onInputRefresh (se fornecido) é chamado a cada ~256 ciclos para
    /// ler o input com menor latência (~9x por frame em vez de 1x).
    /// </summary>
    public byte[] RunFrame(Action? onInputRefresh = null)
    {
        int elapsed = 0;
        int nextInputCheck = 256;
        while (elapsed < CyclesPerFrame)
        {
            int cycles = Cpu.Step();
            Ppu.Step(cycles);
            Timer.Step(cycles);
            elapsed += cycles;

            if (elapsed >= nextInputCheck)
            {
                onInputRefresh?.Invoke();
                nextInputCheck += 256;
            }
        }
        return Ppu.Frame;
    }
}
