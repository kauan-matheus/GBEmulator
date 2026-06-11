using EmuladorGameboy;

// O console do Windows às vezes não mostra acentos/UTF-8 por padrão.
Console.OutputEncoding = System.Text.Encoding.UTF8;

// Caminho da ROM: 1º argumento da linha de comando, ou a Pokémon Red no Desktop.
string romPath = args.Length > 0
    ? args[0]
    : Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        "Pokemon - Red Version (USA, Europe) (SGB Enhanced).gb");

if (!File.Exists(romPath))
{
    Console.WriteLine($"ROM não encontrada: {romPath}");
    Console.WriteLine("Dica: passe o caminho do arquivo .gb como argumento.");
    return;
}

var emu = new Emulator(romPath);
var cart = emu.Cartridge;
var cpu = emu.Cpu;

// ---------- 1) Header do cartucho (Fase 1) ----------
Console.WriteLine("=== Game Boy ROM Header ===");
Console.WriteLine($"Arquivo:       {Path.GetFileName(romPath)}");
Console.WriteLine($"Título:        {cart.Title}");
Console.WriteLine($"Tipo:          0x{cart.CartridgeTypeCode:X2}  ({cart.CartridgeTypeName})");
Console.WriteLine($"ROM:           {cart.RomSizeKB} KB  ({cart.RomBanks} bancos)");
Console.WriteLine($"RAM (cart):    {cart.RamSizeKB} KB");
Console.WriteLine($"Header cksum:  {(cart.IsHeaderChecksumValid() ? "VÁLIDO" : "INVÁLIDO")}");

// ---------- 2) Autoteste da CPU (Fases 3 e 4) ----------
// Montamos programinhas com resultado CONHECIDO na WRAM (0xC000) e conferimos.
// É assim que se testa uma CPU sem precisar de uma ROM de teste externa.
Console.WriteLine();
Console.WriteLine("=== Autoteste da CPU ===");

// Teste 1: LD A,0x05 ; LD B,0x0A ; ADD A,B  -> A = 0x0F, Z = 0
LoadProgram(0xC000, 0x3E, 0x05, 0x06, 0x0A, 0x80);
cpu.Registers.PC = 0xC000;
cpu.Step(); cpu.Step(); cpu.Step();
bool t1 = cpu.Registers.A == 0x0F && !cpu.Registers.FlagZ;

// Teste 2: LD A,0xFF ; INC A  -> A = 0x00, Z = 1, H = 1, N = 0 (overflow do nibble)
LoadProgram(0xC000, 0x3E, 0xFF, 0x3C);
cpu.Registers.PC = 0xC000;
cpu.Step(); cpu.Step();
bool t2 = cpu.Registers.A == 0x00 && cpu.Registers.FlagZ && cpu.Registers.FlagH && !cpu.Registers.FlagN;

// Teste 3: LD A,0xAB ; SWAP A (CB 37)  -> A = 0xBA (troca os nibbles)
LoadProgram(0xC000, 0x3E, 0xAB, 0xCB, 0x37);
cpu.Registers.PC = 0xC000;
cpu.Step(); cpu.Step();
bool t3 = cpu.Registers.A == 0xBA;

// Teste 4: pilha — LD SP,0xFFFE ; LD BC,0x1234 ; PUSH BC ; POP DE -> DE = 0x1234
LoadProgram(0xC000, 0x31, 0xFE, 0xFF, 0x01, 0x34, 0x12, 0xC5, 0xD1);
cpu.Registers.PC = 0xC000;
cpu.Step(); cpu.Step(); cpu.Step(); cpu.Step();
bool t4 = cpu.Registers.DE == 0x1234;

Console.WriteLine($"  Teste 1 (ADD A,B = 0x0F):        {(t1 ? "PASS" : "FAIL")}");
Console.WriteLine($"  Teste 2 (INC overflow -> Z,H):   {(t2 ? "PASS" : "FAIL")}");
Console.WriteLine($"  Teste 3 (SWAP A nibbles):        {(t3 ? "PASS" : "FAIL")}");
Console.WriteLine($"  Teste 4 (PUSH BC / POP DE):      {(t4 ? "PASS" : "FAIL")}");

// ---------- 3) Trace: executando a ROM real ----------
Console.WriteLine();
Console.WriteLine("=== Trace: Pokémon Red a partir de 0x0100 ===");
cpu.Reset(); // volta ao estado pós-boot (PC=0x0100) — o autoteste mexeu nos registradores
emu.RunTraced(20);

// Função local: grava uma sequência de bytes na memória (via barramento).
void LoadProgram(ushort at, params byte[] bytes)
{
    for (int i = 0; i < bytes.Length; i++)
        emu.Mmu.WriteByte((ushort)(at + i), bytes[i]);
}
