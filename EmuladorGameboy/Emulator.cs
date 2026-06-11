using EmuladorGameboy.Cartridges;
using EmuladorGameboy.Core;

namespace EmuladorGameboy;

/// <summary>
/// O "core" do emulador: cria e conecta os componentes (Cartridge, MMU, CPU) e
/// oferece o laço de execução. É o dono de tudo — a UI, mais tarde, vai falar
/// só com esta classe.
/// </summary>
internal sealed class Emulator
{
    public Cartridge Cartridge { get; }
    public Mmu Mmu { get; }
    public Cpu Cpu { get; }

    public Emulator(string romPath)
    {
        // Ordem importa: a MMU precisa do cartucho; a CPU precisa da MMU.
        Cartridge = new Cartridge(romPath);
        Mmu = new Mmu(Cartridge);
        Cpu = new Cpu(Mmu);
    }

    /// <summary>
    /// Executa N instruções imprimindo o estado antes de cada uma (modo trace,
    /// só para depuração). Se topar num opcode não implementado, mostra qual e para.
    /// </summary>
    public void RunTraced(int instructions)
    {
        for (int i = 0; i < instructions; i++)
        {
            Console.WriteLine("  " + Cpu.TraceLine());
            try
            {
                Cpu.Step();
            }
            catch (NotImplementedException ex)
            {
                Console.WriteLine("  >> " + ex.Message);
                break;
            }
        }
    }
}
